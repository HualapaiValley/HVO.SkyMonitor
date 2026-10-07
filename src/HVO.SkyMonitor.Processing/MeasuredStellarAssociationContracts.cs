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
    [property: JsonRequired] string AstronomyAlgorithmVersion);

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

/// <summary>Declared expected-window matcher thresholds in output-image pixels. Nothing is fitted.</summary>
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
    public void Validate()
    {
        if (!Bounded(PredictionSigmaPixels, 1e-3, 64) || !Bounded(WindowSigmas, .5, 10) ||
            !Bounded(MaximumWindowPixels, .1, 64) || !Bounded(AmbiguityRatio, 1, 10) ||
            !Bounded(CrowdingRadiusPixels, 0, 256) || !Bounded(CrowdingMagnitudeDifference, 0, 30) ||
            !Bounded(OffsetRadiusPixels, MaximumWindowPixels, 256) || !Bounded(EdgeMarginPixels, 0, 256) ||
            !Bounded(ExclusionMarginPixels, 0, 64))
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

/// <summary>
/// Compact analytical evidence binding measured image sources to the catalog predictions of one projected scene.
/// Every measured detection appears exactly once, either associated or unassociated, and every star prediction
/// appears exactly once, either associated or unmatched. Labels are eligible only through an association.
/// </summary>
public sealed record MeasuredStellarAssociationsV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] MeasuredStellarAssociationSourceV1 Source,
    [property: JsonRequired] MeasuredStellarAssociationSceneV1 Scene,
    [property: JsonRequired] MeasuredStellarMeasurementV1 Measurement,
    [property: JsonRequired] MeasuredStellarAssociationMethodV1 Method,
    [property: JsonRequired] IReadOnlyList<MeasuredStellarAssociationV1> Associations,
    [property: JsonRequired] IReadOnlyList<MeasuredStellarUnmatchedPredictionV1> UnmatchedPredictions,
    [property: JsonRequired] IReadOnlyList<MeasuredStellarUnassociatedDetectionV1> UnassociatedDetections)
{
    public const string CurrentSchemaVersion = "measured-stellar-associations-v1";

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
        if (!string.Equals(value.SchemaVersion, MeasuredStellarAssociationsV1.CurrentSchemaVersion, StringComparison.Ordinal))
            return Invalid(UnsupportedSchema, nameof(value.SchemaVersion));
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
            !Text(scene.CalibrationVersion) || !Text(scene.ProjectionAlgorithmVersion) || !Text(scene.AstronomyAlgorithmVersion))
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
        if (value.Associations is null || value.UnmatchedPredictions is null || value.UnassociatedDetections is null ||
            value.Associations.Count + value.UnmatchedPredictions.Count > ProjectedSceneJson.MaximumObjectCount ||
            value.Associations.Count + value.UnassociatedDetections.Count != measurement.DetectionCount ||
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
            value.UnmatchedPredictions.Any(static item => item.ReasonCode is not
                (MeasuredStellarAssociationReasonCodes.MeasurementIncomplete or MeasuredStellarAssociationReasonCodes.OutsideAperture))))
            return Invalid(InvalidContract, nameof(value.Measurement.Status));
        return MeasuredStellarAssociationValidationResult.Success;
    }

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
