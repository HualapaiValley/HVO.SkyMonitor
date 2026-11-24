using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Top-level disposition for step 30b regional stellar visibility evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegionalVisibilityAssessmentStatus>))]
public enum RegionalVisibilityAssessmentStatus
{
    Assessed,
    Withheld,
    InsufficientEvidence
}

/// <summary>Per-tile regional visibility disposition. Never reported as a cloud-cover percentage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegionalVisibilityTileStatus>))]
public enum RegionalVisibilityTileStatus
{
    Clear,
    DetectionDeficit,
    InsufficientEvidence,
    Excluded
}

/// <summary>Geometric authority used to map independent catalog predictions to measured detections.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegionalVisibilityGeometryBasis>))]
public enum RegionalVisibilityGeometryBasis
{
    AcceptedAstrometrySolve,
    FixedGeometry,
    Unavailable
}

/// <summary>Focus-metric evidence status from step 4c HFD and step 4b source shape exclusions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegionalVisibilityFocusBasis>))]
public enum RegionalVisibilityFocusBasis
{
    Usable,
    Poor,
    Unavailable
}

/// <summary>Stable reason codes for regional stellar visibility assessments and per-tile support.</summary>
public static class RegionalVisibilityReasonCodes
{
    public const string UnsupportedFormat = "visibility.unsupported-format";
    public const string MissingLinearCalibration = "visibility.missing-linear-calibration";
    public const string MeasurementUnavailable = "visibility.measurement-unavailable";
    public const string PredictionUnavailable = "visibility.prediction-unavailable";
    public const string SimulatorTruthRejected = "visibility.simulator-truth-rejected";
    public const string MissingSensorResponse = "visibility.missing-sensor-response";
    public const string ResponseBasisMismatch = "visibility.response-basis-mismatch";
    public const string ZeroExposure = "visibility.zero-exposure";
    public const string Daylight = "visibility.daylight";
    public const string Twilight = "visibility.twilight";
    public const string MoonlightConfounded = "visibility.moonlight-confounded";
    public const string PrecipitationContaminated = "visibility.precipitation-contaminated";
    public const string SaturationContaminated = "visibility.saturation-contaminated";
    public const string PoorFocus = "visibility.poor-focus";
    public const string FocusUnavailable = "visibility.focus-unavailable";
    public const string PointingUncertain = "visibility.pointing-uncertain";
    public const string FixedGeometryUnverified = "visibility.fixed-geometry-unverified";
    public const string HorizonIncompatible = "visibility.horizon-incompatible";
    public const string AllRegionsExcluded = "visibility.all-regions-excluded";
    public const string InsufficientSupport = "visibility.insufficient-support";
    public const string AcceptedSolveVerified = "visibility.accepted-solve-verified";
    public const string FixedGeometryActive = "visibility.fixed-geometry-active";
    public const string FocusMetricsVerified = "visibility.focus-metrics-verified";
    public const string EnvironmentMissing = "visibility.environment-missing";
    public const string EnvironmentStale = "visibility.environment-stale";
    public const string EnvironmentContradictory = "visibility.environment-contradictory";

    public const string TileClear = "tile.clear";
    public const string TileDetectionDeficit = "tile.detection-deficit";
    public const string TileOutsideAperture = "tile.outside-aperture";
    public const string TileHorizonObstructed = "tile.horizon-obstructed";
    public const string TileHorizonUnknown = "tile.horizon-unknown";
    public const string TileExcludedRegion = "tile.excluded-region";
    public const string TileDefectMasked = "tile.defect-masked";
    public const string TileMoonGlareExcluded = "tile.moon-glare-excluded";
    public const string TileInsufficientExpectedStars = "tile.insufficient-expected-stars";
    public const string TilePointingUncertain = "tile.pointing-uncertain";
    public const string TileWithheld = "tile.withheld";
    public const string TileMarginalStarsExcluded = "tile.marginal-stars-excluded";

    public static IReadOnlySet<string> KnownGlobal { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        UnsupportedFormat,
        MissingLinearCalibration,
        MeasurementUnavailable,
        PredictionUnavailable,
        SimulatorTruthRejected,
        MissingSensorResponse,
        ResponseBasisMismatch,
        ZeroExposure,
        Daylight,
        Twilight,
        MoonlightConfounded,
        PrecipitationContaminated,
        SaturationContaminated,
        PoorFocus,
        FocusUnavailable,
        PointingUncertain,
        FixedGeometryUnverified,
        HorizonIncompatible,
        AllRegionsExcluded,
        InsufficientSupport,
        AcceptedSolveVerified,
        FixedGeometryActive,
        FocusMetricsVerified,
        EnvironmentMissing,
        EnvironmentStale,
        EnvironmentContradictory
    };

    public static IReadOnlySet<string> KnownTile { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TileClear,
        TileDetectionDeficit,
        TileOutsideAperture,
        TileHorizonObstructed,
        TileHorizonUnknown,
        TileExcludedRegion,
        TileDefectMasked,
        TileMoonGlareExcluded,
        TileInsufficientExpectedStars,
        TilePointingUncertain,
        TileWithheld,
        TileMarginalStarsExcluded
    };
}

/// <summary>Qualified photometric sensor response used to determine which predicted stars are expected detectable.</summary>
public sealed record RegionalVisibilityPhotometricProfileV1(
    string ProfileName,
    double MagnitudeZeroElectronsPerSecond,
    double NominalPsfSigmaPixels = 1.0,
    double DarkCurrentElectronsPerSecond = 0.0,
    double VignettingStrength = 0.0,
    double? ElectronsPerAduAtZeroGain = null,
    double? GainControlDivisor = null,
    double ReadNoiseElectrons = 0.0,
    double FullWellElectrons = 65_535.0,
    double ChannelResponseRed = 1.0,
    double ChannelResponseGreen = 1.0,
    double ChannelResponseBlue = 1.0,
    ConfiguredSensorResponseProfile? SensorResponse = null,
    string? CalibrationStatus = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProfileName) || ProfileName.Length > 128 ||
            !double.IsFinite(MagnitudeZeroElectronsPerSecond) || MagnitudeZeroElectronsPerSecond is <= 0 or > 1e12 ||
            !double.IsFinite(NominalPsfSigmaPixels) || NominalPsfSigmaPixels is <= 0 or > 16 ||
            !double.IsFinite(DarkCurrentElectronsPerSecond) || DarkCurrentElectronsPerSecond is < 0 or > 1e12 ||
            !double.IsFinite(VignettingStrength) || VignettingStrength is < 0 or > 1 ||
            !double.IsFinite(ReadNoiseElectrons) || ReadNoiseElectrons is < 0 or > 1e6 ||
            !double.IsFinite(FullWellElectrons) || FullWellElectrons is <= 0 or > 1e9 ||
            !double.IsFinite(ChannelResponseRed) || ChannelResponseRed is <= 0 or > 100 ||
            !double.IsFinite(ChannelResponseGreen) || ChannelResponseGreen is <= 0 or > 100 ||
            !double.IsFinite(ChannelResponseBlue) || ChannelResponseBlue is <= 0 or > 100 ||
            CalibrationStatus is { Length: > 128 })
        {
            throw new ArgumentOutOfRangeException(nameof(RegionalVisibilityPhotometricProfileV1));
        }

        if (SensorResponse is not null)
        {
            ConfiguredSensorResponseResolver.Validate(SensorResponse);
            return;
        }

        if (ElectronsPerAduAtZeroGain is not { } ePerAdu || !double.IsFinite(ePerAdu) || ePerAdu is <= 0 or > 1e6 ||
            GainControlDivisor is { } divisor && (!double.IsFinite(divisor) || divisor is <= 0 or > 1e6))
        {
            throw new ArgumentOutOfRangeException(nameof(RegionalVisibilityPhotometricProfileV1));
        }
    }

    internal bool TryResolve(
        double gainControl,
        FrameLayoutDescriptor layout,
        out MonoSensorResponse? response,
        out double red,
        out double green,
        out double blue,
        out string? failureReason)
    {
        response = null;
        red = ChannelResponseRed;
        green = ChannelResponseGreen;
        blue = ChannelResponseBlue;
        failureReason = null;

        if (!double.IsFinite(gainControl) || gainControl < 0)
        {
            failureReason = RegionalVisibilityReasonCodes.ResponseBasisMismatch;
            return false;
        }

        if (SensorResponse is { } configured)
        {
            if (gainControl < configured.MinimumGainControl || gainControl > configured.MaximumGainControl)
            {
                failureReason = RegionalVisibilityReasonCodes.ResponseBasisMismatch;
                return false;
            }
            response = ConfiguredSensorResponseResolver.Resolve(configured, gainControl);
            if (configured.ColorResponse is { } color)
            {
                red = color.Red;
                green = color.Green;
                blue = color.Blue;
            }
            return true;
        }

        var ePerAduZero = ElectronsPerAduAtZeroGain!.Value;
        double electronsPerAdu;
        if (GainControlDivisor is { } divisor)
        {
            electronsPerAdu = ePerAduZero / Math.Pow(10d, gainControl / divisor);
        }
        else
        {
            if (gainControl <= 0)
            {
                failureReason = RegionalVisibilityReasonCodes.ResponseBasisMismatch;
                return false;
            }
            electronsPerAdu = ePerAduZero / gainControl;
        }

        if (!double.IsFinite(electronsPerAdu) || electronsPerAdu <= 0)
        {
            failureReason = RegionalVisibilityReasonCodes.ResponseBasisMismatch;
            return false;
        }

        var adcBits = Math.Clamp(layout.SampleDepthBits, 1, 16);
        var maxAdu = (1 << adcBits) - 1;
        var black = layout.BlackLevel is { } declaredBlack && double.IsFinite(declaredBlack)
            ? Math.Clamp(declaredBlack, 0, maxAdu)
            : 0;
        response = new MonoSensorResponse
        {
            AdcBitDepth = adcBits,
            FullWellElectrons = FullWellElectrons,
            ElectronsPerAdu = electronsPerAdu,
            ReadNoiseElectrons = ReadNoiseElectrons,
            BlackLevelAdu = black,
            CompatibilityLabel = ProfileName
        };
        return true;
    }
}

/// <summary>Explicit thresholds and geometry bounds for step 30b regional stellar visibility.</summary>
public sealed record RegionalStellarVisibilityOptions(
    int GridColumns = 4,
    int GridRows = 4,
    int MinimumExpectedStarsPerTile = 3,
    int MinimumMissingStarsForDeficit = 2,
    int DeficitVisibilityThresholdMillionths = 600_000,
    double MinimumExpectedSignalToNoise = 8.0,
    double PeakMarginFactor = 1.25,
    double MatchRadiusPixels = 3.5,
    double OffsetSearchRadiusPixels = 20.0,
    double EdgeMarginPixels = 10.0,
    double CrowdingRadiusPixels = 8.0,
    double CrowdingMagnitudeDifference = 1.5,
    int MinimumTileSupportedAreaMillionths = 250_000,
    double MaximumAcceptableHfdPixels = 6.0,
    int MinimumFocusSamples = 3,
    int MinimumTooBroadExclusionsForPoorFocus = 3,
    int MaximumSaturatedFractionMillionths = 100_000,
    double MoonGlareExclusionDegrees = 15.0,
    double MoonWithholdAltitudeDegrees = 10.0,
    double MoonWithholdIlluminatedFraction = 0.70,
    double MoonInFovWithholdIlluminatedFraction = 0.35,
    double MaximumPointingShiftPixels = 2.0,
    double MaximumImpliedRollShiftFraction = 0.60,
    bool IncludeMask = true,
    IReadOnlyList<NormalizedCloudRectangle>? ExcludedRegions = null,
    RegionalVisibilityPhotometricProfileV1? PhotometricProfile = null)
{
    public const int MaximumGridDimension = 32;
    public const int MaximumTileCount = 256;

    public void Validate()
    {
        if (GridColumns is < 1 or > MaximumGridDimension ||
            GridRows is < 1 or > MaximumGridDimension ||
            GridColumns * GridRows > MaximumTileCount ||
            MinimumExpectedStarsPerTile is < 1 or > 1000 ||
            MinimumMissingStarsForDeficit is < 1 or > 1000 ||
            MinimumMissingStarsForDeficit > MinimumExpectedStarsPerTile ||
            DeficitVisibilityThresholdMillionths is <= 0 or >= 1_000_000 ||
            !double.IsFinite(MinimumExpectedSignalToNoise) || MinimumExpectedSignalToNoise is < 3.0 or > 1000.0 ||
            !double.IsFinite(PeakMarginFactor) || PeakMarginFactor is < 1.0 or > 10.0 ||
            !double.IsFinite(MatchRadiusPixels) || MatchRadiusPixels is < 0.5 or > 32.0 ||
            !double.IsFinite(OffsetSearchRadiusPixels) || OffsetSearchRadiusPixels < MatchRadiusPixels || OffsetSearchRadiusPixels > 128.0 ||
            !double.IsFinite(EdgeMarginPixels) || EdgeMarginPixels is < 0 or > 256.0 ||
            !double.IsFinite(CrowdingRadiusPixels) || CrowdingRadiusPixels is < 0 or > 64.0 ||
            !double.IsFinite(CrowdingMagnitudeDifference) || CrowdingMagnitudeDifference is < 0 or > 10.0 ||
            MinimumTileSupportedAreaMillionths is < 1 or > 1_000_000 ||
            !double.IsFinite(MaximumAcceptableHfdPixels) || MaximumAcceptableHfdPixels is < 1.0 or > 64.0 ||
            MinimumFocusSamples is < 1 or > ExposureMeasurementProducts.MaximumHfdSamples ||
            MinimumTooBroadExclusionsForPoorFocus is < 1 or > 1000 ||
            MaximumSaturatedFractionMillionths is < 0 or > 1_000_000 ||
            !double.IsFinite(MoonGlareExclusionDegrees) || MoonGlareExclusionDegrees is < 0 or > 90.0 ||
            !double.IsFinite(MoonWithholdAltitudeDegrees) || MoonWithholdAltitudeDegrees is < 0 or > 90.0 ||
            !double.IsFinite(MoonWithholdIlluminatedFraction) || MoonWithholdIlluminatedFraction is < 0 or > 1.0 ||
            !double.IsFinite(MoonInFovWithholdIlluminatedFraction) || MoonInFovWithholdIlluminatedFraction is < 0 or > 1.0 ||
            !double.IsFinite(MaximumPointingShiftPixels) || MaximumPointingShiftPixels is < 0.25 or > 32.0 ||
            !double.IsFinite(MaximumImpliedRollShiftFraction) || MaximumImpliedRollShiftFraction is <= 0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(RegionalStellarVisibilityOptions));
        }

        if (ExcludedRegions is { } excluded)
        {
            if (excluded.Count > Linear16CloudTransmissionEstimator.MaximumExcludedRegionCount ||
                excluded.Any(static region =>
                    region.LeftMillionths < 0 || region.TopMillionths < 0 ||
                    region.RightMillionths > 1_000_000 || region.BottomMillionths > 1_000_000 ||
                    region.LeftMillionths >= region.RightMillionths ||
                    region.TopMillionths >= region.BottomMillionths))
            {
                throw new ArgumentOutOfRangeException(nameof(ExcludedRegions));
            }
        }

        PhotometricProfile?.Validate();
    }
}

/// <summary>Independent provenance lineage binding step 30b to the exact exposure, measurement, prediction, and optional solve.</summary>
public sealed record RegionalVisibilitySourceV1(
    [property: JsonRequired] Guid CaptureId,
    [property: JsonRequired] Guid RawArtifactId,
    [property: JsonRequired] string RawDescriptorSha256,
    [property: JsonRequired] Guid? MeasurementArtifactId,
    [property: JsonRequired] string? MeasurementIdentitySha256,
    [property: JsonRequired] Guid? PredictionArtifactId,
    [property: JsonRequired] string? PredictionIdentitySha256,
    [property: JsonRequired] Guid? AstrometryArtifactId,
    [property: JsonRequired] string? AstrometryIdentitySha256,
    [property: JsonRequired] Guid? DefectMaskArtifactId,
    [property: JsonRequired] string? DefectMaskSha256);

/// <summary>Captured photometric, geometric, environmental, and confounder facts for the assessment.</summary>
public sealed record RegionalVisibilityExposureConditionsV1(
    [property: JsonRequired] double ExposureSeconds,
    [property: JsonRequired] double Gain,
    [property: JsonRequired] CameraPixelFormat PixelFormat,
    [property: JsonRequired] int WidthPixels,
    [property: JsonRequired] int HeightPixels,
    [property: JsonRequired] ExposureCalibrationDisposition? CalibrationDisposition,
    [property: JsonRequired] double? ElectronsPerAdu,
    [property: JsonRequired] double? ReadNoiseElectrons,
    [property: JsonRequired] double? MeasuredBackgroundAdu,
    [property: JsonRequired] double? MeasuredNoiseSigmaAdu,
    [property: JsonRequired] double? BackgroundElectronsPerSecondPerPixel,
    [property: JsonRequired] int SaturatedFractionMillionths,
    [property: JsonRequired] CaptureSolarRegime? SolarRegime,
    [property: JsonRequired] double? SunAltitudeDegrees,
    [property: JsonRequired] EnvironmentalObservationMatchStatus PrecipitationStatus,
    [property: JsonRequired] bool PrecipitationDetected,
    [property: JsonRequired] double? MoonAltitudeDegrees,
    [property: JsonRequired] double? MoonAzimuthDegrees,
    [property: JsonRequired] double? MoonIlluminatedFraction,
    [property: JsonRequired] bool MoonInFieldOfView,
    [property: JsonRequired] double? EffectivePsfSigmaPixels,
    [property: JsonRequired] double? MeasuredMedianHfdPixels,
    [property: JsonRequired] int ValidFocusSampleCount,
    [property: JsonRequired] int TooBroadExclusionCount,
    [property: JsonRequired] int FixedGeometryAnchorCount,
    [property: JsonRequired] double? EstimatedShiftPixels,
    [property: JsonRequired] double? EstimatedRollDegrees,
    [property: JsonRequired] double? MaxImpliedCornerShiftPixels,
    [property: JsonRequired] string? PhotometricProfileName,
    [property: JsonRequired] string? HorizonProfileIdentitySha256);

/// <summary>Grid-level regional support and classification totals (deliberately no cloud-cover percentage).</summary>
public sealed record RegionalVisibilityGridV1(
    [property: JsonRequired] int Columns,
    [property: JsonRequired] int Rows,
    [property: JsonRequired] int TotalTileCount,
    [property: JsonRequired] int ExcludedTileCount,
    [property: JsonRequired] int InsufficientEvidenceTileCount,
    [property: JsonRequired] int AssessedTileCount,
    [property: JsonRequired] int ClearTileCount,
    [property: JsonRequired] int DeficitTileCount,
    [property: JsonRequired] int TotalExpectedDetectableStars,
    [property: JsonRequired] int TotalMatchedMeasuredStars,
    [property: JsonRequired] int TotalMarginalStars,
    [property: JsonRequired] int TotalMaskedOutStars,
    [property: JsonRequired] int TotalUnassociatedMeasuredStars,
    [property: JsonRequired] int DeficitVisibilityThresholdMillionths,
    [property: JsonRequired] int MinimumExpectedStarsPerTile);

/// <summary>Per-tile expected-vs-measured stellar visibility support and explicit reason codes.</summary>
public sealed record RegionalVisibilityTileV1(
    [property: JsonRequired] int Column,
    [property: JsonRequired] int Row,
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] RegionalVisibilityTileStatus Status,
    [property: JsonRequired] int SupportedAreaFractionMillionths,
    [property: JsonRequired] int PredictedStarCount,
    [property: JsonRequired] int ExpectedDetectableCount,
    [property: JsonRequired] int MarginalStarCount,
    [property: JsonRequired] int MaskedOutStarCount,
    [property: JsonRequired] int MatchedMeasuredCount,
    [property: JsonRequired] int MeasurementExcludedCount,
    [property: JsonRequired] int UnassociatedMeasuredCount,
    [property: JsonRequired] int OffsetCandidateCount,
    [property: JsonRequired] int? VisibilityRatioMillionths,
    [property: JsonRequired] IReadOnlyList<string> ReasonCodes);

/// <summary>Compact row-major tile masks distinguishing assessed tiles from detection-deficit tiles.</summary>
public sealed record RegionalVisibilityMaskV1(
    [property: JsonRequired] string Encoding,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] ReadOnlyMemory<byte> DeficitBits,
    [property: JsonRequired] ReadOnlyMemory<byte> AssessedBits)
{
    public const string RowMajorLsbFirst = "row-major-lsb-first";
}

/// <summary>
/// Canonical regional stellar visibility cloud evidence for one exposure (step 30b).
/// Retains completely separate provenance from the clear-reference transmission method (#105/#1004)
/// and deliberately reports tile support and deficits without a cloud-cover percentage.
/// </summary>
public sealed record RegionalStellarVisibilityV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string MethodId,
    [property: JsonRequired] RegionalVisibilityAssessmentStatus Status,
    [property: JsonRequired] RegionalVisibilityGeometryBasis GeometryBasis,
    [property: JsonRequired] RegionalVisibilityFocusBasis FocusBasis,
    [property: JsonRequired] int ConfidenceMillionths,
    [property: JsonRequired] IReadOnlyList<string> ReasonCodes,
    [property: JsonRequired] RegionalVisibilityGridV1 Grid,
    [property: JsonRequired] IReadOnlyList<RegionalVisibilityTileV1> Tiles,
    [property: JsonRequired] RegionalVisibilityMaskV1? Mask,
    [property: JsonRequired] RegionalVisibilitySourceV1 Sources,
    [property: JsonRequired] RegionalVisibilityExposureConditionsV1 Conditions,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms)
{
    public const string CurrentSchemaVersion = "regional-stellar-visibility-v1";
    public const string CurrentMethodId = "stellar-visibility-deficit-v1";

    [JsonIgnore]
    public string AssessmentIdentitySha256 => RegionalStellarVisibilityJson.ComputeIdentitySha256(this);
}

public readonly record struct RegionalVisibilityValidationResult(
    bool IsValid,
    string? ReasonCode,
    string? FieldPath)
{
    public static RegionalVisibilityValidationResult Success => new(true, null, null);
    public static RegionalVisibilityValidationResult Failure(string reasonCode, string fieldPath)
        => new(false, reasonCode, fieldPath);
}

public sealed record RegionalVisibilityParseResult(
    RegionalStellarVisibilityV1? Assessment,
    RegionalVisibilityValidationResult Validation)
{
    public bool IsValid => Assessment is not null && Validation.IsValid;
}

/// <summary>Canonical JSON serialization, strict parsing, and invariant validation for step 30b.</summary>
public static class RegionalStellarVisibilityJson
{
    public const int MaximumPayloadBytes = 1024 * 1024;
    private const int OneMillion = 1_000_000;
    private const string InvalidContract = "visibility.invalid-contract";
    private const string InvalidJson = "visibility.invalid-json";
    private const string UnsupportedSchema = "visibility.unsupported-schema";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string ComputeIdentitySha256(RegionalStellarVisibilityV1 assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return CaptureContractJson.ComputeCanonicalJsonSha256(
            JsonSerializer.SerializeToElement(assessment, SerializerOptions));
    }

    public static byte[] Serialize(RegionalStellarVisibilityV1 assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var validation = Validate(assessment);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Regional stellar visibility assessment is invalid ({validation.ReasonCode}, {validation.FieldPath}).",
                nameof(assessment));
        }

        var element = JsonSerializer.SerializeToElement(assessment, SerializerOptions);
        var json = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
        if (json.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("Regional stellar visibility assessment exceeds the maximum payload size.", nameof(assessment));
        }

        return json;
    }

    public static RegionalVisibilityParseResult Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length is < 1 or > MaximumPayloadBytes)
        {
            return Failure(InvalidContract, "$");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (HasDuplicateProperties(document.RootElement))
            {
                return Failure(InvalidJson, "$");
            }

            var assessment = document.RootElement.Deserialize<RegionalStellarVisibilityV1>(SerializerOptions);
            if (assessment is null)
            {
                return Failure(InvalidJson, "$");
            }

            var validation = Validate(assessment);
            return new(validation.IsValid ? assessment : null, validation);
        }
        catch (JsonException)
        {
            return Failure(InvalidJson, "$");
        }
    }

    public static RegionalVisibilityValidationResult Validate(RegionalStellarVisibilityV1 assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!string.Equals(assessment.SchemaVersion, RegionalStellarVisibilityV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Invalid(UnsupportedSchema, nameof(assessment.SchemaVersion));
        }

        if (!string.Equals(assessment.MethodId, RegionalStellarVisibilityV1.CurrentMethodId, StringComparison.Ordinal))
        {
            return Invalid(InvalidContract, nameof(assessment.MethodId));
        }

        if (!Enum.IsDefined(assessment.Status) || !Enum.IsDefined(assessment.GeometryBasis) ||
            !Enum.IsDefined(assessment.FocusBasis) || !Millionths(assessment.ConfidenceMillionths) ||
            assessment.ReasonCodes is null || assessment.ReasonCodes.Any(string.IsNullOrWhiteSpace) ||
            assessment.ReasonCodes.Any(code => !RegionalVisibilityReasonCodes.KnownGlobal.Contains(code)) ||
            assessment.ReasonCodes.Distinct(StringComparer.Ordinal).Count() != assessment.ReasonCodes.Count)
        {
            return Invalid(InvalidContract, "summary");
        }

        if (assessment.Status == RegionalVisibilityAssessmentStatus.Assessed)
        {
            if (assessment.ConfidenceMillionths <= 0 || assessment.GeometryBasis == RegionalVisibilityGeometryBasis.Unavailable ||
                assessment.FocusBasis == RegionalVisibilityFocusBasis.Poor)
            {
                return Invalid(InvalidContract, nameof(assessment.Status));
            }
        }
        else
        {
            if (assessment.ConfidenceMillionths != 0 || assessment.ReasonCodes.Count == 0)
            {
                return Invalid(InvalidContract, nameof(assessment.Status));
            }
        }

        var grid = assessment.Grid;
        if (grid is null || grid.Columns is < 1 or > RegionalStellarVisibilityOptions.MaximumGridDimension ||
            grid.Rows is < 1 or > RegionalStellarVisibilityOptions.MaximumGridDimension ||
            grid.TotalTileCount != grid.Columns * grid.Rows ||
            grid.TotalTileCount > RegionalStellarVisibilityOptions.MaximumTileCount ||
            grid.ExcludedTileCount < 0 || grid.InsufficientEvidenceTileCount < 0 ||
            grid.AssessedTileCount < 0 || grid.ClearTileCount < 0 || grid.DeficitTileCount < 0 ||
            grid.AssessedTileCount != grid.ClearTileCount + grid.DeficitTileCount ||
            grid.ExcludedTileCount + grid.InsufficientEvidenceTileCount + grid.AssessedTileCount != grid.TotalTileCount ||
            grid.DeficitVisibilityThresholdMillionths is <= 0 or >= OneMillion ||
            grid.MinimumExpectedStarsPerTile < 1 ||
            grid.TotalExpectedDetectableStars < 0 || grid.TotalMatchedMeasuredStars < 0 ||
            grid.TotalMatchedMeasuredStars > grid.TotalExpectedDetectableStars ||
            grid.TotalMarginalStars < 0 || grid.TotalMaskedOutStars < 0 || grid.TotalUnassociatedMeasuredStars < 0)
        {
            return Invalid(InvalidContract, nameof(assessment.Grid));
        }

        if (assessment.Status == RegionalVisibilityAssessmentStatus.Assessed && grid.AssessedTileCount == 0 ||
            assessment.Status != RegionalVisibilityAssessmentStatus.Assessed && grid.AssessedTileCount != 0)
        {
            return Invalid(InvalidContract, nameof(assessment.Grid));
        }

        if (assessment.Tiles is null || assessment.Tiles.Count != grid.TotalTileCount)
        {
            return Invalid(InvalidContract, nameof(assessment.Tiles));
        }

        int excluded = 0, insufficient = 0, clear = 0, deficit = 0;
        int totalExpected = 0, totalMatched = 0, totalMarginal = 0, totalMasked = 0, totalUnassociated = 0;
        for (var index = 0; index < assessment.Tiles.Count; index++)
        {
            var tile = assessment.Tiles[index];
            if (tile is null || tile.Column != index % grid.Columns || tile.Row != index / grid.Columns ||
                tile.X < 0 || tile.Y < 0 || tile.Width < 1 || tile.Height < 1 ||
                !Enum.IsDefined(tile.Status) || !Millionths(tile.SupportedAreaFractionMillionths) ||
                tile.PredictedStarCount < 0 || tile.ExpectedDetectableCount < 0 || tile.MarginalStarCount < 0 ||
                tile.MaskedOutStarCount < 0 || tile.MatchedMeasuredCount < 0 || tile.MeasurementExcludedCount < 0 ||
                tile.UnassociatedMeasuredCount < 0 || tile.OffsetCandidateCount < 0 ||
                tile.MatchedMeasuredCount > tile.ExpectedDetectableCount ||
                tile.VisibilityRatioMillionths is { } ratio && !Millionths(ratio) ||
                tile.ReasonCodes is null || tile.ReasonCodes.Count == 0 ||
                tile.ReasonCodes.Any(code => !RegionalVisibilityReasonCodes.KnownTile.Contains(code)) ||
                tile.ReasonCodes.Distinct(StringComparer.Ordinal).Count() != tile.ReasonCodes.Count)
            {
                return Invalid(InvalidContract, $"tiles[{index}]");
            }

            if (tile.Status is RegionalVisibilityTileStatus.Clear or RegionalVisibilityTileStatus.DetectionDeficit &&
                (tile.ExpectedDetectableCount < grid.MinimumExpectedStarsPerTile || tile.VisibilityRatioMillionths is null))
            {
                return Invalid(InvalidContract, $"tiles[{index}]");
            }

            switch (tile.Status)
            {
                case RegionalVisibilityTileStatus.Excluded: excluded++; break;
                case RegionalVisibilityTileStatus.InsufficientEvidence: insufficient++; break;
                case RegionalVisibilityTileStatus.Clear: clear++; break;
                case RegionalVisibilityTileStatus.DetectionDeficit: deficit++; break;
            }

            totalExpected += tile.ExpectedDetectableCount;
            totalMatched += tile.MatchedMeasuredCount;
            totalMarginal += tile.MarginalStarCount;
            totalMasked += tile.MaskedOutStarCount;
            totalUnassociated += tile.UnassociatedMeasuredCount;
        }

        if (excluded != grid.ExcludedTileCount || insufficient != grid.InsufficientEvidenceTileCount ||
            clear != grid.ClearTileCount || deficit != grid.DeficitTileCount ||
            totalExpected != grid.TotalExpectedDetectableStars || totalMatched != grid.TotalMatchedMeasuredStars ||
            totalMarginal != grid.TotalMarginalStars || totalMasked != grid.TotalMaskedOutStars ||
            totalUnassociated != grid.TotalUnassociatedMeasuredStars)
        {
            return Invalid(InvalidContract, nameof(assessment.Grid));
        }

        var maskValidation = ValidateMask(assessment.Mask, grid, assessment.Tiles);
        if (!maskValidation.IsValid)
        {
            return maskValidation;
        }

        if (assessment.Sources is null || assessment.Sources.CaptureId == Guid.Empty ||
            assessment.Sources.RawArtifactId == Guid.Empty || !Sha256(assessment.Sources.RawDescriptorSha256) ||
            (assessment.Sources.MeasurementArtifactId is not null) != (assessment.Sources.MeasurementIdentitySha256 is not null) ||
            assessment.Sources.MeasurementIdentitySha256 is not null && !Sha256(assessment.Sources.MeasurementIdentitySha256) ||
            (assessment.Sources.PredictionArtifactId is not null) != (assessment.Sources.PredictionIdentitySha256 is not null) ||
            assessment.Sources.PredictionIdentitySha256 is not null && !Sha256(assessment.Sources.PredictionIdentitySha256) ||
            (assessment.Sources.AstrometryArtifactId is not null) != (assessment.Sources.AstrometryIdentitySha256 is not null) ||
            assessment.Sources.AstrometryIdentitySha256 is not null && !Sha256(assessment.Sources.AstrometryIdentitySha256) ||
            (assessment.Sources.DefectMaskArtifactId is not null) != (assessment.Sources.DefectMaskSha256 is not null) ||
            assessment.Sources.DefectMaskSha256 is not null && !Sha256(assessment.Sources.DefectMaskSha256))
        {
            return Invalid(InvalidContract, nameof(assessment.Sources));
        }

        if (assessment.Conditions is null ||
            !double.IsFinite(assessment.Conditions.ExposureSeconds) || assessment.Conditions.ExposureSeconds < 0 ||
            !double.IsFinite(assessment.Conditions.Gain) || assessment.Conditions.Gain < 0 ||
            !Enum.IsDefined(assessment.Conditions.PixelFormat) ||
            assessment.Conditions.WidthPixels < 1 || assessment.Conditions.HeightPixels < 1 ||
            !Millionths(assessment.Conditions.SaturatedFractionMillionths) ||
            !Enum.IsDefined(assessment.Conditions.PrecipitationStatus) ||
            assessment.Conditions.ValidFocusSampleCount < 0 || assessment.Conditions.TooBroadExclusionCount < 0 ||
            assessment.Conditions.FixedGeometryAnchorCount < 0)
        {
            return Invalid(InvalidContract, nameof(assessment.Conditions));
        }

        if (!Sha256(assessment.RecipeIdentitySha256) || assessment.Algorithms is null ||
            assessment.Algorithms.Count == 0 || assessment.Algorithms.Any(static algorithm =>
                algorithm is null || string.IsNullOrWhiteSpace(algorithm.Name) || string.IsNullOrWhiteSpace(algorithm.Version)))
        {
            return Invalid(InvalidContract, "provenance");
        }

        return RegionalVisibilityValidationResult.Success;
    }

    private static RegionalVisibilityValidationResult ValidateMask(
        RegionalVisibilityMaskV1? mask,
        RegionalVisibilityGridV1 grid,
        IReadOnlyList<RegionalVisibilityTileV1> tiles)
    {
        if (mask is null)
        {
            return RegionalVisibilityValidationResult.Success;
        }

        var expectedBytes = (tiles.Count + 7) / 8;
        if (!string.Equals(mask.Encoding, RegionalVisibilityMaskV1.RowMajorLsbFirst, StringComparison.Ordinal) ||
            mask.Width != grid.Columns || mask.Height != grid.Rows ||
            mask.DeficitBits.Length != expectedBytes || mask.AssessedBits.Length != expectedBytes)
        {
            return Invalid(InvalidContract, nameof(RegionalStellarVisibilityV1.Mask));
        }

        var deficitBits = mask.DeficitBits.Span;
        var assessedBits = mask.AssessedBits.Span;
        for (var index = 0; index < tiles.Count; index++)
        {
            var isDeficit = (deficitBits[index >> 3] & (1 << (index & 7))) != 0;
            var isAssessed = (assessedBits[index >> 3] & (1 << (index & 7))) != 0;
            var expectedDeficit = tiles[index].Status == RegionalVisibilityTileStatus.DetectionDeficit;
            var expectedAssessed = tiles[index].Status is RegionalVisibilityTileStatus.Clear or RegionalVisibilityTileStatus.DetectionDeficit;
            if (isDeficit != expectedDeficit || isAssessed != expectedAssessed)
            {
                return Invalid(InvalidContract, nameof(RegionalStellarVisibilityV1.Mask));
            }
        }

        if ((tiles.Count & 7) != 0)
        {
            var trailingMask = ~((1 << (tiles.Count & 7)) - 1);
            if ((deficitBits[^1] & trailingMask) != 0 || (assessedBits[^1] & trailingMask) != 0)
            {
                return Invalid(InvalidContract, nameof(RegionalStellarVisibilityV1.Mask));
            }
        }

        return RegionalVisibilityValidationResult.Success;
    }

    private static bool Millionths(int value) => value is >= 0 and <= OneMillion;
    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static RegionalVisibilityParseResult Failure(string reasonCode, string path)
        => new(null, Invalid(reasonCode, path));

    private static RegionalVisibilityValidationResult Invalid(string reasonCode, string path)
        => RegionalVisibilityValidationResult.Failure(reasonCode, path);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
