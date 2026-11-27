using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Stable reason codes for predictions and detections that the expected-window matcher did not associate.</summary>
/// <remarks>Prediction and detection codes reuse the astrometric residual vocabulary so #523 consumers share one meaning.</remarks>
public static class MeasuredStellarAssociationReasonCodes
{
    public const string OutsideAperture = AstrometricDiagnosticReasons.OutsideAperture;
    public const string NearEdge = AstrometricDiagnosticReasons.NearEdge;
    public const string CrowdedPrediction = AstrometricDiagnosticReasons.CrowdedPrediction;
    public const string Ambiguous = AstrometricDiagnosticReasons.Ambiguous;
    public const string ClaimedByOther = AstrometricDiagnosticReasons.ClaimedByOther;
    public const string MeasurementExcludedPrefix = AstrometricDiagnosticReasons.MeasurementExcludedPrefix;
    public const string OffsetMeasuredSource = AstrometricDiagnosticReasons.OffsetMeasuredSource;
    public const string NoMeasuredSource = AstrometricDiagnosticReasons.NoMeasuredSource;
    public const string MeasurementIncomplete = "measurement-incomplete";

    public const string NoCatalogPrediction = AstrometricDiagnosticReasons.NoCatalogPrediction;
    public const string NearAssociatedPrediction = AstrometricDiagnosticReasons.NearAssociatedPrediction;
    public const string NearIneligiblePrediction = AstrometricDiagnosticReasons.NearIneligiblePrediction;
    public const string NearUnassociatedPrediction = AstrometricDiagnosticReasons.NearUnassociatedPrediction;

    public const string LabelLowSignalToNoise = "label.low-snr";
    public const string LabelSignalToNoiseUnavailable = "label.snr-unavailable";
    public const string LabelSaturated = "label.saturated";
    public const string LabelTrailed = "label.trailed";

    internal static IReadOnlySet<string> PredictionReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        OutsideAperture, NearEdge, CrowdedPrediction, Ambiguous, ClaimedByOther, OffsetMeasuredSource,
        NoMeasuredSource, MeasurementIncomplete
    };

    internal static IReadOnlySet<string> DetectionReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        NoCatalogPrediction, NearAssociatedPrediction, NearIneligiblePrediction, NearUnassociatedPrediction
    };

    internal static IReadOnlySet<string> LabelRejectionReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        LabelLowSignalToNoise, LabelSignalToNoiseUnavailable, LabelSaturated, LabelTrailed
    };

    internal static bool IsPredictionReason(string? value) =>
        value is not null && (PredictionReasons.Contains(value) ||
            value.StartsWith(MeasurementExcludedPrefix, StringComparison.Ordinal) &&
            StellarExclusionReasons.All.Contains(value[MeasurementExcludedPrefix.Length..], StringComparer.Ordinal));
}

/// <summary>The measured linear image the associations were taken from.</summary>
public sealed record MeasuredStellarAssociationSourceV1(
    [property: JsonRequired] Guid CaptureId,
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] string DescriptorIdentitySha256,
    [property: JsonRequired] FrameArtifactRole Role,
    [property: JsonRequired] string Variant,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] CameraPixelFormat PixelFormat,
    [property: JsonRequired] int WidthPixels,
    [property: JsonRequired] int HeightPixels);

/// <summary>The projected scene whose predictions bounded the search, with its catalog and calibration identity.</summary>
public sealed record MeasuredStellarAssociationSceneV1(
    [property: JsonRequired] string SceneIdentitySha256,
    [property: JsonRequired] ProjectedSceneKind Kind,
    [property: JsonRequired] string CatalogName,
    [property: JsonRequired] string CatalogVersion,
    [property: JsonRequired] string CatalogChecksumSha256,
    [property: JsonRequired] string CalibrationVersion,
    [property: JsonRequired] string ProjectionAlgorithmVersion,
    [property: JsonRequired] string AstronomyAlgorithmVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProjectionGeometryIdentitySha256 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? EffectiveUtc = null);

/// <summary>Image-wide measurement facts. Per-source evidence is carried by the association and detection entries.</summary>
public sealed record MeasuredStellarMeasurementV1(
    [property: JsonRequired] string AlgorithmVersion,
    [property: JsonRequired] string SettingsIdentitySha256,
    [property: JsonRequired] string Reconstruction,
    [property: JsonRequired] StellarMeasurementStatus Status,
    [property: JsonRequired] double? MedianBackground,
    [property: JsonRequired] double? MedianNoiseSigma,
    [property: JsonRequired] int CandidateCount,
    [property: JsonRequired] int DetectionCount,
    [property: JsonRequired] int ExclusionCount);

/// <summary>Declared expected-window matcher thresholds in output-image pixels and angular planet priors. Nothing is fitted.</summary>
public sealed record MeasuredStellarAssociationSettingsV1(
    [property: JsonRequired] double PredictionSigmaPixels = .5,
    [property: JsonRequired] double WindowSigmas = 3,
    [property: JsonRequired] double MaximumWindowPixels = 2,
    [property: JsonRequired] double AmbiguityRatio = 1.5,
    [property: JsonRequired] double CrowdingRadiusPixels = 4,
    [property: JsonRequired] double CrowdingMagnitudeDifference = 2.5,
    [property: JsonRequired] double OffsetRadiusPixels = 4,
    [property: JsonRequired] double EdgeMarginPixels = 6,
    [property: JsonRequired] double ExclusionMarginPixels = 1)
{
    public const double DefaultPlanetEphemerisUncertaintyArcseconds = 30;
    public const double DefaultPlanetPointingUncertaintyArcseconds = 60;

    /// <summary>Optional angular ephemeris uncertainty (1-sigma arcseconds) propagated through the delivered-image projection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlanetEphemerisUncertaintyArcseconds { get; init; }

    /// <summary>Optional angular pointing/calibration uncertainty (1-sigma arcseconds) propagated through the delivered-image projection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlanetPointingUncertaintyArcseconds { get; init; }

    internal double EffectivePlanetEphemerisUncertaintyArcseconds =>
        PlanetEphemerisUncertaintyArcseconds ?? DefaultPlanetEphemerisUncertaintyArcseconds;

    internal double EffectivePlanetPointingUncertaintyArcseconds =>
        PlanetPointingUncertaintyArcseconds ?? DefaultPlanetPointingUncertaintyArcseconds;

    public void Validate()
    {
        if (!Bounded(PredictionSigmaPixels, 1e-3, 64) || !Bounded(WindowSigmas, .5, 10) ||
            !Bounded(MaximumWindowPixels, .1, 64) || !Bounded(AmbiguityRatio, 1, 10) ||
            !Bounded(CrowdingRadiusPixels, 0, 256) || !Bounded(CrowdingMagnitudeDifference, 0, 30) ||
            !Bounded(OffsetRadiusPixels, MaximumWindowPixels, 256) || !Bounded(EdgeMarginPixels, 0, 256) ||
            !Bounded(ExclusionMarginPixels, 0, 64) ||
            PlanetEphemerisUncertaintyArcseconds is { } ephem && !Bounded(ephem, 0, 3600) ||
            PlanetPointingUncertaintyArcseconds is { } pointing && !Bounded(pointing, 0, 3600))
            throw new ArgumentOutOfRangeException(nameof(MeasuredStellarAssociationSettingsV1));
    }

    private static bool Bounded(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;
}

/// <summary>The declared rule that decides whether an association may carry a catalog label.</summary>
public sealed record StellarLabelPolicySettingsV1(
    [property: JsonRequired] double MinimumSignalToNoise = 10,
    [property: JsonRequired] bool AllowSaturated = true,
    [property: JsonRequired] bool AllowTrailed = true)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumSignalToNoise) || MinimumSignalToNoise is < 0 or > 1e6)
            throw new ArgumentOutOfRangeException(nameof(StellarLabelPolicySettingsV1));
    }
}

public sealed record MeasuredStellarAssociationMethodV1(
    [property: JsonRequired] string AlgorithmVersion,
    [property: JsonRequired] MeasuredStellarAssociationSettingsV1 Settings,
    [property: JsonRequired] string LabelPolicyVersion,
    [property: JsonRequired] StellarLabelPolicySettingsV1 LabelPolicy);

/// <summary>One catalog prediction matched one-to-one to one measured source. Pixels are output-image pixel edges.</summary>
public sealed record MeasuredStellarAssociationV1(
    [property: JsonRequired] string CatalogId,
    [property: JsonRequired] int DetectionIndex,
    [property: JsonRequired] double Magnitude,
    [property: JsonRequired] double ExpectedX,
    [property: JsonRequired] double ExpectedY,
    [property: JsonRequired] double MeasuredX,
    [property: JsonRequired] double MeasuredY,
    [property: JsonRequired] double ResidualPixels,
    [property: JsonRequired] double NormalizedResidualSquared,
    [property: JsonRequired] double Flux,
    [property: JsonRequired] double? SignalToNoise,
    [property: JsonRequired] double? CovarianceXX,
    [property: JsonRequired] double? CovarianceXY,
    [property: JsonRequired] double? CovarianceYY,
    [property: JsonRequired] bool Saturated,
    [property: JsonRequired] int SaturatedSampleCount,
    [property: JsonRequired] bool Trailed,
    [property: JsonRequired] double? TrailLengthPixels,
    [property: JsonRequired] double? TrailAngleDegrees,
    [property: JsonRequired] bool LabelEligible,
    [property: JsonRequired] string? LabelRejectionReason);

/// <summary>A star prediction that was not associated, with the first reason in the declared precedence.</summary>
public sealed record MeasuredStellarUnmatchedPredictionV1(
    [property: JsonRequired] string CatalogId,
    [property: JsonRequired] double Magnitude,
    [property: JsonRequired] double ExpectedX,
    [property: JsonRequired] double ExpectedY,
    [property: JsonRequired] string ReasonCode);

/// <summary>A measured source that no prediction claimed. It never carries a catalog identity.</summary>
public sealed record MeasuredStellarUnassociatedDetectionV1(
    [property: JsonRequired] int DetectionIndex,
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Flux,
    [property: JsonRequired] double? SignalToNoise,
    [property: JsonRequired] string ReasonCode);

/// <summary>One point-source planet ephemeris prediction matched one-to-one to one measured source.</summary>
public sealed record MeasuredPointSourcePlanetAssociationV1(
    [property: JsonRequired] string ObjectId,
    [property: JsonRequired] SolarSystemBody Body,
    [property: JsonRequired] int DetectionIndex,
    [property: JsonRequired] double Magnitude,
    [property: JsonRequired] double ExpectedX,
    [property: JsonRequired] double ExpectedY,
    [property: JsonRequired] double MeasuredX,
    [property: JsonRequired] double MeasuredY,
    [property: JsonRequired] double ResidualPixels,
    [property: JsonRequired] double NormalizedResidualSquared,
    [property: JsonRequired] double PropagatedPredictionSigmaPixels,
    [property: JsonRequired] double CombinedUncertaintyPixels,
    [property: JsonRequired] double Flux,
    [property: JsonRequired] double? SignalToNoise,
    [property: JsonRequired] double? CovarianceXX,
    [property: JsonRequired] double? CovarianceXY,
    [property: JsonRequired] double? CovarianceYY,
    [property: JsonRequired] bool Saturated,
    [property: JsonRequired] int SaturatedSampleCount,
    [property: JsonRequired] bool Trailed,
    [property: JsonRequired] double? TrailLengthPixels,
    [property: JsonRequired] double? TrailAngleDegrees,
    [property: JsonRequired] bool LabelEligible,
    [property: JsonRequired] string? LabelRejectionReason);

/// <summary>A point-source planet ephemeris prediction that was not associated, with its declared reason code.</summary>
public sealed record MeasuredPointSourcePlanetUnmatchedPredictionV1(
    [property: JsonRequired] string ObjectId,
    [property: JsonRequired] SolarSystemBody Body,
    [property: JsonRequired] double Magnitude,
    [property: JsonRequired] double ExpectedX,
    [property: JsonRequired] double ExpectedY,
    [property: JsonRequired] string ReasonCode);

/// <summary>
/// Exposure-time ephemeris, compatible mapping, angular uncertainty priors, and one-to-one measured association
/// evidence for point-source solar-system planets (Mercury through Neptune). Omitted when a projected scene contains
/// no point-source planets.
/// </summary>
public sealed record MeasuredPointSourcePlanetEvidenceV1(
    [property: JsonRequired] DateTimeOffset EffectiveUtc,
    [property: JsonRequired] string EphemerisModelVersion,
    [property: JsonRequired] string ProjectionMappingVersion,
    [property: JsonRequired] double PredictionSigmaPixels,
    [property: JsonRequired] double EphemerisUncertaintyArcseconds,
    [property: JsonRequired] double PointingUncertaintyArcseconds,
    [property: JsonRequired] IReadOnlyList<MeasuredPointSourcePlanetAssociationV1> Associations,
    [property: JsonRequired] IReadOnlyList<MeasuredPointSourcePlanetUnmatchedPredictionV1> UnmatchedPredictions);

/// <summary>
/// Compact analytical evidence binding measured image sources to the catalog predictions of one projected scene.
/// Every measured detection appears exactly once, either associated or unassociated, and every star or point-source
/// planet prediction appears exactly once, either associated or unmatched. Labels are eligible only through an association.
/// </summary>
public sealed record MeasuredStellarAssociationsV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] MeasuredStellarAssociationSourceV1 Source,
    [property: JsonRequired] MeasuredStellarAssociationSceneV1 Scene,
    [property: JsonRequired] MeasuredStellarMeasurementV1 Measurement,
    [property: JsonRequired] MeasuredStellarAssociationMethodV1 Method,
    [property: JsonRequired] IReadOnlyList<MeasuredStellarAssociationV1> Associations,
    [property: JsonRequired] IReadOnlyList<MeasuredStellarUnmatchedPredictionV1> UnmatchedPredictions,
    [property: JsonRequired] IReadOnlyList<MeasuredStellarUnassociatedDetectionV1> UnassociatedDetections,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    MeasuredPointSourcePlanetEvidenceV1? PointSourcePlanets = null)
{
    public const string V1SchemaVersion = "measured-stellar-associations-v1";
    public const string CurrentSchemaVersion = "measured-stellar-associations-v2";

    public static IReadOnlyList<string> SupportedSchemaVersions { get; } = [V1SchemaVersion, CurrentSchemaVersion];

    public static bool IsSupportedSchemaVersion(string? schemaVersion) =>
        schemaVersion is V1SchemaVersion or CurrentSchemaVersion;

    [JsonIgnore]
    public string AssociationIdentitySha256 => MeasuredStellarAssociationJson.ComputeIdentitySha256(this);
}

public readonly record struct MeasuredStellarAssociationValidationResult(bool IsValid, string? ReasonCode, string? FieldPath)
{
    public static MeasuredStellarAssociationValidationResult Success => new(true, null, null);

    public static MeasuredStellarAssociationValidationResult Failure(string reasonCode, string fieldPath) =>
        new(false, reasonCode, fieldPath);
}

public sealed record MeasuredStellarAssociationParseResult(
    MeasuredStellarAssociationsV1? Associations,
    MeasuredStellarAssociationValidationResult Validation);

/// <summary>Canonical JSON, strict parsing, structural validation and SHA-256 identity for association products.</summary>
public static class MeasuredStellarAssociationJson
{
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const int MaximumEntryCount = ProjectedSceneJson.MaximumObjectCount + StellarDetector.MaximumSupportedCandidates;
    private const string InvalidContract = "measured-associations.invalid-contract";
    private const string InvalidJson = "measured-associations.invalid-json";
    private const string UnsupportedSchema = "measured-associations.unsupported-schema";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static string ComputeIdentitySha256(MeasuredStellarAssociationsV1 associations)
    {
        ArgumentNullException.ThrowIfNull(associations);
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(associations, SerializerOptions));
    }

    /// <summary>
    /// Computes the canonical uppercase SHA-256 identity of a projected scene's static celestial-to-pixel mapping
    /// geometry (observer location, atmospheric refraction, coordinate convention, optical projection parameters
    /// excluding algorithm-label variation, and image transform). Observation/render epoch compatibility is verified
    /// separately against a physically grounded sidereal sky-motion bound.
    /// </summary>
    public static string ComputeProjectionGeometryIdentitySha256(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var o = scene.Observer;
        var r = scene.Refraction;
        var p = scene.Projection;
        var t = scene.ImageTransform;
        var canonical = new
        {
            observer = new
            {
                latitudeDegrees = NormalizeZero(o.LatitudeDegrees),
                longitudeDegrees = NormalizeZero(o.LongitudeDegrees),
                elevationMeters = NormalizeZero(o.ElevationMeters)
            },
            refraction = new
            {
                enabled = r.Enabled,
                minimumAltitudeDegrees = NormalizeZero(r.MinimumAltitudeDegrees)
            },
            coordinateConvention = scene.CoordinateConvention.ToString(),
            projection = new
            {
                model = p.Model.ToString(),
                aperture = p.Aperture.ToString(),
                calibrationVersion = p.CalibrationVersion,
                widthPixels = p.WidthPixels,
                heightPixels = p.HeightPixels,
                principalPointX = NormalizeZero(p.PrincipalPointX),
                principalPointY = NormalizeZero(p.PrincipalPointY),
                focalLengthXPixels = NormalizeZero(p.FocalLengthXPixels),
                focalLengthYPixels = NormalizeZero(p.FocalLengthYPixels),
                imageCircleRadiusPixels = p.ImageCircleRadiusPixels is { } radius ? NormalizeZero(radius) : (double?)null,
                boresightAltitudeDegrees = NormalizeZero(p.BoresightAltitudeDegrees),
                boresightAzimuthDegrees = NormalizeZero(p.BoresightAzimuthDegrees),
                rollDegrees = NormalizeZero(p.RollDegrees),
                horizontalFlip = p.HorizontalFlip,
                enforceSensorBounds = p.EnforceSensorBounds,
                radialDistortionK1 = NormalizeZero(p.RadialDistortionK1)
            },
            imageTransform = new
            {
                schemaVersion = t.SchemaVersion,
                sourceWidthPixels = t.SourceWidthPixels,
                sourceHeightPixels = t.SourceHeightPixels,
                cropX = t.CropX,
                cropY = t.CropY,
                cropWidth = t.CropWidth,
                cropHeight = t.CropHeight,
                binX = t.BinX,
                binY = t.BinY,
                horizontalMirror = t.HorizontalMirror,
                verticalMirror = t.VerticalMirror,
                rotation = t.Rotation.ToString(),
                outputWidthPixels = t.OutputWidthPixels,
                outputHeightPixels = t.OutputHeightPixels
            }
        };
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(canonical, SerializerOptions))
            .ToUpperInvariant();

        static double NormalizeZero(double value) => value == 0 ? 0 : value;
    }

    public static byte[] Serialize(MeasuredStellarAssociationsV1 associations)
    {
        ArgumentNullException.ThrowIfNull(associations);
        var validation = Validate(associations);
        if (!validation.IsValid)
            throw new ArgumentException(
                $"Measured stellar associations are invalid ({validation.ReasonCode}, {validation.FieldPath}).", nameof(associations));
        var element = JsonSerializer.SerializeToElement(associations, SerializerOptions);
        var json = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
        if (json.Length > MaximumPayloadBytes)
            throw new ArgumentException("Measured stellar associations exceed the maximum payload size.", nameof(associations));
        return json;
    }

    public static MeasuredStellarAssociationParseResult Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaximumPayloadBytes)
            return Failure(InvalidContract, "$");
        try
        {
            using var document = JsonDocument.Parse(json);
            if (HasDuplicateProperties(document.RootElement))
                return Failure(InvalidJson, "$");
            var associations = document.RootElement.Deserialize<MeasuredStellarAssociationsV1>(SerializerOptions);
            if (associations is null)
                return Failure(InvalidJson, "$");
            var validation = Validate(associations);
            return new(validation.IsValid ? associations : null, validation);
        }
        catch (JsonException)
        {
            return Failure(InvalidJson, "$");
        }
    }

    public static MeasuredStellarAssociationValidationResult Validate(MeasuredStellarAssociationsV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(value.SchemaVersion))
            return Invalid(UnsupportedSchema, nameof(value.SchemaVersion));
        var isV1 = string.Equals(value.SchemaVersion, MeasuredStellarAssociationsV1.V1SchemaVersion, StringComparison.Ordinal);
        var source = value.Source;
        if (source is null || source.CaptureId == Guid.Empty || source.ArtifactId == Guid.Empty ||
            !Sha256(source.DescriptorIdentitySha256) || source.Role != FrameArtifactRole.Raw ||
            !Text(source.Variant) || !Sha256(source.RecipeIdentitySha256) ||
            source.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            source.WidthPixels is < 1 or > ProjectedSceneJson.MaximumDimensionPixels ||
            source.HeightPixels is < 1 or > ProjectedSceneJson.MaximumDimensionPixels)
            return Invalid(InvalidContract, nameof(value.Source));
        var scene = value.Scene;
        if (scene is null || !Sha256(scene.SceneIdentitySha256) || !Enum.IsDefined(scene.Kind) ||
            !Text(scene.CatalogName) || !Text(scene.CatalogVersion) || !Sha256(scene.CatalogChecksumSha256) ||
            !Text(scene.CalibrationVersion) || !Text(scene.ProjectionAlgorithmVersion) || !Text(scene.AstronomyAlgorithmVersion) ||
            (isV1 && (scene.ProjectionGeometryIdentitySha256 is not null || scene.EffectiveUtc is not null)) ||
            (!isV1 && (!Sha256(scene.ProjectionGeometryIdentitySha256) || scene.EffectiveUtc is not { } sceneEffectiveUtc || sceneEffectiveUtc == default)))
            return Invalid(InvalidContract, nameof(value.Scene));
        var measurement = value.Measurement;
        if (measurement is null || !Text(measurement.AlgorithmVersion) || !Sha256(measurement.SettingsIdentitySha256) ||
            !Text(measurement.Reconstruction) || !Enum.IsDefined(measurement.Status) ||
            measurement.MedianBackground is { } background && !double.IsFinite(background) ||
            measurement.MedianNoiseSigma is { } noise && (!double.IsFinite(noise) || noise < 0) ||
            measurement.CandidateCount < 0 || measurement.DetectionCount < 0 || measurement.ExclusionCount < 0 ||
            measurement.DetectionCount + measurement.ExclusionCount > measurement.CandidateCount &&
                measurement.Status == StellarMeasurementStatus.Completed ||
            measurement.Status == StellarMeasurementStatus.CandidateBudgetExceeded &&
                (measurement.DetectionCount != 0 || measurement.ExclusionCount != 0))
            return Invalid(InvalidContract, nameof(value.Measurement));
        var method = value.Method;
        if (method is null || !Text(method.AlgorithmVersion) || !Text(method.LabelPolicyVersion) ||
            method.Settings is null || method.LabelPolicy is null)
            return Invalid(InvalidContract, nameof(value.Method));
        try
        {
            method.Settings.Validate();
            method.LabelPolicy.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid(InvalidContract, nameof(value.Method));
        }
        var planets = value.PointSourcePlanets;
        if (isV1 && (planets is not null ||
            method.Settings.PlanetEphemerisUncertaintyArcseconds is not null ||
            method.Settings.PlanetPointingUncertaintyArcseconds is not null))
            return Invalid(InvalidContract, nameof(value.PointSourcePlanets));
        var planetAssociationCount = planets?.Associations?.Count ?? 0;
        var planetUnmatchedCount = planets?.UnmatchedPredictions?.Count ?? 0;
        if (value.Associations is null || value.UnmatchedPredictions is null || value.UnassociatedDetections is null ||
            planets is not null && (planets.Associations is null || planets.UnmatchedPredictions is null ||
                planetAssociationCount + planetUnmatchedCount == 0 || planets.EffectiveUtc == default ||
                (scene.EffectiveUtc is { } expectedEpoch && planets.EffectiveUtc != expectedEpoch) ||
                !Text(planets.EphemerisModelVersion) || !Text(planets.ProjectionMappingVersion) ||
                !Finite(planets.PredictionSigmaPixels) ||
                Math.Abs(planets.PredictionSigmaPixels - method.Settings.PredictionSigmaPixels) > 1e-6 ||
                !Finite(planets.EphemerisUncertaintyArcseconds) ||
                Math.Abs(planets.EphemerisUncertaintyArcseconds - method.Settings.EffectivePlanetEphemerisUncertaintyArcseconds) > 1e-6 ||
                !Finite(planets.PointingUncertaintyArcseconds) ||
                Math.Abs(planets.PointingUncertaintyArcseconds - method.Settings.EffectivePlanetPointingUncertaintyArcseconds) > 1e-6) ||
            value.Associations.Count + value.UnmatchedPredictions.Count + planetAssociationCount + planetUnmatchedCount >
                ProjectedSceneJson.MaximumObjectCount ||
            value.Associations.Count + planetAssociationCount + value.UnassociatedDetections.Count != measurement.DetectionCount ||
            measurement.DetectionCount > StellarDetector.MaximumSupportedCandidates)
            return Invalid(InvalidContract, "entries");

        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        var detectionIndexes = new HashSet<int>();
        for (var index = 0; index < value.Associations.Count; index++)
        {
            var item = value.Associations[index];
            if (item is null || !CatalogId(item.CatalogId) || !catalogIds.Add(item.CatalogId) ||
                item.DetectionIndex < 0 || item.DetectionIndex >= measurement.DetectionCount ||
                !detectionIndexes.Add(item.DetectionIndex) || !Finite(item.Magnitude) ||
                !Pixel(item.ExpectedX, source.WidthPixels) || !Pixel(item.ExpectedY, source.HeightPixels) ||
                !Pixel(item.MeasuredX, source.WidthPixels) || !Pixel(item.MeasuredY, source.HeightPixels) ||
                !NonNegative(item.ResidualPixels) || item.ResidualPixels > method.Settings.MaximumWindowPixels + 1e-6 ||
                !NonNegative(item.NormalizedResidualSquared) ||
                item.NormalizedResidualSquared > method.Settings.WindowSigmas * method.Settings.WindowSigmas + 1e-6 ||
                !Finite(item.Flux) || item.SignalToNoise is { } snr && !Finite(snr) ||
                (item.CovarianceXX is null) != (item.CovarianceYY is null) ||
                (item.CovarianceXX is null) != (item.CovarianceXY is null) ||
                item.CovarianceXX is { } xx && (!NonNegative(xx) || !NonNegative(item.CovarianceYY!.Value) ||
                    !Finite(item.CovarianceXY!.Value)) ||
                item.SaturatedSampleCount < 0 || item.Saturated != item.SaturatedSampleCount > 0 ||
                item.Trailed != item.TrailAngleDegrees is not null ||
                item.TrailLengthPixels is { } length && !NonNegative(length) ||
                item.TrailAngleDegrees is { } angle && (!Finite(angle) || angle is < -90 or >= 90) ||
                item.LabelEligible != (item.LabelRejectionReason is null) ||
                item.LabelRejectionReason is { } rejection &&
                    !MeasuredStellarAssociationReasonCodes.LabelRejectionReasons.Contains(rejection) ||
                item.LabelEligible != StellarLabelPolicy.IsEligible(item, method.LabelPolicy))
                return Invalid(InvalidContract, $"associations[{index}]");
            if (index > 0 && string.CompareOrdinal(value.Associations[index - 1].CatalogId, item.CatalogId) >= 0)
                return Invalid(InvalidContract, $"associations[{index}]");
        }
        for (var index = 0; index < value.UnmatchedPredictions.Count; index++)
        {
            var item = value.UnmatchedPredictions[index];
            if (item is null || !CatalogId(item.CatalogId) || !catalogIds.Add(item.CatalogId) || !Finite(item.Magnitude) ||
                !Finite(item.ExpectedX) || !Finite(item.ExpectedY) ||
                !MeasuredStellarAssociationReasonCodes.IsPredictionReason(item.ReasonCode) ||
                index > 0 && string.CompareOrdinal(value.UnmatchedPredictions[index - 1].CatalogId, item.CatalogId) >= 0)
                return Invalid(InvalidContract, $"unmatchedPredictions[{index}]");
        }
        if (planets is not null)
        {
            var planetIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < planets.Associations.Count; index++)
            {
                var item = planets.Associations[index];
                if (item is null || !IsPointSourcePlanetBody(item.Body) ||
                    !string.Equals(item.ObjectId, PointSourcePlanetId(item.Body), StringComparison.Ordinal) ||
                    !planetIds.Add(item.ObjectId) ||
                    item.DetectionIndex < 0 || item.DetectionIndex >= measurement.DetectionCount ||
                    !detectionIndexes.Add(item.DetectionIndex) || !Finite(item.Magnitude) ||
                    !Pixel(item.ExpectedX, source.WidthPixels) || !Pixel(item.ExpectedY, source.HeightPixels) ||
                    !Pixel(item.MeasuredX, source.WidthPixels) || !Pixel(item.MeasuredY, source.HeightPixels) ||
                    !NonNegative(item.ResidualPixels) ||
                    !Finite(item.PropagatedPredictionSigmaPixels) ||
                    item.PropagatedPredictionSigmaPixels < method.Settings.PredictionSigmaPixels - 1e-6 ||
                    item.ResidualPixels > PlanetMaximumWindow(method.Settings, item.PropagatedPredictionSigmaPixels) + 1e-6 ||
                    !NonNegative(item.NormalizedResidualSquared) ||
                    item.NormalizedResidualSquared > method.Settings.WindowSigmas * method.Settings.WindowSigmas + 1e-6 ||
                    !Finite(item.CombinedUncertaintyPixels) ||
                    item.CombinedUncertaintyPixels < item.PropagatedPredictionSigmaPixels - 1e-6 ||
                    !Finite(item.Flux) || item.SignalToNoise is { } snr && !Finite(snr) ||
                    (item.CovarianceXX is null) != (item.CovarianceYY is null) ||
                    (item.CovarianceXX is null) != (item.CovarianceXY is null) ||
                    item.CovarianceXX is { } xx && (!NonNegative(xx) || !NonNegative(item.CovarianceYY!.Value) ||
                        !Finite(item.CovarianceXY!.Value)) ||
                    item.SaturatedSampleCount < 0 || item.Saturated != item.SaturatedSampleCount > 0 ||
                    item.Trailed != item.TrailAngleDegrees is not null ||
                    item.TrailLengthPixels is { } length && !NonNegative(length) ||
                    item.TrailAngleDegrees is { } angle && (!Finite(angle) || angle is < -90 or >= 90) ||
                    item.LabelEligible != (item.LabelRejectionReason is null) ||
                    item.LabelRejectionReason is { } rejection &&
                        !MeasuredStellarAssociationReasonCodes.LabelRejectionReasons.Contains(rejection) ||
                    item.LabelEligible != StellarLabelPolicy.IsEligible(item, method.LabelPolicy))
                    return Invalid(InvalidContract, $"pointSourcePlanets.associations[{index}]");
                if (index > 0 && string.CompareOrdinal(planets.Associations[index - 1].ObjectId, item.ObjectId) >= 0)
                    return Invalid(InvalidContract, $"pointSourcePlanets.associations[{index}]");
            }
            for (var index = 0; index < planets.UnmatchedPredictions.Count; index++)
            {
                var item = planets.UnmatchedPredictions[index];
                if (item is null || !IsPointSourcePlanetBody(item.Body) ||
                    !string.Equals(item.ObjectId, PointSourcePlanetId(item.Body), StringComparison.Ordinal) ||
                    !planetIds.Add(item.ObjectId) || !Finite(item.Magnitude) ||
                    !Finite(item.ExpectedX) || !Finite(item.ExpectedY) ||
                    !MeasuredStellarAssociationReasonCodes.IsPredictionReason(item.ReasonCode) ||
                    index > 0 && string.CompareOrdinal(planets.UnmatchedPredictions[index - 1].ObjectId, item.ObjectId) >= 0)
                    return Invalid(InvalidContract, $"pointSourcePlanets.unmatchedPredictions[{index}]");
            }
        }
        for (var index = 0; index < value.UnassociatedDetections.Count; index++)
        {
            var item = value.UnassociatedDetections[index];
            if (item is null || item.DetectionIndex < 0 || item.DetectionIndex >= measurement.DetectionCount ||
                !detectionIndexes.Add(item.DetectionIndex) ||
                !Pixel(item.X, source.WidthPixels) || !Pixel(item.Y, source.HeightPixels) || !Finite(item.Flux) ||
                item.SignalToNoise is { } snr && !Finite(snr) ||
                !MeasuredStellarAssociationReasonCodes.DetectionReasons.Contains(item.ReasonCode) ||
                index > 0 && value.UnassociatedDetections[index - 1].DetectionIndex >= item.DetectionIndex)
                return Invalid(InvalidContract, $"unassociatedDetections[{index}]");
        }
        if (measurement.Status != StellarMeasurementStatus.Completed && (value.Associations.Count != 0 ||
            planetAssociationCount != 0 ||
            value.UnmatchedPredictions.Any(static item => item.ReasonCode is not
                (MeasuredStellarAssociationReasonCodes.MeasurementIncomplete or MeasuredStellarAssociationReasonCodes.OutsideAperture)) ||
            planets?.UnmatchedPredictions.Any(static item => item.ReasonCode is not
                (MeasuredStellarAssociationReasonCodes.MeasurementIncomplete or MeasuredStellarAssociationReasonCodes.OutsideAperture)) == true))
            return Invalid(InvalidContract, nameof(value.Measurement.Status));
        return MeasuredStellarAssociationValidationResult.Success;
    }

    internal const double MaximumPlanetWindowPixels = 64;

    internal static double PlanetMaximumWindow(MeasuredStellarAssociationSettingsV1 settings, double propagatedSigmaPixels) =>
        Math.Max(settings.MaximumWindowPixels, Math.Min(MaximumPlanetWindowPixels, settings.WindowSigmas * propagatedSigmaPixels));

    internal static bool IsPointSourcePlanetBody(SolarSystemBody body) => body is
        SolarSystemBody.Mercury or SolarSystemBody.Venus or SolarSystemBody.Mars or
        SolarSystemBody.Jupiter or SolarSystemBody.Saturn or SolarSystemBody.Uranus or SolarSystemBody.Neptune;

    internal static string PointSourcePlanetId(SolarSystemBody body) => $"solar-system:{body}";

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static bool CatalogId(string? value) => Text(value) && !value!.StartsWith("solar-system:", StringComparison.Ordinal);

    private static bool Finite(double value) => double.IsFinite(value);

    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static bool Pixel(double value, int extent) => double.IsFinite(value) && value >= 0 && value <= extent;

    private static bool Sha256(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                    return true;
            }
        }
        return false;
    }

    private static MeasuredStellarAssociationParseResult Failure(string reasonCode, string path) => new(null, Invalid(reasonCode, path));

    private static MeasuredStellarAssociationValidationResult Invalid(string reasonCode, string path) =>
        MeasuredStellarAssociationValidationResult.Failure(reasonCode, path);

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
