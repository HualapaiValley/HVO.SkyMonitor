using HVO.SkyMonitor.CameraAgent.Common.Silo;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    private const string DeferredSiloViewsDirectory = "deferred-centered-views";
    private long _deferredSiloViewCursor;

    private sealed record SiloViewChange(CenteredView View, long Order, bool Live, DateTimeOffset CommittedUtc, string? Previous);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Each failed change is retained for retry while the scan advances to independent changes.")]
    internal async ValueTask<(bool More, string? Status)> PublishSiloChangesAsync(int limit, CancellationToken cancellationToken)
    {
        if (silo is null) return (false, null);
        var cursor = await silo.ReadViewCursorAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? status = null;
        var deferred = ReadDeferredSiloViews(limit);
        foreach (var order in deferred)
        {
            try
            {
                var row = (await ReadSiloViewChangesAsync(order - 1, 1, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
                if (row.Order != order)
                    throw new InvalidDataException("A deferred Silo view change has no local registration.");
                if (row.Change is { } change && await PublishSiloViewChangeAsync(change, cancellationToken).ConfigureAwait(false))
                {
                    var path = silo.PhysicalState.Resolve(DeferredSiloViewPath(order));
                    File.Delete(path);
                    DurableSync.Directory(Path.GetDirectoryName(path)!);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                RecordFailure(order, exception);
            }
            _deferredSiloViewCursor = order;
        }
        if (deferred.Length < limit) _deferredSiloViewCursor = 0;

        var changes = await ReadSiloViewChangesAsync(cursor, limit, cancellationToken).ConfigureAwait(false);
        foreach (var (order, change) in changes)
        {
            if (!await TryPublishAsync(order, change).ConfigureAwait(false))
            {
                var relativePath = DeferredSiloViewPath(order);
                if (!File.Exists(silo.PhysicalState.Resolve(relativePath)))
                {
                    await AtomicPublisher.PublishAsync(silo.PhysicalState, relativePath, PublishMode.CreateNew,
                        static (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
                }
            }
            await silo.WriteViewCursorAsync(order, cancellationToken).ConfigureAwait(false);
        }
        var directory = silo.PhysicalState.Resolve(DeferredSiloViewsDirectory);
        var blocked = Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.pending").Any();
        return (changes.Count == limit, status ?? (blocked ? "silo-centered-view-unpublishable" : null));

        async ValueTask<bool> TryPublishAsync(long order, SiloViewChange? change)
        {
            if (change is null) return false;
            try
            {
                return await PublishSiloViewChangeAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                RecordFailure(order, exception);
                return false;
            }
        }

        void RecordFailure(long order, Exception exception)
        {
            LogSiloViewFailure(logger, order, exception);
            status ??= exception is SiloStoreUnavailableException ? "silo-store-unavailable" : "silo-centered-view-publication-failed";
        }

        async Task<List<(long Order, SiloViewChange? Change)>> ReadSiloViewChangesAsync(long after, int count, CancellationToken token)
        {
            using var command = CreateCommand(connection, null, """
                SELECT b.base_id, b.definition, b.registered_utc,
                       r.revision_id, r.change_order, r.published_utc, r.manifest, r.layers,
                       c.change_order, c.selected_live, c.committed_utc,
                       (SELECT revision_id FROM processing_centered_view_revisions prior
                        WHERE prior.base_id = r.base_id AND prior.change_order < r.change_order
                        ORDER BY prior.change_order DESC LIMIT 1)
                FROM processing_centered_view_changes c
                JOIN processing_centered_view_revisions r ON r.base_id = c.base_id AND r.revision_id = c.revision_id
                JOIN processing_centered_view_bases b ON b.base_id = c.base_id
                WHERE c.change_order > $cursor ORDER BY c.change_order LIMIT $limit;
                """, ("$cursor", after), ("$limit", count));
            var result = new List<(long, SiloViewChange?)>();
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var order = reader.GetInt64(8);
                try
                {
                    result.Add((order, new(ReadView(reader), order, reader.GetBoolean(9),
                        new DateTimeOffset(reader.GetInt64(10), TimeSpan.Zero), ReadNullableText(reader, 11))));
                }
                catch (Exception exception) when (exception is not OperationCanceledException && !token.IsCancellationRequested)
                {
                    RecordFailure(order, exception);
                    result.Add((order, null));
                }
            }
            return result;
        }
    }

    private async ValueTask<bool> PublishSiloViewChangeAsync(SiloViewChange change, CancellationToken cancellationToken)
    {
        var (view, order, live, committedUtc, previous) = change;
        var section = SiloCenteredViews.Section(view, previous);
        var definition = view.Base.Definition;
        var camera = Identity(definition.Stream.Camera);
        if (view.Revision.Order == order)
        {
            var draft = new SiloGroupDraft(SiloJson.CenteredViewGroupKind, view.Base.BaseId, view.Revision.RevisionId,
                view.Revision.PublishedUtc.ToUniversalTime(), definition.Reference.ExposureStartedUtc.ToUniversalTime(),
                definition.Reference.ExposureEndedUtc.ToUniversalTime(), [],
                SiloCenteredViews.ReopenDependencies(view, scienceDisplayed: definition.Kind == CenteredViewKind.Stack),
                new([]), previous, camera, section);
            SiloPublicationOutcome outcome;
            try
            {
                await silo!.Publisher.StageAsync(draft, cancellationToken).ConfigureAwait(false);
                outcome = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
            }
            catch (SiloConflictException conflict)
            {
                LogDeferredSiloView(logger, view.Base.BaseId, view.Revision.RevisionId, conflict.Code);
                return false;
            }
            if (outcome.BlockedCode == "silo.store-unavailable")
                throw new SiloStoreUnavailableException(silo!.Binding.StoreId, "publish centered view", writeMayHaveLanded: true);
            if (outcome.State != SiloStagedState.Visible || outcome.CatalogState != "visible")
            {
                LogDeferredSiloView(logger, view.Base.BaseId, view.Revision.RevisionId,
                    outcome.BlockedCode ?? outcome.CatalogOutcome ?? "unverified");
                return false;
            }
        }
        if (!await IsSiloViewVisibleAsync(view.Base.BaseId, view.Revision.RevisionId, cancellationToken).ConfigureAwait(false))
            return false;
        if (live)
        {
            var current = await silo!.Index.ReadCurrentLiveAsync(camera, cancellationToken).ConfigureAwait(false);
            // A deferred older change can enter the archive after recovery, but cannot replace a newer live issue.
            if (current is null || current.StartedOrder <= order)
            {
                if (!await silo.Publisher.PublishLiveViewAsync(new(camera, section.StreamId, view.Base.BaseId, view.Revision.RevisionId,
                    order, committedUtc.ToUniversalTime()), cancellationToken).ConfigureAwait(false))
                    return false;
            }
        }
        Notify(definition.Stream);
        return true;
    }

    private long[] ReadDeferredSiloViews(int limit)
    {
        var directory = silo!.PhysicalState.Resolve(DeferredSiloViewsDirectory);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.pending")
            .Select(static path => long.Parse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture))
            .Where(order => order > _deferredSiloViewCursor)
            .Order().Take(limit).ToArray();
    }

    private static string DeferredSiloViewPath(long order) =>
        Path.Combine(DeferredSiloViewsDirectory, order.ToString(CultureInfo.InvariantCulture) + ".pending");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Silo centered view {BaseId}/{RevisionId} is deferred with outcome {Outcome}")]
    private static partial void LogDeferredSiloView(ILogger logger, string baseId, string revisionId, string outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Silo centered view change {Order} could not be published")]
    private static partial void LogSiloViewFailure(ILogger logger, long order, Exception exception);

    private async ValueTask<bool> IsSiloViewVisibleAsync(string baseId, string revisionId, CancellationToken cancellationToken) =>
        silo is null || silo.BindingVerified &&
        await silo.Index.HasVisibleViewManifestAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<CenteredViewCurrent?> ReadSiloCurrentAsync(SqliteConnection connection, string cameraId,
        string? streamId, CancellationToken cancellationToken)
    {
        if (!silo!.BindingVerified ||
            await silo.Index.ReadCurrentLiveAsync(cameraId, cancellationToken).ConfigureAwait(false) is not { } live ||
            streamId is not null && live.StreamId != streamId ||
            !await IsSiloViewVisibleAsync(live.GroupId, live.RevisionId, cancellationToken).ConfigureAwait(false))
            return null;

        var view = await ReadExactAsync(connection, null, live.GroupId, live.RevisionId, cancellationToken).ConfigureAwait(false);
        return view is null ? null : new(view, new(live.GroupId, live.RevisionId, live.StartedOrder, live.StartedUtc, null, null));
    }
}
