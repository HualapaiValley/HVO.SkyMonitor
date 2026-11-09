using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

internal static class EnvironmentalPayloadJson
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> payload, int depth = 12)
    {
        var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = depth });
        if (document.RootElement.ValueKind == JsonValueKind.Object && Unique(document.RootElement)) return document;
        document.Dispose();
        throw new JsonException("Ambiguous environmental payload.");
    }
    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }
}
