using System.Text.Json;
using System.Text.Unicode;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The part of a release's <c>release.json</c> (install contract section 4, RoofController's schema 1) that an upgrade
/// reads before it changes anything: the release's version and the cumulative upgrade notes of every release up to it.
/// Every other member, such as the images, assets and state contracts, is left to the acquisition that selects the
/// candidate from the same document (#1309), so a complete release.json reads here unchanged.
/// </summary>
internal sealed record ReleaseManifest(string Version, IReadOnlyList<UpgradeNote> UpgradeNotes)
{
    public const string FileName = "release.json";

    /// <summary>The release.json schema this installer reads.</summary>
    public const int Schema = 1;

    /// <summary>
    /// The largest release.json read. A release's own is a few kilobytes; the bound keeps a hostile or corrupt source
    /// from being parsed into memory whole.
    /// </summary>
    public const int MaximumLength = 1024 * 1024;

    /// <summary>
    /// The most upgrade notes read. A release.json carries the notes of every release that needed any, cumulatively, so
    /// a thousand outlasts a release a week for nineteen years. The bound keeps what a hostile or corrupt source packs
    /// into the size limit from costing more to read, order and print than a real release could.
    /// </summary>
    public const int MaximumUpgradeNotes = 1000;

    /// <summary>
    /// Reads a release.json. Throws <see cref="InstallerException"/> naming what is wrong when it is not schema 1 of a
    /// release version, or when its upgrade notes are not each a release's version and its text. A note's lines end in
    /// LF, and its text holds no other control character but a tab.
    /// </summary>
    public static ReleaseManifest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumLength)
        {
            throw Invalid($"it is larger than {MaximumLength} bytes");
        }
        // The JSON reader decodes only the strings it is asked for, so text that is not Unicode is refused here, wherever
        // it is in the document, rather than when, or whether, a member holding it is read: bytes that are not UTF-8,
        // and half of a surrogate pair escaped on its own (\uD800), which the tokenizer passes.
        if (!Utf8.IsValid(utf8Json.Span))
        {
            throw Invalid("it is not UTF-8 text");
        }
        try
        {
            DecodeEscapedStrings(utf8Json.Span);
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("it is not a JSON object");
            }
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number)
            {
                throw Invalid("it has no schemaVersion");
            }
            if (!schema.TryGetInt32(out var number) || number != Schema)
            {
                throw Invalid($"it is schema {schema.GetRawText()}, and this installer reads schema {Schema}");
            }
            var version = Text(root, "version");
            if (!ProductVersion.IsValid(version))
            {
                throw Invalid("it names no release version");
            }
            return new ReleaseManifest(version!, ReadUpgradeNotes(root));
        }
        catch (JsonException exception)
        {
            throw new InstallerException($"The installer cannot read {FileName}: it is not valid JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InstallerException(
                $"The installer will not use {FileName}: it contains a string that is not valid Unicode.", exception);
        }
    }

    // Decodes every escaped member name and string once, which throws InvalidOperationException for one that is not
    // Unicode text; one with no escapes is the UTF-8 already checked.
    private static void DecodeEscapedStrings(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String && reader.ValueIsEscaped)
            {
                _ = reader.GetString();
            }
        }
    }

    private static UpgradeNote[] ReadUpgradeNotes(JsonElement root)
    {
        if (!root.TryGetProperty("upgradeNotes", out var notes) || notes.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (notes.ValueKind != JsonValueKind.Array)
        {
            throw Invalid("its upgrade notes are not a list");
        }
        if (notes.GetArrayLength() > MaximumUpgradeNotes)
        {
            throw Invalid($"it has more than {MaximumUpgradeNotes} upgrade notes");
        }
        var read = new List<UpgradeNote>();
        // The releases read so far, each its version without build metadata, so a repeat is found as its note is read.
        var releases = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var note in notes.EnumerateArray())
        {
            index++;
            if (note.ValueKind != JsonValueKind.Object || Text(note, "version") is not { } version ||
                !ProductVersion.TryGetPrecedence(version, out var precedence) || Text(note, "text") is not { } text)
            {
                throw Invalid($"upgrade note {index} is not a release's version and its text");
            }
            // The notes are printed to the operator's terminal, so text that could drive it is refused rather than shown.
            // A CRLF line ending is read as LF. A carriage return anywhere else would return to the start of the line and
            // let what follows overwrite a warning, so it is refused like every other control character.
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (lines.Any(static character => char.IsControl(character) && character is not ('\n' or '\t')))
            {
                throw Invalid($"upgrade note {index} ({version}) contains control characters");
            }
            if (!releases.Add(precedence))
            {
                throw Invalid($"it has more than one upgrade note for {version}");
            }
            read.Add(new UpgradeNote(version, lines));
        }
        return [.. read];
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
           value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static InstallerException Invalid(string reason) => new($"The installer will not use {FileName}: {reason}.");
}
