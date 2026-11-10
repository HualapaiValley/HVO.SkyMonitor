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
            var optional = reader.ValueTextEquals(CommittedOutputCountsMember) || reader.ValueTextEquals(FrozenOutputInputCountsMember);
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
        => ReadManifestCounter(frozenJson, CommittedOutputCountsMember, 128);

    private static async ValueTask RecordCommittedOutputCountAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid executionId,
        string nodeId, int outputCount, CancellationToken cancellationToken)
    {
        var frozenJson = await ReadExecutionFrozenPlanJsonAsync(connection, transaction, executionId, cancellationToken).ConfigureAwait(false);
        // This is optional export bookkeeping for work that has already executed.
        // Preserve corrupt frozen bytes and finish the journal transaction; primary
        // readers and manifest downloads retain their existing validation.
        Dictionary<string, int> counts;
        try
        {
            counts = ReadCommittedOutputCounts(frozenJson);
        }
        catch (InvalidDataException)
        {
            return;
        }
        if (counts.TryGetValue(nodeId, out var existing) && existing >= outputCount) return;
        // A failed attempt commits zero outputs; a successful retry can later commit
        // its actual outputs. Ordinal slots persist across attempts, so a later
        // outputless failure must not erase the earlier committed set. Retention
        // cannot lower this count either. The lease and transaction protect both
        // the count and the node/attempt/output associations.
        counts[nodeId] = outputCount;
        var updated = WriteManifestCounter(frozenJson, CommittedOutputCountsMember, counts);
        // Preserve the existing plan-byte policy: bookkeeping never fails completed work.
        if (updated is null) return;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE processing_executions SET frozen_plan_json = $json WHERE execution_id = $execution;";
        update.Parameters.AddWithValue("$json", updated);
        update.Parameters.AddWithValue("$execution", executionId.ToString("N"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
