namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Persisted rollup of <see cref="CentralProcessingUsageRecord"/> per observatory, resource class, and outcome,
/// maintained in the same transaction as each usage row (#429). The completion and usage-byte metrics read this
/// small table, so they are exact, identical on every replica, and never require scanning the usage ledger.
/// </summary>
internal sealed class CentralProcessingUsageRollup
{
    public Guid ObservatoryId { get; set; }

    public string ResourceClass { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public long Attempts { get; set; }

    public long InputBytes { get; set; }

    public long OutputBytes { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
