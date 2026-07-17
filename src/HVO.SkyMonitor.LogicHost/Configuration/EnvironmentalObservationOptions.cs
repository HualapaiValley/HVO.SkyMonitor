using System.ComponentModel.DataAnnotations;

namespace HVO.SkyMonitor.LogicHost.Configuration;

internal sealed class EnvironmentalObservationOptions
{
    [Range(0, 86_400)]
    public int ClockToleranceSeconds { get; init; } = 120;

    [Range(1, 3650)]
    public int ReceiptRetentionDays { get; init; } = 365;

    [Range(1, 86_400)]
    public int RetentionSweepSeconds { get; init; } = 3600;

    [Range(1, 10_000)]
    public int RetentionBatchSize { get; init; } = 1_000;

    [Range(1, 3650)]
    public int MaximumQueryRangeDays { get; init; } = 31;

    [Range(1, 1000)]
    public int MaximumQueryResults { get; init; } = 500;

    [Range(1, 365)]
    public int MaximumCorrelationStalenessDays { get; init; } = 7;
}
