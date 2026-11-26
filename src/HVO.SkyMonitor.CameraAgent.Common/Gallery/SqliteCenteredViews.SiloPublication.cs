using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    internal async ValueTask PublishSiloChangesAsync(int limit, CancellationToken cancellationToken)
    {
        if (silo is null) return;
        var cursor = await silo.ReadViewCursorAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
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
            """, ("$cursor", cursor), ("$limit", limit));
        var changes = new List<(CenteredView View, long Order, bool Live, DateTimeOffset CommittedUtc, string? Previous)>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                changes.Add((ReadView(reader), reader.GetInt64(8), reader.GetBoolean(9), new DateTimeOffset(reader.GetInt64(10), TimeSpan.Zero),
                    ReadNullableText(reader, 11)));
        }

        foreach (var (view, order, live, committedUtc, previous) in changes)
        {
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
                await silo.Publisher.StageAsync(draft, cancellationToken).ConfigureAwait(false);
                var outcome = await silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
                if (outcome.State != SiloStagedState.Visible || outcome.CatalogState != "visible")
                    throw new SiloStoreUnavailableException(silo.Binding.StoreId, "settle centered view", writeMayHaveLanded: true);
            }
            if (live)
            {
                await silo.Publisher.PublishLiveViewAsync(new(camera, section.StreamId, view.Base.BaseId, view.Revision.RevisionId,
                    order, committedUtc.ToUniversalTime()), cancellationToken).ConfigureAwait(false);
            }
            await silo.WriteViewCursorAsync(order, cancellationToken).ConfigureAwait(false);
            Notify(definition.Stream);
        }
    }

    private async ValueTask<bool> IsSiloViewVisibleAsync(string baseId, string revisionId, CancellationToken cancellationToken) =>
        silo is null || silo.BindingVerified &&
        await silo.Index.ReadPublicationAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false) is { State: "visible" };

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
