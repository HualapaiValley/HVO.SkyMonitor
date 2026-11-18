using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    internal const string ManifestInputLedger = "Inputs";
    internal const string ManifestOutputLedger = "Outputs";
    private const string ManifestLedgerTablePrefix = "CREATE TABLE processing_execution_manifest_counts(";

    // Empty node IDs are reserved for the independent status of each ledger. The
    // count rows survive pin/output retention and disappear only with the execution.
    internal static async ValueTask InitializeManifestLedgersAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid executionId, IEnumerable<string> nodeIds, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_execution_manifest_counts(execution_id, ledger_kind, node_id, evidence_count, evidence_status)
            VALUES ($execution, 'Inputs', '', NULL, 'Recorded'), ($execution, 'Outputs', '', NULL, 'Recorded');
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var nodeId in nodeIds)
            await RecordManifestCountAsync(connection, transaction, executionId, ManifestInputLedger,
                nodeId, 0, cancellationToken).ConfigureAwait(false);
    }

    // Both callers already hold the execution lease and the evidence transaction.
    // Updating a count touches this small scalar row, never the frozen plan BLOB.
    internal static async ValueTask RecordManifestCountAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid executionId, string kind, string nodeId, int count,
        CancellationToken cancellationToken)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT evidence_status,
                (SELECT evidence_count FROM processing_execution_manifest_counts
                 WHERE execution_id=$execution AND ledger_kind=$kind AND node_id=$node),
                (SELECT COUNT(*) FROM processing_execution_manifest_counts
                 WHERE execution_id=$execution AND ledger_kind=$kind AND node_id<>'')
            FROM processing_execution_manifest_counts
            WHERE execution_id=$execution AND ledger_kind=$kind AND node_id='';
            """;
        read.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        read.Parameters.AddWithValue("$kind", kind);
        read.Parameters.AddWithValue("$node", nodeId);
        int? existing;
        long entries;
        using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != "Recorded") return;
            existing = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(1);
            entries = reader.GetInt64(2);
        }
        if (nodeId.Length is 0 or > 128 || count < 0 || count > (kind == ManifestInputLedger ? 512 : 128))
        {
            await SetManifestLedgerStateAsync(connection, transaction, executionId, kind, "Corrupt", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (existing is { } saved && (kind == ManifestOutputLedger ? saved >= count : saved == count)) return;
        if (existing is null && entries >= ProcessingRunManifestBounds.MaximumNodes)
        {
            await SetManifestLedgerStateAsync(connection, transaction, executionId, kind, "ExceedsBounds", cancellationToken).ConfigureAwait(false);
            return;
        }
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO processing_execution_manifest_counts(execution_id, ledger_kind, node_id, evidence_count, evidence_status)
            VALUES ($execution, $kind, $node, $count, 'Recorded')
            ON CONFLICT(execution_id, ledger_kind, node_id) DO UPDATE SET evidence_count=excluded.evidence_count;
            """;
        write.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        write.Parameters.AddWithValue("$kind", kind);
        write.Parameters.AddWithValue("$node", nodeId);
        write.Parameters.AddWithValue("$count", count);
        await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask SetManifestLedgerStateAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid executionId, string kind, string state, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processing_execution_manifest_counts SET evidence_status=$state
            WHERE execution_id=$execution AND ledger_kind=$kind AND node_id='';
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$state", state);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Dictionary<string, int>> ReadManifestCountsAsync(SqliteConnection connection,
        Guid executionId, string kind, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, evidence_count, evidence_status FROM processing_execution_manifest_counts
            WHERE execution_id=$execution AND ledger_kind=$kind ORDER BY node_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$limit", ProcessingRunManifestBounds.MaximumNodes + 2);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var header = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var node = reader.GetString(0);
            var state = reader.GetString(2);
            if (state == "ExceedsBounds") throw new ProcessingRunManifestBoundsException();
            if (state != "Recorded") throw new InvalidDataException("The manifest count ledger is corrupt.");
            if (node.Length == 0) { header = true; continue; }
            if (counts.Count >= ProcessingRunManifestBounds.MaximumNodes) throw new ProcessingRunManifestBoundsException();
            counts.Add(node, reader.GetInt32(1));
        }
        if (!header) throw new InvalidDataException("The manifest count ledger header is missing.");
        return counts;
    }

    private static async ValueTask MigrateManifestLedgersAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteSchemaStatementsAsync(connection, transaction, cancellationToken, ManifestLedgerTablePrefix).ConfigureAwait(false);
        // Keyset iteration keeps migration memory bounded to one frozen plan. All
        // rows, optional-member removal and the version marker share one transaction.
        var after = "";
        while (true)
        {
            string id;
            byte[] frozen;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT execution_id, frozen_plan_json FROM processing_executions WHERE execution_id>$after ORDER BY execution_id LIMIT 1;";
                read.Parameters.AddWithValue("$after", after);
                using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) break;
                id = reader.GetString(0);
                frozen = (byte[])reader[1];
            }
            var executionId = Guid.ParseExact(id, "N");
            await InitializeManifestLedgersAsync(connection, transaction, executionId, [], cancellationToken).ConfigureAwait(false);
            await ImportManifestLedgerAsync(connection, transaction, executionId, ManifestInputLedger,
                frozen, FrozenOutputInputCountsMember, 512, cancellationToken).ConfigureAwait(false);
            await ImportManifestLedgerAsync(connection, transaction, executionId, ManifestOutputLedger,
                frozen, CommittedOutputCountsMember, 128, cancellationToken).ConfigureAwait(false);
            byte[] primary;
            try { primary = WithoutCommittedOutputCounts(frozen); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                // Preserve pre-existing primary corruption for its ordinary reader.
                after = id;
                continue;
            }
            if (!ReferenceEquals(primary, frozen))
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE processing_executions SET frozen_plan_json=$primary WHERE execution_id=$execution;";
                update.Parameters.AddWithValue("$primary", primary);
                update.Parameters.AddWithValue("$execution", id);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            after = id;
        }
    }

    private static async ValueTask ImportManifestLedgerAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid executionId, string kind, byte[] frozen, string member, int maximumCount, CancellationToken cancellationToken)
    {
        Dictionary<string, int> counts;
        try { counts = ReadManifestCounter(frozen, member, maximumCount); }
        catch (InvalidDataException)
        {
            await SetManifestLedgerStateAsync(connection, transaction, executionId, kind, "Corrupt", cancellationToken).ConfigureAwait(false);
            return;
        }
        foreach (var (node, count) in counts)
            await RecordManifestCountAsync(connection, transaction, executionId, kind, node, count, cancellationToken).ConfigureAwait(false);
    }
}
