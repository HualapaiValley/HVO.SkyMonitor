using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Replay;
using System.Text;

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
    public ProjectedSceneStagingOptions ProjectedSceneStaging { get; init; } = new();

    [Required]
    public DerivedProductLifecycleOptions DerivedProductLifecycle { get; init; } = new();

    [Required]
    public CaptureDistributionOptions CaptureDistribution { get; init; } = new();

    [Required]
    public ProcessingGraphExecutionOptions ProcessingGraphs { get; init; } = new();

    [Required]
    public ProcessingGraphDeliveryOptions ProcessingGraphDelivery { get; init; } = new();

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
    public ExecutionEvidenceExportOptions ExecutionEvidenceExport { get; init; } = new();

    [Required]
    public ArtifactReadOptions ArtifactRead { get; init; } = new();

    [Required]
    public LocalAutomationOptions Automation { get; init; } = new();

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

        var projectedSceneStagingResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            ProjectedSceneStaging,
            new ValidationContext(ProjectedSceneStaging),
            projectedSceneStagingResults,
            validateAllProperties: true);
        foreach (var result in projectedSceneStagingResults)
        {
            yield return result;
        }

        var derivedLifecycleResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            DerivedProductLifecycle,
            new ValidationContext(DerivedProductLifecycle),
            derivedLifecycleResults,
            validateAllProperties: true);
        foreach (var result in derivedLifecycleResults)
        {
            yield return result;
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

        var processingGraphResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            ProcessingGraphs,
            new ValidationContext(ProcessingGraphs),
            processingGraphResults,
            validateAllProperties: true);
        foreach (var result in processingGraphResults)
        {
            yield return result;
        }

        var processingGraphDeliveryResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            ProcessingGraphDelivery,
            new ValidationContext(ProcessingGraphDelivery),
            processingGraphDeliveryResults,
            validateAllProperties: true);
        foreach (var result in processingGraphDeliveryResults)
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

        var automationResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            Automation,
            new ValidationContext(Automation),
            automationResults,
            validateAllProperties: true);
        foreach (var result in automationResults)
        {
            yield return result;
        }

        var evidenceExportResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            ExecutionEvidenceExport,
            new ValidationContext(ExecutionEvidenceExport),
            evidenceExportResults,
            validateAllProperties: true);
        foreach (var result in evidenceExportResults)
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

public sealed class ProjectedSceneStagingOptions
{
    [Range(1, 4096)]
    public int MaximumFileCount { get; init; } = 128;

    [Range(ProjectedSceneJson.MaximumPayloadBytes, 1024L * 1024 * 1024)]
    public long MaximumTotalBytes { get; init; } = 128L * 1024 * 1024;

    [Range(1, 16384)]
    public int MaximumReconciliationEntries { get; init; } = 512;
}

public sealed class DerivedProductLifecycleOptions
{
    [Range(16, 4096)]
    public int ReconciliationBatchSize { get; init; } = 512;

    [Range(1, 365)]
    public int DiagnosticRetentionDays { get; init; } = 14;

    [Range(1, 1440)]
    public int OrphanRecoveryWindowMinutes { get; init; } = 30;
}

/// <summary>
/// Bounds for the durable graph-execution evidence export lane. Every limit refuses new work at the enlistment or
/// request boundary; none of them discards evidence that is already durable, and none of them can delay acquisition,
/// raw ingress, live processing, local publication, artifact upload, or replay.
/// </summary>
public sealed class ExecutionEvidenceExportOptions : IValidatableObject
{
    /// <summary>When false the lane performs no source sweep at all and the durable store never grows.</summary>
    public bool Enabled { get; init; } = true;

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 30;

    /// <summary>Terminal executions inspected per source sweep query.</summary>
    [Range(1, 256)]
    public int DiscoveryBatchSize { get; init; } = 64;

    /// <summary>Source sweep queries performed per cycle, so one cycle cannot run unbounded.</summary>
    [Range(1, 64)]
    public int MaximumDiscoveryBatchesPerCycle { get; init; } = 8;

    /// <summary>Evidence units in one submission request.</summary>
    [Range(1, 256)]
    public int MaximumRequestUnits { get; init; } = 32;

    /// <summary>Canonical bytes in one submission request.</summary>
    [Range(65536, 16 * 1024 * 1024)]
    public int MaximumRequestBytes { get; init; } = 4 * 1024 * 1024;

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 3600)]
    public int RetryInitialDelaySeconds { get; init; } = 10;

    [Range(1, 86400)]
    public int RetryMaximumDelaySeconds { get; init; } = 300;

    /// <summary>Attempts before a unit is quarantined instead of retried forever.</summary>
    [Range(1, 1000)]
    public int MaximumAttempts { get; init; } = 12;

    [Range(1, 1_000_000)]
    public long MaximumPendingUnits { get; init; } = 50_000;

    [Range(1024L * 1024, 64L * 1024 * 1024 * 1024)]
    public long MaximumPendingBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    [Range(1024L * 1024, 64L * 1024 * 1024 * 1024)]
    public long MaximumStorageBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Canonical bytes one sealed unit may occupy; never above the contract's absolute envelope cap.</summary>
    [Range(4096, 8 * 1024 * 1024)]
    public int MaximumUnitBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Oldest pending age tolerated before the lane reports an explicit degraded state.</summary>
    [Range(1, 8760)]
    public int MaximumPendingAgeHours { get; init; } = 168;

    [Range(1, 8760)]
    public int AcknowledgementRetentionHours { get; init; } = 168;

    [Range(0, 1_000_000)]
    public int MaximumRetainedAcknowledgements { get; init; } = 10_000;

    /// <summary>Replaces operator-identifying trigger references and lease owners before the payload is hashed.</summary>
    public bool RedactOperatorIdentity { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RetryMaximumDelaySeconds < RetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "RetryMaximumDelaySeconds must be greater than or equal to RetryInitialDelaySeconds.",
                [nameof(RetryMaximumDelaySeconds), nameof(RetryInitialDelaySeconds)]);
        }
        if (MaximumRequestBytes > MaximumPendingBytes)
        {
            yield return new ValidationResult(
                "MaximumRequestBytes must not exceed MaximumPendingBytes.",
                [nameof(MaximumRequestBytes), nameof(MaximumPendingBytes)]);
        }
        if (MaximumUnitBytes > GraphExecutionEvidenceLimits.MaximumEnvelopeBytes)
        {
            yield return new ValidationResult(
                "MaximumUnitBytes must not exceed the contract's absolute envelope cap.",
                [nameof(MaximumUnitBytes)]);
        }
        if (RequestTimeoutSeconds > PollIntervalSeconds * 10)
        {
            yield return new ValidationResult(
                "RequestTimeoutSeconds must not exceed ten poll intervals.",
                [nameof(RequestTimeoutSeconds), nameof(PollIntervalSeconds)]);
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

    [Range(4_096, 16_777_216)]
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

/// <summary>
/// Host settings for the local automation runner. The durable definitions themselves live in the
/// versioned local automation store, never in configuration.
/// </summary>
public sealed class LocalAutomationOptions
{
    /// <summary>Whether the runner evaluates definitions. Definitions remain readable when disabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How often the runner evaluates definitions. It bounds how late an occurrence can fire, and
    /// how often a capture-relative definition reads the durable capture sequence.
    /// </summary>
    [Range(5, 3_600)]
    public int PollIntervalSeconds { get; init; } = 30;
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

public enum ReplayExecutionProfile
{
    InProcess,
    LocalRunner
}

public sealed class ProcessingGraphExecutionOptions : IValidatableObject
{
    public ReplayExecutionProfile ReplayProfile { get; init; } = ReplayExecutionProfile.InProcess;

    [Required]
    public LocalReplayRunnerHostOptions LocalRunner { get; init; } = new();

    [Range(1, 4)]
    public int ReplayMaximumConcurrency { get; init; } = 1;

    [Range(1, 86400)]
    public int LiveDeadlineSeconds { get; init; } = 300;

    [Range(1, 604800)]
    public int LiveMaximumQueueAgeSeconds { get; init; } = 900;

    [Range(1, 100000)]
    public int ReplayMaximumPendingCount { get; init; } = 1000;

    [Range(1, long.MaxValue)]
    public long ReplayMaximumPendingBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    [Range(1, 86400)]
    public int ReplayDeadlineSeconds { get; init; } = 3600;

    [Range(1, 2592000)]
    public int ReplayMaximumQueueAgeSeconds { get; init; } = 86400;

    [Range(3, 300)]
    public int ReplayLeaseSeconds { get; init; } = 30;

    [Range(1, 3600)]
    public int ReplayRecoveryPollSeconds { get; init; } = 30;

    [Range(1, 20)]
    public int ReplayMaximumAttempts { get; init; } = 5;

    /// <summary>
    /// Limits live and replay window inputs. Durable processing retention protects at least the newest 100
    /// outputs per agent/node and expands to the selector's bounded candidate scan of
    /// <c>min(512, 4 * MaximumWindowInputs)</c> so a candidate cannot be pruned before the node pins it.
    /// </summary>
    [Range(1, 128)]
    public int MaximumWindowInputs { get; init; } = 32;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(ReplayProfile))
        {
            yield return new ValidationResult("Replay execution profile is invalid.", [nameof(ReplayProfile)]);
        }

        var runnerResults = new List<ValidationResult>();
        Validator.TryValidateObject(
            LocalRunner,
            new ValidationContext(LocalRunner),
            runnerResults,
            validateAllProperties: true);
        foreach (var result in runnerResults)
        {
            yield return result;
        }

        if (ReplayProfile == ReplayExecutionProfile.LocalRunner)
        {
            foreach (var result in LocalRunner.ValidateForExternalProfile())
            {
                yield return result;
            }
            if (ReplayMaximumConcurrency >= 1 &&
                LocalRunner.MaximumTransferBytes >
                LocalReplayRunnerOptions.MaximumAggregateTransferBytes / (ReplayMaximumConcurrency * 2L))
            {
                yield return new ValidationResult(
                    "Local replay runner concurrent request and response buffers exceed the aggregate transfer limit.",
                    [nameof(ReplayMaximumConcurrency), nameof(LocalRunner.MaximumTransferBytes)]);
            }
        }
    }
}

public sealed class ProcessingGraphDeliveryOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Range(5, 3600)]
    public int PollIntervalSeconds { get; init; } = 300;

    [Range(1, 100)]
    public int AcknowledgementBatchSize { get; init; } = 16;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 3600)]
    public int RetryInitialDelaySeconds { get; init; } = 5;

    [Range(1, 86400)]
    public int RetryMaximumDelaySeconds { get; init; } = 300;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RetryMaximumDelaySeconds < RetryInitialDelaySeconds)
        {
            yield return new ValidationResult(
                "RetryMaximumDelaySeconds must be greater than or equal to RetryInitialDelaySeconds.",
                [nameof(RetryMaximumDelaySeconds), nameof(RetryInitialDelaySeconds)]);
        }
    }
}

public sealed class LocalReplayRunnerHostOptions : IValidatableObject
{
    public ReplayRunnerTransport Transport { get; init; } = ReplayRunnerTransport.UnixDomainSocket;

    [Required]
    public string SocketPath { get; init; } = LocalReplayRunnerOptions.DefaultSocketPath;

    [Range(0, 65535)]
    public int LoopbackPort { get; init; }

    public string? AuthorizationKey { get; init; }

    public string? AuthorizationKeyFile { get; init; }

    [Range(1, 300)]
    public int ConnectTimeoutSeconds { get; init; } = 10;

    [Range(1, 300)]
    public int HeartbeatIntervalSeconds { get; init; } = 5;

    [Range(2, 600)]
    public int HeartbeatTimeoutSeconds { get; init; } = 20;

    [Range(4096, 16 * 1024 * 1024)]
    public int MaximumMetadataBytes { get; init; } = 1024 * 1024;

    [Range(4096, LocalReplayRunnerOptions.MaximumTransferBytes)]
    public long MaximumTransferBytes { get; init; } = LocalReplayRunnerOptions.MaximumTransferBytes;

    [Range(0, 86400)]
    public int IdleShutdownSeconds { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Transport))
        {
            yield return new ValidationResult("Local replay runner transport is invalid.", [nameof(Transport)]);
        }
        if (HeartbeatTimeoutSeconds <= HeartbeatIntervalSeconds)
        {
            yield return new ValidationResult(
                "Local replay runner heartbeat timeout must exceed its interval.",
                [nameof(HeartbeatTimeoutSeconds), nameof(HeartbeatIntervalSeconds)]);
        }
        if (MaximumTransferBytes < MaximumMetadataBytes)
        {
            yield return new ValidationResult(
                "Local replay runner transfer limit must include its metadata limit.",
                [nameof(MaximumTransferBytes), nameof(MaximumMetadataBytes)]);
        }
    }

    internal IEnumerable<ValidationResult> ValidateForExternalProfile()
    {
        if (Transport == ReplayRunnerTransport.UnixDomainSocket &&
            (string.IsNullOrWhiteSpace(SocketPath) || SocketPath.Contains('\0', StringComparison.Ordinal) ||
             !Path.IsPathFullyQualified(SocketPath) || Encoding.UTF8.GetByteCount(SocketPath) > 100))
        {
            yield return new ValidationResult(
                "Local replay runner Unix socket path must be absolute, contain no NUL, and be no more than 100 UTF-8 bytes.",
                [nameof(SocketPath)]);
        }
        if (Transport == ReplayRunnerTransport.LoopbackTcp && LoopbackPort == 0)
        {
            yield return new ValidationResult(
                "Local replay runner loopback mode requires a port.",
                [nameof(LoopbackPort)]);
        }

        var directKey = string.IsNullOrEmpty(AuthorizationKey)
            ? 0
            : Encoding.UTF8.GetByteCount(AuthorizationKey);
        var hasKeyFile = !string.IsNullOrWhiteSpace(AuthorizationKeyFile);
        if ((directKey == 0) == !hasKeyFile)
        {
            yield return new ValidationResult(
                "Configure exactly one local replay runner authorization key or owner-only key file.",
                [nameof(AuthorizationKey), nameof(AuthorizationKeyFile)]);
        }
        else if (directKey is > 0 and < 32 or > 4096)
        {
            yield return new ValidationResult(
                "Local replay runner authorization key must contain between 32 and 4096 UTF-8 bytes.",
                [nameof(AuthorizationKey)]);
        }
    }

    internal LocalReplayRunnerOptions ToTransportOptions(int maximumConcurrency) => new()
    {
        Transport = Transport,
        SocketPath = SocketPath,
        LoopbackPort = LoopbackPort,
        PreSharedAuthKey = string.IsNullOrEmpty(AuthorizationKey)
            ? ReadOnlyMemory<byte>.Empty
            : Encoding.UTF8.GetBytes(AuthorizationKey),
        OwnerOnlyAuthKeyFile = AuthorizationKeyFile,
        MaxConcurrency = maximumConcurrency,
        ConnectTimeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds),
        HeartbeatInterval = TimeSpan.FromSeconds(HeartbeatIntervalSeconds),
        HeartbeatTimeout = TimeSpan.FromSeconds(HeartbeatTimeoutSeconds),
        MaxMetadataBytes = MaximumMetadataBytes,
        MaxTotalTransferBytes = MaximumTransferBytes,
        IdleShutdownSeconds = IdleShutdownSeconds
    };
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
