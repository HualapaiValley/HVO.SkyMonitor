using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.LogicHost.Configuration;

internal sealed class FleetStatusOptions : IValidatableObject
{
    [Range(10, 3600)]
    public int RecommendedHeartbeatSeconds { get; init; } = 60;

    [Range(10, 86_400)]
    public int FreshSeconds { get; init; } = 150;

    [Range(10, 86_400)]
    public int OfflineSeconds { get; init; } = 300;

    [Range(0, 86_400)]
    public int ClockToleranceSeconds { get; init; } = 120;

    [Range(1, 168)]
    public int ReceiptRetentionHours { get; init; } = 24;

    [Range(1, 3650)]
    public int SnapshotRetentionDays { get; init; } = 30;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OfflineSeconds <= FreshSeconds)
        {
            yield return new ValidationResult(
                "OfflineSeconds must be greater than FreshSeconds.",
                [nameof(OfflineSeconds), nameof(FreshSeconds)]);
        }
    }
}
