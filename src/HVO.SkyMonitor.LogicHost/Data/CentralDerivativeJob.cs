using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralDerivativeJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SourceCentralArtifactId { get; set; }

    public CentralArtifact? SourceArtifact { get; set; }

    public FrameArtifactRole TargetRole { get; set; }

    public string TargetRecipeVersion { get; set; } = string.Empty;

    public CentralDerivativeJobStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; }

    public DateTimeOffset? AvailableAtUtc { get; set; }

    public string? LeaseOwner { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseAcquiredAtUtc { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastFailedAtUtc { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public Guid? ResultCentralArtifactId { get; set; }

    public CentralArtifact? ResultArtifact { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

internal enum CentralDerivativeJobStatus
{
    Pending,
    Leased,
    Completed,
    RetryableFailure,
    TerminalFailure
}
