using System.Text.Json;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Why the authenticated journal prefix ends (#1965 4.5). Only <see cref="Tail"/> leaves no authority-unknown interval.</summary>
internal static class SiloPrefixEnd
{
    public const string Tail = "tail";
    public const string Gap = "gap";
    public const string Invalid = "invalid";
    public const string ChainMismatch = "chain-mismatch";
    public const string UnprovenGeneration = "unproven-generation";
    public const string SourceUnavailable = "source-unavailable";
    public const string PossiblyIncompleteTail = "possibly-incomplete-tail";
}

internal sealed record SiloWalkedSegment(
    SiloJournalPosition Position, string Sha256, string VersionId, SiloJournalSegment Segment, IReadOnlyList<SiloPositionedRecord> Records,
    bool Witnessed);

internal sealed record SiloWalkEnd(SiloJournalPosition? Last, string? LastSha256, string Reason);

/// <summary>
/// Reads the authenticated journal prefix forward from a known point: every segment in (g, seq) order, gapless within a
/// generation, naming its predecessor's SHA-256, and every generation boundary a witnessed opening that names the end it
/// continues. It stops at the first missing, altered or unauthenticated record or unproven boundary.
/// </summary>
internal sealed class SiloJournalWalker(ISiloObjectStore source, SiloStorageBinding binding, ISiloGenerationWitness witness)
{
    public ISiloObjectStore Source => source;

    /// <summary>
    /// Walk forward from a known point. A segment is delivered only once the next segment names its exact SHA-256, or
    /// as the tail when nothing follows, so an altered segment is never applied even when it still parses. When the
    /// writer's committed head is known, a tail at that position must carry that head's SHA-256.
    /// </summary>
    public async ValueTask<SiloWalkEnd> WalkAsync(
        SiloJournalPosition? after, string? afterSha256, Func<SiloWalkedSegment, ValueTask<bool>> onSegment, CancellationToken cancellationToken,
        SiloJournalPosition? knownHead = null, string? knownHeadSha256 = null)
    {
        var walk = new WalkState(after, afterSha256);
        while (true)
        {
            string? nextKey;
            try
            {
                nextKey = await NextSegmentKeyAsync(walk.ReadPosition, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.SourceUnavailable, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            if (nextKey is null || !SiloObjectNamespace.TryParseSegmentKey(nextKey, out var position))
            {
                return await FinishAsync(walk, SiloPrefixEnd.Tail, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            var continues = walk.ReadPosition is not { } last
                ? position == new SiloJournalPosition(1, 1)
                : position == new SiloJournalPosition(last.Generation, last.Sequence + 1) ||
                  position.Generation > last.Generation && position.Sequence == 1;
            if (!continues)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Gap, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            (string VersionId, byte[] Bytes)? read;
            try
            {
                read = await SiloVerifier.ReadCurrentAsync(source, nextKey, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.SourceUnavailable, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            if (read is not { } found)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Gap, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            SiloJournalSegment segment;
            IReadOnlyList<SiloPositionedRecord> records;
            try
            {
                (segment, records) = Parse(found.Bytes, position);
            }
            catch (SiloInvalidObjectException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            if (!string.Equals(segment.InstanceId, binding.InstanceId, StringComparison.Ordinal) ||
                !string.Equals(segment.StorageBindingId, binding.BindingId, StringComparison.Ordinal))
            {
                throw new SiloConflictException("silo.binding-mismatch",
                    $"Journal segment {position} belongs to another instance or storage binding; nothing was read from this bucket.");
            }
            if (segment.Position != position)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
            }
            if (segment.Previous != walk.ReadPosition || !string.Equals(segment.PreviousSha256, walk.ReadSha256, StringComparison.Ordinal))
            {
                // The successor does not name the pending segment's bytes: the pending segment is unauthenticated.
                return new(walk.DeliveredPosition, walk.DeliveredSha256, SiloPrefixEnd.ChainMismatch);
            }
            var witnessed = true;
            if (position.Sequence == 1 && position.Generation > 1)
            {
                var open = (SiloGenerationOpenRecord)segment.Records[0];
                if (open.PrefixEnd != walk.ReadPosition || !string.Equals(open.PrefixEndSha256, walk.ReadSha256, StringComparison.Ordinal))
                {
                    return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
                }
                witnessed = witness.IsWitnessed(open, position);
                if (!witnessed)
                {
                    return await FinishAsync(walk, SiloPrefixEnd.UnprovenGeneration, onSegment, knownHead, knownHeadSha256).ConfigureAwait(false);
                }
            }
            // This segment names the pending one's exact bytes, so the pending one is authenticated.
            if (walk.Pending is { } confirmed)
            {
                if (!await onSegment(confirmed).ConfigureAwait(false))
                {
                    return new(confirmed.Position, confirmed.Sha256, SiloPrefixEnd.Tail);
                }
                walk.Delivered(confirmed);
            }
            walk.Read(new(position, SiloHash.Sha256(found.Bytes), found.VersionId, segment, records, witnessed));
        }
    }

    private sealed class WalkState(SiloJournalPosition? after, string? afterSha256)
    {
        public SiloJournalPosition? DeliveredPosition { get; private set; } = after;
        public string? DeliveredSha256 { get; private set; } = afterSha256;
        public SiloWalkedSegment? Pending { get; private set; }
        public SiloJournalPosition? ReadPosition => Pending?.Position ?? DeliveredPosition;
        public string? ReadSha256 => Pending?.Sha256 ?? DeliveredSha256;

        public void Delivered(SiloWalkedSegment segment)
        {
            DeliveredPosition = segment.Position;
            DeliveredSha256 = segment.Sha256;
            Pending = null;
        }

        public void Read(SiloWalkedSegment segment) => Pending = segment;
    }

    /// <summary>
    /// The last read segment chains back to authenticated history but nothing after it confirms its bytes. It is the
    /// tail of the evidence and is delivered, unless the writer's known head at that position names other bytes.
    /// </summary>
    private static async ValueTask<SiloWalkEnd> FinishAsync(
        WalkState walk, string reason, Func<SiloWalkedSegment, ValueTask<bool>> onSegment, SiloJournalPosition? knownHead,
        string? knownHeadSha256)
    {
        if (walk.Pending is not { } tail)
        {
            return new(walk.DeliveredPosition, walk.DeliveredSha256, reason);
        }
        if (knownHead == tail.Position && !string.Equals(knownHeadSha256, tail.Sha256, StringComparison.Ordinal))
        {
            return new(walk.DeliveredPosition, walk.DeliveredSha256, SiloPrefixEnd.ChainMismatch);
        }
        await onSegment(tail).ConfigureAwait(false);
        return new(tail.Position, tail.Sha256, reason);
    }

    internal static (SiloJournalSegment Segment, IReadOnlyList<SiloPositionedRecord> Records) Parse(byte[] bytes, SiloJournalPosition position)
    {
        var subject = "journal segment " + position;
        var segment = SiloJson.ReadSegment(bytes, subject);
        using var document = JsonDocument.Parse(bytes);
        var raw = document.RootElement.GetProperty("records").EnumerateArray().Select(static element => element.GetRawText()).ToArray();
        var records = segment.Records.Select((record, index) => new SiloPositionedRecord(segment.Position, index, record, raw[index])).ToArray();
        return (segment, records);
    }

    private async ValueTask<string?> NextSegmentKeyAsync(SiloJournalPosition? after, CancellationToken cancellationToken)
    {
        var startAfter = after is { } position ? SiloObjectNamespace.SegmentKey(position) : null;
        await foreach (var key in source.ListKeysAsync(SiloObjectNamespace.JournalPrefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            if (SiloObjectNamespace.TryParseSegmentKey(key, out _))
            {
                return key;
            }
        }
        return null;
    }

    /// <summary>Publications named past the prefix. Nothing about them is claimed; they are reported as authority-unproven.</summary>
    public async ValueTask<IReadOnlyList<(SiloJournalPosition Position, int Index, string GroupId, string RevisionId)>> ReadUnprovenPublicationsAsync(
        SiloJournalPosition? after, CancellationToken cancellationToken)
    {
        var result = new List<(SiloJournalPosition, int, string, string)>();
        try
        {
            var startAfter = after is { } position ? SiloObjectNamespace.SegmentKey(position) : null;
            await foreach (var key in source.ListKeysAsync(SiloObjectNamespace.JournalPrefix, startAfter, cancellationToken).ConfigureAwait(false))
            {
                if (!SiloObjectNamespace.TryParseSegmentKey(key, out var segmentPosition))
                {
                    continue;
                }
                try
                {
                    if (await SiloVerifier.ReadCurrentAsync(source, key, cancellationToken).ConfigureAwait(false) is not { } found)
                    {
                        continue;
                    }
                    var (segment, _) = Parse(found.Bytes, segmentPosition);
                    for (var index = 0; index < segment.Records.Count; index++)
                    {
                        if (segment.Records[index] is SiloPublishRecord publish)
                        {
                            result.Add((segmentPosition, index, publish.GroupId, publish.RevisionId));
                        }
                    }
                }
                catch (SiloDataException)
                {
                    // An unreadable or unsupported object past the prefix authenticates nothing and names nothing.
                }
            }
        }
        catch (SiloStoreUnavailableException)
        {
            // What could not be listed is still unknown; the interval is already reported as authority-unknown.
        }
        return result;
    }
}

/// <summary>
/// Where to read the journal, whether that source may lag the lost writer (an archive replica), and the writer's committed
/// head when instance State survived, which authenticates the tail and exposes a lost one.
/// </summary>
internal sealed record SiloRebuildOptions(
    ISiloObjectStore JournalSource, bool SourceMayBeIncomplete = false, SiloJournalPosition? KnownHead = null, string? KnownHeadSha256 = null);

internal sealed record SiloRebuildResult(string StartKind, SiloJournalPosition? Anchor, SiloAuthorityReport Authority);

/// <summary>
/// The catalog's authority operations: startup catch-up from the cursor with P3 recovery, and rebuild of an empty index
/// from durable records. Startup work is bounded by the journal after the cursor, never by history size (#1965 F5).
/// </summary>
internal sealed class SiloCatalog(SiloCatalogIndex index, SiloStoreSet stores, ISiloGenerationWitness witness)
{
    public SiloCatalogIndex Index => index;

    public SiloStoreSet Stores => stores;

    /// <summary>
    /// Apply every authenticated segment after the cursor, then run P3 recovery on publications awaiting P4. A cursor that
    /// the journal no longer holds is divergence: the catalog must be rebuilt, not served as authoritative.
    /// </summary>
    public async ValueTask<SiloWalkEnd> CatchUpAsync(SiloFreshEvidence? fresh, CancellationToken cancellationToken)
    {
        var (cursor, cursorSha) = await index.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
        if (cursor is { } position)
        {
            var key = SiloObjectNamespace.SegmentKey(position);
            (string VersionId, byte[] Bytes)? found;
            try
            {
                found = await SiloVerifier.ReadCurrentAsync(stores.Local, key, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                await RecordEndAsync(new(cursor, cursorSha, SiloPrefixEnd.SourceUnavailable), cancellationToken).ConfigureAwait(false);
                return new(cursor, cursorSha, SiloPrefixEnd.SourceUnavailable);
            }
            if (found is not { } segment || !string.Equals(SiloHash.Sha256(segment.Bytes), cursorSha, StringComparison.Ordinal))
            {
                throw new SiloCatalogDivergedException(
                    $"The journal no longer holds the catalog cursor's segment {position} with its SHA-256; rebuild the catalog.");
            }
        }
        var walker = new SiloJournalWalker(stores.Local, index.Binding, witness);
        var end = await walker.WalkAsync(cursor, cursorSha, async segment =>
        {
            await index.ApplySegmentAsync(segment.Position, segment.Sha256, segment.Segment.PreviousSha256, segment.Records,
                segment.Witnessed, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        await RecordEndAsync(end, cancellationToken).ConfigureAwait(false);
        // Reported afresh on every catch-up, so a list from an earlier outage never outlives it.
        await index.RecordUnprovenAsync(end.Reason == SiloPrefixEnd.Tail
            ? []
            : await walker.ReadUnprovenPublicationsAsync(end.Last, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await index.EvaluatePublicationsAsync(stores, SiloEvaluationMode.Startup, fresh, retryClosed: false, cancellationToken)
            .ConfigureAwait(false);
        return end;
    }

    private async ValueTask RecordEndAsync(SiloWalkEnd end, CancellationToken cancellationToken)
    {
        var authority = await index.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
        var unknown = end.Reason != SiloPrefixEnd.Tail;
        await index.RecordAuthorityAsync(authority.StartKind, authority.Start, end.Reason, unknown, cancellationToken).ConfigureAwait(false);
        if (unknown != authority.UnknownInterval)
        {
            await index.RecomputeAllRolesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Retry closed revisions whose outcome may change, such as a timed-out GET (#1965 5.3).</summary>
    public ValueTask<int> RetryClosedAsync(CancellationToken cancellationToken) =>
        index.EvaluatePublicationsAsync(stores, SiloEvaluationMode.Startup, null, retryClosed: true, cancellationToken);

    public ValueTask<int> VerifyRolesAsync(int limit, CancellationToken cancellationToken, bool includeAvailable = false) =>
        index.VerifyRolesAsync(stores, limit, cancellationToken, includeAvailable);

    public ValueTask<SiloReopenResult> ReopenAsync(string groupId, string revisionId, CancellationToken cancellationToken) =>
        index.ReopenAsync(stores, groupId, revisionId, cancellationToken);

    /// <summary>
    /// Rebuild an empty index from durable records (#1965 5.3, 7.5). The index is built beside the live file and published
    /// by rename only when complete, so a crash or refusal leaves the previous file untouched. Replay starts at the newest
    /// trusted anchor whose bytes verify, falls back to the previous one, and otherwise starts at generation 1. Each
    /// manifest is verified through an eligible locator; member roles start unverified for the lazy phase.
    /// </summary>
    public static async ValueTask<SiloRebuildResult> RebuildAsync(
        SiloStateRoot root, SiloStorageBinding binding, SiloStoreSet stores, ISiloGenerationWitness witness, ISiloAnchorTrust trust,
        TimeProvider clock, SiloRebuildOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        var target = root.IndexDatabasePath;
        // A newer or foreign index is refused, never replaced by an older reader's rebuild.
        SiloSqlite.RefuseUnsupportedHeader(target, SiloCatalogIndex.Format);
        var temporary = root.Root.Resolve($"{SiloStateRoot.IndexDatabaseFileName}.rebuild-{Guid.NewGuid():N}");
        try
        {
            using var index = new SiloCatalogIndex(temporary, binding, clock);
            await index.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var walker = new SiloJournalWalker(options.JournalSource, binding, witness);
            var start = await ChooseStartAsync(walker, binding, trust, cancellationToken).ConfigureAwait(false);
            if (start.Checkpoint is { } checkpoint)
            {
                await index.ApplyCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            }
            var end = await walker.WalkAsync(start.Checkpoint?.Covers, start.Checkpoint?.CoversSha256, async segment =>
            {
                await index.ApplySegmentAsync(segment.Position, segment.Sha256, segment.Segment.PreviousSha256, segment.Records,
                    segment.Witnessed, cancellationToken).ConfigureAwait(false);
                return true;
            }, cancellationToken, options.KnownHead, options.KnownHeadSha256).ConfigureAwait(false);
            var lostTail = options.KnownHead is { } head && (end.Last is not { } last || last < head);
            var unknown = end.Reason != SiloPrefixEnd.Tail || options.SourceMayBeIncomplete || lostTail;
            var reason = end.Reason == SiloPrefixEnd.Tail && unknown ? SiloPrefixEnd.PossiblyIncompleteTail : end.Reason;
            await index.RecordAuthorityAsync(start.Checkpoint is null ? "genesis" : "anchor", start.Checkpoint?.Covers, reason, unknown,
                cancellationToken).ConfigureAwait(false);
            if (unknown)
            {
                await index.RecordUnprovenAsync(await walker.ReadUnprovenPublicationsAsync(end.Last, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                await index.RecomputeAllRolesAsync(cancellationToken).ConfigureAwait(false);
            }
            await index.EvaluatePublicationsAsync(stores, SiloEvaluationMode.Rebuild, null, retryClosed: false, cancellationToken)
                .ConfigureAwait(false);
            await index.SealForReplacementAsync(cancellationToken).ConfigureAwait(false);
            DurableSync.File(temporary);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var sidecar = target + suffix;
                if (File.Exists(sidecar))
                {
                    root.Root.Verify(sidecar, "silo-catalog-rebuild");
                    File.Delete(sidecar);
                }
            }
            File.Move(temporary, target, overwrite: true);
            DurableSync.Directory(root.Root.Path);
            using var rebuilt = new SiloCatalogIndex(target, binding, clock);
            await rebuilt.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return new(start.Checkpoint is null ? "genesis" : "anchor", start.Checkpoint?.Covers,
                await rebuilt.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var leftover = temporary + suffix;
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }
            }
        }
    }

    private sealed record Start(SiloJournalCheckpoint? Checkpoint);

    /// <summary>
    /// The newest checkpoint is a trusted anchor only if its exact bytes match an <c>anchor</c> record committed in the
    /// authenticated chain after it, and the seal evidence is trusted. A damaged or untrusted checkpoint authenticates
    /// nothing, and the search falls back.
    /// </summary>
    private static async ValueTask<Start> ChooseStartAsync(
        SiloJournalWalker walker, SiloStorageBinding binding, ISiloAnchorTrust trust, CancellationToken cancellationToken)
    {
        var source = walker.Source;
        var checkpoints = new List<(string Key, SiloJournalPosition Covers)>();
        try
        {
            await foreach (var key in source.ListKeysAsync(SiloObjectNamespace.CheckpointPrefix, null, cancellationToken).ConfigureAwait(false))
            {
                if (SiloObjectNamespace.TryParseCheckpointKey(key, out var covers))
                {
                    checkpoints.Add((key, covers));
                }
            }
        }
        catch (SiloStoreUnavailableException)
        {
            return new(null);
        }
        foreach (var (key, covers) in checkpoints.OrderByDescending(static checkpoint => checkpoint.Covers))
        {
            (string VersionId, byte[] Bytes)? read;
            try
            {
                read = await SiloVerifier.ReadCurrentAsync(source, key, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                continue;
            }
            if (read is not { } found)
            {
                continue;
            }
            SiloJournalCheckpoint checkpoint;
            try
            {
                checkpoint = SiloJson.ReadCheckpoint(found.Bytes, "journal checkpoint " + covers);
            }
            catch (SiloInvalidObjectException)
            {
                continue;
            }
            if (!string.Equals(checkpoint.InstanceId, binding.InstanceId, StringComparison.Ordinal) ||
                !string.Equals(checkpoint.StorageBindingId, binding.BindingId, StringComparison.Ordinal))
            {
                throw new SiloConflictException("silo.binding-mismatch",
                    $"Journal checkpoint {covers} belongs to another instance or storage binding; nothing was read from this bucket.");
            }
            if (checkpoint.Covers != covers)
            {
                continue;
            }
            var sha = SiloHash.Sha256(found.Bytes);
            var sameStore = string.Equals(source.Descriptor.StoreId, binding.StoreId, StringComparison.Ordinal) &&
                string.Equals(source.Descriptor.Bucket, binding.Bucket, StringComparison.Ordinal);
            var trusted = false;
            await walker.WalkAsync(checkpoint.Covers, checkpoint.CoversSha256, segment =>
            {
                foreach (var record in segment.Records)
                {
                    if (record.Record is SiloAnchorRecord anchor &&
                        string.Equals(anchor.Checkpoint.Key, key, StringComparison.Ordinal) &&
                        (!sameStore || string.Equals(anchor.Checkpoint.VersionId, found.VersionId, StringComparison.Ordinal)) &&
                        string.Equals(anchor.CheckpointSha256, sha, StringComparison.Ordinal) && anchor.CheckpointLength == found.Bytes.Length &&
                        anchor.Covers == checkpoint.Covers && string.Equals(anchor.CoversSha256, checkpoint.CoversSha256, StringComparison.Ordinal) &&
                        trust.IsTrusted(anchor, segment.Position))
                    {
                        trusted = true;
                    }
                }
                return ValueTask.FromResult(!trusted);
            }, cancellationToken).ConfigureAwait(false);
            if (trusted)
            {
                return new(checkpoint);
            }
        }
        return new(null);
    }
}

/// <summary>
/// Write a checkpoint of the catalog's applied records at its cursor (#1965 4.5). It becomes a trusted anchor only
/// after #1972 seals it in a backup and appends an <c>anchor</c> record with that evidence.
/// </summary>
internal sealed class SiloCheckpointWriter(
    SiloCatalogIndex index, SiloObjectSpool spool, ISiloObjectStore local, SiloStorageBinding binding, TimeProvider clock)
{
    /// <summary>Returns the anchor record that names the verified checkpoint, ready for seal evidence.</summary>
    public async ValueTask<SiloAnchorRecord> WriteAsync(CancellationToken cancellationToken)
    {
        var (covers, coversSha, entries) = await index.ExportRecordsAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, binding.InstanceId, binding.BindingId, covers, coversSha, entries);
        var bytes = SiloJson.Serialize(checkpoint);
        var sha = SiloHash.Sha256(bytes);
        var name = await spool.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        var key = SiloObjectNamespace.CheckpointKey(covers);
        var created = await SiloCreateOnly.PutVerifiedAsync(local, key, bytes.Length, sha, SiloCreateOnly.FromBytes(bytes),
            SiloFormats.JournalCheckpoint, binding.BindingId, clock, null, cancellationToken).ConfigureAwait(false);
        spool.Delete(name);
        return new(new(local.Descriptor.StoreId, local.Descriptor.Bucket, key, created.VersionId), bytes.Length, sha, covers, coversSha);
    }
}

/// <summary>Journal retention (#1965 4.5): segments behind the newest trusted anchor, except those since the previous one.</summary>
internal static class SiloJournalRetention
{
    /// <summary>
    /// The last position whose segments may be removed, given the positions covered by trusted anchors. With fewer than
    /// two trusted anchors nothing may be removed, because a damaged newest checkpoint must still fall back.
    /// </summary>
    public static SiloJournalPosition? RemovableThrough(IEnumerable<SiloJournalPosition> trustedAnchorCovers)
    {
        var ordered = trustedAnchorCovers.Distinct().Order().ToArray();
        return ordered.Length < 2 ? null : ordered[^2];
    }
}
