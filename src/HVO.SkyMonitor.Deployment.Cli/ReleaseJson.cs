using System.Text.Json;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// How the installer parses the JSON a release gives it: release.json, and the OCI layout, index, manifest and
/// configuration an offline archive holds. Two readers of the same bytes must never see different values, so a property
/// name that appears twice in one object is refused anywhere in the document, compared after its escapes are decoded
/// (<c>"schemaVersion"</c> is <c>"schemaVersion"</c>); <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
/// would otherwise return the last of them. Every escaped string must decode to Unicode, comments and trailing commas are
/// refused, and nesting is bounded. A refusal names what was read, never text from it.
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
    /// here, where half of a surrogate pair escaped on its own (<c>\uD800</c>) is refused, and each object's property
    /// names, decoded, must be distinct.
    /// </summary>
    private static void Scan(ReadOnlySpan<byte> utf8Json, string what)
    {
        var reader = new Utf8JsonReader(utf8Json, ReaderOptions);
        // One set per open container; an array's slot is null, so a property name always belongs to the innermost object.
        var names = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    names.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    names.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    names.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!names.Peek()!.Add(reader.GetString()!))
                    {
                        throw new InstallerException(
                            $"The installer will not use {what}: an object in it names the same property twice.");
                    }
                    break;
                case JsonTokenType.String when reader.ValueIsEscaped:
                    _ = reader.GetString();
                    break;
            }
        }
    }
}
