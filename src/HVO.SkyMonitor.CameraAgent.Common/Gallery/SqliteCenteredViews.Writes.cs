using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    public async ValueTask<CenteredView> RegisterBaseAsync(
        CenteredViewBaseDefinition definition, IReadOnlyList<CenteredViewLayer> initialLayers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var streamId = StreamId(definition.Stream);
        ValidateBase(definition);
        ArgumentNullException.ThrowIfNull(initialLayers);
        var baseId = Identity(new { Schema = CenteredViewBase.SchemaVersion, Definition = definition });
        var layers = NormalizeLayers(definition, initialLayers);
        var revisionId = RevisionId(baseId, previous: null, layers);
        var manifest = CreateManifest(definition, layers);

        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginWrite(connection);
        var existing = await ReadViewAsync(connection, transaction, baseId, first: true, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Revision.RevisionId != revisionId)
                throw new InvalidOperationException("The initial publication conflicts with the registered base.");
            return existing;
        }

        await RequireDistinctBaseAsync(connection, transaction, streamId, definition, cancellationToken).ConfigureAwait(false);
        var time = clock.GetUtcNow();
        var viewBase = new CenteredViewBase(baseId, definition, time);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO processing_centered_view_bases
                (base_id, stream_id, camera_id, reference_id, reference_order, kind, recomputed_from, registered_utc, definition)
            VALUES ($base, $stream, $camera, $reference, $referenceOrder, $kind, $recomputedFrom, $utc, $definition);
            """, cancellationToken,
            ("$base", baseId), ("$stream", streamId), ("$camera", Identity(definition.Stream.Camera)),
            ("$reference", definition.Reference.Capture.CaptureId),
            ("$referenceOrder", definition.ReferenceOrder), ("$kind", (int)definition.Kind),
            ("$recomputedFrom", definition.RecomputedFromBaseId), ("$utc", ToTicks(time)), ("$definition", Serialize(definition)))
            .ConfigureAwait(false);

        var revision = await AddRevisionAsync(connection, transaction, streamId, baseId, revisionId,
            time, manifest, layers, selectedLive: false, cancellationToken).ConfigureAwait(false);
        foreach (var member in definition.Contributors)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO processing_centered_view_members (exposure_id, registered_order, base_id)
                VALUES ($exposure, $order, $base);
                """, cancellationToken, ("$exposure", ExposureIdentity(member.Exposure.Input.Key)), ("$order", revision.Order),
                ("$base", baseId)).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify(definition.Stream);
        return new(viewBase, revision);
    }

    public async ValueTask<CenteredView?> PublishLayerAsync(
        string baseId, CenteredViewLayer layer, CancellationToken cancellationToken = default) =>
        await PublishLayerCoreAsync(baseId, layer, cancellationToken).ConfigureAwait(false);

    private async ValueTask<CenteredView?> PublishLayerCoreAsync(
        string baseId, CenteredViewLayer layer, CancellationToken cancellationToken, bool availabilityObservation = false)
    {
        ValidateHash(baseId);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentOutOfRangeException.ThrowIfLessThan(layer.EvidenceOrder, 1);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginWrite(connection);
        var previous = await ReadViewAsync(connection, transaction, baseId, first: false, cancellationToken).ConfigureAwait(false);
        if (previous is null)
            return null;
        var definition = previous.Base.Definition;
        ValidateLayer(definition, layer);
        var existing = previous.Revision.Layers.SingleOrDefault(value => value.Key == layer.Key);
        if (existing is not null && existing.EvidenceOrder >= layer.EvidenceOrder)
        {
            if (existing.EvidenceOrder > layer.EvidenceOrder || availabilityObservation || Serialize(existing) == Serialize(layer))
                return previous;
            // The feed can observe expired or missing bytes at the original product order.
            // If the same product is recovered, retain the old view and append its restored presentation.
            if (!IsUnavailableProductInventory(existing) || existing.Evidence is { } pin && layer.Evidence != pin)
                throw new InvalidOperationException("The evidence order was already committed with different content.");
        }

        var layers = NormalizeLayers(definition, previous.Revision.Layers.Where(value => value.Key != layer.Key).Append(layer));
        var revisionId = RevisionId(baseId, previous.Revision.RevisionId, layers);
        var manifest = CreateManifest(definition, layers);
        var streamId = StreamId(definition.Stream);
        var isLive = await IsCurrentBaseAsync(connection, transaction, streamId, baseId, cancellationToken).ConfigureAwait(false) &&
            await ReadRunAuthorityAsync(connection, transaction, definition.Stream, cancellationToken).ConfigureAwait(false) != false &&
            await CanIssueForActiveProfileAsync(connection, transaction, definition.Stream, cancellationToken).ConfigureAwait(false);
        var time = clock.GetUtcNow();
        var revision = await AddRevisionAsync(connection, transaction, streamId, baseId, revisionId,
            time, manifest, layers, isLive, cancellationToken).ConfigureAwait(false);
        if (isLive)
            await IssueLiveAsync(connection, transaction, streamId, Identity(definition.Stream.Camera), baseId, revisionId, revision.Order, time, cancellationToken)
                .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify(definition.Stream);
        return previous with { Revision = revision };
    }

    internal ValueTask<CenteredView?> PublishUnavailableProductLayerAsync(string baseId, CenteredViewLayerKey key,
        ExposureProductLookup source, DateTimeOffset observedUtc, CancellationToken token)
    {
        var prefix = source.Availability switch
        {
            ExposureProductAvailability.Expired => "centered.product-expired",
            ExposureProductAvailability.MissingEvidence => "centered.product-missing-evidence",
            ExposureProductAvailability.Failed => "centered.product-failed",
            ExposureProductAvailability.Unavailable => "centered.product-unavailable",
            ExposureProductAvailability.Incompatible => "centered.product-incompatible",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var reason = string.IsNullOrEmpty(source.Reason) ? prefix : $"{prefix}/{source.Reason}";
        var pin = source.Product is { } product ? new CenteredViewArtifact(product.ArtifactId,
            product.OutputIdentitySha256, product.PayloadSha256) : null;
        return PublishLayerCoreAsync(baseId, new(key, source.Revision!.Value, pin, observedUtc,
            CenteredViewReadiness.Incompatible, reason[..Math.Min(reason.Length, 1024)], null), token, availabilityObservation: true);
    }

    private static bool IsUnavailableProductInventory(CenteredViewLayer layer) =>
        layer.Readiness == CenteredViewReadiness.Incompatible && layer.Drawable is null &&
        (layer.Reason?.StartsWith("centered.product-expired", StringComparison.Ordinal) == true ||
         layer.Reason?.StartsWith("centered.product-missing-evidence", StringComparison.Ordinal) == true);

    public async ValueTask<CenteredViewCurrent?> SelectLiveAsync(
        CenteredViewStream stream, string baseId, CancellationToken cancellationToken = default)
    {
        var streamId = StreamId(stream);
        ValidateHash(baseId);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginWrite(connection);
        var candidate = await ReadViewAsync(connection, transaction, baseId, first: false, cancellationToken).ConfigureAwait(false);
        if (candidate is null || candidate.Base.Definition.Stream != stream || candidate.Base.Definition.RecomputedFromBaseId is not null)
            return null;

        var proposed = candidate.Base.Definition;
        if (proposed.Kind == CenteredViewKind.Single &&
            await IsSingleOutsideCenterAsync(connection, transaction, stream.RunId, proposed.Reference.Capture.CaptureId,
                cancellationToken).ConfigureAwait(false))
            return null;

        var cameraId = Identity(stream.Camera);
        var current = await ReadCurrentAsync(connection, transaction, streamId: null, cameraId, cancellationToken).ConfigureAwait(false);
        var authority = await ReadRunAuthorityAsync(connection, transaction, stream, cancellationToken).ConfigureAwait(false);
        if (authority == false || authority is null && current is not null && current.View.Base.Definition.Stream != stream ||
            !await CanIssueForActiveProfileAsync(connection, transaction, stream, cancellationToken).ConfigureAwait(false))
            return null;

        if (current is not null && current.View.Base.Definition.Stream == stream)
        {
            var existing = current.View.Base.Definition;
            if (proposed.ReferenceOrder < existing.ReferenceOrder ||
                proposed.ReferenceOrder == existing.ReferenceOrder &&
                (proposed.Reference.Capture.CaptureId != existing.Reference.Capture.CaptureId || proposed.Kind <= existing.Kind))
                return existing.Stream == stream ? current : null;
        }

        var time = clock.GetUtcNow();
        var order = await AddChangeAsync(connection, transaction, streamId, baseId, candidate.Revision.RevisionId,
            time, selectedLive: true, cancellationToken).ConfigureAwait(false);
        var interval = await IssueLiveAsync(connection, transaction, streamId, cameraId, baseId, candidate.Revision.RevisionId,
            order, time, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Notify(stream);
        return new(candidate, interval);
    }

    // Compare capture order only within a run. In production, the journal's
    // one open run is authoritative across generations, even when counters reset.
    private static async ValueTask<bool?> ReadRunAuthorityAsync(SqliteConnection connection, SqliteTransaction transaction,
        CenteredViewStream stream, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT run_id = $run AND closed_reason IS NULL
                AND json_extract(CAST(snapshot_json AS TEXT), '$.GraphRevisionId') = $generation
            FROM centered_exposure_runs
            WHERE agent_id = $agent AND camera_id = $camera
            ORDER BY closed_reason IS NULL DESC, rowid DESC LIMIT 1;
            """, ("$agent", stream.Camera.AgentId), ("$camera", stream.Camera.CameraId),
            ("$run", stream.RunId.ToString("N")), ("$generation", stream.GenerationId));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) switch
        {
            long result => result != 0,
            null => null,
            _ => false
        };
    }

    private async ValueTask<bool> CanIssueForActiveProfileAsync(SqliteConnection connection, SqliteTransaction transaction,
        CenteredViewStream stream, CancellationToken token)
    {
        if (ingress is null)
            return true; // Stand-alone store tooling has no hosted capture authority.
        if (ingress.RecentCenteredExposures.ReadActive(stream.Camera).RunId != stream.RunId)
            return false;

        // The active schedule and manual pause are written in this same journal.
        // Sample them under the publication transaction so a stale wake cannot
        // issue another revision after activation replaced its frozen profile.
        using var command = CreateCommand(connection, transaction, """
            SELECT EXISTS(
                SELECT 1 FROM capture_schedule_state state
                JOIN capture_schedule_revisions profile ON profile.revision_id = state.active_revision_id
                JOIN capture_control_state control ON control.state_key = 1
                JOIN centered_exposure_runs run ON run.run_id = $run AND run.agent_id = $agent
                    AND run.camera_id = $camera AND run.closed_reason IS NULL
                WHERE state.state_key = 1 AND control.state = 'running'
                    AND state.last_decision_admitted IS NOT 0
                    AND json_extract(CAST(profile.profile_json AS TEXT), '$.centeredExposure.cameraId') = $camera
                    AND json_extract(CAST(profile.profile_json AS TEXT), '$.centeredExposure.executionVersion') = $version
                    AND json_extract(CAST(run.snapshot_json AS TEXT), '$.Schedule.LocalProfile') = profile.profile_sha256
                    AND json_extract(CAST(run.snapshot_json AS TEXT), '$.Schedule.Revision') = profile.schedule_sha256
            );
            """, ("$run", stream.RunId.ToString("N")), ("$agent", stream.Camera.AgentId),
            ("$camera", stream.Camera.CameraId), ("$version", CenteredExposureProfile.CurrentExecutionVersion));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is 1L;
    }

    public async ValueTask<CenteredViewSavedSelection?> SaveAsync(
        string owner, Guid selectionId, string baseId, string revisionId, IReadOnlyList<string> enabledLayerIds,
        CancellationToken cancellationToken = default)
    {
        ValidateText(owner);
        ValidateHash(baseId);
        ValidateHash(revisionId);
        ArgumentOutOfRangeException.ThrowIfEqual(selectionId, Guid.Empty);
        ArgumentNullException.ThrowIfNull(enabledLayerIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(enabledLayerIds.Count, LayeredPresentationJson.MaximumLayerCount);

        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginWrite(connection);
        var view = await ReadExactAsync(connection, transaction, baseId, revisionId, cancellationToken).ConfigureAwait(false);
        if (view is null)
            return null;
        var requested = enabledLayerIds.ToHashSet(StringComparer.Ordinal);
        var ordered = Array.AsReadOnly(view.Revision.Manifest.Layers.Select(layer => layer.LayerIdentitySha256)
            .Where(requested.Contains).ToArray());
        if (requested.Count != enabledLayerIds.Count || requested.Count != ordered.Count)
            throw new ArgumentException("Every enabled layer must appear once in the exact revision.", nameof(enabledLayerIds));

        var existing = await ReadSavedAsync(connection, transaction, owner, selectionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.BaseId != baseId || existing.RevisionId != revisionId || !existing.EnabledLayerIds.SequenceEqual(ordered))
                throw new InvalidOperationException("The selection ID already identifies a different saved view.");
            return existing;
        }

        var time = clock.GetUtcNow();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO processing_centered_view_selections (owner, selection_id, base_id, revision_id, saved_utc, enabled_layers)
            VALUES ($owner, $id, $base, $revision, $utc, $enabled);
            """, cancellationToken, ("$owner", owner), ("$id", selectionId), ("$base", baseId), ("$revision", revisionId),
            ("$utc", ToTicks(time)), ("$enabled", Serialize(ordered))).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(selectionId, baseId, revisionId, ordered, time);
    }

    private static async ValueTask RequireDistinctBaseAsync(SqliteConnection connection, SqliteTransaction transaction,
        string streamId, CenteredViewBaseDefinition definition, CancellationToken token)
    {
        if (definition.RecomputedFromBaseId is { } parentId)
        {
            var parent = await ReadViewAsync(connection, transaction, parentId, first: true, token).ConfigureAwait(false);
            if (parent is null || parent.Base.Definition is { RecomputedFromBaseId: not null } ||
                parent.Base.Definition.Stream != definition.Stream || parent.Base.Definition.Kind != definition.Kind ||
                parent.Base.Definition.Reference.Capture != definition.Reference.Capture ||
                parent.Base.Definition.ReferenceOrder != definition.ReferenceOrder)
                throw new InvalidOperationException("A recomputation must name its original base.");
            return;
        }

        using var command = CreateCommand(connection, transaction, """
            SELECT reference_id, reference_order, kind, definition
            FROM processing_centered_view_bases WHERE recomputed_from IS NULL AND (
                (camera_id = $camera AND reference_id = $reference) OR
                (stream_id = $stream AND reference_order = $order));
            """, ("$stream", streamId), ("$camera", Identity(definition.Stream.Camera)),
            ("$reference", definition.Reference.Capture.CaptureId), ("$order", definition.ReferenceOrder));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var existing = Parse<CenteredViewBaseDefinition>(reader.GetString(3));
            if (reader.GetGuid(0) != definition.Reference.Capture.CaptureId || reader.GetInt64(1) != definition.ReferenceOrder ||
                reader.GetInt32(2) == (int)definition.Kind || existing.Stream != definition.Stream ||
                Serialize(existing.Reference with { Linear = null }) != Serialize(definition.Reference with { Linear = null }) ||
                existing.Reference.Linear is not null && definition.Reference.Linear is not null &&
                existing.Reference.Linear != definition.Reference.Linear)
                throw new InvalidOperationException("The publication conflicts with the existing reference or base for its live slot.");
        }
    }

    private static async ValueTask<bool> IsCurrentBaseAsync(SqliteConnection connection, SqliteTransaction transaction,
        string streamId, string baseId, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT 1 FROM processing_centered_view_intervals
            WHERE stream_id = $stream AND ended_order IS NULL AND base_id = $base;
            """, ("$stream", streamId), ("$base", baseId));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    private static async ValueTask<CenteredViewRevision> AddRevisionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string streamId, string baseId, string revisionId,
        DateTimeOffset time, OverlayManifestV1 manifest, IReadOnlyList<CenteredViewLayer> layers, bool selectedLive,
        CancellationToken token)
    {
        var order = await AddChangeAsync(connection, transaction, streamId, baseId, revisionId, time, selectedLive, token)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO processing_centered_view_revisions (base_id, revision_id, change_order, published_utc, manifest, layers)
            VALUES ($base, $revision, $order, $utc, $manifest, $layers);
            """, token, ("$base", baseId), ("$revision", revisionId), ("$order", order), ("$utc", ToTicks(time)),
            ("$manifest", Serialize(manifest)), ("$layers", Serialize(layers))).ConfigureAwait(false);
        return new(revisionId, baseId, order, time, manifest, layers);
    }

    private static async ValueTask<long> AddChangeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string streamId, string baseId, string revisionId, DateTimeOffset time, bool selectedLive, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO processing_centered_view_changes (stream_id, base_id, revision_id, committed_utc, selected_live)
            VALUES ($stream, $base, $revision, $utc, $selectedLive)
            RETURNING change_order;
            """, ("$stream", streamId), ("$base", baseId), ("$revision", revisionId),
            ("$utc", ToTicks(time)), ("$selectedLive", selectedLive));
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The centered view change has no durable order."));
    }

    private static async ValueTask<CenteredViewLiveInterval> IssueLiveAsync(
        SqliteConnection connection, SqliteTransaction transaction, string streamId, string cameraId, string baseId,
        string revisionId, long order, DateTimeOffset time, CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE processing_centered_view_intervals SET ended_order = $order, ended_utc = $utc
            WHERE camera_id = $camera AND ended_order IS NULL;
            """, token, ("$order", order), ("$utc", ToTicks(time)), ("$camera", cameraId)).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO processing_centered_view_intervals (started_order, stream_id, camera_id, base_id, revision_id, started_utc)
            VALUES ($order, $stream, $camera, $base, $revision, $utc);
            """, token, ("$order", order), ("$utc", ToTicks(time)), ("$stream", streamId),
            ("$camera", cameraId), ("$base", baseId), ("$revision", revisionId)).ConfigureAwait(false);
        return new(baseId, revisionId, order, time, null, null);
    }

    private static string RevisionId(string baseId, string? previous, IReadOnlyList<CenteredViewLayer> layers) =>
        Identity(new { Schema = CenteredViewRevision.SchemaVersion, BaseId = baseId, Previous = previous, Layers = layers });

    private static OverlayManifestV1 CreateManifest(CenteredViewBaseDefinition definition, IReadOnlyList<CenteredViewLayer> layers)
        => LayeredPresentationJson.CreateManifest(definition.Preview.Product, sceneIdentitySha256: null,
            layers.Where(layer => layer.Drawable is not null).Select(layer => layer.Drawable!));
}
