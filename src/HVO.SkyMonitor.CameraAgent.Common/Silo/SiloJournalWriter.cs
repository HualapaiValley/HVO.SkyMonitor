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
    AfterSegmentSpooled,
    AfterSegmentPut,
    AfterSegmentCommitted,
    AfterIndexApplied
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
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var result = await store.PutCreateOnlyAsync(new(key, length, sha256, open, metadata), cancellationToken)
                .ConfigureAwait(false);
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

internal sealed record SiloCommittedSegment(SiloJournalPosition Position, string Sha256, string VersionId, SiloJournalSegment Segment);

/// <summary>
/// The single journal writer for one bucket (#1965 4.5). Sequences are gapless within a generation and each segment
/// names its predecessor's SHA-256. One segment is pending at a time; its exact bytes are spooled before its first PUT
/// and replayed after a crash, so a sequence is never reused for different bytes.
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

    public ISiloObjectStore Store => local;

    /// <summary>Open generation 1 at install. Its segment is the first of the journal.</summary>
    public async ValueTask<SiloCommittedSegment> OpenFirstGenerationAsync(
        IReadOnlyDictionary<string, long> identityFloors, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CompletePendingLockedAsync(cancellationToken).ConfigureAwait(false);
            var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head.Sequence != 0 || head.WriterGeneration != 1)
            {
                throw new InvalidOperationException("Generation 1 opens only an empty journal.");
            }
            return await AppendLockedAsync(
                [new SiloGenerationOpenRecord(1, "install", 0, identityFloors)], null, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Open a later generation after takeover. #1972 proves the highest issued generation through its witnesses and
    /// supplies the record; this writer only orders it and makes it the first segment of the new generation.
    /// </summary>
    public async ValueTask<SiloCommittedSegment> OpenGenerationAsync(SiloGenerationOpenRecord open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CompletePendingLockedAsync(cancellationToken).ConfigureAwait(false);
            var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head.Position is not { } end || open.PrefixEnd != end || !string.Equals(open.PrefixEndSha256, head.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A new generation names the committed journal end it continues.");
            }
            await state.AdvanceWriterGenerationAsync(open.Generation, cancellationToken).ConfigureAwait(false);
            return await AppendLockedAsync([open], null, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Finish a pending segment left by a crash, replaying its exact spooled bytes.</summary>
    public async ValueTask<SiloCommittedSegment?> ResumePendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompletePendingLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SiloCommittedSegment> AppendAsync(
        IReadOnlyList<SiloJournalRecord> records, CancellationToken cancellationToken) =>
        await AppendAsync(records, null, null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Append one segment. A publication names its group revision, so the commit and the group's move to
    /// <c>recorded</c> happen in one transaction.
    /// </summary>
    internal async ValueTask<SiloCommittedSegment> AppendAsync(
        IReadOnlyList<SiloJournalRecord> records, string? groupId, string? revisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0 || records.Any(static record => record is SiloGenerationOpenRecord))
        {
            throw new ArgumentException("Append at least one record; generations open through their own operations.", nameof(records));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CompletePendingLockedAsync(cancellationToken).ConfigureAwait(false);
            return await AppendLockedAsync(records, groupId, revisionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<SiloCommittedSegment> AppendLockedAsync(
        IReadOnlyList<SiloJournalRecord> records, string? groupId, string? revisionId, CancellationToken cancellationToken)
    {
        var head = await state.ReadHeadAsync(cancellationToken).ConfigureAwait(false);
        var position = head.Generation == head.WriterGeneration
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
        await state.InsertPendingAsync(new(position, sha, bytes.Length, groupId, revisionId), cancellationToken).ConfigureAwait(false);
        return await CompletePendingLockedAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The pending segment vanished before it committed.");
    }

    private async ValueTask<SiloCommittedSegment?> CompletePendingLockedAsync(CancellationToken cancellationToken)
    {
        var pending = await state.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }
        var name = SiloObjectSpool.NameOf(pending.Sha256);
        var bytes = await spool.ReadAsync(name, pending.Sha256, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"The exact bytes of pending journal segment {pending.Position} are lost from the spool; the writer stays fenced.");
        var key = SiloObjectNamespace.SegmentKey(pending.Position);
        var created = await SiloCreateOnly.PutVerifiedAsync(local, key, bytes.Length, pending.Sha256, SiloCreateOnly.FromBytes(bytes),
            SiloFormats.JournalSegment, binding.BindingId, clock, () => faults?.Hit(SiloFaultPoint.AfterSegmentPut), cancellationToken)
            .ConfigureAwait(false);
        await state.CommitPendingAsync(pending, created.VersionId, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterSegmentCommitted);
        spool.Delete(name);
        var segment = SiloJson.ReadSegment(bytes, "journal segment " + pending.Position);
        return new(pending.Position, pending.Sha256, created.VersionId, segment);
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
