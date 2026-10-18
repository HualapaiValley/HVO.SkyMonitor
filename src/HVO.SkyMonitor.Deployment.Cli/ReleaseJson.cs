using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// How the installer parses the JSON a release gives it: release.json, and the OCI layout, index, manifest and
/// configuration an offline archive holds. Two readers of the same bytes must never see different values, so a property
/// name that appears twice in one object is refused anywhere in the document, compared after its escapes are decoded
/// (<c>"schemaVersion"</c> is <c>"schemaVersion"</c>); <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
/// would otherwise return the last of them. Docker and containerd decode this JSON with Go's <c>encoding/json</c>, which
/// matches a property to a field regardless of case, Unicode folds included (<c>ſ</c> is <c>s</c>, the Kelvin sign is
/// <c>k</c>), so a second spelling of a field can replace the value this installer read. Every property name must
/// therefore be ASCII, and two names in one object that differ only in ASCII case are refused. Every escaped string must
/// decode to Unicode, comments and trailing commas are refused, and nesting is bounded. A refusal names what was read,
/// never text from it.
/// </summary>
internal static class ReleaseJson
{
    /// <summary>Deeper than any release metadata the installer reads.</summary>
    private const int MaximumDepth = 32;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        // The scan below refuses duplicates first, with a message of its own; the parser refusing them too is the backstop.
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumDepth
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumDepth
    };

    /// <summary>
    /// Parses <paramref name="utf8Json"/>, which <paramref name="what"/> names in a message, such as <c>release.json</c>
    /// or <c>the offline archive's image configuration</c>. Throws <see cref="InstallerException"/> when it is not valid
    /// JSON, when a string in it is not Unicode, or when an object in it names a property twice.
    /// </summary>
    public static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json, string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        try
        {
            Scan(utf8Json.Span, what);
            return JsonDocument.Parse(utf8Json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InstallerException($"The installer cannot read {what}: it is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InstallerException($"The installer will not use {what}: it contains a string that is not valid Unicode.", exception);
        }
    }

    /// <summary>
    /// Reads every token once. The JSON reader decodes only the strings it is asked for, so each escaped string is decoded
    /// here, where half of a surrogate pair escaped on its own (<c>\uD800</c>) is refused. Each object's property names,
    /// decoded, must be ASCII and distinct, ignoring ASCII case.
    /// </summary>
    private static void Scan(ReadOnlySpan<byte> utf8Json, string what)
    {
        var reader = new Utf8JsonReader(utf8Json, ReaderOptions);
        // One set per open container; an array's slot is null, so a property name always belongs to the innermost object.
        // The set compares ignoring case; the names in it are ASCII, so that is exactly Go's folding of them.
        var names = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    names.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    break;
                case JsonTokenType.StartArray:
                    names.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    names.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString()!;
                    if (!System.Text.Ascii.IsValid(name))
                    {
                        throw new InstallerException(
                            $"The installer will not use {what}: an object in it names a property with a character outside " +
                            "ASCII, which Docker's JSON decoder can read as another property's name.");
                    }
                    var seen = names.Peek()!;
                    if (seen.TryGetValue(name, out var earlier))
                    {
                        throw new InstallerException(string.Equals(earlier, name, StringComparison.Ordinal)
                            ? $"The installer will not use {what}: an object in it names the same property twice."
                            : $"The installer will not use {what}: an object in it names one property in two spellings, " +
                              "which Docker's JSON decoder reads as the same name.");
                    }
                    seen.Add(name);
                    break;
                case JsonTokenType.String when reader.ValueIsEscaped:
                    _ = reader.GetString();
                    break;
            }
        }
    }
}
