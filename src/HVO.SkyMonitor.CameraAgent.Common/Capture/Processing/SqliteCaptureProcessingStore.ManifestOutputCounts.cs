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

    private static ValueTask RecordCommittedOutputCountAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid executionId, string nodeId, int outputCount, CancellationToken cancellationToken)
        => RecordManifestCountAsync(connection, transaction, executionId, ManifestOutputLedger, nodeId, outputCount, cancellationToken);
}
