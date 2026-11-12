using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    // Only the selected ledger is semantically parsed. An unrelated optional value
    // can be corrupt/deep without preventing this ledger from recording real work.
    // The primary plan's existing depth validation remains in its primary readers.
    private static Dictionary<string, int> ReadManifestCounter(byte[] frozenJson, string memberName, int maximumCount)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadIsolatedManifestCounter(frozenJson, memberName));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The manifest counts are corrupt.");
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in document.RootElement.EnumerateObject())
            {
                if (node.Name.Length is 0 or > 128 || node.Value.ValueKind != JsonValueKind.Number ||
                    !node.Value.TryGetInt32(out var count) || count < 0 || count > maximumCount ||
                    !counts.TryAdd(node.Name, count))
                    throw new InvalidDataException("The manifest counts are corrupt.");
            }
            return counts;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("The manifest counts are corrupt.", exception);
        }
    }

    private static byte[] ReadIsolatedManifestCounter(byte[] frozenJson, string memberName)
    {
        var reader = CreateManifestCounterReader(frozenJson);
        byte[]? value = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("The frozen execution plan has an invalid member.");
            var selected = reader.ValueTextEquals(memberName);
            if (!reader.Read()) throw new JsonException("The frozen execution plan member has no value.");
            var start = checked((int)reader.TokenStartIndex);
            reader.Skip();
            if (!selected) continue;
            if (value is not null) throw new InvalidDataException("The manifest counts have duplicate members.");
            value = frozenJson.AsSpan(start, checked((int)reader.BytesConsumed) - start).ToArray();
        }
        ValidateManifestCounterRootEnd(ref reader);
        return value ?? "{}"u8.ToArray();
    }

    private static Utf8JsonReader CreateManifestCounterReader(byte[] frozenJson)
    {
        if (frozenJson.Length > MaximumFrozenExecutionPlanBytes)
            throw new InvalidDataException("The frozen execution plan exceeds its byte bound.");
        var reader = new Utf8JsonReader(frozenJson, new JsonReaderOptions { MaxDepth = MaximumFrozenExecutionPlanBytes });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("The frozen execution plan is not a JSON object.");
        return reader;
    }

    private static void ValidateManifestCounterRootEnd(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            throw new JsonException("The frozen execution plan has an invalid root.");
    }
}
