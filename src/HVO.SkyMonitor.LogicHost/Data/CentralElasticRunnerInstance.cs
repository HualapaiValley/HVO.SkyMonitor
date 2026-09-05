namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// Durable record of one elastically provisioned runner instance (#430): provenance (provider, image, architecture),
/// lifecycle timestamps, measured cold start, and the reason it stopped. Feeds orphan cleanup across host restarts
/// and instance-minute accounting.
/// </summary>
internal sealed class CentralElasticRunnerInstance
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Provider { get; set; } = string.Empty;

    /// <summary>The LogicHost machine that launched the instance; only that host reconciles or retires it.</summary>
    public string HostName { get; set; } = string.Empty;

    public string InstanceId { get; set; } = string.Empty;

    public string RunnerId { get; set; } = string.Empty;

    public int? ProcessId { get; set; }

    public string ProcessArchitecture { get; set; } = string.Empty;

    public string RuntimeImage { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string? Reason { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? RegisteredAtUtc { get; set; }

    public DateTimeOffset? LastBusyAtUtc { get; set; }

    public DateTimeOffset? StoppedAtUtc { get; set; }

    public int? ColdStartMilliseconds { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
