using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    public async ValueTask<CenteredViewCurrent?> ReadCurrentAsync(CenteredViewStream stream, CancellationToken cancellationToken = default)
    {
        var streamId = StreamId(stream);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCurrentAsync(connection, null, streamId, cameraId: null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CenteredViewCurrent?> ReadLastLiveAsync(CameraKey camera, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCurrentAsync(connection, null, streamId: null, Identity(camera), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CenteredView?> ReadLatestSingleAsync(CenteredViewStream stream, CancellationToken cancellationToken = default)
    {
        var streamId = StreamId(stream);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT b.base_id, b.definition, b.registered_utc,
                   r.revision_id, r.change_order, r.published_utc, r.manifest, r.layers
            FROM processing_centered_view_bases b
            JOIN processing_centered_view_revisions r ON r.base_id = b.base_id
            WHERE b.base_id = (
                SELECT base_id FROM processing_centered_view_bases
                WHERE stream_id = $stream AND kind = $kind AND recomputed_from IS NULL
                ORDER BY reference_order DESC LIMIT 1
            )
            ORDER BY r.change_order DESC LIMIT 1;
            """, ("$stream", streamId), ("$kind", (int)CenteredViewKind.Single));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadView(reader) : null;
    }

    public ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadArchiveAsync(
        CenteredViewStream stream, Guid referenceCaptureId, CancellationToken cancellationToken = default) =>
        ReadReferenceBasesAsync(StreamId(stream), referenceCaptureId, cancellationToken);

    internal async ValueTask<CenteredView?> ReadRegisteredBaseAsync(CenteredViewStream stream, Guid referenceCaptureId,
        CenteredViewKind kind, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT b.base_id, b.definition, b.registered_utc,
                   r.revision_id, r.change_order, r.published_utc, r.manifest, r.layers
            FROM processing_centered_view_bases b
            JOIN processing_centered_view_revisions r ON r.base_id = b.base_id
            WHERE b.stream_id = $stream AND b.reference_id = $reference AND b.kind = $kind AND b.recomputed_from IS NULL
            ORDER BY r.change_order LIMIT 1;
            """, ("$stream", StreamId(stream)), ("$reference", referenceCaptureId), ("$kind", (int)kind));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadView(reader) : null;
    }

    internal ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadArchiveAsync(
        Guid referenceCaptureId, CancellationToken cancellationToken) =>
        ReadReferenceBasesAsync(null, referenceCaptureId, cancellationToken);

    private async ValueTask<IReadOnlyList<CenteredViewArchiveBase>> ReadReferenceBasesAsync(
        string? streamId, Guid referenceCaptureId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(referenceCaptureId, Guid.Empty);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT b.base_id, b.definition, b.registered_utc,
                (SELECT revision_id FROM processing_centered_view_revisions r
                 WHERE r.base_id = b.base_id ORDER BY change_order LIMIT 1),
                (SELECT revision_id FROM processing_centered_view_revisions r
                 WHERE r.base_id = b.base_id ORDER BY change_order DESC LIMIT 1),
                (SELECT revision_id FROM processing_centered_view_intervals i
                 WHERE i.base_id = b.base_id ORDER BY started_order DESC LIMIT 1)
            FROM processing_centered_view_bases b
            WHERE b.reference_id = $reference AND ($stream IS NULL OR b.stream_id = $stream)
            ORDER BY b.kind, b.recomputed_from IS NOT NULL, b.registered_utc, b.base_id;
            """, ("$stream", streamId), ("$reference", referenceCaptureId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<CenteredViewArchiveBase>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new(ReadBase(reader), reader.GetString(3), reader.GetString(4), ReadNullableText(reader, 5)));
        }
        return values.AsReadOnly();
    }

    public async ValueTask<CenteredView?> ReadRevisionAsync(string baseId, string revisionId, CancellationToken cancellationToken = default)
    {
        ValidateHash(baseId);
        ValidateHash(revisionId);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadExactAsync(connection, null, baseId, revisionId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CenteredViewStream?> ReadReferenceStreamAsync(CameraKey camera, Guid referenceCaptureId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentOutOfRangeException.ThrowIfEqual(referenceCaptureId, Guid.Empty);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT definition FROM processing_centered_view_bases
            WHERE camera_id = $camera AND reference_id = $reference
            GROUP BY stream_id LIMIT 2;
            """, ("$camera", Identity(camera)), ("$reference", referenceCaptureId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var stream = Parse<CenteredViewBaseDefinition>(reader.GetString(0)).Stream;
        if (stream.Camera != camera || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The exact reference capture has conflicting centered publication scopes.");
        return stream;
    }

    internal async ValueTask<List<(CenteredViewBase Base, long Order)>> ReadContributorBasesAsync(
        ExposureKey exposure, long afterOrder, int limit, CancellationToken cancellationToken)
    {
        ValidatePage(afterOrder, limit);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT b.base_id, b.definition, b.registered_utc, m.registered_order
            FROM processing_centered_view_members m
            JOIN processing_centered_view_bases b ON b.base_id = m.base_id
            WHERE m.exposure_id = $exposure AND m.registered_order > $after
            ORDER BY m.registered_order LIMIT $limit;
            """, ("$exposure", Identity(exposure)), ("$after", afterOrder), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<(CenteredViewBase, long)>(limit);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add((ReadBase(reader), reader.GetInt64(3)));
        return values;
    }

    internal async ValueTask<CenteredViewBase?> ReadBaseAsync(string baseId, CancellationToken token)
    {
        ValidateHash(baseId);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT base_id, definition, registered_utc
            FROM processing_centered_view_bases WHERE base_id = $base;
            """, ("$base", baseId));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadBase(reader) : null;
    }

    public async ValueTask<IReadOnlyList<CenteredViewRevision>> ReadRevisionsAsync(
        string baseId, long afterOrder, int limit, CancellationToken cancellationToken = default)
    {
        ValidateHash(baseId);
        ValidatePage(afterOrder, limit);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT revision_id, change_order, published_utc, manifest, layers
            FROM processing_centered_view_revisions
            WHERE base_id = $base AND change_order > $after
            ORDER BY change_order LIMIT $limit;
            """, ("$base", baseId), ("$after", afterOrder), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<CenteredViewRevision>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(ProjectRevision(reader, baseId, 0));
        return values.AsReadOnly();
    }

    public async ValueTask<IReadOnlyList<CenteredViewLiveInterval>> ReadLiveHistoryAsync(
        string baseId, long afterOrder, int limit, CancellationToken cancellationToken = default)
    {
        ValidateHash(baseId);
        ValidatePage(afterOrder, limit);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT revision_id, started_order, started_utc, ended_order, ended_utc
            FROM processing_centered_view_intervals
            WHERE base_id = $base AND started_order > $after
            ORDER BY started_order LIMIT $limit;
            """, ("$base", baseId), ("$after", afterOrder), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<CenteredViewLiveInterval>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(ReadInterval(reader, baseId, 0));
        return values.AsReadOnly();
    }

    public async ValueTask<IReadOnlyList<CenteredViewChange>> ReadChangesAsync(
        CenteredViewStream stream, long afterOrder, int limit, CancellationToken cancellationToken = default)
    {
        var streamId = StreamId(stream);
        ValidatePage(afterOrder, limit);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT change_order, committed_utc, base_id, revision_id, selected_live
            FROM processing_centered_view_changes
            WHERE stream_id = $stream AND change_order > $after
            ORDER BY change_order LIMIT $limit;
            """, ("$stream", streamId), ("$after", afterOrder), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var values = new List<CenteredViewChange>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(reader.GetInt64(0), ReadTime(reader, 1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return values.AsReadOnly();
    }

    public async ValueTask<CenteredViewSavedSelection?> ReadSavedAsync(
        string owner, Guid selectionId, CancellationToken cancellationToken = default)
    {
        ValidateText(owner);
        ArgumentOutOfRangeException.ThrowIfEqual(selectionId, Guid.Empty);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadSavedAsync(connection, null, owner, selectionId, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<CenteredViewCurrent?> ReadCurrentAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string? streamId, string? cameraId, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT b.base_id, b.definition, b.registered_utc,
                   r.revision_id, r.change_order, r.published_utc, r.manifest, r.layers,
                   i.revision_id, i.started_order, i.started_utc, i.ended_order, i.ended_utc
            FROM processing_centered_view_intervals i
            JOIN processing_centered_view_bases b ON b.base_id = i.base_id
            JOIN processing_centered_view_revisions r ON r.base_id = i.base_id AND r.revision_id = i.revision_id
            WHERE i.ended_order IS NULL AND (i.stream_id = $stream OR i.camera_id = $camera);
            """, ("$stream", streamId), ("$camera", cameraId));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return null;
        var view = ReadView(reader);
        return new(view, ReadInterval(reader, view.Base.BaseId, 8));
    }

    private static async ValueTask<CenteredView?> ReadExactAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string baseId, string revisionId, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT b.base_id, b.definition, b.registered_utc,
                   r.revision_id, r.change_order, r.published_utc, r.manifest, r.layers
            FROM processing_centered_view_bases b
            JOIN processing_centered_view_revisions r ON r.base_id = b.base_id
            WHERE b.base_id = $base AND r.revision_id = $revision;
            """, ("$base", baseId), ("$revision", revisionId));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadView(reader) : null;
    }

    private static async ValueTask<CenteredView?> ReadViewAsync(SqliteConnection connection, SqliteTransaction transaction,
        string baseId, bool first, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT b.base_id, b.definition, b.registered_utc,
                   r.revision_id, r.change_order, r.published_utc, r.manifest, r.layers
            FROM processing_centered_view_bases b
            JOIN processing_centered_view_revisions r ON r.base_id = b.base_id
            WHERE b.base_id = $base AND r.change_order = (
                SELECT CASE WHEN $first THEN min(change_order) ELSE max(change_order) END
                FROM processing_centered_view_revisions WHERE base_id = $base
            );
            """, ("$base", baseId), ("$first", first));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadView(reader) : null;
    }

    private static async ValueTask<CenteredViewSavedSelection?> ReadSavedAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string owner, Guid selectionId, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT base_id, revision_id, saved_utc, enabled_layers FROM processing_centered_view_selections
            WHERE owner = $owner AND selection_id = $id;
            """, ("$owner", owner), ("$id", selectionId));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(selectionId, reader.GetString(0), reader.GetString(1), Array.AsReadOnly(Parse<string[]>(reader.GetString(3))),
                ReadTime(reader, 2))
            : null;
    }

    private static CenteredViewBase ReadBase(SqliteDataReader reader)
    {
        var definition = Parse<CenteredViewBaseDefinition>(reader.GetString(1));
        return new(reader.GetString(0), definition with
        {
            Contributors = Array.AsReadOnly(definition.Contributors.ToArray()),
            Window = definition.Window is { } window ? window with { Slots = Array.AsReadOnly(window.Slots.ToArray()) } : null
        },
            ReadTime(reader, 2));
    }

    private static CenteredView ReadView(SqliteDataReader reader)
    {
        var viewBase = ReadBase(reader);
        return new(viewBase, ProjectRevision(reader, viewBase.BaseId, 3));
    }

    private static CenteredViewRevision ProjectRevision(SqliteDataReader reader, string baseId, int start)
    {
        var manifest = Parse<OverlayManifestV1>(reader.GetString(start + 3));
        return new(reader.GetString(start), baseId, reader.GetInt64(start + 1), ReadTime(reader, start + 2),
            manifest with { Layers = Array.AsReadOnly(manifest.Layers.ToArray()) },
            Array.AsReadOnly(Parse<CenteredViewLayer[]>(reader.GetString(start + 4))));
    }

    private static CenteredViewLiveInterval ReadInterval(SqliteDataReader reader, string baseId, int start) =>
        new(baseId, reader.GetString(start), reader.GetInt64(start + 1), ReadTime(reader, start + 2),
            ReadNullableOrder(reader, start + 3), ReadNullableTime(reader, start + 4));
}
