namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

/// <summary>
/// Single-root, single-writer persistence for SWPC facts. S2 owns fetch policy,
/// checkpoints, halt decisions and attention projection; S3 owns merge and retention.
/// Timed operations purge first in their transaction, using only the supplied UTC time.
/// Revisions include purges; stale writes return Conflict. Identity replays never
/// reapply effects or attention. Cancellation and failed writes roll back atomically.
/// </summary>
public interface ISwpcStore : IDisposable
{
    /// <summary>Claims the root and verifies or initializes the STRICT schema, failing closed.</summary>
    ValueTask InitializeAsync(CancellationToken ct);

    ValueTask<SwpcStoreView> ReadAsync(DateTimeOffset atUtc, CancellationToken ct);

    /// <summary>
    /// Durably records admission before network I/O, including S2's checkpoint and
    /// LastAttemptUtc. At most one attempt may remain incomplete across the store.
    /// Begin/complete replay identities expire with the journal row.
    /// </summary>
    ValueTask<SwpcWriteResult> BeginAttemptAsync(SwpcAttemptStart request, CancellationToken ct);

    /// <summary>
    /// Atomically verifies the outcome through SwpcSnapshotComparison, merges facts,
    /// invokes the projection, persists its effects and completes the attempt.
    /// Every first completion invokes the projection, including a completion without
    /// a candidate: AfterMerge equals Before and Touched is empty in that case.
    /// Projection exceptions propagate unwrapped, roll back all changes and leave the
    /// attempt incomplete; logs may contain only their type, within IDs 7810–7824.
    /// Explicit null-outcome settlement accepts only interrupted or storage-failure,
    /// with Preserved validators and zero bytes. It changes no checkpoint, counter,
    /// backoff, halt, last-known-good, validators, last success or last outcome.
    /// Unknown or pruned attempt identities return NotFound without reapplying.
    /// </summary>
    ValueTask<SwpcWriteResult> CompleteAttemptAsync(
        SwpcAttemptCompletion request, SwpcCommitProjection project, CancellationToken ct);

    /// <summary>Persists S2's explicit halt/resume and checkpoint effects, without policy.</summary>
    ValueTask<SwpcWriteResult> ChangeHaltAsync(SwpcHaltCommand request, CancellationToken ct);

    ValueTask<SwpcWriteResult> AcknowledgeAsync(SwpcAcknowledgeCommand request, CancellationToken ct);

    /// <summary>
    /// Purges expired notice versions and raw history; completed journal entries are
    /// retained 35 days from StartedUtc, capped at 50,000, oldest completed first.
    /// Incomplete entries are never pruned. Halt/ack identities retain 35 days,
    /// capped at 10,000. Deletions advance the revision in this same transaction.
    /// </summary>
    ValueTask<SwpcPurgeResult> ApplyRetentionAsync(DateTimeOffset atUtc, CancellationToken ct);

    ValueTask<SwpcDiagnosticsPage> ReadDiagnosticsAsync(SwpcDiagnosticsQuery request, CancellationToken ct);
    ValueTask<SwpcStoreHealth> ReadHealthAsync(DateTimeOffset atUtc, CancellationToken ct);
}

/// <summary>
/// S2's synchronous deterministic completion projection, called under the write gate
/// over one consistent view. It must remain bounded and perform no I/O or store re-entry.
/// </summary>
public delegate SwpcCommitEffects SwpcCommitProjection(SwpcCommitContext context);
