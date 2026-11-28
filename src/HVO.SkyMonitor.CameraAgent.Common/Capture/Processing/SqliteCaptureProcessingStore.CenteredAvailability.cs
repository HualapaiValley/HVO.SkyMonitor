using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string CenteredAvailabilityCutoffIndex = """
        CREATE INDEX ix_processing_centered_output_availability_cutoff
            ON processing_centered_output_availability(output_identity_sha256, change_id DESC, recorded_ticks, state);
        """;
    private const string LegacyCenteredAvailabilityCutoffIndex = """
        CREATE INDEX ix_processing_centered_output_availability_cutoff
            ON processing_centered_output_availability(output_identity_sha256, change_id DESC);
        """;
    private const string CenteredAvailabilitySchemaSql = """
        CREATE TABLE processing_centered_output_availability(
            change_id INTEGER PRIMARY KEY,
            output_identity_sha256 TEXT NOT NULL CHECK(length(output_identity_sha256) = 64),
            state TEXT NOT NULL CHECK(state IN ('Available', 'Missing', 'Quarantined', 'Expired')),
            reason TEXT,
            recorded_ticks INTEGER NOT NULL,
            observed_ticks INTEGER CHECK(observed_ticks >= recorded_ticks)
        ) STRICT;
        """ + "\n" + CenteredAvailabilityCutoffIndex + "\n" + """
        CREATE INDEX ix_processing_centered_output_availability_pending
            ON processing_centered_output_availability(change_id) WHERE observed_ticks IS NULL;
        """;

    private const string CenteredAvailabilityTable = "processing_centered_output_availability";
    private static string LegacySchema10BeforeAvailabilitySql =>
        LegacySchema10Sql.Replace(CenteredAvailabilitySchemaSql, string.Empty, StringComparison.Ordinal);
    private static string LegacySchema10WithAvailabilitySql =>
        LegacySchema10Sql.Replace(CenteredAvailabilityCutoffIndex, LegacyCenteredAvailabilityCutoffIndex, StringComparison.Ordinal);
    private static readonly Lazy<Dictionary<string, string>> LegacySchema10BeforeAvailabilityDefinitions =
        new(() => CreateSchemaDefinitions(LegacySchema10BeforeAvailabilitySql));
    private static readonly Lazy<Dictionary<string, string>> LegacySchema10WithAvailabilityDefinitions =
        new(() => CreateSchemaDefinitions(LegacySchema10WithAvailabilitySql));
    private static readonly Lazy<Dictionary<string, string>> LegacySchema12BeforeAvailabilityDefinitions =
        new(() => CreateSchemaDefinitions(SchemaBeforeAstrometryIndexSql.Replace(CenteredAvailabilitySchemaSql, string.Empty, StringComparison.Ordinal)));
    private static readonly Lazy<Dictionary<string, string>> LegacySchema12WithAvailabilityDefinitions =
        new(() => CreateSchemaDefinitions(SchemaBeforeAstrometryIndexSql.Replace(CenteredAvailabilityCutoffIndex,
            LegacyCenteredAvailabilityCutoffIndex, StringComparison.Ordinal)));
    private const string CenteredViewWindowWorkTable = "processing_centered_view_window_work";
    private const string CenteredViewWindowDueIndex = "ix_processing_centered_view_window_due";
    private static readonly Lazy<Dictionary<string, string>> LegacySchema12BeforeRegistrationStateDefinitions =
        new(() => CreateSchemaDefinitions(SchemaBeforeRegistrationStateSql));
    private static readonly Lazy<Dictionary<string, string>> LegacySchema12BeforeViewRetentionDefinitions =
        new(() => CreateSchemaDefinitions(SchemaBeforeCenteredViewRetentionIndexesSql));
    private static readonly Lazy<Dictionary<string, string>> LegacySchema12BeforeWindowWorkQueueDefinitions =
        new(() => CreateSchemaDefinitions(SchemaBeforeWindowWorkQueueSql));

    internal static string LegacySchema10BeforeAvailabilitySqlForTests => LegacySchema10BeforeAvailabilitySql;
    internal const string LegacyCenteredAvailabilityCutoffIndexForTests = LegacyCenteredAvailabilityCutoffIndex;
    internal const string LegacyCenteredViewWindowWorkSchemaSqlForTests = LegacyCenteredViewWindowWorkSchemaSql;

    private static bool IsEarlierCenteredAvailabilitySchema(Dictionary<string, string> actual)
        => MatchesSchemaDefinitions(actual, actual.ContainsKey(CenteredAvailabilityTable)
            ? LegacySchema12WithAvailabilityDefinitions.Value : LegacySchema12BeforeAvailabilityDefinitions.Value);

    private static bool IsEarlierCenteredSchema(Dictionary<string, string> actual)
        => BeforeWindowWorkQueue(actual) is { } withoutWindowQueue &&
           BeforeViewRetentionIndexes(withoutWindowQueue) is { } earlier &&
           (MatchesSchemaDefinitions(withoutWindowQueue, LegacySchema12BeforeWindowWorkQueueDefinitions.Value) ||
            MatchesSchemaDefinitions(earlier, LegacySchema12BeforeRegistrationStateDefinitions.Value) ||
            MatchesSchemaDefinitions(earlier, LegacySchema12BeforeViewRetentionDefinitions.Value) ||
            MatchesSchemaDefinitions(earlier, PreAstrometrySchema12Definitions.Value) ||
            IsEarlierCenteredAvailabilitySchema(earlier));

    private static Dictionary<string, string>? BeforeWindowWorkQueue(Dictionary<string, string> actual)
    {
        var hasDueIndex = actual.ContainsKey(CenteredViewWindowDueIndex);
        var hasNewTable = actual.TryGetValue(CenteredViewWindowWorkTable, out var table) &&
            table == CanonicalSchemaDefinitions.Value[CenteredViewWindowWorkTable];
        if (!hasDueIndex && !hasNewTable)
            return actual;

        // The window work queue table shape and due index move together. A partial or altered state fails closed.
        if (!hasDueIndex || !hasNewTable ||
            actual[CenteredViewWindowDueIndex] != CanonicalSchemaDefinitions.Value[CenteredViewWindowDueIndex])
            return null;

        var legacy = new Dictionary<string, string>(actual, StringComparer.Ordinal)
        {
            [CenteredViewWindowWorkTable] = LegacySchema12BeforeWindowWorkQueueDefinitions.Value[CenteredViewWindowWorkTable]
        };
        legacy.Remove(CenteredViewWindowDueIndex);
        return legacy;
    }

    private static Dictionary<string, string>? BeforeViewRetentionIndexes(Dictionary<string, string> actual)
    {
        var names = CenteredViewRetentionSchema.IndexNames;
        if (!names.Any(actual.ContainsKey))
            return actual;

        // The retention indexes and the older availability adoption are independent. A complete
        // canonical index group can accompany an older shape. A partial or changed group fails closed.
        if (!names.All(name => actual.TryGetValue(name, out var value) && value == CanonicalSchemaDefinitions.Value[name]))
            return null;

        var withoutIndexes = new Dictionary<string, string>(actual, StringComparer.Ordinal);
        foreach (var name in names)
            withoutIndexes.Remove(name);
        return withoutIndexes;
    }

    private async ValueTask EnsureCurrentCenteredSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // The read-only inspection already verified the full layout. Recheck under the write
        // transaction before adopting a predecessor, including if another initializer won the race.
        if (await ExecuteScalarLongAsync(connection,
                """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE name IN ('ix_processing_outputs_astrometry_lookup', 'ix_processing_centered_view_changes_base',
                    'processing_centered_registration_input_failures', 'ix_processing_centered_view_window_due');
                """,
                cancellationToken).ConfigureAwait(false) == 4)
            return;

#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        var actual = await ReadSchemaDefinitionsAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        if (MatchesSchemaDefinitions(actual, CanonicalSchemaDefinitions.Value))
            return;
        if (!IsEarlierCenteredSchema(actual))
            throw new InvalidDataException("The earlier centered processing schema is not canonical.");

        _faultInjector.Inject(CaptureProcessingFaultPoint.AfterSchemaTransactionBegan, "schema");
        if (BeforeWindowWorkQueue(actual) is { } withoutWindowQueue &&
            BeforeViewRetentionIndexes(withoutWindowQueue) is { } earlier &&
            IsEarlierCenteredAvailabilitySchema(earlier))
            await AdoptCenteredAvailabilityHistoryAsync(transaction, actual.ContainsKey(CenteredAvailabilityTable), cancellationToken)
                .ConfigureAwait(false);
        if (!actual.ContainsKey(LegacyAstrometryJournalSchema.LookupIndexName))
            await ExecuteSchemaStatementsAsync(connection, transaction, cancellationToken, AstrometryLookupIndexPrefix)
                .ConfigureAwait(false);
        if (!actual.ContainsKey("processing_centered_registration_input_failures"))
            await AddCenteredRegistrationStateSchemaAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (!actual.ContainsKey(CenteredViewRetentionSchema.IndexNames[0]))
        {
            using var indexes = connection.CreateCommand();
            indexes.Transaction = transaction;
            indexes.CommandText = CenteredViewRetentionSchema.IndexSql;
            await indexes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!actual.ContainsKey(CenteredViewWindowDueIndex))
            await AdoptCenteredViewWindowWorkQueueAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ValidateSchemaAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        _faultInjector.Inject(CaptureProcessingFaultPoint.BeforeSchemaCommit, "schema");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AdoptCenteredViewWindowWorkQueueAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX ix_processing_centered_view_window_live_base;
            ALTER TABLE processing_centered_view_window_work RENAME TO processing_centered_view_window_work_legacy;
            """ + "\n" + CenteredViewWindowWorkSchemaSql + "\n" + """
            INSERT INTO processing_centered_view_window_work(window_sha256, live_base_id, retry_utc, terminal_reason, retry_count)
            SELECT legacy.window_sha256, legacy.live_base_id, legacy.retry_utc, legacy.terminal_reason, 0
            FROM processing_centered_view_window_work_legacy legacy
            JOIN processing_centered_windows window ON window.window_sha256 = legacy.window_sha256
            WHERE legacy.retry_utc IS NULL
                OR (window.terminal = 1 AND window.closure_json IS NOT NULL AND window.reference_capture_id IS NOT NULL);
            DROP TABLE processing_centered_view_window_work_legacy;
            """ + "\n" + BackfillPendingWindowWorkSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The known legacy schemas use fixed migration statements.")]
    private async ValueTask AdoptCenteredAvailabilityHistoryAsync(SqliteTransaction transaction, bool hadAvailability,
        CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = hadAvailability
            ? "DROP INDEX ix_processing_centered_output_availability_cutoff;\n" + CenteredAvailabilityCutoffIndex
            : CenteredAvailabilitySchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // The covering index identifies the snapshot that adopted trusted clocks. It is changed atomically
        // with this reset, including when an intermediate build already created the availability table.
        // Already frozen closures keep their persisted identities.
        command.CommandText = """
            UPDATE processing_centered_attempt_calendar
            SET admitted_ticks = NULL, dispatched_ticks = $unobserved,
                expired_ticks = CASE WHEN expired_ticks IS NULL THEN NULL ELSE $unobserved END;
            UPDATE processing_centered_product_clock SET settled_ticks = NULL;
            UPDATE centered_exposure_products SET provenance_json = NULL
            WHERE schema_version IN (SELECT value FROM json_each($scenes));
            """;
        command.Parameters.AddWithValue("$unobserved", CenteredWindowRuntimeJournal.UnobservedCalendarTicks);
        command.Parameters.AddWithValue("$scenes", JsonSerializer.Serialize(ProjectedSceneV1.SupportedSchemaVersions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (!hadAvailability)
            await SeedCenteredAvailabilityHistoryAsync(transaction, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SeedCenteredAvailabilityHistoryAsync(SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_centered_output_availability(output_identity_sha256, state, reason, recorded_ticks, observed_ticks)
            SELECT DISTINCT product.output_identity, 'Missing', 'product.availability-history-unobserved', 0, 0
            FROM centered_exposure_products product WHERE product.output_identity IS NOT NULL;
            INSERT INTO processing_centered_output_availability(output_identity_sha256, state, reason, recorded_ticks, observed_ticks)
            SELECT DISTINCT product.output_identity, output.availability_state, output.availability_reason, $now, $now
            FROM centered_exposure_products product
            JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity;
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<long?> RecordCenteredAvailabilityChangeAsync(SqliteTransaction transaction,
        string outputIdentity, string state, string? reason, CancellationToken cancellationToken)
    {
        if (state != "Available")
            await PreserveCenteredSceneHeaderAsync(transaction, outputIdentity, cancellationToken).ConfigureAwait(false);
        using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_centered_output_availability(output_identity_sha256, state, reason, recorded_ticks)
            SELECT $output, $state, $reason, $now
            WHERE EXISTS (
                SELECT 1 FROM processing_outputs output
                JOIN centered_exposure_products product ON product.capture_id = output.capture_id
                    AND product.output_identity = output.output_identity_sha256
                WHERE output.output_identity_sha256 = $output)
            AND ($state <> 'Available' OR EXISTS (
                SELECT 1 FROM processing_centered_output_availability WHERE output_identity_sha256 = $output))
            AND NOT EXISTS (
                SELECT 1 FROM (
                    SELECT state, reason FROM processing_centered_output_availability
                    WHERE output_identity_sha256 = $output ORDER BY change_id DESC LIMIT 1
                ) WHERE state = $state AND reason IS $reason)
            RETURNING change_id;
            """;
        command.Parameters.AddWithValue("$output", outputIdentity);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcTicks);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    // A deterministic scene can be removed from the current output inventory before regeneration.
    // Keep its original payload-free header alongside the settlement, where closure already reads.
    private sealed record HistoricalCenteredScene(CenteredStackArtifactHeader Header, long TrustedFromTicks);

    private async ValueTask PreserveCenteredSceneHeaderAsync(SqliteTransaction transaction,
        string outputIdentity, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection!;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT output.artifact_id,
                CASE WHEN loss.change_id IS NULL THEN 0 ELSE COALESCE((
                    SELECT observed_ticks FROM processing_centered_output_availability restore
                    WHERE restore.output_identity_sha256 = output.output_identity_sha256
                        AND restore.state = 'Available' AND restore.change_id > loss.change_id
                    ORDER BY restore.change_id DESC LIMIT 1), $now) END
            FROM processing_outputs output
            JOIN centered_exposure_products product ON product.capture_id = output.capture_id
                AND product.output_identity = output.output_identity_sha256 AND product.artifact_id = output.artifact_id
                AND product.schema_version = output.product_schema_version
            LEFT JOIN processing_centered_output_availability loss ON loss.change_id = (
                SELECT change_id FROM processing_centered_output_availability history
                WHERE history.output_identity_sha256 = output.output_identity_sha256 AND history.state <> 'Available'
                ORDER BY history.change_id DESC LIMIT 1)
            WHERE output.output_identity_sha256 = $output AND product.provenance_json IS NULL
                AND product.schema_version IN (SELECT value FROM json_each($scenes))
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$output", outputIdentity);
        command.Parameters.AddWithValue("$scenes", JsonSerializer.Serialize(ProjectedSceneV1.SupportedSchemaVersions));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().UtcTicks);
        string artifactId;
        long trustedFrom;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return;
            artifactId = reader.GetString(0);
            trustedFrom = reader.GetInt64(1);
        }

        var output = await ReadOutputByArtifactIdAsync(connection, transaction, Guid.ParseExact(artifactId, "N"), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("The centered scene has no committed output header.");
        var header = CenteredStackArtifactHeader.FromArtifact(CenteredWindowArtifactHeader.FromOutput(output));
        command.CommandText = """
            UPDATE centered_exposure_products SET provenance_json = $header
            WHERE output_identity = $output AND artifact_id = $artifact AND capture_id = $capture
              AND schema_version = $schema AND payload_sha256 = $payload AND content_identity_sha256 IS $content
              AND provenance_json IS NULL;
            """;
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.Parameters.AddWithValue("$capture", output.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$schema", output.ProductSchemaVersion!);
        command.Parameters.AddWithValue("$payload", output.Artifact.ChecksumSha256);
        command.Parameters.AddWithValue("$content", (object?)output.ContentIdentitySha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$header", JsonSerializer.SerializeToUtf8Bytes(new HistoricalCenteredScene(header, trustedFrom)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveCenteredAvailabilityChangeAsync(SqliteConnection connection, long? changeId)
    {
        if (changeId is null) return;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE processing_centered_output_availability SET observed_ticks = MAX(recorded_ticks, $observed)
                WHERE change_id = $change AND observed_ticks IS NULL;
                """;
            command.Parameters.AddWithValue("$change", changeId.Value);
            command.Parameters.AddWithValue("$observed", _timeProvider.GetUtcNow().UtcTicks);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The lifecycle transaction is committed. Recovery can observe the transition from a later snapshot.
        }
        _centeredWindowWakeup?.NotifyCommitted();
    }
}
