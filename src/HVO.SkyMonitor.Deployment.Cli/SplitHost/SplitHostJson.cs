using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Deployment;

internal static class SplitHostJson
{
    internal static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Text(this JsonNode? node, string key) => node?[key]?.GetValue<string>() ?? throw Error("invalid-state");
    public static bool Flag(this JsonNode? node, string key) => node?[key]?.GetValue<bool>() ?? throw Error("invalid-state");
    public static JsonArray Array(this JsonNode? node, string key) => node?[key] as JsonArray ?? throw Error("invalid-state");
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Canonical(JsonNode? node) => Sorted(node)?.ToJsonString(Options) ?? "null";
    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sorted(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Sorted).ToArray()),
        _ => node.DeepClone()
    };
    public static JsonObject Parse(string json)
    {
        // JsonNode accepts duplicate properties until accessed. Reject them at the trust boundary.
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        CheckDuplicates(doc.RootElement);
        return JsonNode.Parse(json) as JsonObject ?? throw Error("invalid-json");
    }
    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                Require(keys.Add(property.Name), "duplicate-json-property");
                CheckDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) CheckDuplicates(value);
    }
    public static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static bool Name(string value) => Match(value, "[a-z0-9][a-z0-9-]{0,31}");
    public static bool Root(string value) => value.StartsWith('/') && value != "/" && !value.EndsWith('/') &&
        !value.Contains("//", StringComparison.Ordinal) && !value.Split('/').Any(part => part is "." or "..") && !value.Any(char.IsControl);
    public static bool Beneath(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code) { if (!condition) throw Error(code); }
    public static InstallerException Error(string code) => new("partial-prepare-cleanup: " + code);
    public static void Keys(JsonNode node, string required, string optional = "")
    {
        Require(node is JsonObject, "invalid-state");
        var actual = ((JsonObject)node).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var mandatory = required.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require(mandatory.All(actual.Contains) && actual.All(key => mandatory.Contains(key, StringComparer.Ordinal) || optional.Split(' ').Contains(key, StringComparer.Ordinal)), "invalid-state");
    }
    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}
