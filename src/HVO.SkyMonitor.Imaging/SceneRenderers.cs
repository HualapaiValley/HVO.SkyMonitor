using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One deterministic sensor defect applied after stochastic sensor noise.</summary>
public sealed record SensorDefect(int X, int Y, double Offset = 0, double? FixedValue = null);

/// <summary>Shared linear sensor and optical settings for scene rendering.</summary>
public record LinearSceneRenderOptions
{
    public double ExposureSeconds { get; init; } = 1;
    public double Gain { get; init; } = 1;
    public double MagnitudeZeroElectronsPerSecond { get; init; } = 10_000;
    public double BackgroundElectronsPerSecond { get; init; }
    public double PsfSigmaPixels { get; init; } = 1;
    public double PsfRadiusPixels { get; init; } = 4;
    public double VignettingStrength { get; init; }
    public double Bias { get; init; }
    public double ReadNoiseStandardDeviation { get; init; }
    public bool ShotNoiseEnabled { get; init; }
    public double DarkCurrentElectronsPerSecond { get; init; }
    public bool DarkNoiseEnabled { get; init; }
    public int Seed { get; init; }
    public IReadOnlyList<SensorDefect> Defects { get; init; } = Array.Empty<SensorDefect>();
    public VirtualCloudRenderContext? Cloud { get; init; }
    public VirtualTransientRenderContext? Transient { get; init; }

    /// <summary>Validates finite, non-negative sensor parameters and bounded optical settings.</summary>
    public virtual void Validate()
    {
        if (!IsNonNegativeFinite(ExposureSeconds) || ExposureSeconds > 86_400 || !IsNonNegativeFinite(Gain) ||
            !IsNonNegativeFinite(MagnitudeZeroElectronsPerSecond) || !IsNonNegativeFinite(BackgroundElectronsPerSecond) ||
            !double.IsFinite(PsfSigmaPixels) || PsfSigmaPixels <= 0 || !double.IsFinite(PsfRadiusPixels) ||
            PsfRadiusPixels <= 0 || PsfRadiusPixels > 64 || !double.IsFinite(VignettingStrength) ||
            VignettingStrength is < 0 or > 1 || !IsNonNegativeFinite(Bias) ||
            !IsNonNegativeFinite(ReadNoiseStandardDeviation) || !IsNonNegativeFinite(DarkCurrentElectronsPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(LinearSceneRenderOptions));
        }

        ArgumentNullException.ThrowIfNull(Defects);
        foreach (var defect in Defects)
        {
            ArgumentNullException.ThrowIfNull(defect);
            if (!double.IsFinite(defect.Offset) || defect.FixedValue is { } fixedValue && !double.IsFinite(fixedValue))
            {
                throw new ArgumentOutOfRangeException(nameof(Defects));
            }
        }

        if (!double.IsFinite(ExposureSeconds * Gain) ||
            !double.IsFinite(BackgroundElectronsPerSecond * ExposureSeconds * Gain) ||
            !double.IsFinite(MagnitudeZeroElectronsPerSecond * ExposureSeconds * Gain) ||
            !double.IsFinite(DarkCurrentElectronsPerSecond * ExposureSeconds * Gain))
        {
            throw new ArgumentOutOfRangeException(nameof(LinearSceneRenderOptions), "Combined exposure and signal scaling must remain finite.");
        }

        Cloud?.Validate();
        Transient?.Validate();
        if (Transient is not null && MagnitudeZeroElectronsPerSecond > 1_000_000_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MagnitudeZeroElectronsPerSecond),
                "Transient signal scaling must remain bounded.");
        }
        var exposure = TimeSpan.FromSeconds(ExposureSeconds);
        if ((Cloud is not null && Cloud.IntegrationDuration != exposure) ||
            (Transient is not null && Transient.IntegrationDuration != exposure) ||
            (Cloud is not null && Transient is not null &&
            (Cloud.IntegrationStartUtc != Transient.IntegrationStartUtc ||
             Cloud.IntegrationDuration != Transient.IntegrationDuration)))
        {
            throw new ArgumentException("Cloud and transient intervals must match the rendered exposure.",
                nameof(LinearSceneRenderOptions));
        }
    }

    private static bool IsNonNegativeFinite(double value) => double.IsFinite(value) && value >= 0;
}

/// <summary>Settings for packed unsigned little-endian Mono16 output.</summary>
public sealed record Mono16SceneRenderOptions : LinearSceneRenderOptions
{
    public MonoSensorResponse? SensorResponse { get; init; }

    public override void Validate()
    {
        base.Validate();
        SensorResponse?.Validate();
    }
}

/// <summary>Resolved electron-domain response for one monochrome sensor gain setting.</summary>
public sealed record MonoSensorResponse
{
    public int AdcBitDepth { get; init; } = 12;
    public double FullWellElectrons { get; init; } = 32_400;
    public double ElectronsPerAdu { get; init; } = 7.9;
    public double ReadNoiseElectrons { get; init; } = 5.9;
    public double BlackLevelAdu { get; init; }
    public string CompatibilityLabel { get; init; } = "Native ADC samples in Mono16 container";

    internal int MaximumAdu => (1 << AdcBitDepth) - 1;

    internal void Validate()
    {
        if (AdcBitDepth is < 1 or > 16 || !double.IsFinite(FullWellElectrons) || FullWellElectrons <= 0 ||
            !double.IsFinite(ElectronsPerAdu) || ElectronsPerAdu <= 0 ||
            !double.IsFinite(ReadNoiseElectrons) || ReadNoiseElectrons < 0 ||
            !double.IsFinite(BlackLevelAdu) || BlackLevelAdu < 0 || BlackLevelAdu > MaximumAdu)
        {
            throw new ArgumentOutOfRangeException(nameof(MonoSensorResponse));
        }
    }
}

/// <summary>Published ASI174MM gain and read-noise response in ZWO gain-control units.</summary>
public static class Asi174MmSensorModel
{
    private static readonly (double Gain, double ReadNoise)[] ReadNoisePoints =
        [(0, 5.9), (100, 4.45), (200, 3.8), (300, 3.58), (400, 3.6)];

    public const string Version = "zwo-asi174mm-12bit-v1";

    /// <summary>Resolves the 0.1 dB ZWO gain control to input-referred sensor values.</summary>
    public static MonoSensorResponse Resolve(double gainControl, double blackLevelAdu = 64)
    {
        if (!double.IsFinite(gainControl) || gainControl is < 0 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(gainControl));
        }

        return new MonoSensorResponse
        {
            AdcBitDepth = 12,
            FullWellElectrons = 32_400,
            ElectronsPerAdu = 7.9 / Math.Pow(10, gainControl / 200),
            ReadNoiseElectrons = InterpolateReadNoise(gainControl),
            BlackLevelAdu = blackLevelAdu,
            CompatibilityLabel = "ASI174MM native 12-bit ADU in Mono16"
        };
    }

    private static double InterpolateReadNoise(double gain)
    {
        for (var index = 1; index < ReadNoisePoints.Length; index++)
        {
            var upper = ReadNoisePoints[index];
            if (gain <= upper.Gain)
            {
                var lower = ReadNoisePoints[index - 1];
                var fraction = (gain - lower.Gain) / (upper.Gain - lower.Gain);
                return lower.ReadNoise + fraction * (upper.ReadNoise - lower.ReadNoise);
            }
        }
        return ReadNoisePoints[^1].ReadNoise;
    }
}

/// <summary>Published ASI178MC gain and read-noise response in native 14-bit sample codes.</summary>
public static class Asi178McSensorModel
{
    private static readonly (double Gain, double ReadNoise)[] ReadNoisePoints =
        [(0, 2.25), (50, 1.92), (100, 1.72), (150, 1.57), (200, 1.44), (270, 1.37), (300, 1.37), (400, 1.35)];

    public const string Version = "zwo-asi178mc-14bit-raw16-v2";

    public static MonoSensorResponse Resolve(double gainControl, double blackLevelContainerAdu = 64)
    {
        if (!double.IsFinite(gainControl) || gainControl is < 0 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(gainControl));
        }

        var nativeElectronsPerAdu = 0.916 * Math.Pow(10, -gainControl / 200);
        return new MonoSensorResponse
        {
            AdcBitDepth = 14,
            FullWellElectrons = Math.Min(15_000, 16_383 * nativeElectronsPerAdu),
            ElectronsPerAdu = nativeElectronsPerAdu,
            ReadNoiseElectrons = InterpolateReadNoise(gainControl),
            BlackLevelAdu = Math.Round(
                blackLevelContainerAdu * 16_383 / ushort.MaxValue,
                MidpointRounding.AwayFromZero)
        };
    }

    private static double InterpolateReadNoise(double gain)
    {
        for (var index = 1; index < ReadNoisePoints.Length; index++)
        {
            var upper = ReadNoisePoints[index];
            if (gain <= upper.Gain)
            {
                var lower = ReadNoisePoints[index - 1];
                var fraction = (gain - lower.Gain) / (upper.Gain - lower.Gain);
                return lower.ReadNoise + fraction * (upper.ReadNoise - lower.ReadNoise);
            }
        }
        return ReadNoisePoints[^1].ReadNoise;
    }
}

/// <summary>Provisional ASI676 response derived from published full-well, noise, gain, and HCG values.</summary>
public static class Asi676SensorModel
{
    private const double GainExample = 82;
    private const double HcgGain = 180;
    private const double MaximumModeledGain = HcgGain;
    private const double PublishedFullWellElectrons = 10_550;
    private const double PublishedMaximumReadNoiseElectrons = 2.9;
    private const double PublishedExampleReadNoiseElectrons = 1.8;
    private const double ProvisionalHcgReadNoiseElectrons = 0.65;
    private const int NativeMaximumAdu = 4095;

    public const string Version = "zwo-asi676-12bit-published-envelope-v1";

    /// <summary>
    /// Resolves the published response envelope through HCG activation. Values above gain 180 require
    /// hardware characterization and are intentionally rejected.
    /// </summary>
    public static MonoSensorResponse Resolve(double gainControl, double blackLevelAdu = 64)
    {
        if (!double.IsFinite(gainControl) || gainControl is < 0 or > MaximumModeledGain)
        {
            throw new ArgumentOutOfRangeException(nameof(gainControl));
        }
        if (!double.IsFinite(blackLevelAdu) || blackLevelAdu is < 0 or >= NativeMaximumAdu)
        {
            throw new ArgumentOutOfRangeException(nameof(blackLevelAdu));
        }

        var zeroGainElectronsPerAdu = PublishedFullWellElectrons / NativeMaximumAdu;
        var electronsPerAdu = zeroGainElectronsPerAdu / Math.Pow(10, gainControl / 200);
        var usableSignalCodes = NativeMaximumAdu - blackLevelAdu;
        var fullWellElectrons = Math.Min(PublishedFullWellElectrons, usableSignalCodes * electronsPerAdu);
        var readNoiseElectrons = gainControl <= GainExample
            ? Interpolate(
                gainControl / GainExample,
                PublishedMaximumReadNoiseElectrons,
                PublishedExampleReadNoiseElectrons)
            : gainControl < HcgGain
                ? PublishedExampleReadNoiseElectrons
                : ProvisionalHcgReadNoiseElectrons;

        return new MonoSensorResponse
        {
            AdcBitDepth = 12,
            FullWellElectrons = fullWellElectrons,
            ElectronsPerAdu = electronsPerAdu,
            ReadNoiseElectrons = readNoiseElectrons,
            BlackLevelAdu = blackLevelAdu,
            CompatibilityLabel = "ASI676 native 12-bit ADU in Mono16 container"
        };
    }

    private static double Interpolate(double fraction, double lower, double upper)
        => lower + fraction * (upper - lower);
}

/// <summary>Linear RGB channel response and white-balance multipliers.</summary>
public readonly record struct RgbChannelSettings(double Red = 1, double Green = 1, double Blue = 1)
{
    internal void Validate()
    {
        if (!double.IsFinite(Red) || Red < 0 || !double.IsFinite(Green) || Green < 0 || !double.IsFinite(Blue) || Blue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RgbChannelSettings));
        }
    }
}

/// <summary>Compatibility RGB24 settings; output is packed R,G,B and is not a Bayer sensor simulation.</summary>
public sealed record Rgb24CompatibilityRenderOptions : LinearSceneRenderOptions
{
    public double FallbackColorIndex { get; init; } = 0.65;
    public RgbChannelSettings ChannelResponse { get; init; } = new(1, 1, 1);
    public RgbChannelSettings WhiteBalance { get; init; } = new(1, 1, 1);

    /// <inheritdoc />
    public override void Validate()
    {
        base.Validate();
        if (!double.IsFinite(FallbackColorIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(FallbackColorIndex));
        }

        ChannelResponse.Validate();
        WhiteBalance.Validate();
    }
}

/// <summary>Linear RGGB photosite settings for little-endian RAW16 output.</summary>
public sealed record BayerRggb16RenderOptions : LinearSceneRenderOptions
{
    public double FallbackColorIndex { get; init; } = 0.65;
    public RgbChannelSettings ChannelResponse { get; init; } = new(0.94, 1, 0.8);
    public MonoSensorResponse SensorResponse { get; init; } = Asi178McSensorModel.Resolve(0);
    public FrameStoredCodeTransform StoredCodeTransform { get; init; } = FrameStoredCodeTransform.RightAlignedV1;
    public int ContainerDepthBits { get; init; } = 16;

    public override void Validate()
    {
        base.Validate();
        if (!double.IsFinite(FallbackColorIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(FallbackColorIndex));
        }
        ChannelResponse.Validate();
        ArgumentNullException.ThrowIfNull(SensorResponse);
        SensorResponse.Validate();
        if (!Enum.IsDefined(StoredCodeTransform) || ContainerDepthBits is < 1 or > 16 ||
            StoredCodeTransform == FrameStoredCodeTransform.IdentityV1 && SensorResponse.AdcBitDepth != ContainerDepthBits ||
            StoredCodeTransform == FrameStoredCodeTransform.RightAlignedV1 && SensorResponse.AdcBitDepth > ContainerDepthBits ||
            StoredCodeTransform is FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1 &&
            SensorResponse.AdcBitDepth >= ContainerDepthBits)
        {
            throw new ArgumentOutOfRangeException(nameof(StoredCodeTransform));
        }
    }
}

/// <summary>Expected object footprint before background, defects, noise, and quantization.</summary>
public sealed record RenderedObjectGeometry(
    string ObjectId,
    PixelPoint SourcePixel,
    PixelPoint DepositedCentroid,
    double ExpectedSignal,
    double DepositedSignal,
    double RetainedEnergyFraction,
    int MinimumX,
    int MinimumY,
    int MaximumX,
    int MaximumY);

/// <summary>Summary statistics over active image-circle samples.</summary>
public sealed record RenderStatistics(long ActivePixelCount, double Minimum, double Maximum, double Mean, long ClippedLow, long ClippedHigh);

/// <summary>A rendered buffer with stable provenance and centroid-supporting object geometry.</summary>
public sealed record SceneRenderResult(
    ReadOnlyMemory<byte> Pixels,
    string AlgorithmVersion,
    string CompatibilityLabel,
    RenderStatistics Statistics,
    IReadOnlyList<RenderedObjectGeometry> Objects);

/// <summary>Deterministic linear Mono16 rendering from frozen visible-scene geometry.</summary>
public static class Mono16SceneRenderer
{
    public const string AlgorithmVersion = "linear-visible-scene-v1";
    public const string ElectronDomainAlgorithmVersion = "electron-domain-visible-scene-v2";
    public const string CloudAlgorithmSuffix = "+virtual-cloud-value-field-v1";
    public const string TransientAlgorithmSuffix = "+virtual-transient-raster-v1";

    /// <summary>Returns flux relative to a magnitude-zero source: 10^(-0.4 * magnitude).</summary>
    public static double RelativeFlux(double magnitude)
    {
        if (!double.IsFinite(magnitude))
        {
            throw new ArgumentOutOfRangeException(nameof(magnitude));
        }

        return Math.Pow(10, -0.4 * magnitude);
    }

    /// <summary>Renders background, PSFs, vignetting, exposure/gain, noise/defects, then clamps and quantizes.</summary>
    public static SceneRenderResult Render(
        VisibleScene scene,
        ImageLayout layout,
        Mono16SceneRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(scene, layout, CameraPixelFormat.Mono16, options ??= new());
        var transient = CreateTransientSignal(scene, layout, options, cancellationToken);
        var plane = RenderCore(
            scene, layout, options, static _ => 1d, out var geometry,
            transient: transient,
            transientChannel: -1,
            cancellationToken: cancellationToken);
        var pixels = new byte[layout.RequiredByteLength];
        var maximumAdu = options.SensorResponse?.MaximumAdu ?? ushort.MaxValue;
        var statistics = QuantizeMono16(scene, layout, plane, pixels, maximumAdu, transient, cancellationToken);
        var algorithmVersion = options.SensorResponse is null ? AlgorithmVersion : ElectronDomainAlgorithmVersion;
        return new SceneRenderResult(
            pixels,
            AppendScenarioVersions(algorithmVersion, options),
            options.SensorResponse is null ? "Mono16 linear sensor" : options.SensorResponse.CompatibilityLabel,
            statistics,
            geometry);
    }

    internal static double[] RenderCore(
        VisibleScene scene,
        ImageLayout layout,
        LinearSceneRenderOptions options,
        Func<ProjectedCelestialObject, double> objectScale,
        out IReadOnlyList<RenderedObjectGeometry> geometry,
        CloudPixelEffect[]? cloudEffects = null,
        VirtualTransientFrameSignal? transient = null,
        int transientChannel = 1,
        double transientSkyScale = 1,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = checked(layout.Width * layout.Height);
        var rates = new double[length];
        var projection = scene.Request.Projection;
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                if (InsideAperture(x, y, projection))
                {
                    rates[y * layout.Width + x] = options.BackgroundElectronsPerSecond;
                }
            }
        }

        var footprints = new List<RenderedObjectGeometry>(scene.Objects.Count);
        foreach (var item in scene.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var flux = RelativeFlux(item.Magnitude) * options.MagnitudeZeroElectronsPerSecond * objectScale(item);
            if (!double.IsFinite(item.Pixel.X) || !double.IsFinite(item.Pixel.Y) || !double.IsFinite(flux) || flux < 0)
            {
                throw new ArgumentException("Scene objects must contain finite projected pixels and renderable flux values.", nameof(scene));
            }

            footprints.Add(AddPsf(rates, layout.Width, layout.Height, projection, item, flux, options));
        }

        if (transient is null)
        {
            RenderSensorPlane(scene, layout, options, rates, cloudEffects, cancellationToken);
        }
        else
        {
            RenderSensorPlaneWithTransient(
                scene, layout, options, rates, cloudEffects, transient, transientChannel, transientSkyScale,
                cancellationToken);
        }

        ApplyDefects(rates, layout.Width, layout.Height, projection, options.Defects);
        geometry = footprints;
        return rates;
    }

    private static void RenderSensorPlane(
        VisibleScene scene,
        ImageLayout layout,
        LinearSceneRenderOptions options,
        double[] rates,
        CloudPixelEffect[]? cloudEffects,
        CancellationToken cancellationToken)
    {
        var projection = scene.Request.Projection;
        var random = new StableRandom(options.Seed);
        var darkExpected = options.DarkCurrentElectronsPerSecond * options.ExposureSeconds;
        var cloud = options.Cloud;
        var requiresCloudEvaluation = cloud?.RequiresEvaluation == true;
        var cloudProjector = !requiresCloudEvaluation || cloudEffects is not null
            ? null
            : ProjectorFactory.Create(projection);
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var index = y * layout.Width + x;
                if (!InsideAperture(x, y, projection))
                {
                    rates[index] = 0;
                    continue;
                }

                if (requiresCloudEvaluation)
                {
                    var effect = cloudEffects is null
                        ? EvaluateCloud(cloudProjector!, cloud!, x, y)
                        : cloudEffects[index];
                    rates[index] = rates[index] * effect.Transmission + options.BackgroundElectronsPerSecond * effect.Scatter;
                }

                var vignetting = 1 - options.VignettingStrength * projection.NormalizedRadiusSquared(x + 0.5, y + 0.5);
                var electrons = rates[index] * vignetting * options.ExposureSeconds;
                if (options.ShotNoiseEnabled)
                {
                    electrons = random.Poisson(electrons);
                }
                electrons += options.DarkNoiseEnabled ? random.Poisson(darkExpected) : darkExpected;
                var response = options switch
                {
                    Mono16SceneRenderOptions { SensorResponse: { } monoResponse } => monoResponse,
                    BayerRggb16RenderOptions bayerOptions => bayerOptions.SensorResponse,
                    _ => null
                };
                if (response is not null)
                {
                    var collectedCharge = Math.Min(electrons, response.FullWellElectrons);
                    var measuredCharge = collectedCharge + random.Gaussian() * response.ReadNoiseElectrons;
                    rates[index] = measuredCharge / response.ElectronsPerAdu + response.BlackLevelAdu;
                }
                else
                {
                    rates[index] = electrons * options.Gain + options.Bias + random.Gaussian() * options.ReadNoiseStandardDeviation;
                }
            }
        }
    }

    private static void RenderSensorPlaneWithTransient(
        VisibleScene scene,
        ImageLayout layout,
        LinearSceneRenderOptions options,
        double[] rates,
        CloudPixelEffect[]? cloudEffects,
        VirtualTransientFrameSignal transient,
        int transientChannel,
        double transientSkyScale,
        CancellationToken cancellationToken)
    {
        var projection = scene.Request.Projection;
        var random = new StableRandom(options.Seed);
        var darkExpected = options.DarkCurrentElectronsPerSecond * options.ExposureSeconds;
        var cloud = options.Cloud;
        var requiresCloudEvaluation = cloud?.RequiresEvaluation == true;
        var cloudProjector = !requiresCloudEvaluation || cloudEffects is not null
            ? null
            : ProjectorFactory.Create(projection);
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var index = y * layout.Width + x;
                var insideAperture = InsideAperture(x, y, projection);
                var hasTransient = transient.TryGet(index, out var transientSignal);
                if (!insideAperture && (!hasTransient || transientSignal.SensorElectrons <= 0))
                {
                    rates[index] = 0;
                    continue;
                }

                if (insideAperture && requiresCloudEvaluation)
                {
                    var effect = cloudEffects is null
                        ? EvaluateCloud(cloudProjector!, cloud!, x, y)
                        : cloudEffects[index];
                    rates[index] = rates[index] * effect.Transmission + options.BackgroundElectronsPerSecond * effect.Scatter;
                }

                var vignetting = insideAperture
                    ? 1 - options.VignettingStrength * projection.NormalizedRadiusSquared(x + 0.5, y + 0.5)
                    : 0;
                var skyElectrons = hasTransient
                    ? (transientChannel < 0
                        ? transientSignal.MonochromeSkyElectrons
                        : transientSignal.SkyElectrons(transientChannel)) * transientSkyScale
                    : 0;
                var electrons = rates[index] * vignetting * options.ExposureSeconds + skyElectrons * vignetting;
                if (options.ShotNoiseEnabled)
                {
                    electrons = random.Poisson(electrons);
                }
                electrons += options.DarkNoiseEnabled ? random.Poisson(darkExpected) : darkExpected;
                if (hasTransient)
                {
                    electrons += transientSignal.SensorElectrons;
                }
                var response = options switch
                {
                    Mono16SceneRenderOptions { SensorResponse: { } monoResponse } => monoResponse,
                    BayerRggb16RenderOptions bayerOptions => bayerOptions.SensorResponse,
                    _ => null
                };
                if (response is not null)
                {
                    var collectedCharge = Math.Min(electrons, response.FullWellElectrons);
                    var measuredCharge = collectedCharge + random.Gaussian() * response.ReadNoiseElectrons;
                    rates[index] = measuredCharge / response.ElectronsPerAdu + response.BlackLevelAdu;
                }
                else
                {
                    rates[index] = electrons * options.Gain + options.Bias + random.Gaussian() * options.ReadNoiseStandardDeviation;
                }
            }
        }
    }

    internal static CloudPixelEffect[] CreateCloudEffects(
        VisibleScene scene,
        ImageLayout layout,
        VirtualCloudRenderContext cloud,
        CancellationToken cancellationToken = default)
    {
        var projection = scene.Request.Projection;
        var projector = ProjectorFactory.Create(projection);
        var effects = new CloudPixelEffect[checked(layout.Width * layout.Height)];
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                if (InsideAperture(x, y, projection))
                {
                    effects[y * layout.Width + x] = EvaluateCloud(projector, cloud, x, y);
                }
            }
        }
        return effects;
    }

    internal static VirtualTransientFrameSignal? CreateTransientSignal(
        VisibleScene scene,
        ImageLayout layout,
        LinearSceneRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.Transient is null)
        {
            return null;
        }
        var signal = VirtualTransientSignalRenderer.Render(
                scene,
                layout,
                options.Transient,
                 options.MagnitudeZeroElectronsPerSecond,
                options.PsfSigmaPixels,
                options.PsfRadiusPixels,
                options.Cloud,
                cancellationToken);
        return signal.ActivePixelCount == 0 ? null : signal;
    }

    private static CloudPixelEffect EvaluateCloud(
        IImageProjector projector,
        VirtualCloudRenderContext cloud,
        int x,
        int y)
    {
        var direction = projector.Unproject(new PixelPoint(x + 0.5, y + 0.5))
            ?? throw new InvalidOperationException("An active cloud sample could not be unprojected.");
        var effect = cloud.Field.Integrate(direction, cloud.IntegrationStartUtc, cloud.IntegrationDuration);
        return new CloudPixelEffect((float)effect.Transmission, (float)effect.Scatter);
    }

    private static RenderedObjectGeometry AddPsf(
        double[] rates, int width, int height, ProjectionContext projection,
        ProjectedCelestialObject item, double flux, LinearSceneRenderOptions options)
    {
        var radius = options.PsfRadiusPixels;
        var minKernelX = (int)Math.Ceiling(item.Pixel.X - radius - 0.5);
        var maxKernelX = (int)Math.Floor(item.Pixel.X + radius - 0.5);
        var minKernelY = (int)Math.Ceiling(item.Pixel.Y - radius - 0.5);
        var maxKernelY = (int)Math.Floor(item.Pixel.Y + radius - 0.5);
        var radiusSquared = radius * radius;
        var denominator = 0d;
        for (var y = minKernelY; y <= maxKernelY; y++)
        {
            for (var x = minKernelX; x <= maxKernelX; x++)
            {
                var distanceSquared = Square(x + 0.5 - item.Pixel.X) + Square(y + 0.5 - item.Pixel.Y);
                if (distanceSquared <= radiusSquared)
                {
                    denominator += Math.Exp(-distanceSquared / (2 * options.PsfSigmaPixels * options.PsfSigmaPixels));
                }
            }
        }

        var deposited = 0d;
        var weightedX = 0d;
        var weightedY = 0d;
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = Math.Max(0, minKernelY); y <= Math.Min(height - 1, maxKernelY); y++)
        {
            for (var x = Math.Max(0, minKernelX); x <= Math.Min(width - 1, maxKernelX); x++)
            {
                var distanceSquared = Square(x + 0.5 - item.Pixel.X) + Square(y + 0.5 - item.Pixel.Y);
                if (distanceSquared > radiusSquared || !InsideAperture(x, y, projection))
                {
                    continue;
                }

                var value = flux * Math.Exp(-distanceSquared / (2 * options.PsfSigmaPixels * options.PsfSigmaPixels)) / denominator;
                rates[y * width + x] += value;
                deposited += value;
                weightedX += value * (x + 0.5);
                weightedY += value * (y + 0.5);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        var centroid = deposited > 0 ? new PixelPoint(weightedX / deposited, weightedY / deposited) : item.Pixel;
        return new RenderedObjectGeometry(item.Id, item.Pixel, centroid, flux, deposited,
            flux > 0 ? deposited / flux : 0, minX, minY, maxX, maxY);
    }

    private static RenderStatistics QuantizeMono16(
        VisibleScene scene,
        ImageLayout layout,
        double[] values,
        byte[] pixels,
        int maximumAdu,
        VirtualTransientFrameSignal? transient,
        CancellationToken cancellationToken)
    {
        var accumulator = new StatisticsAccumulator();
        var projection = scene.Request.Projection;
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var index = y * layout.Width + x;
                if (!InsideAperture(x, y, projection) &&
                    (transient?.TryGet(index, out var signal) != true || signal.SensorElectrons <= 0))
                {
                    continue;
                }

                var value = values[index];
                var sample = accumulator.AddAndQuantize(value, checked((ushort)maximumAdu));
                var offset = y * layout.StrideBytes + x * 2;
                pixels[offset] = (byte)sample;
                pixels[offset + 1] = (byte)(sample >> 8);
            }
        }

        return accumulator.Create();
    }

    private static void ApplyDefects(double[] values, int width, int height, ProjectionContext projection,
        IReadOnlyList<SensorDefect> defects)
    {
        foreach (var defect in defects)
        {
            if ((uint)defect.X >= (uint)width || (uint)defect.Y >= (uint)height)
            {
                throw new ArgumentOutOfRangeException(nameof(defects), "A defect lies outside the image.");
            }

            if (InsideAperture(defect.X, defect.Y, projection))
            {
                var index = defect.Y * width + defect.X;
                values[index] = defect.FixedValue ?? values[index] + defect.Offset;
            }
        }
    }

    internal static void Validate(VisibleScene scene, ImageLayout layout, CameraPixelFormat format, LinearSceneRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        if (layout.PixelFormat != format)
        {
            throw new ArgumentException($"{format} layout is required.", nameof(layout));
        }

        layout.Validate();
        options.Validate();
        var projection = scene.Request.Projection;
        if (projection.WidthPixels > 0 && (projection.WidthPixels != layout.Width || projection.HeightPixels != layout.Height))
        {
            throw new ArgumentException("The image layout must match the frozen scene projection dimensions.", nameof(layout));
        }
    }

    internal static bool InsideAperture(int x, int y, ProjectionContext projection)
        => projection.ContainsSample(x + 0.5, y + 0.5);

    internal static string AppendScenarioVersions(string algorithmVersion, LinearSceneRenderOptions options)
    {
        if (options.Cloud is not null)
        {
            algorithmVersion += CloudAlgorithmSuffix;
        }
        if (options.Transient is not null)
        {
            algorithmVersion += TransientAlgorithmSuffix;
        }
        return algorithmVersion;
    }

    private static double Square(double value) => value * value;
}

/// <summary>RGB24 compatibility rendering from the same scene geometry; this is explicitly not Bayer data.</summary>
public static class Rgb24CompatibilityRenderer
{
    public const string CompatibilityLabel = "RGB24 compatibility (non-Bayer), packed R,G,B";

    /// <summary>Renders packed red, green, blue bytes using B-V color, response, and white-balance factors.</summary>
    public static SceneRenderResult Render(
        VisibleScene scene,
        ImageLayout layout,
        Rgb24CompatibilityRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mono16SceneRenderer.Validate(scene, layout, CameraPixelFormat.Rgb24, options ??= new());
        var channels = new double[3][];
        var cloudEffects = options.Cloud is null || !options.Cloud.RequiresEvaluation
            ? null
            : Mono16SceneRenderer.CreateCloudEffects(scene, layout, options.Cloud, cancellationToken);
        var transient = Mono16SceneRenderer.CreateTransientSignal(scene, layout, options, cancellationToken);
        IReadOnlyList<RenderedObjectGeometry>? geometry = null;
        for (var channel = 0; channel < channels.Length; channel++)
        {
            var selected = channel;
            channels[channel] = Mono16SceneRenderer.RenderCore(scene, layout, options with { Seed = unchecked(options.Seed + channel * 104729) },
                item => ColorFactors(item.ColorIndex ?? options.FallbackColorIndex)[selected], out var currentGeometry,
                cloudEffects, transient, selected, cancellationToken: cancellationToken);
            geometry ??= currentGeometry;
        }

        var response = new[]
        {
            options.ChannelResponse.Red * options.WhiteBalance.Red,
            options.ChannelResponse.Green * options.WhiteBalance.Green,
            options.ChannelResponse.Blue * options.WhiteBalance.Blue
        };
        var pixels = new byte[layout.RequiredByteLength];
        var statistics = new StatisticsAccumulator();
        var projection = scene.Request.Projection;
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var index = y * layout.Width + x;
                if (!Mono16SceneRenderer.InsideAperture(x, y, projection) &&
                    (transient?.TryGet(index, out var signal) != true || signal.SensorElectrons <= 0))
                {
                    continue;
                }

                var offset = y * layout.StrideBytes + x * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    var sensorCompensation = transient?.TryGet(index, out var transientSignal) == true
                        ? transientSignal.SensorElectrons * options.Gain * ChannelWhiteBalance(options.WhiteBalance, channel) *
                          (1 - ChannelResponse(options.ChannelResponse, channel))
                        : 0;
                    pixels[offset + channel] = (byte)statistics.AddAndQuantize(
                        channels[channel][index] * response[channel] + sensorCompensation, byte.MaxValue);
                }
            }
        }

        var algorithmVersion = Mono16SceneRenderer.AppendScenarioVersions(
            Mono16SceneRenderer.AlgorithmVersion, options);
        return new SceneRenderResult(pixels, algorithmVersion, CompatibilityLabel, statistics.Create(), geometry!);
    }

    // Smooth bounded approximation suitable for compatibility previews, normalized to green.
    internal static double[] ColorFactors(double bv)
    {
        bv = Math.Clamp(bv, -0.4, 2.0);
        var red = Math.Exp(0.35 * bv);
        var blue = Math.Exp(-0.65 * bv);
        return [red, 1d, blue];
    }

    private static double ChannelResponse(RgbChannelSettings settings, int channel) => channel switch
    {
        0 => settings.Red,
        1 => settings.Green,
        2 => settings.Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private static double ChannelWhiteBalance(RgbChannelSettings settings, int channel) => channel switch
    {
        0 => settings.Red,
        1 => settings.Green,
        2 => settings.Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };
}

/// <summary>Renders one linear little-endian RAW16 sample per RGGB photosite without demosaicing.</summary>
public static class BayerRggb16Renderer
{
    public const string AlgorithmVersion = "electron-domain-rggb-visible-scene-v1";
    public const string CompatibilityLabel = "Virtual RGGB RAW16 sensor (non-demosaiced)";

    public static SceneRenderResult Render(
        VisibleScene scene,
        ImageLayout layout,
        BayerRggb16RenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mono16SceneRenderer.Validate(scene, layout, CameraPixelFormat.BayerRggb16, options ??= new());
        var responses = new[] { options.ChannelResponse.Red, options.ChannelResponse.Green, options.ChannelResponse.Blue };
        var channels = new double[3][];
        var cloudEffects = options.Cloud is null || !options.Cloud.RequiresEvaluation
            ? null
            : Mono16SceneRenderer.CreateCloudEffects(scene, layout, options.Cloud, cancellationToken);
        var transient = Mono16SceneRenderer.CreateTransientSignal(scene, layout, options, cancellationToken);
        IReadOnlyList<RenderedObjectGeometry>? geometry = null;
        for (var channel = 0; channel < channels.Length; channel++)
        {
            var selected = channel;
            channels[channel] = Mono16SceneRenderer.RenderCore(
                scene,
                layout,
                options with { Seed = unchecked(options.Seed + channel * 104729) },
                item => Rgb24CompatibilityRenderer.ColorFactors(item.ColorIndex ?? options.FallbackColorIndex)[selected] *
                    responses[selected],
                out var currentGeometry,
                cloudEffects,
                transient,
                selected,
                responses[selected],
                cancellationToken);
            geometry ??= currentGeometry;
        }

        var pixels = new byte[layout.RequiredByteLength];
        var statistics = new StatisticsAccumulator();
        var projection = scene.Request.Projection;
        var maximum = checked((ushort)options.SensorResponse.MaximumAdu);
        var codeMapper = new StoredCodeMapper(
            options.StoredCodeTransform, options.SensorResponse.AdcBitDepth, options.ContainerDepthBits);
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var index = y * layout.Width + x;
                if (!Mono16SceneRenderer.InsideAperture(x, y, projection) &&
                    (transient?.TryGet(index, out var signal) != true || signal.SensorElectrons <= 0))
                {
                    continue;
                }

                var channel = (y & 1, x & 1) switch
                {
                    (0, 0) => 0,
                    (1, 1) => 2,
                    _ => 1
                };
                var sample = statistics.AddAndQuantize(channels[channel][index], maximum, codeMapper);
                var offset = y * layout.StrideBytes + x * 2;
                pixels[offset] = (byte)sample;
                pixels[offset + 1] = (byte)(sample >> 8);
            }
        }

        var algorithmVersion = Mono16SceneRenderer.AppendScenarioVersions(AlgorithmVersion, options);
        if (options.StoredCodeTransform == FrameStoredCodeTransform.FullRangeScaledV1)
        {
            algorithmVersion += "+full-range-scaled-v1";
        }
        else if (options.StoredCodeTransform == FrameStoredCodeTransform.LeftShiftedV1)
        {
            algorithmVersion += "+left-shifted-v1";
        }
        else if (options.StoredCodeTransform == FrameStoredCodeTransform.RightAlignedV1 &&
            options.SensorResponse.AdcBitDepth < options.ContainerDepthBits)
        {
            algorithmVersion += "+right-aligned-v1";
        }
        return new SceneRenderResult(pixels, algorithmVersion, CompatibilityLabel, statistics.Create(), geometry!);
    }
}

internal readonly record struct CloudPixelEffect(float Transmission, float Scatter);

internal sealed class StatisticsAccumulator
{
    private long _count;
    private double _minimum = double.PositiveInfinity;
    private double _maximum = double.NegativeInfinity;
    private double _sum;
    private long _clippedLow;
    private long _clippedHigh;

    public ushort AddAndQuantize(double value, ushort maximum)
    {
        _count++;
        _minimum = Math.Min(_minimum, value);
        _maximum = Math.Max(_maximum, value);
        _sum += value;
        if (value < 0)
        {
            _clippedLow++;
        }
        else if (value > maximum)
        {
            _clippedHigh++;
        }

        return (ushort)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, maximum);
    }

    public ushort AddAndQuantize(double value, ushort maximum, StoredCodeMapper codeMapper)
    {
        var native = (ushort)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, maximum);
        var stored = codeMapper.Map(native);
        _count++;
        _minimum = Math.Min(_minimum, stored);
        _maximum = Math.Max(_maximum, stored);
        _sum += stored;
        if (value < 0)
        {
            _clippedLow++;
        }
        else if (value > maximum)
        {
            _clippedHigh++;
        }

        return stored;
    }

    public RenderStatistics Create()
        => new(_count, _count == 0 ? 0 : _minimum, _count == 0 ? 0 : _maximum, _count == 0 ? 0 : _sum / _count,
            _clippedLow, _clippedHigh);
}

internal readonly record struct StoredCodeMapper(
    FrameStoredCodeTransform Transform,
    int SampleDepthBits,
    int ContainerDepthBits)
{
    internal ushort Map(ushort value)
    {
        var mapped = Transform switch
        {
            FrameStoredCodeTransform.IdentityV1 or FrameStoredCodeTransform.RightAlignedV1 => value,
            FrameStoredCodeTransform.LeftShiftedV1 => value << (ContainerDepthBits - SampleDepthBits),
            FrameStoredCodeTransform.FullRangeScaledV1 => checked((int)(
                ((long)value * ((1 << ContainerDepthBits) - 1) + ((1 << SampleDepthBits) - 1) / 2) /
                ((1 << SampleDepthBits) - 1))),
            _ => throw new InvalidOperationException("The stored-code transform is invalid.")
        };
        return checked((ushort)mapped);
    }
}

internal sealed class StableRandom
{
    private ulong _state;
    private bool _hasGaussian;
    private double _gaussian;

    public StableRandom(int seed) => _state = unchecked((uint)seed) + 0x9e3779b97f4a7c15UL;

    public double Gaussian()
    {
        if (_hasGaussian)
        {
            _hasGaussian = false;
            return _gaussian;
        }

        var radius = Math.Sqrt(-2 * Math.Log(Math.Max(double.Epsilon, Uniform())));
        var angle = 2 * Math.PI * Uniform();
        _gaussian = radius * Math.Sin(angle);
        _hasGaussian = true;
        return radius * Math.Cos(angle);
    }

    public double Poisson(double mean)
    {
        if (mean <= 0)
        {
            return 0;
        }

        if (mean >= 30)
        {
            return Math.Max(0, Math.Round(mean + Math.Sqrt(mean) * Gaussian()));
        }

        var limit = Math.Exp(-mean);
        var product = 1d;
        var count = 0;
        do
        {
            count++;
            product *= Uniform();
        }
        while (product > limit);
        return count - 1;
    }

    private double Uniform()
    {
        var value = NextUInt64() >> 11;
        return value * (1d / (1UL << 53));
    }

    private ulong NextUInt64()
    {
        var value = _state;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        _state = value;
        return value * 2685821657736338717UL;
    }
}
