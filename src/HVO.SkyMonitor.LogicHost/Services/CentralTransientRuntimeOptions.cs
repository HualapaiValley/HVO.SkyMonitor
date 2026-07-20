using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralTransientOptions : IValidatableObject
{
    public const string SectionName = "CentralTransient";

    public TransientDetectorExecutionMode Mode { get; init; } = TransientDetectorExecutionMode.Off;
    public FrameArtifactRole SourceRole { get; init; } = FrameArtifactRole.Raw;
    public TimeSpan WindowTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumAdjacentStartInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumAssociationTimeGap { get; init; } = TimeSpan.FromSeconds(5);
    public double MaximumAssociationEndpointGapPixels { get; init; } = 8;
    public double MinimumAssociationAlignmentCosine { get; init; } = 0.85;
    public double StarMaximumMagnitude { get; init; } = 6.5;
    public int StarMaximumResults { get; init; } = 2_000;
    public double StarSourceSupportRadiusPixels { get; init; } = 1;
    public CentralTransientExtractionOptions Extraction { get; init; } = new();
    public CentralTransientAssessmentOptions Assessment { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Mode is not (TransientDetectorExecutionMode.Off or TransientDetectorExecutionMode.Central))
        {
            yield return new ValidationResult("LogicHost transient mode must be Off or Central.", [nameof(Mode)]);
        }
        if (SourceRole is not (FrameArtifactRole.Raw or FrameArtifactRole.Calibrated))
        {
            yield return new ValidationResult("The transient source role must be Raw or Calibrated.", [nameof(SourceRole)]);
        }
        if (WindowTimeout < TimeSpan.FromSeconds(1) || WindowTimeout > TimeSpan.FromHours(24) ||
            MaximumAdjacentStartInterval <= TimeSpan.Zero || MaximumAdjacentStartInterval > TimeSpan.FromHours(1) ||
            MaximumAssociationTimeGap < TimeSpan.Zero || MaximumAssociationTimeGap > MaximumAdjacentStartInterval ||
            !double.IsFinite(MaximumAssociationEndpointGapPixels) || MaximumAssociationEndpointGapPixels <= 0 ||
            MaximumAssociationEndpointGapPixels > 4096 || !double.IsFinite(MinimumAssociationAlignmentCosine) ||
            MinimumAssociationAlignmentCosine is < 0 or > 1 || !double.IsFinite(StarMaximumMagnitude) ||
            StarMaximumResults is < 1 or > 100_000 || !double.IsFinite(StarSourceSupportRadiusPixels) ||
            StarSourceSupportRadiusPixels is <= 0 or > 63)
        {
            yield return new ValidationResult("Central transient window or association values are invalid.");
        }
        foreach (var result in Extraction.Validate())
        {
            yield return result;
        }
        foreach (var result in Assessment.Validate())
        {
            yield return result;
        }
    }

    public CentralTransientExecutionOptionsV1 CreateExecutionOptions()
        => new(
            CentralTransientExecutionOptionsV1.CurrentSchemaVersion,
            Extraction.ToContract(),
            Assessment.ToContract(),
            WindowTimeout.Ticks,
            MaximumAdjacentStartInterval.Ticks,
            MaximumAssociationTimeGap.Ticks,
            MaximumAssociationEndpointGapPixels,
            MinimumAssociationAlignmentCosine,
            StarMaximumMagnitude,
            StarMaximumResults,
            StarSourceSupportRadiusPixels,
            CentralTransientMaskPolicyV1.ProfileBoundProjectedStarsV1);
}

internal sealed class CentralTransientExtractionOptions
{
    public ushort MinimumResidualAdu { get; init; } = 1;
    public int MinimumComponentPixels { get; init; } = 2;
    public long MinimumIntegratedSignalAdu { get; init; } = 2;
    public int MaximumCandidates { get; init; } = 32;
    public int ProfileSampleCount { get; init; } = 8;
    public int MaximumSaturationBridgePixels { get; init; } = 256;
    public int MaximumForegroundPixels { get; init; } = 100_000;
    public double MaximumFragmentGapPixels { get; init; } = 3;
    public double MinimumFragmentAlignmentCosine { get; init; } = 0.85;

    public TransientCandidateExtractionOptionsV1 ToContract()
        => new(MinimumResidualAdu, MinimumComponentPixels, MinimumIntegratedSignalAdu, MaximumCandidates,
            ProfileSampleCount, MaximumSaturationBridgePixels, MaximumForegroundPixels,
            MaximumFragmentGapPixels, MinimumFragmentAlignmentCosine);

    public IEnumerable<ValidationResult> Validate()
    {
        if (MinimumResidualAdu == 0 || MinimumComponentPixels <= 0 || MinimumIntegratedSignalAdu <= 0 ||
            MaximumCandidates is < 1 or > 64 || ProfileSampleCount is < 2 or > 64 ||
            MaximumSaturationBridgePixels is < 1 or > 1_000_000 ||
            MaximumForegroundPixels is < 1 or > 10_000_000 ||
            !double.IsFinite(MaximumFragmentGapPixels) || MaximumFragmentGapPixels is < 0 or > 1024 ||
            NegativeZero(MaximumFragmentGapPixels) || !double.IsFinite(MinimumFragmentAlignmentCosine) ||
            MinimumFragmentAlignmentCosine is < 0 or > 1 || NegativeZero(MinimumFragmentAlignmentCosine))
        {
            yield return new ValidationResult(
                "Central transient extraction values are invalid.", [nameof(CentralTransientOptions.Extraction)]);
        }
    }

    private static bool NegativeZero(double value)
        => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;
}

internal sealed class CentralTransientAssessmentOptions
{
    public double MinimumMeteorLengthPixels { get; init; } = 2.5;
    public double MinimumMeteorElongation { get; init; } = 1.3;
    public double MaximumMeteorMeanWidthPixels { get; init; } = 10;
    public double CompactSensorMaximumLengthPixels { get; init; } = 2.5;
    public double SensorArtifactMaximumMeanWidthPixels { get; init; } = 1.8;
    public double StationaryMaximumDisplacementPixels { get; init; } = 0.5;
    public double EnvironmentalMinimumMeanWidthPixels { get; init; } = 12;
    public long FireballMinimumIntegratedSignalAdu { get; init; } = 100_000;
    public double FlareMinimumPeakToEndpointRatio { get; init; } = 3;
    public int PersistentTrackMinimumObservations { get; init; } = 3;
    public double AircraftMinimumBrightnessRatio { get; init; } = 3;
    public long AircraftMinimumIntegratedSignalAdu { get; init; } = 1_000;
    public double SmoothMotionMaximumTurnDegrees { get; init; } = 180;
    public double SmoothMotionMaximumStepRatio { get; init; } = 10;

    public TransientDeterministicAssessmentOptionsV1 ToContract()
        => new(MinimumMeteorLengthPixels, MinimumMeteorElongation, MaximumMeteorMeanWidthPixels,
            CompactSensorMaximumLengthPixels, SensorArtifactMaximumMeanWidthPixels,
            StationaryMaximumDisplacementPixels, EnvironmentalMinimumMeanWidthPixels,
            FireballMinimumIntegratedSignalAdu, FlareMinimumPeakToEndpointRatio,
            PersistentTrackMinimumObservations, AircraftMinimumBrightnessRatio,
            AircraftMinimumIntegratedSignalAdu, SmoothMotionMaximumTurnDegrees,
            SmoothMotionMaximumStepRatio);

    public IEnumerable<ValidationResult> Validate()
    {
        var options = ToContract();
        if (!Positive(options.MinimumMeteorLengthPixels) || !Positive(options.MinimumMeteorElongation) ||
            !Positive(options.MaximumMeteorMeanWidthPixels) || !Positive(options.CompactSensorMaximumLengthPixels) ||
            !Positive(options.SensorArtifactMaximumMeanWidthPixels) || options.StationaryMaximumDisplacementPixels < 0 ||
            !double.IsFinite(options.StationaryMaximumDisplacementPixels) ||
            NegativeZero(options.StationaryMaximumDisplacementPixels) ||
            !Positive(options.EnvironmentalMinimumMeanWidthPixels) || options.FireballMinimumIntegratedSignalAdu <= 0 ||
            !Positive(options.FlareMinimumPeakToEndpointRatio) || options.PersistentTrackMinimumObservations < 3 ||
            !Positive(options.AircraftMinimumBrightnessRatio) || options.AircraftMinimumIntegratedSignalAdu <= 0 ||
            !Positive(options.SmoothMotionMaximumTurnDegrees) || options.SmoothMotionMaximumTurnDegrees > 180 ||
            !double.IsFinite(options.SmoothMotionMaximumStepRatio) || options.SmoothMotionMaximumStepRatio < 1)
        {
            yield return new ValidationResult(
                "Central transient assessment values are invalid.", [nameof(CentralTransientOptions.Assessment)]);
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool NegativeZero(double value)
        => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;
}

internal enum CentralTransientMaskPolicyV1
{
    ProfileBoundProjectedStarsV1
}

internal sealed record CentralTransientExecutionOptionsV1(
    string SchemaVersion,
    TransientCandidateExtractionOptionsV1 Extraction,
    TransientDeterministicAssessmentOptionsV1 Assessment,
    long WindowTimeoutTicks,
    long MaximumAdjacentStartIntervalTicks,
    long MaximumAssociationTimeGapTicks,
    double MaximumAssociationEndpointGapPixels,
    double MinimumAssociationAlignmentCosine,
    double StarMaximumMagnitude,
    int StarMaximumResults,
    double StarSourceSupportRadiusPixels,
    CentralTransientMaskPolicyV1 MaskPolicy)
{
    public const string CurrentSchemaVersion = "central-transient-execution-options-v1";
}

internal static class CentralTransientExecutionOptionsJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static (string Json, string Sha256) Serialize(CentralTransientExecutionOptionsV1 options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var json = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(options)).GetRawText();
        return (json, ProcessingIdentity.ComputePayloadSha256(Encoding.UTF8.GetBytes(json)));
    }

    public static CentralTransientExecutionOptionsV1 Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<CentralTransientExecutionOptionsV1>(json, SerializerOptions)
            ?? throw new JsonException("Central transient execution options are required.");
    }
}

internal static class CentralTransientRuntime
{
    public const string RecipeName = "central-transient-validation";
    public const string RecipeVersion = "central-transient-validation-v1";
    public const string Variant = "central-transient-validation-v1";
    public const string SubmissionSchemaVersion = "central-transient-submission-v1";
    public const string ExtractionOptionsSchemaVersion = "transient-candidate-extraction-options-v1";
}
