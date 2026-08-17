using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Options;

public sealed class CameraAgentHostOptions : IValidatableObject
{
    [Required]
    [MinLength(1)]
    public string ConfigFilePath { get; init; } = "cameraagent.sample.json";

    [Required]
    [MinLength(1)]
    public string RawIngressRoot { get; init; } = string.Empty;

    [Range(0, long.MaxValue)]
    public long RawIngressReserveBytes { get; init; } = 64L * 1024 * 1024;

    [Range(1, 300)]
    public int RawIngressSqliteBusyTimeoutSeconds { get; init; } = 5;

    [Required]
    public CaptureDistributionOptions CaptureDistribution { get; init; } = new();

    [Required]
    public ProvisioningStartupGateOptions ProvisioningStartupGate { get; init; } = new();

    [Required]
    public CentralIntegrationOptions CentralIntegration { get; init; } = new();

    [Required]
    public TransientDetectionOptions TransientDetection { get; init; } = new();

    [Required]
    public EnvironmentalObservationDeliveryOptions EnvironmentalDelivery { get; init; } = new();

    [Required]
    public EnvironmentalAcquisitionOptions EnvironmentalAcquisition { get; init; } = new();

    [Required]
    public ArtifactReadOptions ArtifactRead { get; init; } = new();

    [Range(1, 60)]
    public int OperationsReferenceLifetimeMinutes { get; init; } = 15;

    public string? AgentId { get; init; }

    [Range(1, 1440)]
    public int RetentionSweepIntervalMinutes { get; init; } = 30;

    [Range(1, 99)]
    public double DiskPressureThresholdPercent { get; init; } = 10;

    [Range(2, 100)]
    public double DiskPressureRecoveryPercent { get; init; } = 15;

    [Range(1, 3650)]
    public int DiskPressureRetentionDays { get; init; } = 1;

    [Range(1, 100)]
    public int UploadBatchSize { get; init; } = 10;

    [Range(1, 3600)]
    public int UploadPollIntervalSeconds { get; init; } = 10;

    [Range(1, 3600)]
    public int UploadRetryInitialDelaySeconds { get; init; } = 10;

    [Range(1, 86400)]
    public int UploadRetryMaximumDelaySeconds { get; init; } = 300;

    [Range(0, int.MaxValue)]
    public int UploadBandwidthLimitBytesPerSecond { get; init; }

    [Required]
    public ObservatoryLocation Observatory { get; init; } = new(0, 0, 0, "UTC");

    [Required]
    public DeploymentLocationOptions DeploymentLocation { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DiskPressureRecoveryPercent <= DiskPressureThresholdPercent)
        {
            yield return new ValidationResult(
                "DiskPressureRecoveryPercent must be greater than DiskPressureThresholdPercent.",
                [nameof(DiskPressureRecoveryPercent), nameof(DiskPressureThresholdPercent)]);
        }

        if (UploadRetryMaximumDelaySeconds < UploadRetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "UploadRetryMaximumDelaySeconds must be greater than or equal to UploadRetryInitialDelaySeconds.",
                [nameof(UploadRetryMaximumDelaySeconds), nameof(UploadRetryInitialDelaySeconds)]);
        }

        var distributionResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            CaptureDistribution,
            new ValidationContext(CaptureDistribution),
            distributionResults,
            validateAllProperties: true);
        foreach (var result in distributionResults)
        {
            yield return result;
        }

        var centralIntegrationResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            CentralIntegration,
            new ValidationContext(CentralIntegration),
            centralIntegrationResults,
            validateAllProperties: true);
        foreach (var result in centralIntegrationResults)
        {
            yield return result;
        }

        var transientResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            TransientDetection,
            new ValidationContext(TransientDetection),
            transientResults,
            validateAllProperties: true);
        foreach (var result in transientResults)
        {
            yield return result;
        }
        if (TransientDetection.Mode is TransientOperatingMode.Central or TransientOperatingMode.Hybrid &&
            !CaptureDistribution.UploadEnabled)
        {
            yield return new ValidationResult(
                "Central and Hybrid transient detection require the upload lane.",
                [nameof(TransientDetection), nameof(CaptureDistribution)]);
        }
        if (CentralIntegration.Mode == CentralIntegrationMode.Disabled &&
            TransientDetection.Mode is TransientOperatingMode.Central or TransientOperatingMode.Hybrid)
        {
            yield return new ValidationResult(
                "Central and Hybrid transient detection require central integration.",
                [nameof(CentralIntegration), nameof(TransientDetection)]);
        }

        var environmentalResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            EnvironmentalDelivery,
            new ValidationContext(EnvironmentalDelivery),
            environmentalResults,
            validateAllProperties: true);
        foreach (var result in environmentalResults)
        {
            yield return result;
        }
        var environmentalAcquisitionResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            EnvironmentalAcquisition,
            new ValidationContext(EnvironmentalAcquisition),
            environmentalAcquisitionResults,
            validateAllProperties: true);
        foreach (var result in environmentalAcquisitionResults)
        {
            yield return result;
        }

        var artifactReadResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            ArtifactRead,
            new ValidationContext(ArtifactRead),
            artifactReadResults,
            validateAllProperties: true);
        foreach (var result in artifactReadResults)
        {
            yield return result;
        }

        var locationResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            DeploymentLocation,
            new ValidationContext(DeploymentLocation),
            locationResults,
            validateAllProperties: true);
        foreach (var result in locationResults)
        {
            yield return result;
        }
    }
}

public sealed class ProvisioningStartupGateOptions
{
    public bool Enabled { get; init; }
}

public sealed class DeploymentLocationOptions : IValidatableObject
{
    [EnumDataType(typeof(DeploymentLocationSourceKind))]
    public DeploymentLocationSourceKind SourceKind { get; init; } = DeploymentLocationSourceKind.Manual;

    [Required(AllowEmptyStrings = false)]
    [MaxLength(128)]
    public string LocationId { get; init; } = "local-deployment";

    [Required(AllowEmptyStrings = false)]
    [MaxLength(512)]
    public string Source { get; init; } = "local-configuration";

    [Range(0, double.MaxValue)]
    public double? HorizontalAccuracyMeters { get; init; }

    public DateTimeOffset? EffectiveFromUtc { get; init; }

    public DateTimeOffset? EffectiveUntilUtc { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(SourceKind) || SourceKind == DeploymentLocationSourceKind.Unspecified)
        {
            yield return new ValidationResult(
                "SourceKind must be Gps, Manual, or Inherited.",
                [nameof(SourceKind)]);
        }
        if (HorizontalAccuracyMeters is { } accuracy && !double.IsFinite(accuracy))
        {
            yield return new ValidationResult(
                "HorizontalAccuracyMeters must be finite when specified.",
                [nameof(HorizontalAccuracyMeters)]);
        }
        if (EffectiveFromUtc is { } from && EffectiveUntilUtc is { } until && until <= from)
        {
            yield return new ValidationResult(
                "EffectiveUntilUtc must be later than EffectiveFromUtc.",
                [nameof(EffectiveFromUtc), nameof(EffectiveUntilUtc)]);
        }
    }
}

public enum CentralIntegrationMode
{
    Enabled,
    Disabled
}

public sealed class CentralIntegrationOptions
{
    [EnumDataType(typeof(CentralIntegrationMode))]
    public CentralIntegrationMode Mode { get; init; } = CentralIntegrationMode.Enabled;
}

public sealed class ArtifactReadOptions
{
    [Range(1, 2_048)]
    public int MaximumPreviewDimension { get; init; } = 2_048;

    [Range(1, 1_073_741_824)]
    public long MaximumPreviewSourceBytes { get; init; } = 64L * 1024 * 1024;

    [Range(1, 16_777_216)]
    public int MaximumPreviewEncodedBytes { get; init; } = 16 * 1024 * 1024;

    [Range(1, 32)]
    public int MaximumConcurrentPreviews { get; init; } = 2;

    [Range(1, 1_073_741_824)]
    public long PreviewCacheBytes { get; init; } = 64L * 1024 * 1024;

    [Range(1, 4_096)]
    public int ValidationCacheEntries { get; init; } = 256;
}

public enum TransientOperatingMode
{
    Off,
    Edge,
    Central,
    Hybrid
}

public sealed class TransientDetectionOptions : IValidatableObject
{
    [EnumDataType(typeof(TransientOperatingMode))]
    public TransientOperatingMode Mode { get; init; }

    public bool Required { get; init; }

    [Range(1, 10_080)]
    public int CandidateTimeoutMinutes { get; init; } = 10;

    [Range(100, 60_000)]
    public int WorkerPollIntervalMilliseconds { get; init; } = 1_000;

    [Range(1, 3_600)]
    public int RetryInitialDelaySeconds { get; init; } = 1;

    [Range(1, 86_400)]
    public int RetryMaximumDelaySeconds { get; init; } = 60;

    [Range(1, 100)]
    public int MaximumAttempts { get; init; } = 5;

    [Range(1, 120)]
    public int DeliveryRequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 3_600)]
    public int MaximumAdjacentStartIntervalSeconds { get; init; } = 30;

    [Range(-30, 30)]
    public double StarMaximumMagnitude { get; init; } = 8;

    [Range(1, 100_000)]
    public int StarMaximumResults { get; init; } = 2_000;

    [Range(0.5, 256)]
    public double StarSupportRadiusSourcePixels { get; init; } = 5;

    [Required]
    public TransientCandidateAssociationOptions Association { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Mode))
        {
            yield return new ValidationResult(
                "Transient detection mode is not supported.",
                [nameof(Mode)]);
        }
        if (Required && Mode is not (TransientOperatingMode.Edge or TransientOperatingMode.Hybrid))
        {
            yield return new ValidationResult(
                "Transient detection can be required only in Edge or Hybrid mode.",
                [nameof(Required), nameof(Mode)]);
        }
        if (RetryMaximumDelaySeconds < RetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "RetryMaximumDelaySeconds must be greater than or equal to RetryInitialDelaySeconds.",
                [nameof(RetryMaximumDelaySeconds), nameof(RetryInitialDelaySeconds)]);
        }
        var associationResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            Association,
            new ValidationContext(Association),
            associationResults,
            validateAllProperties: true);
        foreach (var result in associationResults)
        {
            yield return result;
        }
    }
}

public sealed class TransientCandidateAssociationOptions
{
    public const string CurrentAlgorithmVersion = "adjacent-candidate-association-v1";

    [Required]
    [RegularExpression("adjacent-candidate-association-v1")]
    public string AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;

    [Range(0.001, 3_600)]
    public double MaximumStartIntervalSeconds { get; init; } = 30;

    [Range(0, 4_096)]
    public double MaximumEndpointGapPixels { get; init; } = 24;

    [Range(0, 1)]
    public double MinimumAbsolutePrincipalAxisAlignment { get; init; } = 0.85;
}

public sealed class EnvironmentalObservationDeliveryOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Range(1, 1000)]
    public int BatchSize { get; init; } = 100;

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 10;

    [Range(10, 3600)]
    public int LeaseSeconds { get; init; } = 120;

    [Range(1, 3599)]
    public int RequestTimeoutSeconds { get; init; } = 60;

    [Range(1, 3600)]
    public int RetryInitialDelaySeconds { get; init; } = 5;

    [Range(1, 86400)]
    public int RetryMaximumDelaySeconds { get; init; } = 300;

    [Range(1, 100)]
    public int MaximumAttempts { get; init; } = 10;

    [Range(1, 1_000_000)]
    public int MaximumPendingCount { get; init; } = 10_000;

    [Range(1, long.MaxValue)]
    public long MaximumPendingBytes { get; init; } = 64L * 1024 * 1024;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RetryMaximumDelaySeconds < RetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "RetryMaximumDelaySeconds must be greater than or equal to RetryInitialDelaySeconds.",
                [nameof(RetryMaximumDelaySeconds), nameof(RetryInitialDelaySeconds)]);
        }
        if (RequestTimeoutSeconds * 2 > LeaseSeconds)
        {
            yield return new ValidationResult(
                "RequestTimeoutSeconds must not exceed half of LeaseSeconds.",
                [nameof(RequestTimeoutSeconds), nameof(LeaseSeconds)]);
        }
    }
}

public sealed class EnvironmentalAcquisitionOptions : IValidatableObject
{
    public bool Enabled { get; init; }

    [Range(1, 16)]
    public int MaximumConcurrency { get; init; } = 4;

    [Range(16, 10_000)]
    public int QueueCapacity { get; init; } = 256;

    [Range(100, 120_000)]
    public int SourceTimeoutMilliseconds { get; init; } = 5_000;

    [Range(0, 5_000)]
    public int BeforeCaptureWaitMilliseconds { get; init; } = 250;

    [Range(1, 1_000_000)]
    public int MaximumHistoryCount { get; init; } = 100_000;

    [Range(1, long.MaxValue)]
    public long MaximumHistoryBytes { get; init; } = 256L * 1024 * 1024;

    [Range(1, 3_650)]
    public int RetentionDays { get; init; } = 31;

    [Range(1, 10_000)]
    public int RetentionBatchSize { get; init; } = 1_000;

    public IReadOnlyList<EnvironmentalSourceConfiguration> Sources { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled && Sources.Count > 0)
        {
            yield return new ValidationResult(
                "Environmental sources require environmental acquisition to be enabled.",
                [nameof(Enabled), nameof(Sources)]);
        }
        if (Enabled && Sources.Count == 0)
        {
            yield return new ValidationResult(
                "Enabled environmental acquisition requires at least one source.",
                [nameof(Enabled), nameof(Sources)]);
        }
        if (Sources.Count > 128)
        {
            yield return new ValidationResult("At most 128 environmental sources may be configured.", [nameof(Sources)]);
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (source is null)
            {
                yield return new ValidationResult("Environmental source entries cannot be null.", [nameof(Sources)]);
                continue;
            }
            var results = new List<ValidationResult>();
            Validator.TryValidateObject(source, new ValidationContext(source), results, validateAllProperties: true);
            foreach (var result in results)
            {
                yield return result;
            }
            if (!string.IsNullOrWhiteSpace(source.Id) && !identities.Add(source.Id))
            {
                yield return new ValidationResult("Environmental source identifiers must be unique.", [nameof(Sources)]);
            }
        }
    }
}

public sealed class EnvironmentalSourceConfiguration : IValidatableObject
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Id { get; init; } = string.Empty;

    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string Type { get; init; } = string.Empty;

    public EnvironmentalObservationKind Kind { get; init; }

    public bool Required { get; init; } = true;

    [MinLength(1)]
    public IReadOnlyList<EnvironmentalAcquisitionTrigger> Triggers { get; init; } = [];

    public DateTimeOffset ScheduleEpochUtc { get; init; } = DateTimeOffset.UnixEpoch;

    [Range(1, 86_400)]
    public int PeriodSeconds { get; init; } = 30;

    [Range(1, 1_000_000)]
    public int EveryNthCapture { get; init; } = 1;

    [Range(1, 86_400)]
    public int ValidForSeconds { get; init; } = 120;

    [Range(1, 86_400)]
    public int StaleAfterSeconds { get; init; } = 45;

    [StringLength(128)]
    public string? RigId { get; init; }

    public JsonElement Options { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Id != Id.Trim() || Type != Type.Trim() || RigId is not null && RigId != RigId.Trim())
        {
            yield return new ValidationResult("Environmental source identities must be trimmed.");
        }
        if (!Enum.IsDefined(Kind) || Triggers.Count == 0 || Triggers.Any(static trigger => !Enum.IsDefined(trigger)) ||
            Triggers.Distinct().Count() != Triggers.Count)
        {
            yield return new ValidationResult("Environmental source kind and triggers must be supported.");
        }
        if (ScheduleEpochUtc.Offset != TimeSpan.Zero || ScheduleEpochUtc == default)
        {
            yield return new ValidationResult("Environmental source schedule epoch must be UTC.", [nameof(ScheduleEpochUtc)]);
        }
        if (StaleAfterSeconds > ValidForSeconds)
        {
            yield return new ValidationResult(
                "Environmental source stale duration cannot exceed its validity duration.",
                [nameof(StaleAfterSeconds), nameof(ValidForSeconds)]);
        }
        if (Options.ValueKind is not JsonValueKind.Object and not JsonValueKind.Undefined)
        {
            yield return new ValidationResult("Environmental source options must be a JSON object.", [nameof(Options)]);
        }
    }

    public EnvironmentalSourceDescriptor ToDescriptor(JsonElement? resolvedOptions = null)
        => new(
            Id,
            Type,
            Kind,
            Required,
            Triggers,
            ScheduleEpochUtc,
            PeriodSeconds,
            EveryNthCapture,
            ValidForSeconds,
            StaleAfterSeconds,
            RigId,
            resolvedOptions ?? Options);
}

public sealed class CaptureDistributionOptions : IValidatableObject
{
    public bool UploadEnabled { get; init; }

    [Range(100, 60_000)]
    public int PollIntervalMilliseconds { get; init; } = 1_000;

    [Range(10, 3_600)]
    public int LeaseSeconds { get; init; } = 120;

    [Range(1, 1_200)]
    public int LeaseRenewalSeconds { get; init; } = 30;

    [Range(1, 3_600)]
    public int RetryInitialDelaySeconds { get; init; } = 1;

    [Range(1, 86_400)]
    public int RetryMaximumDelaySeconds { get; init; } = 60;

    [Range(1, 100)]
    public int MaximumAttempts { get; init; } = 5;

    [Range(1, 600)]
    public int ShutdownDrainSeconds { get; init; } = 30;

    [Range(1, 1_000_000)]
    public int RequiredMaximumPendingCount { get; init; } = 10_000;

    [Range(1, long.MaxValue)]
    public long RequiredMaximumPendingBytes { get; init; } = 128_793_600_000;

    [Range(1, 525_600)]
    public int RequiredMaximumOldestAgeMinutes { get; init; } = 10_080;

    [Range(1, 1_000_000)]
    public int OptionalMaximumPendingCount { get; init; } = 1_000;

    [Range(1, long.MaxValue)]
    public long OptionalMaximumPendingBytes { get; init; } = 12_879_360_000;

    [Range(1, 525_600)]
    public int OptionalMaximumOldestAgeMinutes { get; init; } = 1_440;

    [Range(1, 99)]
    public int PressureRecoveryPercent { get; init; } = 80;

    public IReadOnlyList<SecondaryCaptureLaneOptions> SecondaryLanes { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LeaseRenewalSeconds * 2 >= LeaseSeconds)
        {
            yield return new ValidationResult(
                "LeaseRenewalSeconds must be less than half of LeaseSeconds.",
                [nameof(LeaseRenewalSeconds), nameof(LeaseSeconds)]);
        }

        if (RetryMaximumDelaySeconds < RetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "RetryMaximumDelaySeconds must be greater than or equal to RetryInitialDelaySeconds.",
                [nameof(RetryMaximumDelaySeconds), nameof(RetryInitialDelaySeconds)]);
        }

        if (SecondaryLanes.Count > 16)
        {
            yield return new ValidationResult(
                "No more than 16 secondary capture lanes may be configured.",
                [nameof(SecondaryLanes)]);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in SecondaryLanes)
        {
            if (!CaptureLaneName.IsValid(lane.Name) || lane.Name is "standard" or "upload" or "transient")
            {
                yield return new ValidationResult(
                    "Secondary lane names must be lowercase, begin with a letter, contain only letters, digits, or hyphens, and must not use a reserved name.",
                    [nameof(SecondaryLanes)]);
            }
            else if (!names.Add(lane.Name))
            {
                yield return new ValidationResult(
                    $"Secondary capture lane '{lane.Name}' is configured more than once.",
                    [nameof(SecondaryLanes)]);
            }
            if (lane.Required && !lane.Enabled)
            {
                yield return new ValidationResult(
                    $"Required secondary capture lane '{lane.Name}' must be enabled.",
                    [nameof(SecondaryLanes)]);
            }
        }
    }
}

public sealed class SecondaryCaptureLaneOptions
{
    [Required(AllowEmptyStrings = false)]
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public bool Required { get; init; }
}

internal static class CaptureLaneName
{
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        return value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }
}
