using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private volatile CenteredLayerGraph? _centeredLayerGraph;

    private sealed record CenteredLayerGraph(string Revision,
        IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>> Bindings);

    internal async ValueTask<IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>>?>
        ReadCenteredLayerBindingsAsync(string revision, CancellationToken token)
    {
        if (_centeredLayerGraph is { } cached && cached.Revision == revision)
            return cached.Bindings;
        if (revision.Length != 64 || revision.Any(character => !Uri.IsHexDigit(character)))
            return null;

        ProcessingGraphRevisionSnapshot snapshot;
        try
        {
            snapshot = await ReadRevisionAsync(revision, token).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // An older retained run may predate the graph registry.
            return null;
        }
        var graph = ProcessingGraphJson.Parse(snapshot.DefinitionJson).Definition
            ?? throw new InvalidDataException("The centered run's frozen graph is invalid.");
        var bindings = CenteredViewLayerBindings.ReadConfigured(graph);
        _centeredLayerGraph = new(revision, bindings);
        return bindings;
    }

    internal async ValueTask<SqliteConnection> OpenCenteredViewsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<ExposureProductLookup>> ReadCenteredViewSourcesAsync(
        ExposureKey key, Guid executionId, long after, int limit, CancellationToken token)
    {
        using var connection = await OpenCenteredViewsAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductProjection + "\n" + """
            WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
                AND product.execution_id = $execution AND product.revision > $after
            ORDER BY product.revision LIMIT $limit;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var items = new List<ExposureProductLookup>(limit);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            items.Add(ReadCenteredLookup(reader, out _));
        return items;
    }

    internal async ValueTask<CenteredAuthoritativeAssociationWriter?> ReadAuthoritativeAssociationWriterAsync(
        ExposureKey key, Guid executionId, CancellationToken token)
    {
        using var connection = await OpenCenteredViewsAsync(token).ConfigureAwait(false);
        using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.CommandText = """
                SELECT node.node_id, COALESCE(json_extract(output.value, '$.variant'), json_extract(output.value, '$.Variant'))
                FROM processing_execution_nodes node
                JOIN json_each(node.outputs_json) output
                WHERE node.execution_id = $execution
                    AND COALESCE(json_extract(output.value, '$.variant'), json_extract(output.value, '$.Variant')) IS NOT NULL
                    AND (
                        json_extract(output.value, '$.recipe.name') = $recipe
                        OR json_extract(output.value, '$.Recipe.Name') = $recipe
                        OR (
                            COALESCE(json_extract(output.value, '$.schemaVersion'), json_extract(output.value, '$.SchemaVersion')) IN ($v1, $v2)
                            AND (
                                node.node_id = $producer
                                OR COALESCE(json_extract(output.value, '$.variant'), json_extract(output.value, '$.Variant')) = $variant
                            )
                        )
                    )
                ORDER BY node.rowid
                LIMIT 1;
                """;
            nodeCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            nodeCommand.Parameters.AddWithValue("$v1", MeasuredStellarAssociationsV1.V1SchemaVersion);
            nodeCommand.Parameters.AddWithValue("$v2", MeasuredStellarAssociationsV1.CurrentSchemaVersion);
            nodeCommand.Parameters.AddWithValue("$variant", ExposureAssociationProducts.Variant);
            nodeCommand.Parameters.AddWithValue("$producer", ExposureAssociationProducts.StepAlias);
            nodeCommand.Parameters.AddWithValue("$recipe", ExposureAssociationProducts.RecipeName);
            using var reader = await nodeCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                return new CenteredAuthoritativeAssociationWriter(reader.GetString(0), reader.GetString(1));
        }

        using var productCommand = connection.CreateCommand();
        productCommand.CommandText = """
            SELECT product.producer, product.variant
            FROM centered_exposure_products product
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = product.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            LEFT JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
            WHERE product.capture_id = $capture AND run.run_id = $run
                AND run.agent_id = $agent AND run.camera_id = $camera
                AND product.execution_id = $execution
                AND product.schema_version IN ($v1, $v2, $dispV1, $dispV2)
                AND (
                    product.schema_version IN ($dispV1, $dispV2)
                    OR (
                        output.output_identity_sha256 IS NOT NULL
                        AND json_valid(CAST(output.descriptor_json AS TEXT))
                        AND COALESCE(
                            json_extract(CAST(output.descriptor_json AS TEXT), '$.artifact.recipe.name'),
                            json_extract(CAST(output.descriptor_json AS TEXT), '$.descriptor.artifact.recipe.name'),
                            json_extract(CAST(output.descriptor_json AS TEXT), '$.Artifact.Recipe.Name')
                        ) = $recipe
                    )
                    OR output.output_identity_sha256 IS NULL
                )
            ORDER BY
                CASE
                    WHEN output.output_identity_sha256 IS NOT NULL
                        AND json_valid(CAST(output.descriptor_json AS TEXT))
                        AND COALESCE(
                            json_extract(CAST(output.descriptor_json AS TEXT), '$.artifact.recipe.name'),
                            json_extract(CAST(output.descriptor_json AS TEXT), '$.descriptor.artifact.recipe.name'),
                            json_extract(CAST(output.descriptor_json AS TEXT), '$.Artifact.Recipe.Name')
                        ) = $recipe THEN 0
                    WHEN product.schema_version IN ($dispV1, $dispV2) THEN 1
                    WHEN product.variant IN ($variant, $measuredVariant) OR product.producer = $producer THEN 2
                    ELSE 3
                END,
                product.revision
            LIMIT 1;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(productCommand, key);
        productCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        productCommand.Parameters.AddWithValue("$v1", MeasuredStellarAssociationsV1.V1SchemaVersion);
        productCommand.Parameters.AddWithValue("$v2", MeasuredStellarAssociationsV1.CurrentSchemaVersion);
        productCommand.Parameters.AddWithValue("$dispV1", CenteredViewLayerBindings.DispositionPrefix + MeasuredStellarAssociationsV1.V1SchemaVersion);
        productCommand.Parameters.AddWithValue("$dispV2", CenteredViewLayerBindings.DispositionPrefix + MeasuredStellarAssociationsV1.CurrentSchemaVersion);
        productCommand.Parameters.AddWithValue("$variant", ExposureAssociationProducts.Variant);
        productCommand.Parameters.AddWithValue("$measuredVariant", "measured-stellar-associations-v1");
        productCommand.Parameters.AddWithValue("$producer", ExposureAssociationProducts.StepAlias);
        productCommand.Parameters.AddWithValue("$recipe", ExposureAssociationProducts.RecipeName);
        using var productReader = await productCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await productReader.ReadAsync(token).ConfigureAwait(false)
            ? new CenteredAuthoritativeAssociationWriter(productReader.GetString(0), productReader.GetString(1))
            : null;
    }

    private static async ValueTask AddCenteredViewSchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CenteredViewSchemaSql + "\n" + CenteredViewRuntimeSchemaSql + "\n" + CenteredViewRetentionSchema.IndexSql + "\n" + BackfillPendingWindowWorkSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AddCenteredViewRuntimeSchemaAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CenteredViewRuntimeSchemaSql + "\n" + CenteredViewRetentionSchema.IndexSql + "\n" + BackfillPendingWindowWorkSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpgradeUnstartedWindowCheckpointAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM processing_centered_windows);";
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not 0L)
            throw new InvalidOperationException("The pre-runtime centered window checkpoint contains work without an admitted slot calendar. Preserve the database for explicit state disposition.");
        // Schema 11 never assigned these attempts to calendar slots. The next
        // acquisition starts at ordinal one in a new run; old evidence remains addressable.
        command.CommandText = """
            UPDATE centered_exposure_attempts
            SET status = 'expired', reason = 'schema-11.missing-slot-calendar'
            WHERE status IN ('pending', 'dispatched') AND run_id IN (
                SELECT run_id FROM centered_exposure_runs WHERE closed_reason IS NULL);
            UPDATE centered_exposure_runs SET closed_reason = 'schema-11.missing-slot-calendar'
            WHERE closed_reason IS NULL;
            DROP TABLE processing_centered_windows;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await AddCenteredWindowSchemaAsync(connection, transaction, token).ConfigureAwait(false);
    }

    private const string LegacyCenteredWindowCheckpointSchemaSql = """
        CREATE TABLE processing_centered_windows(
            window_sha256 TEXT PRIMARY KEY CHECK(length(window_sha256) = 64),
            deadline_utc_ticks INTEGER NOT NULL,
            checkpoint_json BLOB NOT NULL,
            reference_capture_id TEXT CHECK(reference_capture_id IS NULL OR length(reference_capture_id) = 32),
            closure_json BLOB,
            publication_json BLOB,
            terminal INTEGER NOT NULL DEFAULT 0 CHECK(terminal IN (0, 1)),
            CHECK(terminal = 0 OR closure_json IS NOT NULL),
            CHECK(reference_capture_id IS NULL OR closure_json IS NOT NULL),
            CHECK(publication_json IS NULL OR (closure_json IS NOT NULL AND terminal = 1))
        ) STRICT;
        CREATE INDEX ix_processing_centered_windows_pending
            ON processing_centered_windows(deadline_utc_ticks, window_sha256)
            WHERE terminal = 0;
        CREATE UNIQUE INDEX ix_processing_centered_windows_reference
            ON processing_centered_windows(reference_capture_id) WHERE reference_capture_id IS NOT NULL;
        """;

    private const string BackfillPendingWindowWorkSql = """
        INSERT OR IGNORE INTO processing_centered_view_window_work(window_sha256, retry_utc)
        SELECT window.window_sha256, 0
        FROM processing_centered_windows window
        LEFT JOIN processing_centered_view_window_work work ON work.window_sha256 = window.window_sha256
        WHERE window.terminal = 1 AND window.closure_json IS NOT NULL AND window.reference_capture_id IS NOT NULL
            AND work.window_sha256 IS NULL;
        """;

    private const string LegacyCenteredViewWindowWorkSchemaSql = """
        CREATE TABLE processing_centered_view_window_work (
            window_sha256 TEXT PRIMARY KEY NOT NULL REFERENCES processing_centered_windows(window_sha256),
            live_base_id TEXT REFERENCES processing_centered_view_bases(base_id),
            retry_utc INTEGER,
            terminal_reason TEXT,
            CHECK(retry_utc IS NULL OR terminal_reason IS NULL)
        ) STRICT;
        CREATE INDEX ix_processing_centered_view_window_live_base
            ON processing_centered_view_window_work(live_base_id) WHERE live_base_id IS NOT NULL;
        """;

    private const string CenteredViewWindowWorkSchemaSql = """
        CREATE TABLE processing_centered_view_window_work (
            window_sha256 TEXT PRIMARY KEY NOT NULL REFERENCES processing_centered_windows(window_sha256),
            live_base_id TEXT REFERENCES processing_centered_view_bases(base_id),
            retry_utc INTEGER,
            terminal_reason TEXT,
            retry_count INTEGER NOT NULL DEFAULT 0 CHECK(retry_count >= 0),
            CHECK(retry_utc IS NULL OR terminal_reason IS NULL)
        ) STRICT;
        CREATE INDEX ix_processing_centered_view_window_live_base
            ON processing_centered_view_window_work(live_base_id) WHERE live_base_id IS NOT NULL;
        CREATE INDEX ix_processing_centered_view_window_due
            ON processing_centered_view_window_work(retry_utc, window_sha256) WHERE retry_utc IS NOT NULL;
        """;

    private const string CenteredViewRuntimePrefixSql = """
        CREATE INDEX ix_processing_centered_view_archive_reference
            ON processing_centered_view_bases(reference_id);
        CREATE TABLE processing_centered_view_cursors (
            agent_id TEXT NOT NULL,
            camera_id TEXT NOT NULL,
            consumed_revision INTEGER NOT NULL CHECK(consumed_revision > 0),
            PRIMARY KEY(agent_id, camera_id)
        ) STRICT;
        CREATE TABLE processing_centered_view_retry (
            agent_id TEXT NOT NULL,
            camera_id TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision > 0),
            retry_utc INTEGER NOT NULL,
            PRIMARY KEY(agent_id, camera_id, revision)
        ) STRICT;
        CREATE INDEX ix_processing_centered_view_retry_due
            ON processing_centered_view_retry(agent_id, camera_id, retry_utc, revision);
        """;

    private const string CenteredViewRuntimeSuffixSql = """
        CREATE TABLE processing_centered_view_trail_work (
            revision INTEGER PRIMARY KEY AUTOINCREMENT,
            capture_id TEXT NOT NULL,
            candidate_id TEXT NOT NULL,
            stage_key TEXT NOT NULL,
            stage_state TEXT NOT NULL,
            observed_utc INTEGER NOT NULL,
            projection_json TEXT,
            consumed INTEGER NOT NULL DEFAULT 0 CHECK(consumed IN (0, 1)),
            retry_utc INTEGER,
            UNIQUE(capture_id, candidate_id, stage_key)
        ) STRICT;
        CREATE INDEX ix_processing_centered_view_trail_due
            ON processing_centered_view_trail_work(revision) WHERE consumed = 0;
        CREATE INDEX ix_processing_centered_view_trail_capture
            ON processing_centered_view_trail_work(capture_id, revision);
        """;

    private const string LegacyCenteredViewRuntimeSchemaSql =
        CenteredViewRuntimePrefixSql + "\n" + LegacyCenteredViewWindowWorkSchemaSql + "\n" + CenteredViewRuntimeSuffixSql;

    private const string CenteredViewRuntimeSchemaSql =
        CenteredViewRuntimePrefixSql + "\n" + CenteredViewWindowWorkSchemaSql + "\n" + CenteredViewRuntimeSuffixSql;

    private const string CenteredViewSchemaSql = """
        CREATE TABLE processing_centered_view_bases (
            base_id TEXT PRIMARY KEY NOT NULL,
            stream_id TEXT NOT NULL,
            camera_id TEXT NOT NULL,
            reference_id TEXT NOT NULL,
            reference_order INTEGER NOT NULL,
            kind INTEGER NOT NULL,
            recomputed_from TEXT REFERENCES processing_centered_view_bases(base_id),
            registered_utc INTEGER NOT NULL,
            definition TEXT NOT NULL
        ) STRICT;
        CREATE UNIQUE INDEX ix_processing_centered_view_live_bases
            ON processing_centered_view_bases(stream_id, reference_order, kind) WHERE recomputed_from IS NULL;
        CREATE INDEX ix_processing_centered_view_reference
            ON processing_centered_view_bases(stream_id, reference_id, kind, registered_utc);
        CREATE INDEX ix_processing_centered_view_capture
            ON processing_centered_view_bases(camera_id, reference_id);
        CREATE INDEX ix_processing_centered_view_single
            ON processing_centered_view_bases(stream_id, kind, reference_order DESC) WHERE recomputed_from IS NULL;
        CREATE TABLE processing_centered_view_changes (
            change_order INTEGER PRIMARY KEY AUTOINCREMENT,
            stream_id TEXT NOT NULL,
            base_id TEXT NOT NULL REFERENCES processing_centered_view_bases(base_id),
            revision_id TEXT NOT NULL,
            committed_utc INTEGER NOT NULL,
            selected_live INTEGER NOT NULL CHECK(selected_live IN (0, 1))
        ) STRICT;
        CREATE INDEX ix_processing_centered_view_change_stream
            ON processing_centered_view_changes(stream_id, change_order);
        CREATE TABLE processing_centered_view_revisions (
            base_id TEXT NOT NULL REFERENCES processing_centered_view_bases(base_id),
            revision_id TEXT NOT NULL,
            change_order INTEGER NOT NULL UNIQUE REFERENCES processing_centered_view_changes(change_order),
            published_utc INTEGER NOT NULL,
            manifest TEXT NOT NULL,
            layers TEXT NOT NULL,
            PRIMARY KEY(base_id, revision_id)
        ) STRICT;
        CREATE INDEX ix_processing_centered_view_revision_order
            ON processing_centered_view_revisions(base_id, change_order);
        CREATE TABLE processing_centered_view_members (
            exposure_id TEXT NOT NULL,
            registered_order INTEGER NOT NULL REFERENCES processing_centered_view_changes(change_order),
            base_id TEXT NOT NULL REFERENCES processing_centered_view_bases(base_id),
            PRIMARY KEY(exposure_id, registered_order)
        ) STRICT;
        CREATE TABLE processing_centered_view_intervals (
            started_order INTEGER PRIMARY KEY REFERENCES processing_centered_view_changes(change_order),
            stream_id TEXT NOT NULL,
            camera_id TEXT NOT NULL,
            base_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            started_utc INTEGER NOT NULL,
            ended_order INTEGER REFERENCES processing_centered_view_changes(change_order),
            ended_utc INTEGER,
            FOREIGN KEY(base_id, revision_id) REFERENCES processing_centered_view_revisions(base_id, revision_id),
            CHECK((ended_order IS NULL) = (ended_utc IS NULL)),
            CHECK(ended_order IS NULL OR ended_order > started_order)
        ) STRICT;
        CREATE UNIQUE INDEX ix_processing_centered_view_current
            ON processing_centered_view_intervals(camera_id) WHERE ended_order IS NULL;
        CREATE INDEX ix_processing_centered_view_current_stream
            ON processing_centered_view_intervals(stream_id) WHERE ended_order IS NULL;
        CREATE INDEX ix_processing_centered_view_history
            ON processing_centered_view_intervals(base_id, started_order);
        CREATE TABLE processing_centered_view_selections (
            owner TEXT NOT NULL,
            selection_id TEXT NOT NULL,
            base_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            saved_utc INTEGER NOT NULL,
            enabled_layers TEXT NOT NULL,
            PRIMARY KEY(owner, selection_id),
            FOREIGN KEY(base_id, revision_id) REFERENCES processing_centered_view_revisions(base_id, revision_id)
        ) STRICT;
        """;
}
