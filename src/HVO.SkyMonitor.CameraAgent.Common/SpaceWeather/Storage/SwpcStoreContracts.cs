using System.Collections.Immutable;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public enum SwpcFetchOutcome
{
    Updated,
    Unchanged,
    StaleSnapshot,
    NotModified,
    Rejected,
    TransientFailure,
    RateLimited,
    Halted
}
public enum SwpcFreshness
{
    None,
    Fresh,
    Stale,
    Outdated
}
public enum SwpcAttemptTrigger
{
    Scheduled,
    Manual
}
public enum SwpcValidatorOutcome
{
    NotSent,
    NotModified,
    Replaced,
    Preserved
}
public enum SwpcWriteStatus
{
    Applied,
    Replayed,
    Conflict,
    NotFound
}

public sealed record SwpcValidators(string? ETag, string? LastModified);
public sealed record SwpcHalt(string ReasonCode, DateTimeOffset SinceUtc);
// Pass-through state: S2 computes every value; S3 only validates and persists it.
public sealed record SwpcFetchCheckpoint(DateTimeOffset? NextAttemptNotBeforeUtc,
    DateTimeOffset? RetryNotBeforeUtc, bool InBackoff,
    int ConsecutiveTransientFailures, int ConsecutiveRateLimited, int ConsecutiveRejected,
    DateTimeOffset? RecordDriftStampUtc);

public sealed record SwpcStoredMessage(SwpcMessageVersion Version,
    string IntroducingSnapshotSha256, DateTimeOffset RetrievedUtc);
public sealed record SwpcNoticeData(ImmutableArray<SwpcStoredMessage> Versions,
    ImmutableArray<SwpcNoticeGroup> Groups, ImmutableHashSet<string> OrphanGroups,
    ImmutableDictionary<SwpcMessageNode, int> RaisedRanks,
    ImmutableDictionary<SwpcMessageNode, SwpcAcknowledgement> Acknowledgements);
public sealed record SwpcStoredProduct(SwpcSnapshot? LastKnownGood, SwpcValidators Validators,
    DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastAttemptUtc,
    SwpcFetchOutcome? LastOutcome, SwpcFetchCheckpoint Checkpoint,
    SwpcHalt? ProductHalt, SwpcFreshness Freshness);
public sealed record SwpcStoreView(long Revision,
    ImmutableDictionary<SwpcProduct, SwpcStoredProduct> Products,
    SwpcHalt? ProviderHalt, SwpcNoticeData Notices);

public sealed record SwpcAttemptStart(Guid AttemptId, long ExpectedRevision,
    SwpcProduct Product, string RunKey, SwpcAttemptTrigger Trigger, DateTimeOffset StartedUtc,
    SwpcValidators SentValidators, SwpcFetchCheckpoint CheckpointAfterStart);
public sealed record SwpcAttemptCompletion(Guid AttemptId, long ExpectedRevision,
    DateTimeOffset AtUtc, DateTimeOffset EndedUtc, DateTimeOffset? RetrievedUtc,
    SwpcFetchOutcome? Outcome, int? HttpStatus, string ReasonCode, long BytesRead,
    SwpcValidatorOutcome ValidatorOutcome, SwpcSnapshot? Candidate);
public sealed record SwpcCommitContext(SwpcAttemptCompletion Attempt,
    SwpcStoreView Before, SwpcStoreView AfterMerge, ImmutableHashSet<SwpcMessageNode> Touched);
public sealed record SwpcCommitEffects(SwpcFetchCheckpoint ProductCheckpoint,
    SwpcHaltChange? HaltChange, SwpcAttentionResult? NoticeAttention,
    ImmutableArray<SwpcAttentionEvent> ProviderAttention);

// Product == null addresses the provider; Value == null clears that scope.
public sealed record SwpcHaltChange(SwpcProduct? Product, SwpcHalt? Value);
public sealed record SwpcHaltCommand(Guid CommandId, long ExpectedRevision, DateTimeOffset AtUtc,
    SwpcHaltChange Change, ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint> Checkpoints);
public sealed record SwpcAcknowledgeCommand(Guid CommandId, long ExpectedRevision,
    DateTimeOffset AtUtc, string Group, SwpcAcknowledgement Acknowledgement);
public sealed record SwpcWriteResult(SwpcWriteStatus Status, string? ReasonCode,
    SwpcStoreView View, int AddedVersions, ImmutableArray<string> Diagnostics,
    ImmutableArray<SwpcAttentionEvent> Attention);
public sealed record SwpcPurgeResult(long Revision, int GroupsDeleted, int VersionsDeleted,
    int SnapshotsDeleted, long RawBytesDeleted);

public sealed record SwpcDiagnosticsQuery(DateTimeOffset AtUtc,
    SwpcProduct? Product, long? BeforeSequence, int Limit);
public sealed record SwpcJournalEntry(long Sequence, Guid AttemptId, SwpcProduct Product,
    SwpcAttemptTrigger Trigger, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc,
    SwpcFetchOutcome? Outcome, int? HttpStatus, string? ReasonCode, long? BytesRead,
    SwpcValidatorOutcome? ValidatorOutcome, string? SnapshotSha256,
    ImmutableArray<string> Diagnostics);
public sealed record SwpcDiagnosticsPage(ImmutableArray<SwpcJournalEntry> Attempts,
    long? NextBeforeSequence);
public sealed record SwpcProductHealth(string? LastKnownGoodSha256,
    DateTimeOffset? InFileStampUtc, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastAttemptUtc, SwpcFetchOutcome? LastOutcome,
    SwpcFreshness Freshness, SwpcFetchCheckpoint Checkpoint, SwpcHalt? ProductHalt);
/// <summary>Redacted storage health, including all retained attempts and their oldest start.</summary>
public sealed record SwpcStoreHealth(int SchemaVersion, long Revision,
    ImmutableDictionary<SwpcProduct, SwpcProductHealth> Products, SwpcHalt? ProviderHalt,
    long SnapshotCount, long RawBodyBytes, long MessageVersionCount, long AttemptCount,
    int IncompleteAttempts, ImmutableDictionary<string, long> ReasonCounts,
    DateTimeOffset? OldestRetainedAttemptStartedUtc);
