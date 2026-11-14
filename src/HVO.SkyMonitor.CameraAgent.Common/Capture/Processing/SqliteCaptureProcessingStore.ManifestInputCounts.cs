using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string FrozenOutputInputCountsMember = "frozenOutputInputCounts";

    private static async ValueTask RecordFrozenOutputInputCountAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid executionId, string nodeId, CancellationToken cancellationToken)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT COUNT(*) FROM processing_execution_output_input_pins WHERE execution_id=$execution AND node_id=$node;";
        read.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        read.Parameters.AddWithValue("$node", nodeId);
        var count = checked((int)(long)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        await RecordManifestCountAsync(connection, transaction, executionId, ManifestInputLedger, nodeId, count, cancellationToken).ConfigureAwait(false);
    }
}
