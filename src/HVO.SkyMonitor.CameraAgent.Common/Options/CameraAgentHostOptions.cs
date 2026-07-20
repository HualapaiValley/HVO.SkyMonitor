using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;

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
    public TransientDetectionOptions TransientDetection { get; init; } = new();

    [Required]
    public EnvironmentalObservationDeliveryOptions EnvironmentalDelivery { get; init; } = new();

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
    }
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
    }
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
