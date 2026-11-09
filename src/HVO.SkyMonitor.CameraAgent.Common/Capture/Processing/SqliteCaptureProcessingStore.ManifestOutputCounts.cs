using System.Buffers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private const string CommittedOutputCountsMember = "committedOutputCounts";

    // The optional member is not part of the processing plan. Walk its tokens iteratively
    // under the existing byte bound, then validate the primary plan with its original
    // depth/shape rules. Raising the primary plan's depth limit would hide corruption.
    private static byte[] WithoutCommittedOutputCounts(byte[] frozenJson)
    {
        if (frozenJson.Length > MaximumFrozenExecutionPlanBytes)
            throw new InvalidDataException("The frozen execution plan exceeds its byte bound.");
        var reader = new Utf8JsonReader(frozenJson, new JsonReaderOptions { MaxDepth = MaximumFrozenExecutionPlanBytes });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The frozen execution plan is not a JSON object.");
        var buffer = new ArrayBufferWriter<byte>(frozenJson.Length);
        buffer.Write("{"u8);
        var removed = false;
        var written = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("The frozen execution plan has an invalid member.");
            var start = checked((int)reader.TokenStartIndex);
            var optional = reader.ValueTextEquals(CommittedOutputCountsMember);
            if (!reader.Read()) throw new JsonException("The frozen execution plan member has no value.");
            reader.Skip();
            if (optional)
            {
                removed = true;
                continue;
            }
            if (written) buffer.Write(","u8);
            buffer.Write(frozenJson.AsSpan(start, checked((int)reader.BytesConsumed) - start));
            written = true;
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            throw new JsonException("The frozen execution plan has an invalid root.");
        buffer.Write("}"u8);
        return removed ? buffer.WrittenSpan.ToArray() : frozenJson;
    }

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
        // Validate primary evidence independently. Corrupt optional counts cannot roll
        // back a processing completion; preserve them so export reports corruption.
        Dictionary<string, int> counts;
        try
        {
            counts = ReadCommittedOutputCounts(frozenJson);
        }
        catch (InvalidDataException)
        {
            using var primary = JsonDocument.Parse(WithoutCommittedOutputCounts(frozenJson));
            return;
        }
        if (counts.TryGetValue(nodeId, out var existing) && existing >= outputCount) return;
        // A failed attempt commits zero outputs; a successful retry can later commit
        // its actual outputs. Ordinal slots persist across attempts, so a later
        // outputless failure must not erase the earlier committed set. Retention
        // cannot lower this count either. The lease and transaction protect both
        // the count and the node/attempt/output associations.
        counts[nodeId] = outputCount;
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
