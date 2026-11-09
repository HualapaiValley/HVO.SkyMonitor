using System.Buffers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string CommittedOutputCountsMember = "committedOutputCounts";

    private static Dictionary<string, int> ReadCommittedOutputCounts(byte[] frozenJson)
    {
        try
        {
            using var document = JsonDocument.Parse(frozenJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The frozen execution plan is not a JSON object.");
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = false;
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Name != CommittedOutputCountsMember) continue;
                if (seen || member.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("The committed output counts are corrupt.");
                seen = true;
                foreach (var node in member.Value.EnumerateObject())
                {
                    if (node.Name.Length is 0 or > 128 || node.Value.ValueKind != JsonValueKind.Number ||
                        !node.Value.TryGetInt32(out var count) || count is < 0 or > 128 ||
                        !counts.TryAdd(node.Name, count))
                        throw new InvalidDataException("The committed output counts are corrupt.");
                }
            }
            return counts;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The frozen execution plan is corrupt.", exception);
        }
    }

    private static async ValueTask RecordCommittedOutputCountAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid executionId,
        string nodeId, int outputCount, CancellationToken cancellationToken)
    {
        var frozenJson = await ReadExecutionFrozenPlanJsonAsync(connection, transaction, executionId, cancellationToken).ConfigureAwait(false);
        var counts = ReadCommittedOutputCounts(frozenJson);
        if (counts.TryGetValue(nodeId, out var existing))
        {
            if (existing != outputCount)
                throw new ProcessingGraphStoreConflictException("The committed output count cannot be replaced.");
            return;
        }
        counts.Add(nodeId, outputCount);
        using var document = JsonDocument.Parse(frozenJson);
        var buffer = new ArrayBufferWriter<byte>(frozenJson.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var member in document.RootElement.EnumerateObject())
            {
                if (member.Name == CommittedOutputCountsMember) continue;
                writer.WritePropertyName(member.Name);
                writer.WriteRawValue(member.Value.GetRawText(), skipInputValidation: true);
            }
            writer.WriteStartObject(CommittedOutputCountsMember);
            foreach (var (node, count) in counts) writer.WriteNumber(node, count);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        // Export bookkeeping must not make an otherwise valid processing node fail at the
        // existing plan-byte bound. Without room, export fails closed with the unrecorded reason.
        if (buffer.WrittenCount > MaximumFrozenExecutionPlanBytes) return;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE processing_executions SET frozen_plan_json = $json WHERE execution_id = $execution;";
        update.Parameters.AddWithValue("$json", buffer.WrittenSpan.ToArray());
        update.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
