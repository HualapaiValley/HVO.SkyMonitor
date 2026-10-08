using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Explicit native aperture and work limits for conditional simulated source admission.</summary>
public sealed record StellarExposureRenderSettings(
    double MinimumSignalToNoise = 5,
    long MaximumKernelCellVisits = 100000000,
    int MaximumSparsePixels = 65536,
    int DigitalBinX = 1,
    int DigitalBinY = 1,
    bool DigitalAverage = true,
    int CfaOriginX = 0,
    int CfaOriginY = 0)
{
    /// <summary>Validates bounded settings; digital bins describe extraction, never charge collection.</summary>
    public void Validate()
    {
        if (!double.IsFinite(MinimumSignalToNoise) || MinimumSignalToNoise is < 1e-6 or > 1e6 ||
            MaximumKernelCellVisits is < 1 or > 1000000000 || MaximumSparsePixels is < 1 or > 65536 ||
            DigitalBinX is < 1 or > 64 || DigitalBinY is < 1 or > 64 ||
            CfaOriginX is < 0 or > 1 || CfaOriginY is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(StellarExposureRenderSettings));
    }
}

/// <summary>Expected aperture evidence before stochastic noise and clipping; this is not a measured detection.</summary>
public sealed record StellarExposurePrediction(
    string ObjectId, bool Admitted, string Reason, StellarSignalPrediction Signal,
    bool ExpectedSaturation, double RetainedOpticalEnergy, RenderedObjectGeometry Footprint);

/// <summary>Actual bounded projection/prediction/deposition work, with native buffer and per-plane limits.</summary>
public sealed record StellarRenderStatistics(
    int CatalogCandidates, int GeometricallySupportedSources, int AdmittedSources,
    long TemporalSamples, long PredictionKernelCellVisits, long RenderKernelCellVisits,
    int RenderPlanes, long NativeBufferPixels, long MaximumKernelCellVisitsPerPlane, int MaximumSparsePixels);

/// <summary>An admitted source with original temporal weights and matched instantaneous cloud transmission.</summary>
public sealed class AdmittedStellarExposure
{
    internal AdmittedStellarExposure(StellarExposureObject source, WeightedPointSpreadSample[] samples,
        StellarExposurePrediction prediction)
    {
        Source = source;
        Samples = Array.AsReadOnly(samples);
        Prediction = prediction;
    }

    /// <summary>Gets catalog and midpoint reference geometry.</summary>
    public StellarExposureObject Source { get; }
    /// <summary>Gets bounded temporal samples; no clipping or missing time is renormalized.</summary>
    public IReadOnlyList<WeightedPointSpreadSample> Samples { get; }
    /// <summary>Gets conditional prediction and swept footprint evidence.</summary>
    public StellarExposurePrediction Prediction { get; }
}

/// <summary>
/// Frozen camera-aware admission shared by all render planes. Sparse kernels are discarded
/// after prediction and regenerated per plane, so memory does not grow by a raster per star.
/// </summary>
public sealed class StellarExposureRenderPlan
{
    private readonly LinearSceneRenderOptions _options;

    private StellarExposureRenderPlan(StellarExposureGeometry geometry, LinearSceneRenderOptions options,
        StellarExposureRenderSettings settings, List<AdmittedStellarExposure> sources,
        List<StellarExposurePrediction> predictions, long visits, long samples)
    {
        Geometry = geometry;
        _options = Normalize(options);
        Settings = settings;
        Sources = new ReadOnlyCollection<AdmittedStellarExposure>(sources.ToArray());
        Predictions = new ReadOnlyCollection<StellarExposurePrediction>(predictions.ToArray());
        PredictionKernelCellVisits = visits;
        TemporalSampleCount = samples;
    }

    /// <summary>Identifies conditional native electron-aperture admission and temporal deposition.</summary>
    public const string AlgorithmVersion = "camera-aware-stellar-exposure-v1";
    /// <summary>Identifies ordered canonical prediction records, delimited by newline bytes.</summary>
    public const string PredictionIdentityVersion = "stellar-expected-predictions-sha256-v1";
    /// <summary>Gets complete bounded catalog and temporal geometry.</summary>
    public StellarExposureGeometry Geometry { get; }
    /// <summary>Gets explicit admission and work limits.</summary>
    public StellarExposureRenderSettings Settings { get; }
    /// <summary>Gets sources selected by expected SNR or saturation.</summary>
    public IReadOnlyList<AdmittedStellarExposure> Sources { get; }
    /// <summary>Gets all geometrically supported predictions, including reason-coded rejection.</summary>
    public IReadOnlyList<StellarExposurePrediction> Predictions { get; }
    /// <summary>Gets actual normalization and deposition work during admission.</summary>
    public long PredictionKernelCellVisits { get; }
    /// <summary>Gets original projected temporal sample count before photometric rejection.</summary>
    public long TemporalSampleCount { get; }

    /// <summary>
    /// Hashes deterministic conditional source evidence without creating a catalog-sized JSON buffer.
    /// The identity proves reproducibility of predictions, never a measured detection.
    /// </summary>
    public string ComputePredictionsSha256(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(PredictionIdentityVersion + "\n"));
        foreach (var prediction in Predictions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(prediction));
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(canonical));
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal StellarRenderStatistics Statistics(int planes, long renderVisits)
        => new(Geometry.CandidateCount, Predictions.Count, Sources.Count, TemporalSampleCount,
            PredictionKernelCellVisits, renderVisits, planes,
            (long)Geometry.Request.Projection.WidthPixels * Geometry.Request.Projection.HeightPixels,
            Settings.MaximumKernelCellVisits, Settings.MaximumSparsePixels);

    /// <summary>
    /// Bounds both admission conditions before querying. A photosite cannot receive more than
    /// the complete source charge. If background can approach clipping, no finite positive
    /// source threshold is conservative: use the caller's explicit catalog fidelity ceiling.
    /// </summary>
    public static double? BestCaseMagnitudeCeiling(LinearSceneRenderOptions options, double minimumSignalToNoise)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var maximumResponse = options switch
        {
            BayerRggb16RenderOptions bayer => Math.Exp(.7) * Math.Max(bayer.ChannelResponse.Red,
                Math.Max(bayer.ChannelResponse.Green, bayer.ChannelResponse.Blue)),
            Rgb24CompatibilityRenderOptions => 3 * Math.Exp(.7),
            _ => 1
        };
        var snrCeiling = StellarVisibilityModel.BestCaseMagnitudeCeiling(options.MagnitudeZeroElectronsPerSecond,
            options.ExposureSeconds, maximumResponse, minimumSignalToNoise);
        if (snrCeiling is null) return null;
        var response = SensorResponse(options);
        var dark = options.DarkCurrentElectronsPerSecond * options.ExposureSeconds;
        // Cloud transmission + scatter is at most one. Include outward float-rounding
        // allowance because the native background path adds two float conversions.
        var background = options.MaximumBackgroundRate * options.ExposureSeconds *
            (options.Cloud?.RequiresEvaluation == true ? 1 + Math.ScaleB(1d, -23) : 1);
        var requiredCharge = double.MaxValue;
        foreach (var weight in DisplayWeights(options))
        {
            var cap = MaximumCharge(options, response, weight);
            if (cap == double.MaxValue) continue;
            if (cap == 0 || dark < cap && background >= cap - dark)
                return double.PositiveInfinity;
            requiredCharge = Math.Min(requiredCharge, dark < cap ? cap - dark - background : cap);
        }
        if (requiredCharge == double.MaxValue) return snrCeiling;
        var saturationCeiling = 2.5 * Math.Log10(options.MagnitudeZeroElectronsPerSecond *
            options.ExposureSeconds * maximumResponse / requiredCharge) + .5;
        return Math.Max(snrCeiling.Value, saturationCeiling);
    }

    /// <summary>Prepares conditional aperture photometry using native channels, clipping and digital-bin covariance.</summary>
    public static StellarExposureRenderPlan Prepare(StellarExposureGeometry geometry,
        LinearSceneRenderOptions options, StellarExposureRenderSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        settings ??= new();
        settings.Validate();
        if (options.StellarExposure is not null ||
            options.ExposureSeconds != (geometry.EndUtc - geometry.StartUtc).TotalSeconds)
            throw new ArgumentException("Stellar geometry must bind the exact rendered exposure.", nameof(options));
        var temporalStep = TemporalPointSpreadRaster.MaximumTemporalStepPixels(options.PsfSigmaPixels, options.PsfRadiusPixels);
        if (geometry.MaximumSampleMotionPixels > temporalStep * (1 + 1e-12) ||
            geometry.PsfSupportRadiusPixels < options.PsfRadiusPixels)
            throw new InvalidOperationException("stellar-exposure-psf-temporal-resolution-insufficient");
        var projection = geometry.Request.Projection;
        if ((long)projection.WidthPixels * projection.HeightPixels > 16777216)
            throw new InvalidOperationException("stellar-exposure-active-pixel-budget-exceeded");
        var sources = new List<AdmittedStellarExposure>();
        var predictions = new List<StellarExposurePrediction>();
        var visits = 0L;
        var sampleCount = 0L;
        var projector = options.Cloud?.RequiresEvaluation == true ? ProjectorFactory.Create(projection) : null;
        var displayWeights = DisplayWeights(options);
        var maximumDisplayWeight = displayWeights.Max();
        foreach (var source in geometry.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = source.Samples.Select(sample => new WeightedPointSpreadSample(sample.Pixel,
                sample.ExposureFraction, options.Cloud?.RequiresEvaluation == true
                    ? options.Cloud.Field.Evaluate(sample.Horizontal,
                        options.Cloud.IntegrationStartUtc + (sample.Utc - geometry.StartUtc)).Transmission : 1)).ToArray();
            sampleCount += samples.Length;
            var raster = TemporalPointSpreadRaster.Rasterize(samples, projection, options.PsfSigmaPixels,
                options.PsfRadiusPixels, Remaining(settings, visits), settings.MaximumSparsePixels, cancellationToken);
            visits += raster.KernelCellVisits;
            var sourceRate = Mono16SceneRenderer.RelativeFlux(source.Source.Magnitude) * options.MagnitudeZeroElectronsPerSecond;
            var color = Rgb24CompatibilityRenderer.ColorFactors(source.Source.ColorIndex ?? FallbackColor(options));
            var response = SensorResponse(options);
            var readVariance = response is null
                ? Math.Pow(options.Gain > 0 ? options.ReadNoiseStandardDeviation / options.Gain : 0, 2)
                : response.ReadNoiseElectrons * response.ReadNoiseElectrons;
            var nativePixels = CompleteBinSupport(raster.Pixels, projection, settings);
            if (nativePixels.Count > settings.MaximumSparsePixels)
                throw new InvalidOperationException("stellar-exposure-aperture-budget-exceeded");
            var fractions = raster.Pixels.ToDictionary(static pixel => pixel.Index, static pixel => pixel.AfterTransmission);
            var aperture = new List<StellarApertureSample>();
            var saturated = false;
            var sourceCausesSaturation = false;
            var signalSum = 0d;
            var weightedX = 0d;
            var weightedY = 0d;
            var extractionWeight = settings.DigitalAverage ? 1d / (settings.DigitalBinX * settings.DigitalBinY) : 1;
            var maximumX = -1; var maximumY = -1;
            var minimumX = projection.WidthPixels; var minimumY = projection.HeightPixels;
            foreach (var index in nativePixels.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x = index % projection.WidthPixels; var y = index / projection.WidthPixels;
                var vignette = 1 - options.VignettingStrength * projection.NormalizedRadiusSquared(x + .5, y + .5);
                var backgroundTransmission = 1d;
                var diskTransmission = 1d;
                if (projector is not null)
                {
                    var direction = projector.Unproject(new(x + .5, y + .5))
                        ?? throw new InvalidOperationException("An active stellar aperture could not be unprojected.");
                    var cloud = options.Cloud!;
                    var effect = cloud.Field.Integrate(direction, cloud.IntegrationStartUtc, cloud.IntegrationDuration);
                    backgroundTransmission = (float)effect.Transmission + (float)effect.Scatter;
                    diskTransmission = (float)effect.Transmission;
                }
                var channelCount = options is Rgb24CompatibilityRenderOptions ? 3 : 1;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var multiplier = options switch
                    {
                        BayerRggb16RenderOptions bayer => color[CfaChannel(x, y, settings)] *
                            Channel(bayer.ChannelResponse, CfaChannel(x, y, settings)),
                        Rgb24CompatibilityRenderOptions => color[channel],
                        _ => 1
                    };
                    var sourceElectrons = sourceRate * options.ExposureSeconds * fractions.GetValueOrDefault(index) * vignette * multiplier;
                    var skyChannel = options switch
                    {
                        BayerRggb16RenderOptions => CfaChannel(x, y, settings),
                        Rgb24CompatibilityRenderOptions => channel,
                        _ => -1
                    };
                    var backgroundElectrons = (options.BackgroundRate(x, y, skyChannel) * backgroundTransmission +
                        options.DiskRate(x, y, skyChannel) * diskTransmission) * options.ExposureSeconds * vignette;
                    var darkElectrons = options.DarkCurrentElectronsPerSecond * options.ExposureSeconds;
                    var channelWeight = maximumDisplayWeight > 0 ? displayWeights[channel] / maximumDisplayWeight : 0;
                    if (response is null && options.Gain == 0) channelWeight = 0;
                    aperture.Add(new(sourceElectrons, backgroundElectrons, darkElectrons, readVariance, extractionWeight * channelWeight));
                    if (aperture.Count > 65536) throw new InvalidOperationException("stellar-exposure-aperture-budget-exceeded");
                    var totalCharge = sourceElectrons + backgroundElectrons + darkElectrons;
                    var maximumCharge = MaximumCharge(options, response, displayWeights[channel]);
                    saturated |= sourceElectrons > 0 && totalCharge >= maximumCharge;
                    sourceCausesSaturation |= sourceElectrons > 0 && (sourceElectrons >= maximumCharge ||
                        backgroundElectrons + darkElectrons < maximumCharge && totalCharge >= maximumCharge);
                    signalSum += sourceElectrons * channelWeight;
                    weightedX += sourceElectrons * channelWeight * (x + .5); weightedY += sourceElectrons * channelWeight * (y + .5);
                }
                if (fractions.GetValueOrDefault(index) <= 0) continue;
                minimumX = Math.Min(minimumX, x); minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x); maximumY = Math.Max(maximumY, y);
            }
            var prediction = StellarVisibilityModel.Predict(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(aperture));
            var admitted = prediction.SignalToNoise >= settings.MinimumSignalToNoise || sourceCausesSaturation;
            var reference = source.MidpointReference?.Pixel ?? source.Samples[0].Pixel;
            var centroid = signalSum > 0 ? new PixelPoint(weightedX / signalSum, weightedY / signalSum) : reference;
            var footprint = new RenderedObjectGeometry(source.Source.Id, reference, centroid,
                sourceRate * options.ExposureSeconds, signalSum, raster.RetainedAfterTransmission,
                minimumX, minimumY, maximumX, maximumY);
            var diagnostic = new StellarExposurePrediction(source.Source.Id, admitted,
                saturated && admitted ? "expected-saturation" : admitted ? "expected-snr-qualified" :
                    saturated ? "background-saturated-below-expected-snr" : "below-expected-snr",
                prediction, saturated, raster.RetainedAfterTransmission, footprint);
            predictions.Add(diagnostic);
            if (admitted) sources.Add(new(source, samples, diagnostic));
        }
        return new(geometry, options, settings, sources, predictions, visits, sampleCount);
    }

    internal void ValidateFor(VisibleScene scene, ImageLayout layout, LinearSceneRenderOptions options)
    {
        if (options is BayerRggb16RenderOptions && (Settings.CfaOriginX != 0 || Settings.CfaOriginY != 0))
            throw new ArgumentException("The native RGGB renderer cannot publish a shifted CFA phase as RGGB.", nameof(options));
        if (scene.Request.Projection != Geometry.Request.Projection || scene.Request.Utc != Geometry.Request.Utc ||
            scene.Request.CatalogMetadata != Geometry.Request.CatalogMetadata ||
            layout.Width != Geometry.Request.Projection.WidthPixels || layout.Height != Geometry.Request.Projection.HeightPixels ||
            Normalize(options) != _options)
            throw new ArgumentException("Stellar admission does not match the scene and sensor options.", nameof(options));
    }

    internal static long Remaining(StellarExposureRenderSettings settings, long used)
        => used < settings.MaximumKernelCellVisits ? settings.MaximumKernelCellVisits - used
            : throw new InvalidOperationException("stellar-exposure-raster-budget-exceeded");

    private static HashSet<int> CompleteBinSupport(IReadOnlyList<PointSpreadRasterPixel> pixels,
        ProjectionContext projection, StellarExposureRenderSettings settings)
    {
        var result = new HashSet<int>();
        foreach (var pixel in pixels)
        {
            var x0 = pixel.Index % projection.WidthPixels / settings.DigitalBinX * settings.DigitalBinX;
            var y0 = pixel.Index / projection.WidthPixels / settings.DigitalBinY * settings.DigitalBinY;
            for (var y = y0; y < Math.Min(y0 + settings.DigitalBinY, projection.HeightPixels); y++)
                for (var x = x0; x < Math.Min(x0 + settings.DigitalBinX, projection.WidthPixels); x++)
                    if (projection.ContainsSample(x + .5, y + .5))
                    {
                        result.Add(y * projection.WidthPixels + x);
                        if (result.Count > settings.MaximumSparsePixels)
                            throw new InvalidOperationException("stellar-exposure-aperture-budget-exceeded");
                    }
        }
        return result;
    }

    private static LinearSceneRenderOptions Normalize(LinearSceneRenderOptions options)
        => options with { Seed = 0, StellarExposure = null };
    private static double[] DisplayWeights(LinearSceneRenderOptions options)
        => options is Rgb24CompatibilityRenderOptions rgb
            ? [rgb.ChannelResponse.Red * rgb.WhiteBalance.Red, rgb.ChannelResponse.Green * rgb.WhiteBalance.Green,
                rgb.ChannelResponse.Blue * rgb.WhiteBalance.Blue] : [1d];

    private static double MaximumCharge(LinearSceneRenderOptions options, MonoSensorResponse? response, double displayWeight)
        => response is null
            ? options.Gain > 0 && displayWeight > 0
                ? Math.Max(0, (options is Rgb24CompatibilityRenderOptions ? byte.MaxValue / displayWeight : ushort.MaxValue) - options.Bias) / options.Gain
                : double.MaxValue
            : Math.Min(response.FullWellElectrons, Math.Max(0, response.MaximumAdu - response.BlackLevelAdu) * response.ElectronsPerAdu);

    private static MonoSensorResponse? SensorResponse(LinearSceneRenderOptions options) => options switch
    {
        Mono16SceneRenderOptions mono => mono.SensorResponse,
        BayerRggb16RenderOptions bayer => bayer.SensorResponse,
        _ => null
    };
    private static double FallbackColor(LinearSceneRenderOptions options) => options switch
    {
        Rgb24CompatibilityRenderOptions rgb => rgb.FallbackColorIndex,
        BayerRggb16RenderOptions bayer => bayer.FallbackColorIndex,
        _ => 0
    };
    internal static int CfaChannel(int x, int y, StellarExposureRenderSettings settings)
        => ((y + settings.CfaOriginY) & 1, (x + settings.CfaOriginX) & 1) switch { (0, 0) => 0, (1, 1) => 2, _ => 1 };
    internal static double Channel(RgbChannelSettings response, int channel)
        => channel switch { 0 => response.Red, 1 => response.Green, 2 => response.Blue, _ => throw new ArgumentOutOfRangeException(nameof(channel)) };
}
