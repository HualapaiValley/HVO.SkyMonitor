using System.Buffers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string FrozenOutputInputCountsMember = "frozenOutputInputCounts";

    private static Dictionary<string, int> ReadFrozenOutputInputCounts(byte[] frozenJson)
    {
        try
        {
            using var document = JsonDocument.Parse(frozenJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The frozen execution plan is not an object.");
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = false;
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Name != FrozenOutputInputCountsMember) continue;
                if (seen || member.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The frozen input counts are corrupt.");
                seen = true;
                foreach (var node in member.Value.EnumerateObject())
                {
                    if (node.Name.Length is 0 or > 128 || node.Value.ValueKind != JsonValueKind.Number ||
                        !node.Value.TryGetInt32(out var count) || count is < 0 or > 512 || !counts.TryAdd(node.Name, count))
                        throw new InvalidDataException("The frozen input counts are corrupt.");
                }
            }
            return counts;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("The frozen input counts are corrupt.", exception);
        }
    }

    // Admission proves that no historical processing inputs have been frozen yet.
    // Initialize execution copies only: immutable revision/plan identities stay unchanged.
    internal static byte[] InitializeFrozenOutputInputCounts(byte[] frozenJson, IEnumerable<string> nodeIds)
    {
        var counts = nodeIds.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        return WriteFrozenOutputInputCounts(frozenJson, counts) ?? frozenJson;
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
        var updated = WriteFrozenOutputInputCounts(frozen, counts);
        if (updated is null) return;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE processing_executions SET frozen_plan_json=$json WHERE execution_id=$execution;";
        update.Parameters.AddWithValue("$json", updated);
        update.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[]? WriteFrozenOutputInputCounts(byte[] frozen, Dictionary<string, int> counts)
    {
        using var document = JsonDocument.Parse(frozen);
        var buffer = new ArrayBufferWriter<byte>(frozen.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Name == FrozenOutputInputCountsMember) continue;
                writer.WritePropertyName(member.Name);
                writer.WriteRawValue(member.Value.GetRawText(), skipInputValidation: true);
            }
            writer.WriteStartObject(FrozenOutputInputCountsMember);
            foreach (var (node, value) in counts) writer.WriteNumber(node, value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        // Preserve the existing byte limit; an unrecordable history fails closed on export.
        return buffer.WrittenCount <= MaximumFrozenExecutionPlanBytes ? buffer.WrittenSpan.ToArray() : null;
    }

}
