namespace HVO.SkyMonitor.LogicHost.Data;

internal enum CentralProcessingRunnerStatus
{
    Active,
    Stale,
    Retired
}

internal enum CentralProcessingRunnerWarmState
{
    Cold,
    Warming,
    Warm,
    Degraded
}

/// <summary>
/// A registered self-hosted processing runner (<c>processing-runner-v1</c>). The row is the durable identity a runner
/// lease owner (<see cref="CentralDerivativeJob.LeaseOwner"/>) refers to, binds that identity to the credential
/// subject that registered it, and records the advertised capabilities, the recipes the host resolved as eligible for
/// it, and its heartbeat/warm state for scheduling and health.
/// </summary>
internal sealed class CentralProcessingRunner
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string RunnerId { get; set; } = string.Empty;

    public string ClientSubject { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string OperatingSystem { get; set; } = string.Empty;

    public string OsArchitecture { get; set; } = string.Empty;

    public string ProcessArchitecture { get; set; } = string.Empty;

    public string RuntimeIdentifier { get; set; } = string.Empty;

    public string FrameworkDescription { get; set; } = string.Empty;

    public int ProcessorCount { get; set; }

    public long TotalMemoryBytes { get; set; }

    public string ResourceClass { get; set; } = string.Empty;

    public bool GpuAvailable { get; set; }

    public string LatencyClass { get; set; } = string.Empty;

    /// <summary>Canonical JSON of the full advertised <c>ProcessingRunnerCapabilities</c>.</summary>
    public string CapabilitiesJson { get; set; } = "{}";

    /// <summary>JSON array of the recipe names the host resolved as claimable for this runner.</summary>
    public string EligibleRecipesJson { get; set; } = "[]";

    public int MaxConcurrency { get; set; }

    public long MaxTransferBytes { get; set; }

    public CentralProcessingRunnerWarmState WarmState { get; set; }

    public CentralProcessingRunnerStatus Status { get; set; }

    public int ProcessId { get; set; }

    public DateTimeOffset ProcessStartedUtc { get; set; }

    public int Generation { get; set; }

    public int AvailableSlots { get; set; }

    public DateTimeOffset RegisteredAtUtc { get; set; }

    public DateTimeOffset LastHeartbeatAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? RetiredAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
