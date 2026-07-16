namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralDerivativeJobAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CentralDerivativeJobId { get; set; }

    public CentralDerivativeJob? Job { get; set; }

    public int AttemptNumber { get; set; }

    public string WorkerId { get; set; } = string.Empty;

    public DateTimeOffset LeaseAcquiredAtUtc { get; set; }

    public DateTimeOffset LeaseExpiresAtUtc { get; set; }

    public DateTimeOffset? EndedAtUtc { get; set; }

    public CentralDerivativeAttemptOutcome Outcome { get; set; }

    public string? ReasonCode { get; set; }

    public long InputBytes { get; set; }

    public long OutputBytes { get; set; }

    public long RecipeDurationTicks { get; set; }
}

internal enum CentralDerivativeAttemptOutcome
{
    Leased,
    Completed,
    RetryableFailure,
    TerminalFailure,
    LeaseExpired,
    Canceled,
    Skipped,
    Quarantined,
    Superseded
}

internal sealed class CentralArtifactProcessingEvidence
{
    public Guid CentralArtifactId { get; set; }

    public Guid DevicePublicId { get; set; }

    public CentralArtifact? Artifact { get; set; }

    public string OutputIdentitySha256 { get; set; } = string.Empty;

    public string RequestedRecipeIdentitySha256 { get; set; } = string.Empty;

    public string RecipeIdentitySha256 { get; set; } = string.Empty;

    public string AlgorithmsJson { get; set; } = "[]";

    public string CompatibilityJson { get; set; } = "{}";

    public long TotalIntegrationTicks { get; set; }

    public Guid CentralDerivativeJobId { get; set; }

    public CentralDerivativeJob? Job { get; set; }

    public int AttemptNumber { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
