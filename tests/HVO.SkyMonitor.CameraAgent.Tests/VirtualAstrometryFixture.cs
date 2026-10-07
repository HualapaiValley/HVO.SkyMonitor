using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>Qualification input boundary: no projected scene, source IDs, optical truth or truth mask.</summary>
internal sealed record VirtualAstrometryPixels(ReadOnlyMemory<byte> Payload, FrameLayoutDescriptor Layout,
    DateTimeOffset StartUtc, TimeSpan Exposure, double Gain, string? ParentPayloadSha256 = null)
{
    internal static VirtualAstrometryPixels FromCapture(CameraFrame frame) => new(frame.PixelData.ToArray(),
        frame.Layout ?? throw new InvalidOperationException("An authoritative pixel layout is required."),
        frame.TimestampUtc, frame.Metadata.Exposure, frame.Metadata.Gain);
}

internal sealed record VirtualAstrometryProfile(string Name, CameraModuleConfig Config);

/// <summary>
/// One selected measurer's sources, bound to the frame context that names its algorithm and settings. The v1 shape is the
/// issue #1126 report shape; <see cref="Candidate"/> carries the v2 reason-coded exclusions and trail metadata.
/// </summary>
internal sealed record VirtualAstrometryMeasurement(string Measurer, IReadOnlyList<StellarDetection> Detections,
    int CandidateCount, double? Background, double? NoiseSigma, AstrometricFrameContext Frame, StellarMeasurementResult? Candidate);

/// <summary>
/// One frozen issue #1126 projection family. Every non-equidistant family is a labelled synthetic substitution on the
/// ASI174 mono sensor; <see cref="NominalFocalFactor"/> offsets the declared nominal focal from the truth focal.
/// </summary>
internal sealed record VirtualProjectionFamily(string Name, ProjectionModel Model, double FieldOfViewDegrees,
    double FocalLengthMillimeters, double NominalFocalFactor, string CalibrationVersion);

/// <summary>
/// Rendered VirtualSky star depth. Issue #1168 owns this render depth; the solver catalog depth stays
/// <see cref="VirtualAstrometryFixture.SolverOptions"/>, whose deeper selections belong to issue #1167.
/// </summary>
internal sealed record VirtualRenderDepth(double MaximumMagnitude, int MaximumResults);

/// <summary>
/// One frozen issue #1168 render variant. Every variant other than <c>none</c> renders the native views only, under the
/// 1168xx seed family; a cloud scenario is refused on any readout that takes VirtualSky's native-readout path.
/// </summary>
/// <param name="ExpectedOutcome">
/// <see cref="Accepted"/> fails on any rejection; <see cref="AcceptedOrFailClosed"/> records a rejection as the case
/// disposition; <see cref="Rejected"/> fails on any measured mapping. Every accepted mapping is scored in full.
/// </param>
internal sealed record VirtualLongExposureVariant(string Name, VirtualRenderDepth Depth,
    double BackgroundElectronsPerSecond, string ExpectedOutcome, VirtualCloudScenarioDefinition? CloudScenario = null)
{
    internal const string Accepted = "accepted";
    internal const string AcceptedOrFailClosed = "accepted-or-fail-closed";
    internal const string Rejected = "rejected";
}

/// <summary>A predeclared renderer refusal. Any other exception, or this one in an undeclared cell, fails the run.</summary>
internal sealed record VirtualRenderRefusal(string Stage, string ExceptionType, string Message)
{
    internal const string NativeReadoutClouds =
        "VirtualSky native readout currently supports byte-aligned monochrome identity, digital-sum, and digital-average modes without clouds, sensor-plane transient tracks, or synthetic calibration.";

    /// <summary>Returns whether <paramref name="exception"/> is exactly this refusal, by exact type and message.</summary>
    internal bool Matches(Exception exception) =>
        exception.GetType().FullName == ExceptionType && string.Equals(exception.Message, Message, StringComparison.Ordinal);
}

internal static class VirtualAstrometryFixture
{
    /// <summary>Per-process family selector; unset selects the unchanged equidistant regression matrix.</summary>
    internal const string FamilyVariable = "HVO_PROJECTION_FAMILY";
    /// <summary>Issue #1168 per-process selectors; unset reproduces the issue #1126 one-second v1 matrix.</summary>
    internal const string ExposureVariable = "HVO_EXPOSURE_SECONDS";
    internal const string MeasurerVariable = "HVO_ASTROMETRY_MEASURER";
    internal const string VariantVariable = "HVO_LONG_EXPOSURE_VARIANT";
    internal static readonly IReadOnlyList<int> ExposureSecondsValues = [1, 20, 60];
    internal static readonly IReadOnlyList<string> Measurers = ["v1", "v2"];
    internal static readonly VirtualRenderDepth QualificationDepth = new(5, 2000);
    /// <summary>The #522 product night render depth, rendered against the unchanged magnitude-5 solver catalog.</summary>
    internal static readonly VirtualRenderDepth ProductDepth = new(6.5, 32768);

    /// <summary>Frozen variants. Bright background is ten times the qualification sky, a moonlit or light-polluted proxy.</summary>
    internal static readonly IReadOnlyList<VirtualLongExposureVariant> Variants =
    [
        new("none", QualificationDepth, 2, VirtualLongExposureVariant.Accepted),
        new("product-depth", ProductDepth, 2, VirtualLongExposureVariant.AcceptedOrFailClosed),
        new("bright-background", QualificationDepth, 20, VirtualLongExposureVariant.AcceptedOrFailClosed),
        new("clouds-partial", QualificationDepth, 2, VirtualLongExposureVariant.AcceptedOrFailClosed, Clouds("clouds-partial", .4, .9)),
        new("overcast", QualificationDepth, 2, VirtualLongExposureVariant.Rejected, Clouds("overcast", 1, 1))
    ];

    /// <summary>Variants other than <c>none</c> render under the 1168xx seed family, paired by the partition seed's last two digits.</summary>
    internal static int Seed(int partitionSeed, VirtualLongExposureVariant variant) =>
        variant.Name == Variants[0].Name ? partitionSeed : 116800 + partitionSeed % 100;

    /// <summary>Drifting, evolving cloud fields from one fixed epoch, so each sky time sees a different deterministic field.</summary>
    private static VirtualCloudScenarioDefinition Clouds(string id, double coverage, double opacity) => new()
    {
        ScenarioId = $"virtual-astrometry-{id}",
        Seed = 116800,
        EpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        DriftEastCellsPerSecond = .002,
        DriftNorthCellsPerSecond = .001,
        EvolutionCellsPerSecond = .0005,
        Keyframes = [new() { Coverage = coverage, MaximumOpacity = opacity, ScatterFraction = .2 }]
    };
    private const double Asi174PixelMillimeters = .00586;
    private static double RectilinearFieldDegrees(double focalMillimeters) =>
        2 * Math.Atan(1936 * Asi174PixelMillimeters / 2 / focalMillimeters) * 180 / Math.PI;

    /// <summary>Frozen matrix. Orthographic requires a nominal focal at or above the declared image-circle radius, so it is offset upward.</summary>
    internal static readonly IReadOnlyList<VirtualProjectionFamily> Families =
    [
        new("equidistant", ProjectionModel.EquidistantFisheye, 180, 0, 1 / 1.037, "virtual-fisheye-180-equidistant-v1"),
        new("equisolid", ProjectionModel.EquisolidFisheye, 180, 0, 1 / 1.037, "virtual-asi174-equisolid-180-substitution-v1"),
        new("stereographic", ProjectionModel.StereographicFisheye, 180, 0, 1 / 1.037, "virtual-asi174-stereographic-180-substitution-v1"),
        new("orthographic", ProjectionModel.OrthographicFisheye, 160, 0, 1.037, "virtual-asi174-orthographic-160-substitution-v1"),
        new("rectilinear", ProjectionModel.Perspective, RectilinearFieldDegrees(6), 6, 1 / 1.037, "virtual-asi174-rectilinear-6mm-substitution-v1"),
        new("rectilinear-8mm", ProjectionModel.Perspective, RectilinearFieldDegrees(8), 8, 1 / 1.037, "virtual-asi174-rectilinear-8mm-substitution-v1")
    ];

    internal static VirtualProjectionFamily Family
    {
        get
        {
            var name = Environment.GetEnvironmentVariable(FamilyVariable);
            return string.IsNullOrWhiteSpace(name) ? Families[0]
                : Families.SingleOrDefault(f => f.Name == name) ?? throw new InvalidOperationException($"Unknown {FamilyVariable} '{name}'.");
        }
    }

    internal static int ExposureSeconds => Select(ExposureVariable, ExposureSecondsValues.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray()) is { } value
        ? int.Parse(value, CultureInfo.InvariantCulture) : ExposureSecondsValues[0];

    internal static TimeSpan Exposure => TimeSpan.FromSeconds(ExposureSeconds);

    internal static string Measurer => Select(MeasurerVariable, Measurers) ?? Measurers[0];

    internal static VirtualLongExposureVariant Variant =>
        Select(VariantVariable, [.. Variants.Select(v => v.Name)]) is { } name ? Variants.Single(v => v.Name == name) : Variants[0];

    /// <summary>Unset selects the default; an unknown value fails the run rather than silently selecting another matrix.</summary>
    private static string? Select(string variable, IReadOnlyList<string> values)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? null
            : values.Contains(value, StringComparer.Ordinal) ? value : throw new InvalidOperationException($"Unknown {variable} '{value}'.");
    }

    /// <summary>
    /// The frozen issue #1168 renderer refusals. The rectilinear 6 mm full-frame views need more than the renderer's 64
    /// temporal slots at the PSF-limited 0.15 px step for 60 s; the native-readout path refuses every cloud scenario.
    /// </summary>
    internal static VirtualRenderRefusal? DeclaredRefusal(VirtualProjectionFamily family, VirtualAstrometryProfile profile,
        int exposureSeconds, VirtualLongExposureVariant variant)
    {
        var rig = profile.Config.Rig;
        if (variant.CloudScenario is not null && rig.Sensor.PixelFormat == CameraPixelFormat.Mono16)
            return new("initialize", typeof(NotSupportedException).FullName!, VirtualRenderRefusal.NativeReadoutClouds);
        var fullFrame = rig.Readout is null || rig.Readout.Roi.Width == rig.Sensor.WidthPixels && rig.Readout.Roi.Height == rig.Sensor.HeightPixels;
        return family.Name == "rectilinear" && exposureSeconds == 60 && fullFrame
            ? new("capture", typeof(InvalidOperationException).FullName!, "stellar-exposure-temporal-budget-exceeded")
            : null;
    }

    /// <summary>The fixed night gain; only exposure varies across the long-exposure matrix.</summary>
    internal static CaptureRequest Request(DateTimeOffset utc, TimeSpan exposure) => new(utc, TimeSpan.FromSeconds(60), CaptureMode.Still,
        new(exposure, 150, null, null));

    internal static readonly ObserverLocation Observer = new(35.347, -113.878, 1000);
    internal static readonly AstrometricSolverOptions SolverOptions = new(MaximumCatalogMagnitude: 5);
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static IReadOnlyList<VirtualAstrometryProfile> Profiles(int seed) => Profiles(seed, Family, Variant);

    internal static IReadOnlyList<VirtualAstrometryProfile> Profiles(int seed, VirtualProjectionFamily family) => Profiles(seed, family, Variants[0]);

    internal static IReadOnlyList<VirtualAstrometryProfile> Profiles(int seed, VirtualProjectionFamily family, VirtualLongExposureVariant variant)
    {
        var mono = LoadRig("virtual-asi174.full.json") with
        {
            Orientation = new(82, 137, 13),
            Readout = new(new(0, 0, 1936, 1216), 1, 1, FrameBinningAlgorithm.IdentityV1,
                CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,
                FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample, 64, 4095)
        };
        if (family.Model != ProjectionModel.EquidistantFisheye)
            mono = mono with { Optics = FamilyOptics(mono.Optics, family) };
        var roi = mono.Readout! with { Roi = new(240, 96, 1440, 1024) };
        var bin = mono.Readout! with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 };
        List<VirtualAstrometryProfile> profiles =
        [
            Profile("mono-native", mono, seed, variant),
            Profile("mono-roi", mono with { Readout = roi }, seed, variant),
            Profile("mono-bin2", mono with { Readout = bin }, seed, variant),
            Profile("mono-roi-bin2", mono with { Readout = roi with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 } }, seed, variant),
            Profile("mono-mirror", mono with { Optics = mono.Optics with { HorizontalFlip = !mono.Optics.HorizontalFlip } }, seed, variant),
            Profile("mono-roll", mono with { Orientation = new(82, 137, -57) }, seed, variant)
        ];
        // CFA is qualified for equidistant only; CFA with every other family is outside the published envelope.
        if (family.Model == ProjectionModel.EquidistantFisheye)
            profiles.Add(Profile("cfa-native", LoadRig("virtual-asi178mc.full.json") with { Orientation = new(84, 73, 25) }, seed, variant));
        return variant.Name == Variants[0].Name ? profiles : [.. profiles.Where(p => p.Name is "mono-native" or "cfa-native")];
    }

    private static OpticsProfile FamilyOptics(OpticsProfile optics, VirtualProjectionFamily family) =>
        family.Model == ProjectionModel.Perspective
            ? optics with
            {
                ProjectionModel = family.Model.ToString(),
                LensKind = LensKind.Rectilinear,
                FocalLengthMillimeters = family.FocalLengthMillimeters,
                FieldOfViewDegrees = family.FieldOfViewDegrees,
                ImageCircleRadiusPixels = null,
                CalibrationVersion = family.CalibrationVersion
            }
            : optics with
            {
                ProjectionModel = family.Model.ToString(),
                FieldOfViewDegrees = family.FieldOfViewDegrees,
                CalibrationVersion = family.CalibrationVersion
            };

    private static CameraRigConfig LoadRig(string filename)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, filename)));
        return document.RootElement.GetProperty("rig").Deserialize<CameraRigConfig>(JsonOptions)!;
    }

    private static VirtualAstrometryProfile Profile(string name, CameraRigConfig rig, int seed, VirtualLongExposureVariant variant)
    {
        var options = new VirtualSkyCameraModuleOptions
        {
            Seed = seed,
            MaximumMagnitude = variant.Depth.MaximumMagnitude,
            MaximumResults = variant.Depth.MaximumResults,
            MagnitudeZeroElectronsPerSecond = 60000,
            BackgroundElectronsPerSecond = variant.BackgroundElectronsPerSecond,
            PsfSigmaPixels = 1,
            PsfRadiusPixels = 4,
            VignettingStrength = .25,
            ShotNoiseEnabled = true,
            Asi174Sensor = new() { Enabled = rig.Sensor.PixelFormat == CameraPixelFormat.Mono16 },
            CloudScenario = variant.CloudScenario
        };
        return new(name, new(new(Observer.LatitudeDegrees, Observer.LongitudeDegrees, Observer.ElevationMeters, "America/Phoenix"),
            new("VirtualSky", JsonSerializer.SerializeToElement(options, JsonOptions)), rig,
            CapturePipelineConfig.Empty, "virtual-astrometry-qualification"));
    }

    internal static AstrometricCalibration NominalCalibration(VirtualAstrometryProfile profile)
    {
        // Nominal intrinsics are declared sensor/optics specifications. Capture pose never enters this model.
        // A substituted family is identified by its frozen calibration version; the equidistant and CFA rigs keep their v1 constants.
        var family = Families.SingleOrDefault(f => f.CalibrationVersion == profile.Config.Rig.Optics.CalibrationVersion);
        var nominalFocal = family is { Model: not ProjectionModel.EquidistantFisheye }
            ? VirtualAstrometryReference.NativeFocal(profile.Config.Rig) * family.NominalFocalFactor
            : profile.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.Mono16
                ? 595.84 / (Math.PI / 2) / 1.037 : 735.553926 / 1.037;
        var nominalRig = profile.Config.Rig with
        {
            Orientation = new(52, 290, -41),
            Optics = profile.Config.Rig.Optics with
            {
                FocalLengthMillimeters = 0,
                FocalLengthXPixels = nominalFocal,
                FocalLengthYPixels = nominalFocal,
                CalibrationVersion = "virtual-astrometry-nominal-v1"
            }
        };
        var readout = SensorReadoutResolver.Resolve(nominalRig.Sensor, nominalRig.Readout!);
        return new(RigProjectionContextFactory.Create(nominalRig), nominalRig.Optics.CalibrationVersion,
            CaptureContractJson.ComputeCanonicalJsonSha256(readout.Layout));
    }

    internal static StellarDetectionOptions DetectionOptions(FrameLayoutDescriptor layout) => new(
        MinimumPeakAboveBackground: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 32 : 8,
        MinimumMinorSigma: .2, MaximumMajorSigma: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 2.3 : 1.8,
        MaximumVarianceRatio: 2.5, IsolationRadiusPixels: 12d / (layout.Readout?.BinX ?? 1));

    /// <summary>Candidate v2 settings: CFA luminance spreads one photosite, saturated or noisy, over its 3x3 reconstruction support.</summary>
    internal static StellarMeasurementOptions MeasurementOptions(FrameLayoutDescriptor layout) => new(
        MinimumPeakAboveBackground: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 32 : 8,
        SaturationDilationPixels: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 1 : 0,
        // The mono 16-sample (4x4) saturated-core budget, dilated by the one-pixel CFA support to 6x6.
        MaximumSaturatedSamples: layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 36 : 16,
        NoiseEstimator: layout.PixelFormat == CameraPixelFormat.BayerRggb16
            ? StellarNoiseEstimator.ClippedSpread : StellarNoiseEstimator.AdjacentDifferences);

    internal static (double[] Samples, bool[] Valid) Decode(VirtualAstrometryPixels input)
    {
        var (samples, saturated) = DecodeLinear(input);
        return (samples, [.. saturated.Select(static value => !value)]);
    }

    /// <summary>Decodes declared linear samples and the stored white-level saturation mask without discarding saturated photosites.</summary>
    internal static (double[] Samples, bool[] Saturated) DecodeLinear(VirtualAstrometryPixels input)
    {
        var layout = input.Layout;
        var validation = layout.Validate();
        if (!validation.IsValid || input.Payload.Length != layout.ByteLength ||
            layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.Packing != FrameSamplePacking.ByteAligned || layout.ContainerDepthBits != 16 ||
            layout.ByteOrder is not (FrameByteOrder.LittleEndian or FrameByteOrder.BigEndian) ||
            layout.WhiteLevel is null || layout.LevelCodeSpace is not (FrameLevelCodeSpace.NativeSample or FrameLevelCodeSpace.StoredContainer))
            throw new ArgumentException("Qualification requires an exact, validated linear 16-bit payload with declared levels.", nameof(input));
        var white = layout.WhiteLevel.Value;
        if (layout.LevelCodeSpace == FrameLevelCodeSpace.NativeSample)
        {
            white = layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.RightAlignedV1 or FrameStoredCodeTransform.IdentityV1 => white,
                FrameStoredCodeTransform.FullRangeScaledV1 => white * ushort.MaxValue / ((1 << layout.SampleDepthBits) - 1),
                FrameStoredCodeTransform.LeftShiftedV1 => white * (1 << (16 - layout.SampleDepthBits)),
                _ => throw new ArgumentException("Opaque sample codes cannot establish saturation.", nameof(input))
            };
        }
        var samples = new double[checked(layout.Width * layout.Height)];
        var saturated = new bool[samples.Length];
        for (var y = 0; y < layout.Height; y++)
            for (var x = 0; x < layout.Width; x++)
            {
                var bytes = input.Payload.Span.Slice(y * layout.StrideBytes + x * 2, 2);
                var value = layout.ByteOrder == FrameByteOrder.LittleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
                samples[y * layout.Width + x] = value;
                saturated[y * layout.Width + x] = value >= white;
            }
        return (samples, saturated);
    }

    internal static StellarDetectionResult Measure(VirtualAstrometryPixels input, AstrometricCalibration nominal)
    {
        var (samples, valid) = Decode(input);
        var aperture = nominal.Projection;
        if (aperture.WidthPixels != input.Layout.Width || aperture.HeightPixels != input.Layout.Height)
            throw new ArgumentException("Nominal aperture must describe the measured pixel view.", nameof(nominal));
        // This is the declared optical aperture, independent of pose, stars, or the captured scene.
        for (var y = 0; y < input.Layout.Height; y++)
            for (var x = 0; x < input.Layout.Width; x++)
                valid[y * input.Layout.Width + x] &= aperture.ContainsSample(x + .5, y + .5);
        if (input.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
        {
            var pattern = (input.Layout.Readout?.CfaOriginX ?? 0, input.Layout.Readout?.CfaOriginY ?? 0) switch
            {
                (0, 0) => BayerPattern.Rggb,
                (1, 0) => BayerPattern.Grbg,
                (0, 1) => BayerPattern.Gbrg,
                (1, 1) => BayerPattern.Bggr,
                _ => throw new ArgumentException("Invalid declared CFA origin.", nameof(input))
            };
            var luminance = LinearBayerReconstruction.Reconstruct(samples, valid, input.Layout.Width, input.Layout.Height, pattern).ToLuminance();
            return StellarDetector.Detect(luminance.Pixels.Span, luminance.ValidMask.Span,
                input.Layout.Width, input.Layout.Height, DetectionOptions(input.Layout));
        }
        return StellarDetector.Detect(samples, valid, input.Layout.Width, input.Layout.Height, DetectionOptions(input.Layout));
    }

    /// <summary>Candidate measurement: local background, saturation, trail and reason-coded exclusions on final pixels only.</summary>
    internal static StellarMeasurementResult MeasureV2(VirtualAstrometryPixels input, AstrometricCalibration nominal)
    {
        var (samples, saturated) = DecodeLinear(input);
        var aperture = nominal.Projection;
        if (aperture.WidthPixels != input.Layout.Width || aperture.HeightPixels != input.Layout.Height)
            throw new ArgumentException("Nominal aperture must describe the measured pixel view.", nameof(nominal));
        var valid = new bool[samples.Length];
        for (var y = 0; y < input.Layout.Height; y++)
            for (var x = 0; x < input.Layout.Width; x++)
                valid[y * input.Layout.Width + x] = aperture.ContainsSample(x + .5, y + .5);
        var options = MeasurementOptions(input.Layout);
        if (input.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
        {
            var luminance = LinearBayerReconstruction.Reconstruct(samples, valid, input.Layout.Width, input.Layout.Height, Pattern(input.Layout)).ToLuminance();
            return StellarSourceMeasurer.Measure(luminance.Pixels.Span, luminance.ValidMask.Span, saturated,
                input.Layout.Width, input.Layout.Height, options);
        }
        return StellarSourceMeasurer.Measure(samples, valid, saturated, input.Layout.Width, input.Layout.Height, options);
    }

    internal static AstrometricFrameContext FrameContextV2(VirtualAstrometryPixels input) => FrameContext(input) with
    {
        DetectionAlgorithmVersion = StellarSourceMeasurer.AlgorithmVersion,
        DetectionSettingsIdentitySha256 = StellarSourceMeasurer.SettingsIdentity(MeasurementOptions(input.Layout))
    };

    internal static AstrometricSolveResult SolveV2(VirtualAstrometryPixels input, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, StellarMeasurementResult measured, AstrometricFrameAssessment? prior = null)
    {
        var detections = Detections(measured);
        return prior is null ? AstrometricSolver.Solve(FrameContextV2(input), nominal, catalog, detections, SolverOptions)
            : AstrometricSolver.Refine(FrameContextV2(input), nominal, catalog, detections, prior, SolverOptions);
    }

    internal static AstrometricDetection[] Detections(StellarMeasurementResult measured) =>
        [.. measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux))];

    internal static VirtualAstrometryMeasurement Measure(VirtualAstrometryPixels input, AstrometricCalibration nominal, string measurer)
    {
        if (measurer == "v1")
        {
            var baseline = Measure(input, nominal);
            return new(measurer, baseline.Detections, baseline.CandidateCount, baseline.Background, baseline.NoiseSigma, FrameContext(input), null);
        }
        var candidate = MeasureV2(input, nominal);
        return new(measurer, candidate.Detections, candidate.CandidateCount, candidate.MedianBackground, candidate.MedianNoiseSigma,
            FrameContextV2(input), candidate);
    }

    internal static AstrometricSolveResult Solve(VirtualAstrometryMeasurement measured, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, AstrometricFrameAssessment? prior = null, AstrometricFrameContext? frame = null)
    {
        var detections = measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
        return prior is null ? AstrometricSolver.Solve(frame ?? measured.Frame, nominal, catalog, detections, SolverOptions)
            : AstrometricSolver.Refine(frame ?? measured.Frame, nominal, catalog, detections, prior, SolverOptions);
    }

    internal static AstrometricResidualDiagnostics Diagnose(AstrometricCalibration nominal, AstrometricCatalogData catalog,
        StellarMeasurementResult measured, AstrometricSolveResult solved) => AstrometricResidualAnalyzer.Analyze(nominal, catalog, SolverOptions, solved,
        Detections(measured), [.. measured.Exclusions.Select(e => new AstrometricMeasurementExclusion(e.Peak, e.ReasonCode))],
        [.. measured.Detections.Where(d => d.CentroidCovariance is not null).Select(d => new AstrometricPixelCovariance(d.Index,
            d.CentroidCovariance!.Value.XX, d.CentroidCovariance.Value.XY, d.CentroidCovariance.Value.YY))]);

    private static BayerPattern Pattern(FrameLayoutDescriptor layout) => (layout.Readout?.CfaOriginX ?? 0, layout.Readout?.CfaOriginY ?? 0) switch
    {
        (0, 0) => BayerPattern.Rggb,
        (1, 0) => BayerPattern.Grbg,
        (0, 1) => BayerPattern.Gbrg,
        (1, 1) => BayerPattern.Bggr,
        _ => throw new ArgumentException("Invalid declared CFA origin.", nameof(layout))
    };

    internal static AstrometricFrameContext FrameContext(VirtualAstrometryPixels input)
    {
        var payloadHash = Convert.ToHexString(SHA256.HashData(input.Payload.Span));
        var descriptorHash = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "virtual-astrometry-logical-exposure-v1",
            input.Layout,
            input.StartUtc,
            input.Exposure,
            input.Gain,
            input.ParentPayloadSha256,
            payloadHash
        });
        var sourceId = new Guid(Convert.FromHexString(descriptorHash)[..16]);
        var captureId = new Guid(Convert.FromHexString(payloadHash)[..16]);
        return new(captureId, sourceId, descriptorHash, payloadHash,
            CaptureContractJson.ComputeCanonicalJsonSha256(Observer), Observer, input.StartUtc, input.StartUtc + input.Exposure,
            StellarDetector.AlgorithmVersion, CaptureContractJson.ComputeCanonicalJsonSha256(DetectionOptions(input.Layout)));
    }

    internal static (VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels) DeriveCfaPhase(
        VirtualAstrometryProfile nativeProfile, VirtualAstrometryPixels native, int x, int y)
    {
        if (native.Layout.PixelFormat != CameraPixelFormat.BayerRggb16 || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentException("Only declared one-photosite phase views are supported.");
        var width = native.Layout.Width - x; var height = native.Layout.Height - y;
        var rig = nativeProfile.Config.Rig with
        {
            Readout = nativeProfile.Config.Rig.Readout! with
            {
                Roi = new(x, y, width, height),
                CfaOriginX = x,
                CfaOriginY = y,
                StrideBytes = width * 2
            }
        };
        var layout = SensorReadoutResolver.Resolve(rig.Sensor, rig.Readout).Layout;
        var pixels = new byte[checked((int)layout.ByteLength)];
        for (var row = 0; row < height; row++)
            native.Payload.Span.Slice((row + y) * native.Layout.StrideBytes + x * 2, width * 2)
                .CopyTo(pixels.AsSpan(row * layout.StrideBytes));
        var profile = new VirtualAstrometryProfile($"cfa-derived-phase-{x}{y}", nativeProfile.Config with { Rig = rig });
        return (profile, new(pixels, layout, native.StartUtc, native.Exposure, native.Gain,
            Convert.ToHexString(SHA256.HashData(native.Payload.Span))));
    }

    internal static AstrometricSolveResult Solve(VirtualAstrometryPixels input, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, StellarDetectionResult measured, AstrometricFrameAssessment? prior = null)
    {
        var detections = measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
        return prior is null ? AstrometricSolver.Solve(FrameContext(input), nominal, catalog, detections, SolverOptions)
            : AstrometricSolver.Refine(FrameContext(input), nominal, catalog, detections, prior, SolverOptions);
    }
}
