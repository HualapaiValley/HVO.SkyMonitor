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
    }
}
