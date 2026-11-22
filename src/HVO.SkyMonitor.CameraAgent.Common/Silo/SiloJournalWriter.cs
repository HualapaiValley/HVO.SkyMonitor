using System.Diagnostics.CodeAnalysis;
namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Observable crash boundaries of publication. Tests throw from a point to simulate a process crash there.</summary>
internal enum SiloFaultPoint
{
    AfterPayloadPut,
    AfterPayloadVerified,
    AfterManifestSpooled,
    AfterManifestPut,
    AfterManifestQualified,
    AfterPayloadReplaced,
    AfterSegmentSpooled,
    AfterSegmentPending,
    AfterSegmentPut,
    AfterSegmentCommitted,
    AfterIndexApplied,
    AfterVisibleCommitted,
    AfterReplacementReady,
    AfterTargetRetired,
    AfterAuthorityRecorded
}

internal interface ISiloFaults
{
    void Hit(SiloFaultPoint point);
}

/// <summary>A store whose profile is not persistence-qualified cannot have its locators named by a committed record.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloStoreNotQualifiedException(string storeId)
    : InvalidOperationException($"Silo store '{storeId}' does not run a persistence-qualified profile; no committed record may name its locators.")
{
    public string StoreId { get; } = storeId;
}

internal sealed record SiloCreated(string VersionId, DateTimeOffset VerifiedUtc, bool Created);

/// <summary>
/// The one write rule for payloads, manifests, segments and checkpoints (#1965 5.2, 5.3): the exact bytes are already
/// spooled; the key is created once with a supplied checksum; the version is read back by exact GET with an independent
/// SHA-256. An uncertain write is retried with the same bytes. A create-only conflict compares SHA-256: equal means the
/// earlier write succeeded, different stops the writer, and nothing is replaced.
/// </summary>
internal static class SiloCreateOnly
{
    private const int MaximumAttempts = 3;

    public static async ValueTask<SiloCreated> PutVerifiedAsync(
        ISiloObjectStore store, string key, long length, string sha256, Func<CancellationToken, ValueTask<Stream>> open,
        string? format, string bindingId, TimeProvider clock, Action? afterPut, CancellationToken cancellationToken)
    {
        if (!store.Descriptor.PersistenceQualified)
        {
            throw new SiloStoreNotQualifiedException(store.Descriptor.StoreId);
        }
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SiloObjectHeaders.Sha256] = sha256,
            [SiloObjectHeaders.Binding] = bindingId
        };
        if (format is not null)
        {
            metadata[SiloObjectHeaders.Format] = format;
        }
        var landed = false;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            SiloPutResult result;
            try
            {
                result = await store.PutCreateOnlyAsync(new(key, length, sha256, open, metadata), cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException refused) when (landed && !refused.WriteMayHaveLanded)
            {
                // A later attempt that definitely did not apply proves nothing about an earlier one that did.
                throw new SiloStoreUnavailableException(store.Descriptor.StoreId, "a create-only write of " + key + " after an earlier one landed",
                    writeMayHaveLanded: true, refused);
            }
            landed = true;
            var version = result.Created
                ? result.VersionId
                : await store.LocateCurrentVersionAsync(key, cancellationToken).ConfigureAwait(false);
            afterPut?.Invoke();
            if (version is null)
            {
                continue;
            }
            var locator = new SiloExactLocator(store.Descriptor.StoreId, store.Descriptor.Bucket, key, version);
            var probe = await SiloVerifier.ProbeAsync(store, locator, length, sha256, clock, captureBytes: false, cancellationToken)
                .ConfigureAwait(false);
            switch (probe.Status)
            {
                case SiloProbeStatus.Verified:
                    return new(version, probe.ObservedUtc, result.Created);
                case SiloProbeStatus.Absent:
                    // The acknowledged version was lost before qualification. Replaying the same bytes names only the new one.
                    continue;
                case SiloProbeStatus.Damaged when result.Created:
                    throw new SiloConflictException("silo.readback-mismatch",
                        $"The version just created at {locator} did not read back with its exact bytes ({probe.Detail}).");
                case SiloProbeStatus.Damaged:
                    throw new SiloConflictException("silo.create-only-conflict",
                        $"Key {key} already holds different bytes at {version}; it is never replaced.");
                default:
                    throw new SiloStoreUnavailableException(store.Descriptor.StoreId, "exact readback of " + key, writeMayHaveLanded: true);
            }
        }
        throw new SiloStoreUnavailableException(store.Descriptor.StoreId, "a stable create-only write of " + key, writeMayHaveLanded: true);
    }

    public static Func<CancellationToken, ValueTask<Stream>> FromBytes(byte[] bytes) =>
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
}

internal sealed record SiloCommittedSegment(SiloJournalPosition Position, string Sha256, string VersionId);

/// <summary>
/// The single journal writer for one bucket (#1965 4.5). Sequences are gapless within a generation and each segment
/// names its predecessor's SHA-256. One segment is pending at a time; its exact bytes are spooled before its first PUT,
/// and every write attempt is recorded before it is sent. Every append carries an operation key bound to the canonical
/// fingerprint of its records: a retry of the same operation, pending or committed, returns that segment, and a retry
/// of the key with any other records is a conflict.
/// </summary>
internal sealed class SiloJournalWriter(
    SiloPublicationState state,
    SiloObjectSpool spool,
    ISiloObjectStore local,
    SiloStorageBinding binding,
    TimeProvider clock,
    ISiloFaults? faults = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record Completed(SiloCommittedSegment Segment, string OperationId);

    public ISiloObjectStore Store => local;

    /// <summary>The operation key of a generation opening: the generation and the exact prefix end it continues.</summary>
    public static string GenerationOpenOperation(SiloGenerationOpenRecord open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return open.Generation == 1 ? "generation-open:1" : $"generation-open:{open.Generation}:{open.PrefixEndSha256}";
    }

    /// <summary>The canonical fingerprint of an operation's records: what its key promises on every retry.</summary>
    public static string Fingerprint(IReadOnlyList<SiloJournalRecord> records) => SiloHash.Sha256(SiloJson.Serialize(records));

    /// <summary>Open generation 1 at install. Its segment is the first of the journal.</summary>
    public async ValueTask<SiloCommittedSegment> OpenFirstGenerationAsync(
        IReadOnlyDictionary<string, long> identityFloors, CancellationToken cancellationToken)
    {
        var open = new SiloGenerationOpenRecord(1, "install", 0, identityFloors);
        var operation = GenerationOpenOperation(open);
        SiloJournalRecord[] records = [open];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await AlreadyAsync(operation, Fingerprint(records), cancellationToken).ConfigureAwait(false) is { } done)
            {
                return done;
            }
            var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head.Sequence != 0 || head.WriterGeneration != 1)
            {
                throw new InvalidOperationException("Generation 1 opens only an empty journal.");
            }
            return await AppendLockedAsync(records, operation, null, null, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Open a later generation after takeover. #1972 proves the highest issued generation through its witnesses and
    /// supplies the record; this writer only orders it and makes it the first segment of the new generation. The writer
    /// generation advances in the same transaction that records the opening as pending, so a crash at any point leaves
    /// either nothing or an opening that the same call, or startup, completes.
    /// </summary>
    public async ValueTask<SiloCommittedSegment> OpenGenerationAsync(SiloGenerationOpenRecord open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        var operation = GenerationOpenOperation(open);
        SiloJournalRecord[] records = [open];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await AlreadyAsync(operation, Fingerprint(records), cancellationToken).ConfigureAwait(false) is { } done)
            {
                return done;
            }
            var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head.Position is not { } end || open.PrefixEnd != end || !string.Equals(open.PrefixEndSha256, head.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A new generation names the committed journal end it continues.");
            }
            if (open.Generation <= head.WriterGeneration)
            {
                throw new InvalidOperationException("A new writer generation is above every issued generation.");
            }
            return await AppendLockedAsync(records, operation, null, null, open.Generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reconcile a pending segment left by a crash or a failed write before anything else is appended or cleaned up. A
    /// segment that may have been written is replayed with its exact spooled bytes. A publication's segment that provably
    /// never reached the store is withdrawn instead, so its group is requalified before its first actual commit.
    /// </summary>
    public async ValueTask<SiloCommittedSegment?> ReconcilePendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await ReconcilePendingLockedAsync(cancellationToken).ConfigureAwait(false))?.Segment;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Append one segment for one operation. Retrying the same operation key never appends it twice.</summary>
    public ValueTask<SiloCommittedSegment> AppendAsync(
        IReadOnlyList<SiloJournalRecord> records, string operationId, CancellationToken cancellationToken) =>
        AppendAsync(records, operationId, null, null, cancellationToken);

    /// <summary>
    /// A publication names its group revision, so the commit and the group's move to <c>recorded</c> happen in one
    /// transaction.
    /// </summary>
    internal async ValueTask<SiloCommittedSegment> AppendAsync(
        IReadOnlyList<SiloJournalRecord> records, string operationId, string? groupId, string? revisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        SiloText.RequireText(operationId, nameof(operationId), 256);
        if (records.Count == 0 || records.Any(static record => record is SiloGenerationOpenRecord))
        {
            throw new ArgumentException("Append at least one record; generations open through their own operations.", nameof(records));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AlreadyAsync(operationId, Fingerprint(records), cancellationToken).ConfigureAwait(false)
                ?? await AppendLockedAsync(records, operationId, groupId, revisionId, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The operation's segment if it is committed, or once its pending segment is completed; otherwise null. The same key
    /// with other records is refused before anything is written or replayed.
    /// </summary>
    private async ValueTask<SiloCommittedSegment?> AlreadyAsync(string operationId, string fingerprint, CancellationToken cancellationToken)
    {
        if (await state.ReadPendingAsync(cancellationToken).ConfigureAwait(false) is { } pending &&
            string.Equals(pending.OperationId, operationId, StringComparison.Ordinal) && !Withdrawable(pending))
        {
            RequireSameOperation(operationId, pending.OperationSha256, fingerprint);
        }
        var resumed = await ReconcilePendingLockedAsync(cancellationToken).ConfigureAwait(false);
        if (resumed is not null && string.Equals(resumed.OperationId, operationId, StringComparison.Ordinal))
        {
            return resumed.Segment;
        }
        if (await state.ReadOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is not { } committed)
        {
            return null;
        }
        RequireSameOperation(operationId, committed.OperationSha256, fingerprint);
        return new(committed.Position, committed.Sha256, committed.VersionId);
    }

    private static void RequireSameOperation(string operationId, string recorded, string requested)
    {
        if (!string.Equals(recorded, requested, StringComparison.Ordinal))
        {
            throw new SiloConflictException("silo.operation-conflict",
                $"Operation {operationId} is already journaled with other records; a retry under its key must ask for exactly the same records.");
        }
    }

    private async ValueTask<SiloCommittedSegment> AppendLockedAsync(
        IReadOnlyList<SiloJournalRecord> records, string operationId, string? groupId, string? revisionId, long? openGeneration,
        CancellationToken cancellationToken)
    {
        var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        var position = openGeneration is { } opening
            ? new SiloJournalPosition(opening, 1)
            : head.Generation == head.WriterGeneration
                ? new SiloJournalPosition(head.WriterGeneration, head.Sequence + 1)
                : new SiloJournalPosition(head.WriterGeneration, 1);
        var segment = new SiloJournalSegment(SiloFormats.JournalSegment, binding.InstanceId, binding.BindingId,
            position.Generation, position.Sequence, clock.GetUtcNow().ToUniversalTime(), records, head.Position, head.Sha256);
        try
        {
            SiloJson.ValidateSegment(segment);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The journal segment would violate its format.", exception);
        }
        var bytes = SiloJson.Serialize(segment);
        var sha = SiloHash.Sha256(bytes);
        await spool.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterSegmentSpooled);
        var pending = new SiloPendingSegment(position, sha, bytes.Length, operationId, Fingerprint(records), groupId, revisionId);
        await state.InsertPendingAsync(pending, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterSegmentPending);
        return (await SendPendingLockedAsync(pending, cancellationToken).ConfigureAwait(false)).Segment;
    }

    /// <summary>A publication's segment that provably never reached the store; it is requalified, never replayed.</summary>
    private static bool Withdrawable(SiloPendingSegment pending) => pending.GroupId is not null && pending.ProvablyUnapplied;

    private async ValueTask<Completed?> ReconcilePendingLockedAsync(CancellationToken cancellationToken)
    {
        if (await state.ReadPendingAsync(cancellationToken).ConfigureAwait(false) is not { } pending)
        {
            return null;
        }
        if (Withdrawable(pending) && await state.WithdrawPendingAsync(pending, cancellationToken).ConfigureAwait(false))
        {
            spool.Delete(SiloObjectSpool.NameOf(pending.Sha256));
            return null;
        }
        return await SendPendingLockedAsync(pending, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Write the pending segment's exact bytes once and commit it. The attempt is recorded as in flight before it is sent,
    /// and its outcome after: a definite refusal leaves it provably unapplied, anything else may have landed.
    /// </summary>
    private async ValueTask<Completed> SendPendingLockedAsync(SiloPendingSegment pending, CancellationToken cancellationToken)
    {
        var name = SiloObjectSpool.NameOf(pending.Sha256);
        var bytes = await spool.ReadAsync(name, pending.Sha256, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"The exact bytes of pending journal segment {pending.Position} are lost from the spool; the writer stays fenced.");
        var key = SiloObjectNamespace.SegmentKey(pending.Position);
        await state.BeginPendingAttemptAsync(pending, cancellationToken).ConfigureAwait(false);
        SiloCreated created;
        try
        {
            created = await SiloCreateOnly.PutVerifiedAsync(local, key, bytes.Length, pending.Sha256, SiloCreateOnly.FromBytes(bytes),
                SiloFormats.JournalSegment, binding.BindingId, clock, () => faults?.Hit(SiloFaultPoint.AfterSegmentPut), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SiloStoreUnavailableException unavailable)
        {
            await state.EndPendingAttemptAsync(pending, unavailable.WriteMayHaveLanded, cancellationToken).ConfigureAwait(false);
            throw;
        }
        await state.CommitPendingAsync(pending, created.VersionId, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterSegmentCommitted);
        spool.Delete(name);
        return new(new(pending.Position, pending.Sha256, created.VersionId), pending.OperationId);
    }

    /// <summary>Run work that must not interleave with an append, such as removing unreferenced spool files.</summary>
    internal async ValueTask<T> ExclusiveAsync<T>(Func<ValueTask<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
