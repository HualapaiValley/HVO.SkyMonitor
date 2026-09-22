using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public enum ProcessingGraphRevisionLifecycle
{
    Draft,
    Validated,
    Active,
    Retired
}

public enum ProcessingGraphRegistryMode
{
    ConfiguredBasic,
    Named
}

public enum ProcessingGraphExecutionClass
{
    Live,
    Replay
}

public enum ProcessingGraphExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Expired
}

public sealed record ProcessingGraphRevisionState(
    string RevisionId,
    string Name,
    string Revision,
    ProcessingGraphRevisionLifecycle Lifecycle,
    string DefinitionIdentitySha256,
    string SharedPlanIdentitySha256,
    string LocalPlanIdentitySha256,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ValidatedUtc,
    DateTimeOffset? ActivatedUtc,
    DateTimeOffset? RetiredUtc);

public sealed record ProcessingGraphRegistryState(
    ProcessingGraphRegistryMode Mode,
    string ActiveRevisionId,
    string ConfiguredBasicRevisionId,
    long StateVersion,
    IReadOnlyList<ProcessingGraphRevisionState> Revisions);

public sealed record ProcessingGraphExecutionState(
    Guid ExecutionId,
    ProcessingGraphExecutionClass ExecutionClass,
    ProcessingGraphExecutionStatus Status,
    Guid CaptureId,
    Guid PrimaryArtifactId,
    string GraphRevisionId,
    string GraphDefinitionIdentitySha256,
    string SharedPlanIdentitySha256,
    string LocalPlanIdentitySha256,
    string TriggerKind,
    string? TriggerReference,
    int Priority,
    DateTimeOffset AcceptedUtc,
    DateTimeOffset AvailableUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset MaximumAgeUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureReason,
    bool CancellationRequested,
    int AttemptCount);

/// <summary>
/// One output the execution associated, with whether this execution published it.
/// <para>
/// <c>Published</c> is the association's durable <c>published_flag</c> joined with the execution's
/// own <c>allow_automatic_publication</c>. The flag alone is identity-level: a replay of the live
/// revision reproduces identical output identities and its association rows inherit the live
/// publication, so the flag alone would report a replay as having published. A replay execution is
/// inserted with automatic publication disabled and therefore records <c>false</c> for every output
/// here (#973). The evidence that a replay published nothing is this value, not the delivery
/// outbox's backlog.
/// </para>
/// </summary>
public sealed record ProcessingGraphExecutionOutputState(
    int Ordinal,
    string OutputIdentitySha256,
    Guid ArtifactId,
    FrameArtifactRole Role,
    string Variant,
    string AvailabilityState,
    string? AvailabilityReason,
    bool Published);

public enum ProcessingGraphExecutionInputKind
{
    RawCapture,
    ProcessingOutput
}

public sealed record ProcessingGraphExecutionInputState(
    int Ordinal,
    int WindowPosition,
    ProcessingGraphExecutionInputKind Kind,
    Guid CaptureId,
    Guid ArtifactId,
    string? DescriptorSha256,
    string? PayloadSha256,
    string? OutputIdentitySha256);

/// <summary>
/// One recorded attempt at one node, including the route the attempt actually took.
/// <para>
/// <c>ExecutionRoute</c> answers issue #799: which nodes dispatched to the local replay runner and
/// which ran in process. It is recorded by the attempt rather than derived from the host's configured
/// replay profile, because the profile says what the host was started with and the route says what
/// happened. An attempt that never reached the recipe execution adapter keeps
/// <see cref="ProcessingNodeExecutionRoute.Unknown"/>, which is how a complementary node is told apart
/// from a recipe-backed one that ran locally, and which is also what an interrupted attempt keeps.
/// </para>
/// </summary>
public sealed record ProcessingGraphNodeAttemptState(
    int AttemptNumber,
    string LeaseOwner,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    string Status,
    ProcessingOutcomeStatus? Outcome,
    string? Reason,
    TimeSpan? Duration,
    ProcessingNodeExecutionRoute ExecutionRoute);

public sealed record ProcessingGraphExecutionNodeState(
    string NodeId,
    bool Required,
    string PlanSha256,
    string Status,
    string? Reason,
    int AttemptCount,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<ProcessingGraphExecutionInputState> Inputs,
    IReadOnlyList<ProcessingGraphNodeAttemptState> Attempts,
    IReadOnlyList<ProcessingGraphExecutionOutputState> Outputs)
{
    /// <summary>Dependencies frozen with this execution, not today's active graph revision.</summary>
    public IReadOnlyList<ProcessingGraphDependencyDefinition> Dependencies { get; init; } = [];
}

/// <summary>One terminal execution key and the immutable ordering value the evidence exporter sweeps by.</summary>
internal sealed record ProcessingGraphTerminalExecution(
    Guid ExecutionId,
    long AcceptedUnixMs);

public sealed record ProcessingGraphExecutionDetail(
    ProcessingGraphExecutionState Execution,
    IReadOnlyList<ProcessingGraphExecutionNodeState> Nodes);

public sealed record ProcessingReplaySubmission(
    Guid CaptureId,
    string GraphRevisionId,
    Guid? PrimaryArtifactId = null,
    string TriggerKind = "operator",
    string? TriggerReference = null,
    int Priority = 0,
    string? Reason = null);

public sealed record ProcessingReplaySubmissionResult(
    ProcessingGraphExecutionState Execution,
    bool Replayed);

internal sealed record ProcessingGraphRevisionSnapshot(
    ProcessingGraphRevisionState State,
    CapturePipelineConfig Pipeline,
    byte[] PipelineJson,
    byte[] DefinitionJson,
    byte[] FrozenPlanJson,
    ImmutableArray<ProcessingExecutionNodeSeed> Nodes);

internal sealed record ProcessingExecutionNodeSeed(
    string NodeId,
    bool Required,
    string PlanSha256,
    string SharedPlanNodeIdentitySha256,
    string DependenciesJson,
    string InputsJson,
    string OutputsJson,
    string? WindowJson);

internal sealed record ProcessingLiveExecutionSeed(
    Guid ExecutionId,
    Guid CaptureId,
    Guid PrimaryArtifactId,
    ProcessingGraphRevisionSnapshot Revision,
    DateTimeOffset AcceptedUtc,
    DateTimeOffset AvailableUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset MaximumAgeUtc,
    byte[] ConfigurationJson);

public sealed record ProcessingExecutionContext(
    Guid ExecutionId,
    ProcessingGraphExecutionClass ExecutionClass,
    string GraphRevisionId,
    string LocalPlanIdentitySha256,
    bool AllowAutomaticPublication,
    long WorkId,
    string? LeaseToken,
    string? LeaseOwner = null,
    DateTimeOffset? DeadlineUtc = null,
    int DurableAttempt = 0);

internal sealed record ProcessingReplayLease(
    long WorkId,
    ProcessingGraphExecutionState Execution,
    ProcessingGraphRevisionSnapshot Revision,
    CameraModuleConfig Configuration,
    CaptureLoopSubmission Submission,
    RawIngress.RawCaptureReceipt RawCapture,
    string LeaseToken,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresUtc,
    int ClaimCount);

internal sealed record ProcessingReplaySource(
    long RawCaptureRowId,
    long PayloadBytes,
    string DescriptorSha256,
    string PayloadSha256,
    CameraModuleConfig Configuration,
    CaptureLoopSubmission Submission,
    RawIngress.RawCaptureReceipt RawCapture);

internal sealed record ProcessingFrozenRawInput(
    long RawCaptureRowId,
    int WindowPosition,
    ReconstructionDescriptor Descriptor,
    string DescriptorSha256,
    string PayloadSha256,
    string PayloadRelativePath);

public interface IProcessingGraphOperations
{
    ValueTask<ProcessingGraphRegistryState> GetRegistryAsync(CancellationToken cancellationToken);

    ValueTask<ProcessingGraphRevisionState> CreateRevisionAsync(
        string name,
        string revision,
        CapturePipelineConfig pipeline,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphRegistryState> ActivateRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphRegistryState> RollbackRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphRevisionState> ValidateRevisionAsync(
        string revisionId,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphRegistryState> RetireRevisionAsync(
        string revisionId,
        long expectedVersion,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken);

    ValueTask<ProcessingReplaySubmissionResult> SubmitReplayAsync(
        ProcessingReplaySubmission submission,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ProcessingGraphExecutionState>> ReadExecutionsAsync(
        ProcessingGraphExecutionClass? executionClass,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphExecutionState?> ReadExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    /// <summary>Finds the one live execution for a capture through the durable unique capture index.</summary>
    ValueTask<Guid?> ReadLiveExecutionIdAsync(Guid captureId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the immutable pipeline definition stored with a graph revision, or null when no
    /// revision has that identifier; a malformed identifier is rejected with an argument
    /// exception like every other revision read. A read-only accessor for operator inspection;
    /// it changes no durable behaviour.
    /// </summary>
    ValueTask<CapturePipelineConfig?> ReadRevisionPipelineAsync(
        string revisionId,
        CancellationToken cancellationToken);

    ValueTask<ProcessingGraphExecutionState> CancelReplayAsync(
        Guid executionId,
        string idempotencyKey,
        string actor,
        string? reason,
        CancellationToken cancellationToken);
}

public sealed class ProcessingGraphStoreConflictException : InvalidOperationException
{
    public ProcessingGraphStoreConflictException()
    {
    }

    public ProcessingGraphStoreConflictException(string message) : base(message)
    {
    }

    public ProcessingGraphStoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when a replay submission cannot freeze a required auxiliary source, such as the committed projected-scene
/// product of the requested capture, because it is missing, unavailable, or ambiguous. The submission is rejected
/// before any execution row, work item, or pin is written.
/// </summary>
public sealed class ProcessingReplaySourceException : InvalidOperationException
{
    public ProcessingReplaySourceException()
    {
    }

    public ProcessingReplaySourceException(string message)
        : base(message)
    {
    }

    public ProcessingReplaySourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ProcessingReplayCapacityException : InvalidOperationException
{
    public ProcessingReplayCapacityException()
    {
    }

    public ProcessingReplayCapacityException(string message) : base(message)
    {
    }

    public ProcessingReplayCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
