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

    private const string CenteredProductWithSemanticsProjection = """
        SELECT product.revision, product.capture_id, run.run_id, run.agent_id, run.camera_id,
               product.execution_id, product.producer, product.schema_version, product.variant,
               product.output_identity, product.disposition, product.reason, product.provenance_json,
               product.artifact_id, product.payload_sha256, product.content_identity_sha256,
               output.availability_state, output.availability_reason,
               raw.raw_artifact_id, raw.descriptor_sha256, raw.payload_sha256,
               output.descriptor_json, node.outputs_json, exec_output.output_ordinal
        FROM centered_exposure_products product
        JOIN centered_exposure_attempts attempt ON attempt.capture_id = product.capture_id
        JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
        JOIN raw_captures raw ON raw.capture_id = product.capture_id
        LEFT JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
        LEFT JOIN processing_execution_nodes node ON node.execution_id = product.execution_id AND node.node_id = product.producer
        LEFT JOIN processing_execution_outputs exec_output
            ON exec_output.execution_id = product.execution_id
            AND exec_output.node_id = product.producer
            AND exec_output.output_identity_sha256 = product.output_identity
        """;

    internal async ValueTask<CenteredProductSemanticProvenanceV1?> ReadCenteredProductSemanticsAsync(
        ExposureKey key, ExposureProductSlot slot, CancellationToken token)
    {
        using var connection = await OpenCenteredViewsAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT product.provenance_json, output.descriptor_json, node.outputs_json, exec_output.output_ordinal
            FROM centered_exposure_products product
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = product.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            LEFT JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
            LEFT JOIN processing_execution_nodes node ON node.execution_id = product.execution_id AND node.node_id = product.producer
            LEFT JOIN processing_execution_outputs exec_output
                ON exec_output.execution_id = product.execution_id
                AND exec_output.node_id = product.producer
                AND exec_output.output_identity_sha256 = product.output_identity
            WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
                AND product.execution_id = $execution AND product.producer = $producer
                AND product.schema_version = $schema AND product.variant = $variant
            LIMIT 1;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        command.Parameters.AddWithValue("$execution", slot.ExecutionId.ToString("N"));
        command.Parameters.AddWithValue("$producer", slot.Producer);
        command.Parameters.AddWithValue("$schema", slot.SchemaVersion);
        command.Parameters.AddWithValue("$variant", slot.Variant);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return null;
        var provenanceJson = await reader.IsDBNullAsync(0, token).ConfigureAwait(false)
            ? null : await reader.GetFieldValueAsync<byte[]>(0, token).ConfigureAwait(false);
        var descriptorJson = await reader.IsDBNullAsync(1, token).ConfigureAwait(false)
            ? null : await reader.GetFieldValueAsync<byte[]>(1, token).ConfigureAwait(false);
        var outputsJson = await reader.IsDBNullAsync(2, token).ConfigureAwait(false)
            ? null : reader.GetString(2);
        var outputOrdinal = await reader.IsDBNullAsync(3, token).ConfigureAwait(false)
            ? (int?)null : reader.GetInt32(3);
        return ResolveProductSemantics(provenanceJson, descriptorJson, outputsJson, slot.Variant, outputOrdinal);
    }

    private static CenteredProductSemanticProvenanceV1? ResolveProductSemantics(
        byte[]? provenanceJson, byte[]? descriptorJson, string? outputsJson, string variant, int? outputOrdinal)
    {
        if (CenteredProductSemanticProvenanceV1.TryParse(provenanceJson) is { } stored)
            return stored;
        var fromDescriptor = CenteredProductSemanticProvenanceV1.TryParseFromDescriptorJson(descriptorJson);
        var fromNodeOutputs = CenteredProductSemanticProvenanceV1.TryParseFromNodeOutputsJson(
            outputsJson, variant, out var nodePortIndex);
        var recipeName = fromDescriptor?.RecipeName ?? fromNodeOutputs?.RecipeName;
        var implementationVersion = fromDescriptor?.RecipeImplementationVersion ?? fromNodeOutputs?.RecipeImplementationVersion;
        if (recipeName is null && implementationVersion is null)
            return null;
        var layerKinds = CenteredViewLayerBindings.InferPresentationLayerKinds(
            recipeName, implementationVersion, nodePortIndex ?? outputOrdinal);
        return new(CenteredProductSemanticProvenanceV1.CurrentSchemaVersion, null, recipeName, implementationVersion, layerKinds);
    }

    private static async ValueTask PreserveCenteredProductSemanticsBeforeDeleteAsync(
        SqliteTransaction transaction, string outputIdentity, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection!;
        var toPreserve = new List<(long Revision, byte[] SemanticsJson)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT product.revision, product.variant, output.descriptor_json, node.outputs_json, exec_output.output_ordinal
                FROM centered_exposure_products product
                JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
                LEFT JOIN processing_execution_nodes node
                    ON node.execution_id = product.execution_id AND node.node_id = product.producer
                LEFT JOIN processing_execution_outputs exec_output
                    ON exec_output.execution_id = product.execution_id
                    AND exec_output.node_id = product.producer
                    AND exec_output.output_identity_sha256 = product.output_identity
                WHERE product.output_identity = $output
                    AND product.provenance_json IS NULL
                    AND product.schema_version <> $linear
                    AND product.schema_version NOT IN (SELECT value FROM json_each($scenes));
                """;
            select.Parameters.AddWithValue("$output", outputIdentity);
            select.Parameters.AddWithValue("$linear", CenteredExposureProductReader.LinearSchemaVersion);
            select.Parameters.AddWithValue("$scenes", System.Text.Json.JsonSerializer.Serialize(HVO.SkyMonitor.Astronomy.ProjectedSceneV1.SupportedSchemaVersions));
            using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var variant = reader.GetString(1);
                var descriptorJson = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                    ? null : await reader.GetFieldValueAsync<byte[]>(2, cancellationToken).ConfigureAwait(false);
                var outputsJson = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                    ? null : reader.GetString(3);
                var outputOrdinal = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false)
                    ? (int?)null : reader.GetInt32(4);
                if (ResolveProductSemantics(null, descriptorJson, outputsJson, variant, outputOrdinal) is { } semantics)
                    toPreserve.Add((reader.GetInt64(0), semantics.Serialize()));
            }
        }
        foreach (var (revision, semanticsJson) in toPreserve)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE centered_exposure_products SET provenance_json = $provenance
                WHERE revision = $revision AND provenance_json IS NULL;
                """;
            update.Parameters.AddWithValue("$provenance", semanticsJson);
            update.Parameters.AddWithValue("$revision", revision);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask<IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>>>
        ReadRecoveredCenteredLayerBindingsAsync(ExposureKey key, Guid executionId, CancellationToken token)
    {
        var authoritativeResolution = await ReadAuthoritativeAssociationResolutionAsync(key, executionId, token)
            .ConfigureAwait(false);
        using var connection = await OpenCenteredViewsAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = CenteredProductWithSemanticsProjection + "\n" + """
            WHERE product.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
                AND product.execution_id = $execution
            ORDER BY product.revision;
            """;
        SqliteRawCaptureJournal.AddExposureParameters(command, key);
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        var entries = new List<(ExposureProductLookup Lookup, CenteredProductSemanticProvenanceV1? Semantics)>();
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var lookup = ReadCenteredLookup(reader, out _);
                var provenanceJson = await reader.IsDBNullAsync(12, token).ConfigureAwait(false)
                    ? null : await reader.GetFieldValueAsync<byte[]>(12, token).ConfigureAwait(false);
                var descriptorJson = await reader.IsDBNullAsync(21, token).ConfigureAwait(false)
                    ? null : await reader.GetFieldValueAsync<byte[]>(21, token).ConfigureAwait(false);
                var outputsJson = await reader.IsDBNullAsync(22, token).ConfigureAwait(false)
                    ? null : reader.GetString(22);
                var outputOrdinal = await reader.IsDBNullAsync(23, token).ConfigureAwait(false)
                    ? (int?)null : reader.GetInt32(23);
                var semantics = ResolveProductSemantics(provenanceJson, descriptorJson, outputsJson, lookup.Slot.Variant, outputOrdinal);
                entries.Add((lookup, semantics));
            }
        }

        var declaredOutputs = new List<RecoveredNodeDeclaredOutput>();
        using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.CommandText = """
                SELECT node_id, outputs_json
                FROM processing_execution_nodes
                WHERE execution_id = $execution
                ORDER BY node_id COLLATE BINARY;
                """;
            nodeCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            using var nodeReader = await nodeCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await nodeReader.ReadAsync(token).ConfigureAwait(false))
            {
                var nodeId = nodeReader.GetString(0);
                var outputsJson = await nodeReader.IsDBNullAsync(1, token).ConfigureAwait(false)
                    ? null : nodeReader.GetString(1);
                var parsed = CenteredProductSemanticProvenanceV1.ParseDeclaredNodeOutputs(nodeId, outputsJson);
                if (parsed.Count > 0)
                    declaredOutputs.AddRange(parsed);
            }
        }

        return CenteredViewLayerBindings.BuildRecoveredBindings(entries, authoritativeResolution, declaredOutputs);
    }

    internal async ValueTask<CenteredAuthoritativeAssociationWriter?> ReadAuthoritativeAssociationWriterAsync(
        ExposureKey key, Guid executionId, CancellationToken token)
    {
        var resolution = await ReadAuthoritativeAssociationResolutionAsync(key, executionId, token).ConfigureAwait(false);
        return resolution.State == CenteredAuthoritativeAssociationResolutionState.Proven ? resolution.Writer : null;
    }

    internal async ValueTask<CenteredAuthoritativeAssociationResolution> ReadAuthoritativeAssociationResolutionAsync(
        ExposureKey key, Guid executionId, CancellationToken token)
    {
        using var connection = await OpenCenteredViewsAsync(token).ConfigureAwait(false);
        CenteredAuthoritativeAssociationWriter? nodeCandidate = null;
        using (var nodeCommand = connection.CreateCommand())
        {
            nodeCommand.CommandText = """
                SELECT node.node_id, COALESCE(json_extract(output.value, '$.variant'), json_extract(output.value, '$.Variant'))
                FROM processing_execution_nodes node
                JOIN json_each(node.outputs_json) output
                WHERE node.execution_id = $execution
                    AND COALESCE(json_extract(output.value, '$.variant'), json_extract(output.value, '$.Variant')) IS NOT NULL
                    AND (
                        COALESCE(json_extract(output.value, '$.recipe.name'), json_extract(output.value, '$.Recipe.Name')) = $recipe
                        OR (
                            COALESCE(json_extract(output.value, '$.recipe.name'), json_extract(output.value, '$.Recipe.Name')) IS NULL
                            AND COALESCE(json_extract(output.value, '$.schemaVersion'), json_extract(output.value, '$.SchemaVersion')) IN ($v1, $v2)
                            AND (
                                node.node_id = $producer
                                OR COALESCE(json_extract(output.value, '$.variant'), json_extract(output.value, '$.Variant')) = $variant
                            )
                        )
                    )
                ORDER BY node.node_id COLLATE BINARY, CAST(output.key AS INTEGER)
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
                nodeCandidate = new(reader.GetString(0), reader.GetString(1));
        }

        CenteredAuthoritativeAssociationResolution? productCandidate = null;
        using (var productCommand = connection.CreateCommand())
        {
            productCommand.CommandText = """
                WITH candidates AS (
                    SELECT product.producer, product.variant, product.revision,
                        CASE
                            WHEN product.provenance_json IS NOT NULL
                                AND json_valid(CAST(product.provenance_json AS TEXT))
                                AND json_extract(CAST(product.provenance_json AS TEXT), '$.schemaVersion') = $semSchema
                                AND (
                                    json_extract(CAST(product.provenance_json AS TEXT), '$.recipeName') IS NOT NULL
                                    OR json_extract(CAST(product.provenance_json AS TEXT), '$.stepAlias') IS NOT NULL
                                )
                            THEN CASE
                                WHEN (json_extract(CAST(product.provenance_json AS TEXT), '$.recipeName') = $recipe
                                      OR json_extract(CAST(product.provenance_json AS TEXT), '$.stepAlias') = $producer)
                                    AND NOT (
                                        json_type(CAST(product.provenance_json AS TEXT), '$.layerKinds') = 'array'
                                        AND json_array_length(CAST(product.provenance_json AS TEXT), '$.layerKinds') = 0
                                    )
                                THEN 0
                                ELSE -1
                            END
                            WHEN output.output_identity_sha256 IS NOT NULL
                                AND json_valid(CAST(output.descriptor_json AS TEXT))
                                AND COALESCE(
                                    json_extract(CAST(output.descriptor_json AS TEXT), '$.artifact.recipe.name'),
                                    json_extract(CAST(output.descriptor_json AS TEXT), '$.descriptor.artifact.recipe.name'),
                                    json_extract(CAST(output.descriptor_json AS TEXT), '$.Artifact.Recipe.Name')
                                ) IS NOT NULL
                            THEN CASE
                                WHEN COALESCE(
                                    json_extract(CAST(output.descriptor_json AS TEXT), '$.artifact.recipe.name'),
                                    json_extract(CAST(output.descriptor_json AS TEXT), '$.descriptor.artifact.recipe.name'),
                                    json_extract(CAST(output.descriptor_json AS TEXT), '$.Artifact.Recipe.Name')
                                ) = $recipe
                                THEN 0
                                ELSE -1
                            END
                            WHEN node.outputs_json IS NOT NULL
                                AND json_valid(node.outputs_json)
                                AND EXISTS (
                                    SELECT 1 FROM json_each(node.outputs_json) nout
                                    WHERE COALESCE(json_extract(nout.value, '$.variant'), json_extract(nout.value, '$.Variant')) = product.variant
                                      AND COALESCE(json_extract(nout.value, '$.recipe.name'), json_extract(nout.value, '$.Recipe.Name')) IS NOT NULL
                                )
                            THEN CASE
                                WHEN EXISTS (
                                    SELECT 1 FROM json_each(node.outputs_json) nout
                                    WHERE COALESCE(json_extract(nout.value, '$.variant'), json_extract(nout.value, '$.Variant')) = product.variant
                                      AND COALESCE(json_extract(nout.value, '$.recipe.name'), json_extract(nout.value, '$.Recipe.Name')) = $recipe
                                )
                                THEN 0
                                ELSE -1
                            END
                            WHEN product.schema_version IN ($dispV1, $dispV2) THEN 0
                            WHEN product.producer = $producer OR product.variant = $variant THEN 1
                            WHEN output.output_identity_sha256 IS NULL
                                OR product.disposition <> 'Available'
                                OR output.availability_state <> 'Available'
                            THEN 2
                            ELSE -1
                        END AS match_rank
                    FROM centered_exposure_products product
                    JOIN centered_exposure_attempts attempt ON attempt.capture_id = product.capture_id
                    JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
                    LEFT JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
                    LEFT JOIN processing_execution_nodes node ON node.execution_id = product.execution_id AND node.node_id = product.producer
                    WHERE product.capture_id = $capture AND run.run_id = $run
                        AND run.agent_id = $agent AND run.camera_id = $camera
                        AND product.execution_id = $execution
                        AND product.schema_version IN ($v1, $v2, $dispV1, $dispV2)
                )
                -- Order by ordinal producer before match_rank so an expired higher-priority writer whose
                -- provenance/outputs metadata was cleared fails closed (UnidentifiableExpired) rather than
                -- promoting a surviving lower-priority writer.
                SELECT producer, variant, match_rank
                FROM candidates
                WHERE match_rank >= 0
                ORDER BY producer COLLATE BINARY, match_rank, revision, variant COLLATE BINARY
                LIMIT 1;
                """;
            SqliteRawCaptureJournal.AddExposureParameters(productCommand, key);
            productCommand.Parameters.AddWithValue("$execution", executionId.ToString("N"));
            productCommand.Parameters.AddWithValue("$v1", MeasuredStellarAssociationsV1.V1SchemaVersion);
            productCommand.Parameters.AddWithValue("$v2", MeasuredStellarAssociationsV1.CurrentSchemaVersion);
            productCommand.Parameters.AddWithValue("$dispV1", CenteredViewLayerBindings.DispositionPrefix + MeasuredStellarAssociationsV1.V1SchemaVersion);
            productCommand.Parameters.AddWithValue("$dispV2", CenteredViewLayerBindings.DispositionPrefix + MeasuredStellarAssociationsV1.CurrentSchemaVersion);
            productCommand.Parameters.AddWithValue("$variant", ExposureAssociationProducts.Variant);
            productCommand.Parameters.AddWithValue("$producer", ExposureAssociationProducts.StepAlias);
            productCommand.Parameters.AddWithValue("$recipe", ExposureAssociationProducts.RecipeName);
            productCommand.Parameters.AddWithValue("$semSchema", CenteredProductSemanticProvenanceV1.CurrentSchemaVersion);
            using var productReader = await productCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await productReader.ReadAsync(token).ConfigureAwait(false))
            {
                var writer = new CenteredAuthoritativeAssociationWriter(productReader.GetString(0), productReader.GetString(1));
                productCandidate = productReader.GetInt32(2) == 2
                    ? CenteredAuthoritativeAssociationResolution.UnidentifiableExpired(writer)
                    : CenteredAuthoritativeAssociationResolution.Resolved(writer);
            }
        }

        // Compare declared node output contracts against observed product candidates by ordinal producer so
        // partial metadata loss on an earlier expired producer never promotes a surviving lower-priority node contract.
        if (nodeCandidate is { } declared)
        {
            if (productCandidate is { Writer: { } observed } &&
                StringComparer.Ordinal.Compare(observed.Producer, declared.Producer) < 0)
                return productCandidate.Value;
            return CenteredAuthoritativeAssociationResolution.Resolved(declared);
        }

        return productCandidate ?? CenteredAuthoritativeAssociationResolution.None;
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
