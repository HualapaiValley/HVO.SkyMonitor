using System.Text.Json;
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
        try
        {
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
        var read = new List<UpgradeNote>();
        var index = 0;
        foreach (var note in notes.EnumerateArray())
        {
            index++;
            if (note.ValueKind != JsonValueKind.Object || Text(note, "version") is not { } version ||
                !ProductVersion.IsValid(version) || Text(note, "text") is not { } text)
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
            if (read.Any(existing => ProductVersion.Compare(existing.Version, version) == 0))
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
