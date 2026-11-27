using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal enum SiloFixtureMode { Inventory, DryRun, Publish }

internal sealed record SiloFixtureCapacity(long StateFreeBytes, long StoreFreeBytes)
{
    internal bool Allows(long reserve, long charge) =>
        Math.Min(StateFreeBytes, StoreFreeBytes) - charge >= reserve;
}

internal sealed record SiloFixtureReport(string Format, SiloFixtureMode Mode, string SelectionSha256, string SourceId,
    DateTimeOffset AsOfUtc, DateTimeOffset? CapturedFromUtc, DateTimeOffset? CapturedToUtc, int DisplayRetentionDays,
    long MaximumBytes, long MinimumFreeBytes, SiloFixtureCapacity? InitialCapacity, long CandidateBytes, long PlannedBytes,
    IReadOnlyList<SiloFixtureRecord> Records, string Lineage = "source-processing-lineage-not-retained");

internal delegate ValueTask<bool> SiloFixturePublish(SiloGroupDraft draft,
    Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open, CancellationToken cancellationToken);

internal sealed class SiloFixtureImport(ConfinedReadDirectory source, SiloFixtureInventory inventory)
{
    internal const string ReportFormat = "hvo.cameraagent.silo.fixture-import-report/1";
    // Charge both copies even when the two roots are on separate disks. Include filesystem, object and journal overhead.
    private const long PublicationOverheadBytes = 1024 * 1024;

    public async ValueTask<SiloFixtureReport> RunAsync(SiloFixtureMode mode, Func<SiloFixtureCapacity>? readCapacity,
        SiloFixturePublish? publish,
        CancellationToken cancellationToken)
    {
        var selection = inventory.Selection;
        var output = new List<SiloFixtureRecord>(inventory.Records.Count);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        var initialCapacity = readCapacity?.Invoke();
        long plannedBytes = 0;
        long projectedCharge = 0;
        string? stop = null;

        foreach (var record in inventory.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Status != "selected")
            {
                output.Add(record);
                continue;
            }
            if (stop is not null)
            {
                output.Add(record with { Status = "skipped", Reason = stop });
                continue;
            }

            var length = record.Objects!.Sum(static item => item.Length);
            var charge = 2 * length + PublicationOverheadBytes;
            var capacity = mode == SiloFixtureMode.Publish ? readCapacity!() : initialCapacity;
            if (length > selection.MaximumBytes - plannedBytes)
                stop = "byte-limit";
            else if (capacity is not null && !capacity.Allows(selection.MinimumFreeBytes, charge + projectedCharge))
                stop = "free-space-reserve";

            if (stop is not null)
            {
                output.Add(record with { Status = "skipped", Reason = stop });
                continue;
            }
            if (record.UsableReopenImage is { } dependency && !settled.Contains(dependency.SourceArtifactId))
            {
                output.Add(record with { Status = "skipped", Reason = "reopen-dependency-unavailable" });
                continue;
            }

            plannedBytes += length;
            if (mode == SiloFixtureMode.DryRun)
                projectedCharge += charge;

            var result = await ProcessAsync(mode, record, publish,
                () => readCapacity!().Allows(selection.MinimumFreeBytes, charge), cancellationToken).ConfigureAwait(false);
            output.Add(result);
            if (result.Reason == "free-space-reserve")
            {
                plannedBytes -= length;
                stop = result.Reason;
            }
            else if (result.Status == "failed")
            {
                stop = "deferred-after-failure";
            }
            else
            {
                settled.Add(record.Source!.SourceArtifactId);
            }
        }

        var timed = inventory.Records.Where(static record => record.Source is not null).ToArray();
        return new(ReportFormat, mode, inventory.SelectionSha256, selection.SourceId, inventory.AsOfUtc,
            timed.Select(static record => (DateTimeOffset?)record.Source!.CapturedFromUtc).Min(),
            timed.Select(static record => (DateTimeOffset?)record.Source!.CapturedToUtc).Max(),
            selection.DisplayRetentionDays, selection.MaximumBytes, selection.MinimumFreeBytes, initialCapacity,
            inventory.Records.Where(static record => record.Status == "selected")
                .Sum(static record => record.Objects!.Sum(static item => item.Length)), plannedBytes, output);
    }

    private async ValueTask<SiloFixtureRecord> ProcessAsync(SiloFixtureMode mode, SiloFixtureRecord record,
        SiloFixturePublish? publish, Func<bool> roomToPublish, CancellationToken cancellationToken)
    {
        if (mode == SiloFixtureMode.Inventory)
            return record;
        if (mode == SiloFixtureMode.DryRun)
            return record with { Status = "would-import" };

        var image = record.Objects![0];
        var sidecar = record.Objects[1];
        byte[] imageBytes;
        byte[] sidecarBytes;
        try
        {
            // Read both before staging, even if Silo already has this group. Only these authenticated bytes reach the spool.
            imageBytes = await SiloFixtureInventory.ReadBoundedAsync(source, image.File, SiloFixtureInventory.MaximumImageBytes,
                cancellationToken).ConfigureAwait(false);
            sidecarBytes = await SiloFixtureInventory.ReadBoundedAsync(source, sidecar.File, SiloFixtureInventory.MaximumSidecarBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return record with { Status = "failed", Reason = "source-changed-or-nonconfined" };
        }
        if (imageBytes.LongLength != image.Length || SiloHash.Sha256(imageBytes) != image.Sha256 ||
            sidecarBytes.LongLength != sidecar.Length || SiloHash.Sha256(sidecarBytes) != sidecar.Sha256)
            return record with { Status = "failed", Reason = "source-changed-or-nonconfined" };

        if (!roomToPublish())
            return record with { Status = "skipped", Reason = "free-space-reserve" };

        var draft = BuildDraft(record, image, sidecar);
        try
        {
            var added = await publish!(draft, (member, token) => SiloArtifactStorage.OpenMemoryAsync(
                member.ArtifactRevision == SiloFixtureInventory.ImageRevision ? imageBytes : sidecarBytes, token),
                cancellationToken).ConfigureAwait(false);
            return record with { Status = added ? "imported" : "skipped", Reason = added ? null : "already-imported" };
        }
        catch (SiloConflictException exception)
        {
            return record with { Status = "failed", Reason = exception.Code };
        }
        catch (Exception exception) when (exception is IOException or SiloDataException or SiloCatalogDivergedException or Microsoft.Data.Sqlite.SqliteException)
        {
            return record with { Status = "failed", Reason = "publication-unavailable" };
        }
    }

    private SiloGroupDraft BuildDraft(SiloFixtureRecord record, SiloFixtureObject image, SiloFixtureObject sidecar)
    {
        var id = Guid.ParseExact(record.ArtifactId!, "D");
        var facts = record.Source!;
        var sourceId = inventory.Selection.SourceId;
        SiloManifestDependency[] dependencies = record.UsableReopenImage is { } prior
            ? [new(SiloFixtureInventory.ArtifactIdFor(sourceId, prior.SourceArtifactId).ToString("D"),
                SiloFixtureInventory.ImageRevision, prior.Sha256, SiloDependencyKind.Reopen, Required: true)]
            : [];
        return new(SiloFixtureInventory.GroupKind, record.GroupId!, SiloFixtureInventory.GroupRevision, facts.CapturedToUtc,
            facts.CapturedFromUtc, facts.CapturedToUtc,
            [SiloArtifactStorage.Member(id, SiloFixtureInventory.ImageRevision, image.Role, SiloRetentionClass.Display,
                 "image/png", image.Length, image.Sha256),
             SiloArtifactStorage.Member(id, SiloFixtureInventory.SidecarRevision, sidecar.Role, SiloRetentionClass.Metadata,
                 "application/json", sidecar.Length, sidecar.Sha256)],
            dependencies, new([], ImportSource: SiloJson.SerializeToElement(new
            {
                format = SiloFixtureInventory.SidecarFormat,
                sourceId,
                facts.SourceCaptureId,
                facts.SourceArtifactId,
                facts.SourceCameraId,
                facts.CapturedFromUtc,
                facts.CapturedToUtc,
                lineage = "source-processing-lineage-not-retained"
            })), CameraId: facts.SourceCameraId);
    }

    internal static async ValueTask<bool> PublishPairAsync(SiloPublicationState state, SiloPublisher publisher, SiloCatalog catalog,
        SiloGroupDraft draft, Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open, CancellationToken cancellationToken)
    {
        var wasVisible = await state.ReachedFirstP4Async(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
        var pending = await state.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (!wasVisible && pending is not null && (pending.GroupId != draft.GroupId || pending.RevisionId != draft.RevisionId))
            throw new SiloConflictException("fixture-other-publication-pending", "Another publication is still pending.");

        await publisher.StageAsync(draft, open, cancellationToken).ConfigureAwait(false);
        var outcome = await publisher.PublishAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
        if (outcome.BlockedCode is { } conflict)
            throw new SiloConflictException(conflict, "Silo could not settle the fixture publication.");
        if (outcome.State != SiloStagedState.Visible ||
            !(await catalog.ReopenAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false)).Reopenable)
            throw new SiloConflictException("fixture-reopen-failed", "The fixture did not pass exact Silo reopen verification.");
        return !wasVisible;
    }
}
