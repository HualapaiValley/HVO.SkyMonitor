namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Auditable usage for one terminal derivative attempt (#429): who consumed which capacity for how long and with how
/// many bytes, keyed by observatory and camera so future billing can aggregate without a payment provider.
/// </summary>
internal sealed class CentralProcessingUsageRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ObservatoryId { get; set; }

    public Guid DevicePublicId { get; set; }

    public Guid CentralDerivativeJobId { get; set; }

    public int AttemptNumber { get; set; }

    public string RecipeName { get; set; } = string.Empty;

    public string ResourceClass { get; set; } = string.Empty;

    public string WorkerId { get; set; } = string.Empty;

    public CentralDerivativeAttemptOutcome Outcome { get; set; }

    public string? ReasonCode { get; set; }

    public DateTimeOffset LeaseAcquiredAtUtc { get; set; }

    public DateTimeOffset EndedAtUtc { get; set; }

    public long InputBytes { get; set; }

    public long OutputBytes { get; set; }

    public long RecipeDurationTicks { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
}
