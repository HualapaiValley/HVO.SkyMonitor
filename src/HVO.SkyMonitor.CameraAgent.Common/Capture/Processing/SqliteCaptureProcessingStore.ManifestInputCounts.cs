using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string FrozenOutputInputCountsMember = "frozenOutputInputCounts";

    private static Dictionary<string, int> ReadFrozenOutputInputCounts(byte[] frozenJson)
        => ReadManifestCounter(frozenJson, FrozenOutputInputCountsMember, 512);

    // Admission proves that no historical processing inputs have been frozen yet.
    // Initialize execution copies only: immutable revision/plan identities stay unchanged.
    internal static byte[] InitializeFrozenOutputInputCounts(byte[] frozenJson, IEnumerable<string> nodeIds)
    {
        var counts = nodeIds.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        return WriteManifestCounter(frozenJson, FrozenOutputInputCountsMember, counts) ?? frozenJson;
    }

    // This ledger shares the existing frozen JSON, not the pins' cascading foreign key.
    // It is written in the same lease-checked transaction that freezes/replaces the pins.
    private static async ValueTask RecordFrozenOutputInputCountAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid executionId, string nodeId, CancellationToken cancellationToken)
    {
        var frozen = await ReadExecutionFrozenPlanJsonAsync(connection, transaction, executionId, cancellationToken).ConfigureAwait(false);
        Dictionary<string, int> counts;
        try { counts = ReadFrozenOutputInputCounts(frozen); }
        catch (InvalidDataException) { return; } // Optional export corruption cannot roll back input resolution.
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT COUNT(*) FROM processing_execution_output_input_pins WHERE execution_id=$execution AND node_id=$node;";
        read.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        read.Parameters.AddWithValue("$node", nodeId);
        var count = checked((int)(long)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        if (counts.TryGetValue(nodeId, out var existing) && existing == count) return;
        counts[nodeId] = count;
        var updated = WriteManifestCounter(frozen, FrozenOutputInputCountsMember, counts);
        if (updated is null) return;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE processing_executions SET frozen_plan_json=$json WHERE execution_id=$execution;";
        update.Parameters.AddWithValue("$json", updated);
        update.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
