using System.Text.Json.Serialization;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal enum SiloFixtureMode { Inventory, DryRun, Publish }

internal sealed record SiloFixtureCapacity(long StateFreeBytes, long StoreFreeBytes)
{
    internal bool Allows(long reserve, long charge) =>
        Math.Min(StateFreeBytes, StoreFreeBytes) - charge >= reserve;
}

internal sealed record SiloFixtureReport(string Format, SiloFixtureMode Mode, string SelectionSha256, string SourceId,
    DateTimeOffset AsOfUtc, DateTimeOffset? CapturedFromUtc, DateTimeOffset? CapturedToUtc,
    long MaximumBytes, long MinimumFreeBytes, SiloFixtureCapacity? InitialCapacity, long CandidateBytes, long PlannedBytes,
    IReadOnlyList<SiloFixtureRecord> Records, string Lineage = "source-processing-lineage-not-retained")
{
    [JsonIgnore]
    public int ExitCode => Records.Any(static row => row.Status == "failed" ||
        row.Status == "skipped" && row.Reason != "already-imported") ? 2 : 0;
}

internal delegate ValueTask<bool> SiloFixturePublish(SiloGroupDraft draft,
    Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open, Action requireHeadroom, CancellationToken cancellationToken);

internal sealed class SiloFixtureImport(ConfinedReadDirectory source, SiloFixtureInventory inventory)
{
    internal const string ReportFormat = "hvo.cameraagent.silo.fixture-import-report/1";
    private const string ReserveFailure = "free-space-reserve";
    private const string ProbeFailure = "capacity-probe-unavailable";
    // Charge both copies even when the two roots are on separate disks. Include filesystem, object and journal overhead.
    private const long PublicationOverheadBytes = 1024 * 1024;

    internal ValueTask<SiloFixtureReport> RunForecastAsync(SiloFixtureTarget target, SiloCatalog? catalog,
        Func<SiloFixtureCapacity> readCapacity, CancellationToken cancellationToken)
    {
        var pending = target.Pending;
        return RunAsync(SiloFixtureMode.DryRun, readCapacity, ForecastAsync, cancellationToken);

        async ValueTask<bool> ForecastAsync(SiloGroupDraft draft,
            Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> _, Action requireHeadroom, CancellationToken token)
        {
            var wouldImport = await ForecastPairAsync(target, pending, catalog, draft, requireHeadroom, token).ConfigureAwait(false);
            if (wouldImport && pending?.GroupId == draft.GroupId)
                pending = null;
            return wouldImport;
        }
    }

    public async ValueTask<SiloFixtureReport> RunAsync(SiloFixtureMode mode, Func<SiloFixtureCapacity>? readCapacity,
        SiloFixturePublish? publish,
        CancellationToken cancellationToken)
    {
        var selection = inventory.Selection;
        var output = new List<SiloFixtureRecord>(inventory.Records.Count);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        var initialCapacity = mode == SiloFixtureMode.DryRun ? readCapacity!() : null;
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
            if (length > selection.MaximumBytes - plannedBytes)
                stop = "byte-limit";

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
            var result = await ProcessAsync(mode, record, publish, RequireHeadroom, cancellationToken).ConfigureAwait(false);
            output.Add(result);
            if (result.Status == "would-import")
                projectedCharge += charge;
            if (result.Reason is ReserveFailure or ProbeFailure)
                plannedBytes -= length;
            if (result.Reason == ReserveFailure)
            {
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

            void RequireHeadroom()
            {
                if (mode == SiloFixtureMode.DryRun)
                {
                    if (!initialCapacity!.Allows(selection.MinimumFreeBytes, charge + projectedCharge))
                        throw new SiloConflictException(ReserveFailure, "Publication would cross the free-space reserve.");
                    return;
                }

                SiloFixtureCapacity capacity;
                try
                {
                    capacity = readCapacity!();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SiloConflictException)
                {
                    throw new SiloConflictException(ProbeFailure, "Could not recheck free space on the target volumes.");
                }
                initialCapacity ??= capacity;
                if (!capacity.Allows(selection.MinimumFreeBytes, charge))
                    throw new SiloConflictException(ReserveFailure, "Publication would cross the free-space reserve.");
            }
        }

        var timed = inventory.Records.Where(static record => record.Source is not null).ToArray();
        return new(ReportFormat, mode, inventory.SelectionSha256, selection.SourceId, inventory.AsOfUtc,
            timed.Select(static record => (DateTimeOffset?)record.Source!.CapturedFromUtc).Min(),
            timed.Select(static record => (DateTimeOffset?)record.Source!.CapturedToUtc).Max(),
            selection.MaximumBytes, selection.MinimumFreeBytes, initialCapacity,
            inventory.Records.Where(static record => record.Status == "selected")
                .Sum(static record => record.Objects!.Sum(static item => item.Length)), plannedBytes, output);
    }

    private async ValueTask<SiloFixtureRecord> ProcessAsync(SiloFixtureMode mode, SiloFixtureRecord record,
        SiloFixturePublish? publish, Action requireHeadroom, CancellationToken cancellationToken)
    {
        if (mode == SiloFixtureMode.Inventory)
            return record;
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

        var draft = BuildDraft(record, image, sidecar);
        try
        {
            var added = await publish!(draft, (member, token) => SiloArtifactStorage.OpenMemoryAsync(
                member.ArtifactRevision == SiloFixtureInventory.ImageRevision ? imageBytes : sidecarBytes, token),
                requireHeadroom, cancellationToken).ConfigureAwait(false);
            var status = added ? (mode == SiloFixtureMode.DryRun ? "would-import" : "imported") : "skipped";
            return record with { Status = status, Reason = added ? null : "already-imported" };
        }
        catch (SiloConflictException exception)
        {
            return record with { Status = exception.Code == ReserveFailure ? "skipped" : "failed", Reason = exception.Code };
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
            ? [new(SiloFixtureInventory.ArtifactIdFor(sourceId, prior.SourceArtifactId, prior.Sha256).ToString("D"),
                SiloFixtureInventory.ImageRevision, prior.Sha256, SiloDependencyKind.Reopen, Required: true)]
            : [];
        return new(SiloFixtureInventory.GroupKind, record.GroupId!, SiloFixtureInventory.GroupRevision, facts.CapturedToUtc,
            facts.CapturedFromUtc, facts.CapturedToUtc,
            [SiloArtifactStorage.Member(id, SiloFixtureInventory.ImageRevision, image.Role, image.RetentionClass,
                 "image/png", image.Length, image.Sha256),
             SiloArtifactStorage.Member(id, SiloFixtureInventory.SidecarRevision, sidecar.Role, sidecar.RetentionClass,
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
                imageSha256 = image.Sha256,
                sidecarSha256 = sidecar.Sha256,
                lineage = "source-processing-lineage-not-retained"
            })), CameraId: facts.SourceCameraId);
    }

    internal static async ValueTask<bool> PublishPairAsync(SiloPublicationState state, SiloPublisher publisher, SiloCatalog catalog,
        SiloGroupDraft draft, Func<SiloDraftMember, CancellationToken, ValueTask<Stream>> open, Action requireHeadroom,
        CancellationToken cancellationToken)
    {
        var wasVisible = await state.ReachedFirstP4Async(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
        if (!wasVisible)
        {
            RequireCompatiblePending(draft, await state.ReadPendingAsync(cancellationToken).ConfigureAwait(false));
            requireHeadroom();
        }

        await publisher.StageAsync(draft, open, cancellationToken).ConfigureAwait(false);
        var outcome = await publisher.PublishAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
        if (outcome.BlockedCode is { } conflict)
            throw new SiloConflictException(conflict, "Silo could not settle the fixture publication.");
        if (outcome.State != SiloStagedState.Visible)
            throw new SiloConflictException("fixture-reopen-failed", "The fixture did not pass exact Silo reopen verification.");
        await RequireReopenAsync(catalog, draft, cancellationToken).ConfigureAwait(false);
        return !wasVisible;
    }

    private static async ValueTask<bool> ForecastPairAsync(SiloFixtureTarget target, SiloPendingSegment? pending,
        SiloCatalog? catalog, SiloGroupDraft draft, Action requireHeadroom, CancellationToken cancellationToken)
    {
        target.Receipts.TryGetValue(draft.GroupId, out var receipt);
        var visible = receipt?.State == SiloStagedState.Visible;
        if (!visible)
        {
            RequireCompatiblePending(draft, pending);
            requireHeadroom();
        }
        SiloPublisher.ValidateDraft(draft);
        if (receipt is not null)
        {
            SiloPublisher.RequireMatchingDraft(draft, receipt.DraftSha256, SiloHash.Sha256(SiloJson.Serialize(draft)));
            if (receipt.State is SiloStagedState.Visible or SiloStagedState.Abandoned && receipt.BlockedCode is { } conflict)
                throw new SiloConflictException(conflict, "Silo could not settle the fixture publication.");
            if (receipt.State == SiloStagedState.Abandoned)
                throw new SiloConflictException("fixture-reopen-failed", "The fixture publication was abandoned.");
        }
        if (visible)
            await RequireReopenAsync(catalog, draft, cancellationToken).ConfigureAwait(false);
        return !visible;
    }

    private static void RequireCompatiblePending(SiloGroupDraft draft, SiloPendingSegment? pending)
    {
        if (pending is not null && (pending.GroupId != draft.GroupId || pending.RevisionId != draft.RevisionId))
            throw new SiloConflictException("fixture-other-publication-pending", "Another publication is still pending.");
    }

    private static async ValueTask RequireReopenAsync(SiloCatalog? catalog, SiloGroupDraft draft, CancellationToken cancellationToken)
    {
        if (catalog is null ||
            !(await catalog.ReopenAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false)).Reopenable)
            throw new SiloConflictException("fixture-reopen-failed", "The fixture did not pass exact Silo reopen verification.");
    }
}
