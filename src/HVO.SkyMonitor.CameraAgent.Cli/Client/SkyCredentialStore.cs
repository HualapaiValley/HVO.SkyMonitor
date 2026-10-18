using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>What <c>hvo-sky</c> keeps between runs. The API key is saved only by <see cref="SkyCredentialStore"/>.</summary>
internal sealed record SkyStoredCredentials
{
    /// <summary>The credentials file's own version, so a later client can refuse a shape it does not understand.</summary>
    public int SchemaVersion { get; init; } = SkyCredentialStore.SchemaVersion;

    /// <summary>CameraAgent's address, such as <c>http://127.0.0.1:5130/</c>.</summary>
    public Uri? Url { get; init; }

    /// <summary>An owner API key. A secret: it is never printed, logged or written to JSON output.</summary>
    public string? ApiKey { get; init; }

    // The key is left out, so the record can be printed while debugging.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"SchemaVersion = {SchemaVersion}, Url = {Url}, ApiKey = {(ApiKey is null ? "(none)" : $"(key {SkyApiKey.IdOf(ApiKey) ?? "malformed"})")}");
        return true;
    }
}

/// <summary>The credentials file or a credential variable cannot be used. Nothing was sent.</summary>
internal sealed class SkyCredentialFileException : Exception
{
    public SkyCredentialFileException()
    {
    }

    public SkyCredentialFileException(string message)
        : base(message)
    {
    }

    public SkyCredentialFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Credentials for <c>hvo-sky</c> (install contract section 8): from the environment (<c>HVO_SKY_URL</c>,
/// <c>HVO_SKY_API_KEY</c>), or from a JSON file that only its owner may read (<c>0600</c>, in a <c>0700</c> directory).
/// A file that other users can read or write, or one in a directory they can change, is refused rather than used,
/// because it holds a key.
/// </summary>
internal static class SkyCredentialStore
{
    public const int SchemaVersion = 1;

    public const string UrlVariable = "HVO_SKY_URL";

    public const string ApiKeyVariable = "HVO_SKY_API_KEY";

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OthersWriteMask = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    private const UnixFileMode OthersMask = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>The largest credentials file read; a real one is a few hundred bytes.</summary>
    private const int MaximumFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary><c>$XDG_CONFIG_HOME/hvo-sky/credentials.json</c>, or <c>~/.config/hvo-sky/credentials.json</c>.</summary>
    public static string GetDefaultPath(Func<string, string?> getEnvironmentVariable, string? userProfile = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var configHome = getEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathRooted(configHome))
        {
            configHome = Path.Combine(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configHome, "hvo-sky", "credentials.json");
    }

    /// <summary>The address and key the environment sets, each null when its variable is not set.</summary>
    /// <exception cref="SkyCredentialFileException">A variable is set to a value that cannot be used.</exception>
    public static (Uri? Url, string? ApiKey) FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var url = getEnvironmentVariable(UrlVariable);
        var key = getEnvironmentVariable(ApiKeyVariable);
        Uri? address = null;
        if (!string.IsNullOrWhiteSpace(url) && !SkyAddress.TryParse(url, out address, out var error))
        {
            throw new SkyCredentialFileException($"{UrlVariable}: {error}");
        }

        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(key))
        {
            apiKey = key.Trim();
            if (!SkyApiKey.IsWellFormed(apiKey))
            {
                // Never echoed: a malformed key may still be most of a real one.
                throw new SkyCredentialFileException(
                    $"{ApiKeyVariable} is not a CameraAgent API key: one starts with {SkyApiKey.Prefix} and is {SkyApiKey.Length} characters long.");
            }
        }

        return (address, apiKey);
    }

    /// <summary>Reads the file, or returns null when there is none.</summary>
    /// <exception cref="SkyCredentialFileException">
    /// Other users can read or write the file, or change its directory; or it is not a valid credentials file.
    /// </exception>
    public static SkyStoredCredentials? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            CheckDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if ((File.GetUnixFileMode(path) & OthersMask) != 0)
            {
                throw new SkyCredentialFileException(
                    $"{path} can be read or changed by other users. Run 'chmod 600 {path}', then try again.");
            }
        }

        SkyStoredCredentials? stored;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaximumFileBytes)
            {
                throw new SkyCredentialFileException($"{path} is not a valid credentials file: it is too large.");
            }

            stored = JsonSerializer.Deserialize<SkyStoredCredentials>(stream, FileJson);
        }
        catch (JsonException ex)
        {
            throw new SkyCredentialFileException($"{path} is not a valid credentials file.", ex);
        }

        if (stored is null)
        {
            throw new SkyCredentialFileException($"{path} is not a valid credentials file.");
        }

        if (stored.SchemaVersion != SchemaVersion)
        {
            throw new SkyCredentialFileException(
                $"{path} has credentials schema {stored.SchemaVersion}, which this hvo-sky does not read. Run 'hvo-sky login' to save them again.");
        }

        if (stored.Url is not null && !SkyAddress.TryParse(stored.Url.OriginalString, out _, out var error))
        {
            throw new SkyCredentialFileException($"The address in {path} cannot be used: {error}");
        }

        if (stored.ApiKey is not null && !SkyApiKey.IsWellFormed(stored.ApiKey))
        {
            throw new SkyCredentialFileException(
                $"The API key in {path} is not a CameraAgent API key. Run 'hvo-sky login' to save a key again.");
        }

        return stored;
    }

    /// <summary>
    /// Writes the file atomically with mode <c>0600</c>, creating its directory with mode <c>0700</c> when missing.
    /// </summary>
    /// <exception cref="SkyCredentialFileException">Other users can change the directory.</exception>
    public static void Save(string path, SkyStoredCredentials credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(credentials);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (Directory.Exists(directory))
        {
            CheckDirectory(directory);
        }
        else if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, OwnerDirectoryMode);
        }

        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerFileMode;
            }

            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, credentials with { SchemaVersion = SchemaVersion }, FileJson);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Another user who can change the directory can replace the file, whatever the file's own mode.
    private static void CheckDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(directory) & OthersWriteMask) != 0)
        {
            throw new SkyCredentialFileException(
                $"{directory} can be changed by other users, who could replace the credentials file. Run 'chmod 700 {directory}', then try again.");
        }
    }
}

/// <summary>CameraAgent's address: an absolute http or https URL, kept with a trailing slash.</summary>
internal static class SkyAddress
{
    public static bool TryParse(string? text, out Uri? address, out string? error)
    {
        address = null;
        error = null;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host))
        {
            error = "it is not a CameraAgent address. Give an http or https address, such as http://127.0.0.1:5130/.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "a CameraAgent address may not include a user name, a query or a fragment.";
            return false;
        }

        // Canonical, with a trailing slash, so relative routes resolve under it and it reads the same wherever it came from.
        var absolute = parsed.AbsoluteUri;
        address = new Uri(absolute.EndsWith('/') ? absolute : absolute + "/");
        return true;
    }

    /// <summary>
    /// True when requests to <paramref name="address"/> carry the key across a network unencrypted: plain HTTP to a host
    /// other than this machine's loopback.
    /// </summary>
    public static bool SendsKeyInClear(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Scheme == Uri.UriSchemeHttp && !address.IsLoopback;
    }
}
