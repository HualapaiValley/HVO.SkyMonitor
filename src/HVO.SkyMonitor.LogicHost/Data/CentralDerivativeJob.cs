using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralDerivativeJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SourceCentralArtifactId { get; set; }

    public CentralArtifact? SourceArtifact { get; set; }

    public FrameArtifactRole TargetRole { get; set; }

    public string TargetRecipeVersion { get; set; } = string.Empty;

    public string TargetVariant { get; set; } = string.Empty;

    public string RecipeName { get; set; } = string.Empty;

    public string RecipeOptionsJson { get; set; } = "{}";

    public string InputSelectorJson { get; set; } = "{}";

    public string RequestedRecipeIdentitySha256 { get; set; } = string.Empty;

    public string ExpectedRecipeIdentitySha256 { get; set; } = string.Empty;

    public string RequestIdentitySha256 { get; set; } = string.Empty;

    public string? TraceParent { get; set; }

    public string? TraceState { get; set; }

    public CentralDerivativeJobStatus Status { get; set; }

    public DateTimeOffset? ResolutionDeadlineUtc { get; set; }

    public DateTimeOffset? ResolutionStartedAtUtc { get; set; }

    public DateTimeOffset? ResolutionCompletedAtUtc { get; set; }

    public CentralDerivativeWindowOutcome? MissingInputOutcome { get; set; }

    public string? StateReasonCode { get; set; }

    public string? InputSetIdentitySha256 { get; set; }

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

    public DateTimeOffset? CancellationRequestedAtUtc { get; set; }

    public string? CancellationRequestedBy { get; set; }

    public Guid? SupersededByJobId { get; set; }

    public CentralDerivativeJob? SupersededByJob { get; set; }

    public Guid? PredecessorJobId { get; set; }

    public CentralDerivativeJob? PredecessorJob { get; set; }

    public Guid? RetainedResultCentralArtifactId { get; set; }

    public ICollection<CentralDerivativeJobAttempt> Attempts { get; } = [];

    public ICollection<CentralDerivativeJobInputRequirement> InputRequirements { get; } = [];

    public ICollection<CentralDerivativeJobInput> Inputs { get; } = [];

    public ICollection<CentralDerivativeJobCanonicalInput> CanonicalInputs { get; } = [];

    public byte[] RowVersion { get; set; } = [];
}

internal enum CentralDerivativeJobStatus
{
    Waiting,
    Pending,
    Leased,
    Completed,
    RetryableFailure,
    TerminalFailure,
    CancelRequested,
    Canceled,
    Skipped,
    Quarantined,
    Superseded
}

internal enum CentralDerivativeWindowOutcome
{
    Run,
    Skip,
    Fail,
    Quarantine
}
