using System.Diagnostics.CodeAnalysis;
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

/// <summary>What a scan past the authenticated prefix found, and whether it read everything it listed.</summary>
internal sealed record SiloUnprovenScan(
    IReadOnlyList<(SiloJournalPosition Position, int Index, string GroupId, string RevisionId)> Found, bool Complete)
{
    /// <summary>Nothing was read: whatever is already known about the gap is kept.</summary>
    public static SiloUnprovenScan NotRead { get; } = new([], false);

    /// <summary>The committed head was read again: nothing past it is unproven authority.</summary>
    public static SiloUnprovenScan Healed { get; } = new([], true);
}

/// <summary>
/// What bounds a walk of the journal (#1965 4.5). Without the writer's evidence, as after total loss, the last segment
/// that chains back is the tail of the evidence. With it, the committed head bounds every walk and its bytes must match;
/// a writer that committed nothing proves the journal empty, so nothing at all is authority, even a segment that landed.
/// </summary>
internal readonly record struct SiloJournalBound(bool Known, SiloJournalPosition? Head, string? HeadSha256)
{
    public static SiloJournalBound Unavailable => default;

    public static SiloJournalBound Of(SiloWriterEvidence? writer) =>
        writer is null ? Unavailable : new(true, writer.Head, writer.HeadSha256);

    /// <summary>Whether a segment at this position may be read as authority.</summary>
    public bool Admits(SiloJournalPosition position) => !Known || Head is { } head && position <= head;

    /// <summary>Whether a walk that ended here read the whole committed journal, exactly.</summary>
    public bool IsReachedBy(SiloJournalPosition? last, string? lastSha256) =>
        !Known || last == Head && string.Equals(lastSha256, HeadSha256, StringComparison.Ordinal);
}

/// <summary>
/// Reads the authenticated journal prefix forward from a known point: every segment in (g, seq) order, gapless within a
/// generation, naming its predecessor's SHA-256, and every generation boundary an opening witnessed by its exact bytes.
/// A segment is delivered only once the next segment names its exact SHA-256, or, at the writer's known committed head,
/// once its SHA-256 equals that head's. Nothing past a known head, and nothing at all in a journal the writer proves
/// empty, is read as authority. Without the writer's evidence, the last segment that chains back is the tail of the
/// evidence. The walk stops at the first missing, altered or unauthenticated record or unproven boundary.
/// </summary>
internal sealed class SiloJournalWalker(ISiloObjectStore source, SiloStorageBinding binding, ISiloGenerationWitness witness)
{
    public ISiloObjectStore Source => source;

    public async ValueTask<SiloWalkEnd> WalkAsync(
        SiloJournalPosition? after, string? afterSha256, Func<SiloWalkedSegment, ValueTask<bool>> onSegment, CancellationToken cancellationToken,
        SiloJournalBound bound = default)
    {
        var walk = new WalkState(after, afterSha256);
        while (true)
        {
            if (bound.Known && (bound.Head is not { } head || walk.ReadPosition is { } read && read >= head))
            {
                return await FinishAsync(walk, SiloPrefixEnd.Tail, onSegment, bound).ConfigureAwait(false);
            }
            string? nextKey;
            try
            {
                nextKey = await NextSegmentKeyAsync(walk.ReadPosition, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.SourceUnavailable, onSegment, bound).ConfigureAwait(false);
            }
            if (nextKey is null || !SiloObjectNamespace.TryParseSegmentKey(nextKey, out var position))
            {
                return await FinishAsync(walk, SiloPrefixEnd.Tail, onSegment, bound).ConfigureAwait(false);
            }
            var continues = walk.ReadPosition is not { } last
                ? position == new SiloJournalPosition(1, 1)
                : position == new SiloJournalPosition(last.Generation, last.Sequence + 1) ||
                  position.Generation > last.Generation && position.Sequence == 1;
            if (!continues || !bound.Admits(position))
            {
                return await FinishAsync(walk, SiloPrefixEnd.Gap, onSegment, bound).ConfigureAwait(false);
            }
            (string VersionId, byte[] Bytes)? found;
            try
            {
                found = await SiloVerifier.ReadCurrentAsync(source, nextKey, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.SourceUnavailable, onSegment, bound).ConfigureAwait(false);
            }
            catch (SiloInvalidObjectException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, bound).ConfigureAwait(false);
            }
            if (found is not { } current)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Gap, onSegment, bound).ConfigureAwait(false);
            }
            SiloJournalSegment segment;
            IReadOnlyList<SiloPositionedRecord> records;
            try
            {
                (segment, records) = Parse(current.Bytes, position);
            }
            catch (SiloInvalidObjectException)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, bound).ConfigureAwait(false);
            }
            if (!string.Equals(segment.InstanceId, binding.InstanceId, StringComparison.Ordinal) ||
                !string.Equals(segment.StorageBindingId, binding.BindingId, StringComparison.Ordinal))
            {
                throw new SiloConflictException("silo.binding-mismatch",
                    $"Journal segment {position} belongs to another instance or storage binding; nothing was read from this bucket.");
            }
            if (segment.Position != position)
            {
                return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, bound).ConfigureAwait(false);
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
                    return await FinishAsync(walk, SiloPrefixEnd.Invalid, onSegment, bound).ConfigureAwait(false);
                }
                witnessed = witness.IsWitnessed(open, position, SiloHash.Sha256(current.Bytes));
                if (!witnessed)
                {
                    return await FinishAsync(walk, SiloPrefixEnd.UnprovenGeneration, onSegment, bound).ConfigureAwait(false);
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
            walk.Read(new(position, SiloHash.Sha256(current.Bytes), current.VersionId, segment, records, witnessed));
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
    /// Nothing after the last read segment confirms its bytes. At the writer's known head it is delivered only if its
    /// SHA-256 is the head's; otherwise it chains back to authenticated history and is the tail of the evidence.
    /// </summary>
    private static async ValueTask<SiloWalkEnd> FinishAsync(
        WalkState walk, string reason, Func<SiloWalkedSegment, ValueTask<bool>> onSegment, SiloJournalBound bound)
    {
        if (walk.Pending is not { } tail)
        {
            return new(walk.DeliveredPosition, walk.DeliveredSha256, reason);
        }
        if (bound.Known && bound.Head == tail.Position && !string.Equals(bound.HeadSha256, tail.Sha256, StringComparison.Ordinal))
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

    /// <summary>The predecessor hash named by the first segment after a position: the authenticated SHA-256 of that position.</summary>
    public async ValueTask<string?> SuccessorNamedShaAsync(SiloJournalPosition position, CancellationToken cancellationToken)
    {
        var key = await NextSegmentKeyAsync(position, cancellationToken).ConfigureAwait(false);
        if (key is null || !SiloObjectNamespace.TryParseSegmentKey(key, out var next) ||
            await SiloVerifier.ReadCurrentAsync(source, key, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }
        var (segment, _) = Parse(found.Bytes, next);
        return segment.Previous == position ? segment.PreviousSha256 : null;
    }

    /// <summary>
    /// Publications named past the prefix. Nothing about them is claimed; they are reported as authority-unproven. The
    /// scan is complete only if the listing and every listed read finished.
    /// </summary>
    public async ValueTask<SiloUnprovenScan> ReadUnprovenPublicationsAsync(SiloJournalPosition? after, CancellationToken cancellationToken)
    {
        var result = new List<(SiloJournalPosition, int, string, string)>();
        var complete = true;
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
                catch (SiloStoreUnavailableException)
                {
                    // What could not be read is still unknown; the names already known stay.
                    complete = false;
                }
            }
        }
        catch (SiloStoreUnavailableException)
        {
            // What could not be listed is still unknown; the interval is already reported as authority-unknown.
            complete = false;
        }
        return new(result, complete);
    }
}

/// <summary>
/// Where to read the journal; whether that source may lag the writer (an archive replica); the writer's surviving
/// evidence, which authenticates the tail, exposes a lost tail and names P3s awaiting their first P4; and the largest
/// checkpoint the reader accepts.
/// </summary>
internal sealed record SiloRebuildOptions(
    ISiloObjectStore JournalSource,
    bool SourceMayBeIncomplete = false,
    SiloWriterEvidence? Writer = null,
    long MaximumCheckpointBytes = SiloJson.MaximumMetadataObjectBytes);

internal sealed record SiloRebuildResult(string StartKind, SiloJournalPosition? Anchor, SiloAuthorityReport Authority);

/// <summary>
/// The catalog's authority operations: startup catch-up bounded by the writer's committed head, with P3 recovery, and
/// rebuild of an empty index from durable records. Startup work is bounded by the journal after the cursor, never by
/// history size (#1965 F5).
/// </summary>
internal sealed class SiloCatalog(SiloCatalogIndex index, SiloStoreSet stores, ISiloGenerationWitness witness, ISiloFaults? faults = null)
{
    public SiloCatalogIndex Index => index;

    public SiloStoreSet Stores => stores;

    /// <summary>
    /// Apply every authenticated segment after the cursor up to the writer's committed head, then run P3 recovery on
    /// publications awaiting P4. A segment past the committed head is not authority; the head itself must carry the
    /// head's SHA-256; and the authority-unknown interval clears only when the committed head is read again. A cursor the
    /// journal no longer holds, or one ahead of the committed head, is divergence: the catalog must be rebuilt.
    /// </summary>
    public async ValueTask<SiloWalkEnd> CatchUpAsync(SiloWriterEvidence writer, SiloFreshEvidence? fresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var (cursor, cursorSha) = await index.ReadCursorAsync(cancellationToken).ConfigureAwait(false);
        if (cursor is { } position)
        {
            if (writer.Head is not { } head || position > head)
            {
                throw new SiloCatalogDivergedException($"The catalog cursor {position} is ahead of the writer's committed head; rebuild the catalog.");
            }
            var key = SiloObjectNamespace.SegmentKey(position);
            (string VersionId, byte[] Bytes)? found;
            try
            {
                found = await SiloVerifier.ReadCurrentAsync(stores.Local, key, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloStoreUnavailableException)
            {
                await RecordEndAsync(new(cursor, cursorSha, SiloPrefixEnd.SourceUnavailable), unknown: true, SiloUnprovenScan.NotRead,
                    cancellationToken).ConfigureAwait(false);
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
        }, cancellationToken, SiloJournalBound.Of(writer)).ConfigureAwait(false);
        var healed = end.Reason == SiloPrefixEnd.Tail && SiloJournalBound.Of(writer).IsReachedBy(end.Last, end.LastSha256);
        var unproven = healed
            ? SiloUnprovenScan.Healed
            : await walker.ReadUnprovenPublicationsAsync(end.Last, cancellationToken).ConfigureAwait(false);
        await RecordEndAsync(healed ? end : end with { Reason = end.Reason == SiloPrefixEnd.Tail ? SiloPrefixEnd.PossiblyIncompleteTail : end.Reason },
            !healed, unproven, cancellationToken).ConfigureAwait(false);
        await index.EvaluatePublicationsAsync(stores, fresh, retryClosed: false, cancellationToken).ConfigureAwait(false);
        return end;
    }

    private async ValueTask RecordEndAsync(SiloWalkEnd end, bool unknown, SiloUnprovenScan unproven, CancellationToken cancellationToken)
    {
        await index.RecordAuthorityEndAsync(null, null, end.Reason, unknown, unproven, cancellationToken).ConfigureAwait(false);
        faults?.Hit(SiloFaultPoint.AfterAuthorityRecorded);
    }

    /// <summary>Retry closed revisions whose outcome may change, such as a timed-out GET (#1965 5.3).</summary>
    public ValueTask<int> RetryClosedAsync(CancellationToken cancellationToken) =>
        index.EvaluatePublicationsAsync(stores, null, retryClosed: true, cancellationToken);

    public ValueTask<string?> ConfirmP4Async(string groupId, string revisionId, SiloFreshEvidence fresh, CancellationToken cancellationToken) =>
        index.ConfirmP4Async(stores, groupId, revisionId, fresh, cancellationToken);

    public ValueTask<int> VerifyRolesAsync(int limit, CancellationToken cancellationToken, bool includeAvailable = false) =>
        index.VerifyRolesAsync(stores, limit, cancellationToken, includeAvailable);

    public ValueTask<SiloReopenResult> ReopenAsync(string groupId, string revisionId, CancellationToken cancellationToken) =>
        index.ReopenAsync(stores, groupId, revisionId, cancellationToken);

    public ValueTask<SiloReopenResult> ReopenViewAsync(
        string streamId, string referenceCaptureId, string baseId, string revisionId, CancellationToken cancellationToken) =>
        index.ReopenViewAsync(stores, streamId, referenceCaptureId, baseId, revisionId, cancellationToken);

    /// <summary>
    /// Rebuild an empty index from durable records (#1965 5.3, 7.5). The live catalog, including committed pages in its
    /// WAL, is first proven to be this instance's own supported catalog on a copy, so a foreign or newer one is refused
    /// untouched. The new index is built aside, published as a complete <c>.replacement</c>, and swapped in by rename;
    /// <see cref="SiloCatalogFiles.Recover"/> finishes or discards any interruption. Replay starts at the newest trusted
    /// anchor whose exact version verifies, falls back to older ones, and otherwise starts at generation 1. With the
    /// writer's evidence, nothing past its committed head is adopted and a P3 awaiting its first P4 is recovered under
    /// P3 recovery rather than as settled history.
    /// </summary>
    public static async ValueTask<SiloRebuildResult> RebuildAsync(
        SiloStateRoot root, SiloStorageBinding binding, SiloStoreSet stores, ISiloGenerationWitness witness, ISiloAnchorTrust trust,
        TimeProvider clock, SiloRebuildOptions options, CancellationToken cancellationToken, ISiloFaults? faults = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(options);
        var target = root.IndexDatabasePath;
        var metadata = SiloPublicationState.BindingMetadata(binding);
        await SiloCatalogFiles.RecoverAsync(target, SiloCatalogIndex.Format, metadata, cancellationToken).ConfigureAwait(false);
        await SiloSqlite.InspectAsync(target, SiloCatalogIndex.Format, metadata, root.Root.Path, cancellationToken).ConfigureAwait(false);
        var building = SiloCatalogFiles.Building(target);
        SiloCatalogFiles.DeleteWithSidecars(building);
        var writer = options.Writer;
        var bound = SiloJournalBound.Of(writer);
        string startKind;
        SiloJournalPosition? anchor;
        try
        {
            using (var index = new SiloCatalogIndex(building, binding, clock))
            {
                await index.InitializeAsync(cancellationToken).ConfigureAwait(false);
                var walker = new SiloJournalWalker(options.JournalSource, binding, witness);
                var start = await ChooseStartAsync(walker, binding, trust, bound, options.MaximumCheckpointBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (start is { } checkpoint)
                {
                    await index.ApplyCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                }
                var end = await walker.WalkAsync(start?.Covers, start?.CoversSha256, async segment =>
                {
                    await index.ApplySegmentAsync(segment.Position, segment.Sha256, segment.Segment.PreviousSha256, segment.Records,
                        segment.Witnessed, cancellationToken).ConfigureAwait(false);
                    return true;
                }, cancellationToken, bound).ConfigureAwait(false);
                var reachedHead = bound.IsReachedBy(end.Last, end.LastSha256);
                var unknown = end.Reason != SiloPrefixEnd.Tail || options.SourceMayBeIncomplete || !reachedHead;
                var reason = end.Reason == SiloPrefixEnd.Tail && unknown ? SiloPrefixEnd.PossiblyIncompleteTail : end.Reason;
                startKind = start is null ? "genesis" : "anchor";
                anchor = start?.Covers;
                var unproven = unknown
                    ? await walker.ReadUnprovenPublicationsAsync(end.Last, cancellationToken).ConfigureAwait(false)
                    : SiloUnprovenScan.Healed;
                await index.RecordAuthorityEndAsync(startKind, anchor, reason, unknown, unproven, cancellationToken).ConfigureAwait(false);
                await index.MarkHistoryAsync(writer?.AwaitingFirstP4 ?? SiloWriterEvidence.Empty.AwaitingFirstP4, cancellationToken)
                    .ConfigureAwait(false);
                await index.EvaluatePublicationsAsync(stores, null, retryClosed: false, cancellationToken).ConfigureAwait(false);
                await index.SealForReplacementAsync(cancellationToken).ConfigureAwait(false);
            }
            DurableSync.File(building);
            File.Move(building, SiloCatalogFiles.Replacement(target));
            DurableSync.Directory(root.Root.Path);
        }
        finally
        {
            SiloCatalogFiles.DeleteWithSidecars(building);
        }
        // From here a complete replacement exists; any interruption is finished by Recover.
        faults?.Hit(SiloFaultPoint.AfterReplacementReady);
        if (File.Exists(target))
        {
            SiloCatalogFiles.Retire(target);
        }
        faults?.Hit(SiloFaultPoint.AfterTargetRetired);
        SiloCatalogFiles.Recover(target);
        using var rebuilt = new SiloCatalogIndex(target, binding, clock);
        await rebuilt.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return new(startKind, anchor, await rebuilt.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Choose the newest trusted anchor (#1965 4.5). A checkpoint's covered SHA-256 is the predecessor hash named by the
    /// first segment after it; the authenticated chain from there may hold several trusted <c>anchor</c> records for that
    /// checkpoint, each naming an exact version (and, for a replica, a receipt) of its own. Every one is tried in chain
    /// order until one's exact version reads back with its length and SHA-256 within the reader's bound; only then does
    /// selection fall back to an older checkpoint, and finally to generation 1. A receipt that names other bytes, another
    /// key or another store never qualifies its anchor.
    /// </summary>
    private static async ValueTask<SiloJournalCheckpoint?> ChooseStartAsync(
        SiloJournalWalker walker, SiloStorageBinding binding, ISiloAnchorTrust trust, SiloJournalBound bound, long maximumBytes,
        CancellationToken cancellationToken)
    {
        var source = walker.Source;
        var candidates = new List<(string Key, SiloJournalPosition Covers)>();
        try
        {
            await foreach (var key in source.ListKeysAsync(SiloObjectNamespace.CheckpointPrefix, null, cancellationToken).ConfigureAwait(false))
            {
                if (SiloObjectNamespace.TryParseCheckpointKey(key, out var covers) && bound.Admits(covers))
                {
                    candidates.Add((key, covers));
                }
            }
        }
        catch (SiloStoreUnavailableException)
        {
            return null;
        }
        foreach (var (key, covers) in candidates.OrderByDescending(static candidate => candidate.Covers))
        {
            string? coversSha;
            try
            {
                coversSha = await walker.SuccessorNamedShaAsync(covers, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloInvalidObjectException)
            {
                continue;
            }
            catch (SiloStoreUnavailableException)
            {
                continue;
            }
            if (coversSha is null)
            {
                continue;
            }
            SiloJournalCheckpoint? qualified = null;
            await walker.WalkAsync(covers, coversSha, async segment =>
            {
                foreach (var anchor in segment.Records.Select(static record => record.Record).OfType<SiloAnchorRecord>())
                {
                    if (string.Equals(anchor.Checkpoint.Key, key, StringComparison.Ordinal) && anchor.Covers == covers &&
                        string.Equals(anchor.CoversSha256, coversSha, StringComparison.Ordinal) && trust.IsTrusted(anchor, segment.Position) &&
                        await QualifyAsync(source, binding, trust, anchor, maximumBytes, cancellationToken).ConfigureAwait(false) is { } checkpoint)
                    {
                        qualified = checkpoint;
                        return false;
                    }
                }
                return true;
            }, cancellationToken, bound).ConfigureAwait(false);
            if (qualified is not null)
            {
                return qualified;
            }
        }
        return null;
    }

    /// <summary>
    /// One anchor's checkpoint, if its exact version reads back: the anchor's own version in the primary, or the exact
    /// version a verified receipt names in a replica. Anything else leaves this anchor unqualified.
    /// </summary>
    private static async ValueTask<SiloJournalCheckpoint?> QualifyAsync(
        ISiloObjectStore source, SiloStorageBinding binding, ISiloAnchorTrust trust, SiloAnchorRecord anchor, long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (anchor.CheckpointLength > maximumBytes)
        {
            return null;
        }
        var sameStore = string.Equals(source.Descriptor.StoreId, binding.StoreId, StringComparison.Ordinal) &&
            string.Equals(source.Descriptor.Bucket, binding.Bucket, StringComparison.Ordinal);
        var locator = sameStore
            ? anchor.Checkpoint with { StoreId = source.Descriptor.StoreId, Bucket = source.Descriptor.Bucket }
            : trust.ReceiptedCopy(anchor, source.Descriptor) is { } receipted &&
              string.Equals(receipted.StoreId, source.Descriptor.StoreId, StringComparison.Ordinal) &&
              string.Equals(receipted.Bucket, source.Descriptor.Bucket, StringComparison.Ordinal) &&
              string.Equals(receipted.Key, anchor.Checkpoint.Key, StringComparison.Ordinal)
                ? receipted
                : null;
        if (locator is null)
        {
            return null;
        }
        SiloJournalCheckpoint checkpoint;
        try
        {
            var probe = await SiloVerifier.ProbeAsync(source, locator, anchor.CheckpointLength, anchor.CheckpointSha256, TimeProvider.System,
                captureBytes: true, cancellationToken).ConfigureAwait(false);
            if (probe is not { Status: SiloProbeStatus.Verified, Bytes: { } bytes })
            {
                return null;
            }
            checkpoint = SiloJson.ReadCheckpoint(bytes, "journal checkpoint " + anchor.Covers);
        }
        catch (SiloInvalidObjectException)
        {
            return null;
        }
        catch (SiloStoreUnavailableException)
        {
            return null;
        }
        if (!string.Equals(checkpoint.InstanceId, binding.InstanceId, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.StorageBindingId, binding.BindingId, StringComparison.Ordinal))
        {
            throw new SiloConflictException("silo.binding-mismatch",
                $"Journal checkpoint {anchor.Covers} belongs to another instance or storage binding; nothing was read from this bucket.");
        }
        return checkpoint.Covers == anchor.Covers && string.Equals(checkpoint.CoversSha256, anchor.CoversSha256, StringComparison.Ordinal)
            ? checkpoint
            : null;
    }
}

/// <summary>The checkpoint would exceed what any reader accepts; it is refused before anything is written.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloCheckpointTooLargeException(long length, long maximum)
    : InvalidOperationException($"A checkpoint of {length} bytes exceeds the {maximum}-byte bound every reader enforces; it was not written.")
{
    public long Length { get; } = length;
    public long Maximum { get; } = maximum;
}

/// <summary>
/// Write a checkpoint of the catalog's applied records at its cursor (#1965 4.5). It becomes a trusted anchor only
/// after #1972 seals it in a backup and appends an <c>anchor</c> record with that evidence. A checkpoint larger than the
/// readers' bound is refused rather than written, so the writer never produces one that would disable a rebuild.
/// </summary>
internal sealed class SiloCheckpointWriter(
    SiloCatalogIndex index, SiloObjectSpool spool, ISiloObjectStore local, SiloStorageBinding binding, TimeProvider clock,
    long maximumBytes = SiloJson.MaximumMetadataObjectBytes)
{
    /// <summary>Returns the anchor record that names the verified checkpoint's exact version, ready for seal evidence.</summary>
    public async ValueTask<SiloAnchorRecord> WriteAsync(CancellationToken cancellationToken)
    {
        var (covers, coversSha, entries) = await index.ExportRecordsAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = new SiloJournalCheckpoint(SiloFormats.JournalCheckpoint, binding.InstanceId, binding.BindingId, covers, coversSha, entries);
        var bytes = SiloJson.Serialize(checkpoint);
        if (bytes.Length > maximumBytes)
        {
            throw new SiloCheckpointTooLargeException(bytes.Length, maximumBytes);
        }
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
