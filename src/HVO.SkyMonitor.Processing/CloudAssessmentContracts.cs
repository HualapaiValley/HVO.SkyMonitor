using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum CloudAssessmentStatus
{
    Quantified,
    InsufficientEvidence,
    Contaminated
}

public enum CloudAssessmentQuality
{
    Good,
    Degraded,
    Unusable
}

public static class CloudAssessmentReasonCodes
{
    public const string MissingClearReference = "cloud.missing-clear-reference";
    public const string IncompatibleClearReference = "cloud.incompatible-clear-reference";
    public const string MissingCalibration = "cloud.missing-calibration";
    public const string InsufficientValidSupport = "cloud.insufficient-valid-support";
    public const string SaturationContamination = "cloud.saturation-contamination";
    public const string PrecipitationContamination = "cloud.precipitation-contamination";
    public const string Daylight = "cloud.daylight";
    public const string Twilight = "cloud.twilight";
    public const string EnvironmentMissing = "cloud.environment-missing";
    public const string EnvironmentStale = "cloud.environment-stale";
}

public sealed record CloudAssessmentSourceV1(
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] FrameArtifactRole Role,
    [property: JsonRequired] string Variant,
    [property: JsonRequired] string RecipeIdentitySha256);

public sealed record CloudAssessmentCalibrationV1(
    [property: JsonRequired] ushort BlackLevel,
    [property: JsonRequired] ushort WhiteLevel,
    [property: JsonRequired] ushort SaturationLevel,
    [property: JsonRequired] string CalibrationIdentity,
    [property: JsonRequired] string MaskIdentity,
    [property: JsonRequired] string SensorIdentity,
    [property: JsonRequired] string ProcessingProfileIdentity);

public sealed record CloudAssessmentEnvironmentV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CaptureSolarRegime? SolarRegime,
    [property: JsonRequired] EnvironmentalObservationMatchStatus PrecipitationStatus,
    [property: JsonRequired] Guid? PrecipitationObservationId,
    [property: JsonRequired] string? PrecipitationContentSha256,
    [property: JsonRequired] bool PrecipitationDetected,
    [property: JsonRequired] string? InputIdentitySha256 = null)
{
    public const string CurrentSchemaVersion = "cloud-assessment-environment-v1";
}

public sealed record CloudAssessmentGridV1(
    [property: JsonRequired] int Columns,
    [property: JsonRequired] int Rows,
    [property: JsonRequired] int TransmissionThresholdMillionths,
    [property: JsonRequired] int ValidRegionCount,
    [property: JsonRequired] int CloudyRegionCount,
    [property: JsonRequired] long ValidSampleCount,
    [property: JsonRequired] long CloudySampleCount);

public sealed record CloudAssessmentRegionV1(
    [property: JsonRequired] int Column,
    [property: JsonRequired] int Row,
    [property: JsonRequired] int X,
    [property: JsonRequired] int Y,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] long ConsideredSampleCount,
    [property: JsonRequired] long AcceptedSampleCount,
    [property: JsonRequired] long SaturatedSampleCount,
    [property: JsonRequired] int? TransmissionMillionths,
    [property: JsonRequired] bool IsCloudy);

public sealed record CloudAssessmentMaskV1(
    [property: JsonRequired] string Encoding,
    [property: JsonRequired] int Width,
    [property: JsonRequired] int Height,
    [property: JsonRequired] ReadOnlyMemory<byte> Bits)
{
    public const string RowMajorLsbFirst = "row-major-lsb-first";
}

/// <summary>Canonical image-derived cloud evidence with explicit source and environmental lineage.</summary>
public sealed record CloudAssessmentV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CloudAssessmentStatus Status,
    [property: JsonRequired] CloudAssessmentQuality Quality,
    [property: JsonRequired] IReadOnlyList<string> ReasonCodes,
    [property: JsonRequired] int? CoverageMillionths,
    [property: JsonRequired] int ConfidenceMillionths,
    [property: JsonRequired] CloudAssessmentGridV1 Grid,
    [property: JsonRequired] IReadOnlyList<CloudAssessmentRegionV1> Regions,
    [property: JsonRequired] CloudAssessmentMaskV1? Mask,
    [property: JsonRequired] CloudAssessmentSourceV1 Current,
    [property: JsonRequired] CloudAssessmentSourceV1? ClearReference,
    [property: JsonRequired] CloudAssessmentCalibrationV1? Calibration,
    [property: JsonRequired] CloudAssessmentEnvironmentV1 Environment,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms)
{
    public const string CurrentSchemaVersion = "cloud-assessment-v1";
}

public readonly record struct CloudAssessmentValidationResult(
    bool IsValid,
    string? ReasonCode,
    string? FieldPath)
{
    public static CloudAssessmentValidationResult Success => new(true, null, null);

    public static CloudAssessmentValidationResult Failure(string reasonCode, string fieldPath)
        => new(false, reasonCode, fieldPath);
}

public sealed record CloudAssessmentParseResult(
    CloudAssessmentV1? Assessment,
    CloudAssessmentValidationResult Validation);

public static class CloudAssessmentJson
{
    public const int MaximumPayloadBytes = 1024 * 1024;
    private const int OneMillion = 1_000_000;
    private const string InvalidContract = "cloud.invalid-contract";
    private const string InvalidJson = "cloud.invalid-json";
    private const string UnsupportedSchema = "cloud.unsupported-schema";
    private static readonly HashSet<string> KnownReasonCodes = new(StringComparer.Ordinal)
    {
        CloudAssessmentReasonCodes.MissingClearReference,
        CloudAssessmentReasonCodes.IncompatibleClearReference,
        CloudAssessmentReasonCodes.MissingCalibration,
        CloudAssessmentReasonCodes.InsufficientValidSupport,
        CloudAssessmentReasonCodes.SaturationContamination,
        CloudAssessmentReasonCodes.PrecipitationContamination,
        CloudAssessmentReasonCodes.Daylight,
        CloudAssessmentReasonCodes.Twilight,
        CloudAssessmentReasonCodes.EnvironmentMissing,
        CloudAssessmentReasonCodes.EnvironmentStale
    };
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(CloudAssessmentV1 assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var validation = Validate(assessment);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Cloud assessment is invalid ({validation.ReasonCode}, {validation.FieldPath}).",
                nameof(assessment));
        }
        var element = JsonSerializer.SerializeToElement(assessment, SerializerOptions);
        var json = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(element));
        if (json.Length > MaximumPayloadBytes)
        {
            throw new ArgumentException("Cloud assessment exceeds the maximum payload size.", nameof(assessment));
        }
        return json;
    }

    public static CloudAssessmentParseResult Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaximumPayloadBytes)
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
            var assessment = document.RootElement.Deserialize<CloudAssessmentV1>(SerializerOptions);
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

    public static CloudAssessmentValidationResult Validate(CloudAssessmentV1 assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (!string.Equals(assessment.SchemaVersion, CloudAssessmentV1.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            return Invalid(UnsupportedSchema, nameof(assessment.SchemaVersion));
        }
        if (!Enum.IsDefined(assessment.Status) || !Enum.IsDefined(assessment.Quality) ||
            assessment.ReasonCodes is null || assessment.ReasonCodes.Any(string.IsNullOrWhiteSpace) ||
            assessment.ReasonCodes.Any(reason => !KnownReasonCodes.Contains(reason)) ||
            assessment.ReasonCodes.Distinct(StringComparer.Ordinal).Count() != assessment.ReasonCodes.Count ||
            !Millionths(assessment.ConfidenceMillionths) ||
            assessment.CoverageMillionths is { } coverage && !Millionths(coverage))
        {
            return Invalid(InvalidContract, "summary");
        }
        if (assessment.Status == CloudAssessmentStatus.Quantified
            ? assessment.CoverageMillionths is null || assessment.ClearReference is null || assessment.Quality == CloudAssessmentQuality.Unusable
            : assessment.CoverageMillionths is not null || assessment.Quality != CloudAssessmentQuality.Unusable)
        {
            return Invalid(InvalidContract, nameof(assessment.CoverageMillionths));
        }
        if (assessment.Grid is null || assessment.Grid.Columns < 1 || assessment.Grid.Rows < 1 ||
            assessment.Grid.TransmissionThresholdMillionths is <= 0 or >= OneMillion ||
            assessment.Grid.ValidRegionCount is < 0 ||
            assessment.Grid.CloudyRegionCount is < 0 ||
            assessment.Grid.CloudyRegionCount > assessment.Grid.ValidRegionCount ||
            assessment.Grid.ValidSampleCount < 0 || assessment.Grid.CloudySampleCount < 0 ||
            assessment.Grid.CloudySampleCount > assessment.Grid.ValidSampleCount)
        {
            return Invalid(InvalidContract, nameof(assessment.Grid));
        }
        int regionCount;
        try
        {
            regionCount = checked(assessment.Grid.Columns * assessment.Grid.Rows);
        }
        catch (OverflowException)
        {
            return Invalid(InvalidContract, nameof(assessment.Grid));
        }
        if (regionCount > Linear16CloudTransmissionEstimator.MaximumTileCount ||
            assessment.Regions is null || assessment.Regions.Count != regionCount)
        {
            return Invalid(InvalidContract, nameof(assessment.Regions));
        }
        var validRegions = 0;
        var cloudyRegions = 0;
        long validSamples = 0;
        long cloudySamples = 0;
        for (var index = 0; index < assessment.Regions.Count; index++)
        {
            var region = assessment.Regions[index];
            if (region is null || region.Column != index % assessment.Grid.Columns ||
                region.Row != index / assessment.Grid.Columns || region.X < 0 || region.Y < 0 ||
                region.Width < 1 || region.Height < 1 || region.ConsideredSampleCount < 0 ||
                region.AcceptedSampleCount < 0 || region.SaturatedSampleCount < 0 ||
                region.AcceptedSampleCount + region.SaturatedSampleCount > region.ConsideredSampleCount ||
                region.TransmissionMillionths is { } transmission && !Millionths(transmission) ||
                region.IsCloudy != (region.TransmissionMillionths is { } measured &&
                    measured < assessment.Grid.TransmissionThresholdMillionths))
            {
                return Invalid(InvalidContract, $"regions[{index}]");
            }
            validRegions += region.TransmissionMillionths.HasValue ? 1 : 0;
            cloudyRegions += region.IsCloudy ? 1 : 0;
            validSamples += region.TransmissionMillionths.HasValue ? region.AcceptedSampleCount : 0;
            cloudySamples += region.IsCloudy ? region.AcceptedSampleCount : 0;
        }
        if (validRegions != assessment.Grid.ValidRegionCount || cloudyRegions != assessment.Grid.CloudyRegionCount ||
            validSamples != assessment.Grid.ValidSampleCount || cloudySamples != assessment.Grid.CloudySampleCount)
        {
            return Invalid(InvalidContract, nameof(assessment.Grid));
        }
        var expectedCoverage = validSamples == 0
            ? (int?)null
            : checked((int)(((Int128)cloudySamples * OneMillion + validSamples / 2) / validSamples));
        if (assessment.Status == CloudAssessmentStatus.Quantified &&
            (assessment.Calibration is null || assessment.CoverageMillionths != expectedCoverage) ||
            assessment.Status != CloudAssessmentStatus.Quantified && assessment.ReasonCodes.Count == 0)
        {
            return Invalid(InvalidContract, nameof(assessment.CoverageMillionths));
        }
        var maskValidation = ValidateMask(assessment.Mask, assessment.Grid, assessment.Regions);
        if (!maskValidation.IsValid)
        {
            return maskValidation;
        }
        if (!ValidSource(assessment.Current) || assessment.ClearReference is not null && !ValidSource(assessment.ClearReference) ||
            assessment.Current.ArtifactId == assessment.ClearReference?.ArtifactId)
        {
            return Invalid(InvalidContract, "sources");
        }
        if (assessment.Calibration is { } calibration &&
            (calibration.WhiteLevel <= calibration.BlackLevel ||
             calibration.SaturationLevel <= calibration.BlackLevel ||
             calibration.SaturationLevel > calibration.WhiteLevel ||
             string.IsNullOrWhiteSpace(calibration.CalibrationIdentity) ||
             string.IsNullOrWhiteSpace(calibration.MaskIdentity) ||
             string.IsNullOrWhiteSpace(calibration.SensorIdentity) ||
             string.IsNullOrWhiteSpace(calibration.ProcessingProfileIdentity)))
        {
            return Invalid(InvalidContract, nameof(assessment.Calibration));
        }
        var environment = assessment.Environment;
        if (environment is null ||
            !string.Equals(environment.SchemaVersion, CloudAssessmentEnvironmentV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            !Enum.IsDefined(environment.PrecipitationStatus))
        {
            return Invalid(InvalidContract, nameof(assessment.Environment));
        }
        var precipitationIsInvalid = environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Missing
            ? environment.PrecipitationObservationId is not null ||
              environment.PrecipitationContentSha256 is not null || environment.PrecipitationDetected ||
              environment.InputIdentitySha256 is not null && !Sha256(environment.InputIdentitySha256)
            : environment.PrecipitationObservationId is null ||
              !Sha256(environment.PrecipitationContentSha256) || !Sha256(environment.InputIdentitySha256);
        if (precipitationIsInvalid)
        {
            return Invalid(InvalidContract, nameof(assessment.Environment));
        }
        var hasMissingReference = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.MissingClearReference);
        var hasIncompatibleReference = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.IncompatibleClearReference);
        var hasMissingCalibration = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.MissingCalibration);
        var hasInsufficientSupport = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.InsufficientValidSupport);
        var hasSaturation = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.SaturationContamination);
        var hasPrecipitation = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.PrecipitationContamination);
        var hasDaylight = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.Daylight);
        var hasTwilight = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.Twilight);
        var hasEnvironmentMissing = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.EnvironmentMissing);
        var hasEnvironmentStale = assessment.ReasonCodes.Contains(CloudAssessmentReasonCodes.EnvironmentStale);
        var unusableReason = hasMissingReference || hasIncompatibleReference || hasMissingCalibration ||
            hasInsufficientSupport || hasSaturation || hasPrecipitation || hasDaylight;
        if (hasMissingReference != (assessment.ClearReference is null) ||
            hasIncompatibleReference && assessment.ClearReference is null ||
            hasMissingCalibration != (assessment.Calibration is null) ||
            assessment.Status == CloudAssessmentStatus.Quantified && unusableReason ||
            assessment.Status == CloudAssessmentStatus.Contaminated != (hasSaturation || hasPrecipitation) ||
            assessment.Status == CloudAssessmentStatus.InsufficientEvidence && !unusableReason ||
            assessment.Quality == CloudAssessmentQuality.Good &&
                (assessment.Status != CloudAssessmentStatus.Quantified || hasTwilight || hasEnvironmentMissing || hasEnvironmentStale) ||
            assessment.Quality == CloudAssessmentQuality.Degraded && assessment.Status != CloudAssessmentStatus.Quantified ||
            hasPrecipitation != environment.PrecipitationDetected ||
            hasDaylight != (environment.SolarRegime == CaptureSolarRegime.Day) ||
            hasTwilight != (environment.SolarRegime == CaptureSolarRegime.Twilight) ||
            hasEnvironmentStale != (environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Stale) ||
            hasEnvironmentMissing != (environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Missing ||
                environment.SolarRegime is null))
        {
            return Invalid(InvalidContract, nameof(assessment.ReasonCodes));
        }
        if (!Sha256(assessment.RecipeIdentitySha256) || assessment.Algorithms is null ||
            assessment.Algorithms.Count == 0 || assessment.Algorithms.Any(static algorithm =>
                algorithm is null || string.IsNullOrWhiteSpace(algorithm.Name) || string.IsNullOrWhiteSpace(algorithm.Version)))
        {
            return Invalid(InvalidContract, "provenance");
        }
        return CloudAssessmentValidationResult.Success;
    }

    private static CloudAssessmentValidationResult ValidateMask(
        CloudAssessmentMaskV1? mask,
        CloudAssessmentGridV1 grid,
        IReadOnlyList<CloudAssessmentRegionV1> regions)
    {
        if (mask is null)
        {
            return CloudAssessmentValidationResult.Success;
        }
        var expectedBytes = (regions.Count + 7) / 8;
        if (!string.Equals(mask.Encoding, CloudAssessmentMaskV1.RowMajorLsbFirst, StringComparison.Ordinal) ||
            mask.Width != grid.Columns || mask.Height != grid.Rows || mask.Bits.Length != expectedBytes)
        {
            return Invalid(InvalidContract, nameof(CloudAssessmentV1.Mask));
        }
        var bits = mask.Bits.Span;
        for (var index = 0; index < regions.Count; index++)
        {
            var set = (bits[index >> 3] & 1 << (index & 7)) != 0;
            if (set != regions[index].IsCloudy)
            {
                return Invalid(InvalidContract, nameof(CloudAssessmentV1.Mask));
            }
        }
        if ((regions.Count & 7) != 0 &&
            (bits[^1] & ~((1 << (regions.Count & 7)) - 1)) != 0)
        {
            return Invalid(InvalidContract, nameof(CloudAssessmentV1.Mask));
        }
        return CloudAssessmentValidationResult.Success;
    }

    private static bool ValidSource(CloudAssessmentSourceV1? source)
        => source is not null && source.ArtifactId != Guid.Empty && Enum.IsDefined(source.Role) &&
           !string.IsNullOrWhiteSpace(source.Variant) && Sha256(source.RecipeIdentitySha256);

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

    private static CloudAssessmentParseResult Failure(string reasonCode, string path)
        => new(null, Invalid(reasonCode, path));

    private static CloudAssessmentValidationResult Invalid(string reasonCode, string path)
        => CloudAssessmentValidationResult.Failure(reasonCode, path);

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
