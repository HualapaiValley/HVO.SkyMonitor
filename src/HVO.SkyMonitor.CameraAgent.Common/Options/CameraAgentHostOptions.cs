using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Options;

public sealed class CameraAgentHostOptions : IValidatableObject
{
    [Required]
    [MinLength(1)]
    public string ConfigFilePath { get; init; } = "cameraagent.sample.json";

    public string? AgentId { get; init; }

    [Range(1, 1440)]
    public int RetentionSweepIntervalMinutes { get; init; } = 30;

    [Range(1, 99)]
    public double DiskPressureThresholdPercent { get; init; } = 10;

    [Range(2, 100)]
    public double DiskPressureRecoveryPercent { get; init; } = 15;

    [Range(1, 3650)]
    public int DiskPressureRetentionDays { get; init; } = 1;

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
    }
}
