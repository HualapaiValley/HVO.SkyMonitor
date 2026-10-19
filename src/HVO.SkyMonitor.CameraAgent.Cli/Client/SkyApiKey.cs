using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>
/// A CameraAgent API key's shape (docs/contracts/cameraagent-operator-settings-v1.md): <c>hvo_sky_</c>, a 16-character
/// lowercase hex identifier, <c>_</c> and 43 base64url characters. The identifier is public and safe to show; nothing
/// here ever returns the rest of the key.
/// </summary>
internal static partial class SkyApiKey
{
    public const string Prefix = "hvo_sky_";

    public const int IdLength = 16;

    public const int Length = 68;

    /// <summary>True when <paramref name="value"/> is a whole key in the exact format CameraAgent issues.</summary>
    public static bool IsWellFormed(string? value) => value is { Length: Length } && WholeKey().IsMatch(value);

    /// <summary>The key's public identifier, or null when the value is not a well-formed key.</summary>
    public static string? IdOf(string? value) => IsWellFormed(value) ? value![Prefix.Length..(Prefix.Length + IdLength)] : null;

    /// <summary>
    /// True when <paramref name="text"/> holds what looks like a key, anywhere in it. The command line refuses such an
    /// argument without echoing it, since an argument is visible to other users and kept in shell history.
    /// </summary>
    public static bool AppearsIn(string? text) => text is not null && EmbeddedKey().IsMatch(text);

    [GeneratedRegex("\\Ahvo_sky_[0-9a-f]{16}_[A-Za-z0-9_-]{43}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex WholeKey();

    [GeneratedRegex("hvo_sky_[0-9a-f]{16}_", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedKey();
}
