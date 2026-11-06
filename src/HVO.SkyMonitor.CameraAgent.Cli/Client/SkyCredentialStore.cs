using System.Net;
using System.Net.NetworkInformation;
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
/// A file that other users can read or write, own, or replace by changing its folder is refused rather than used, because
/// it holds a key; so is one that exists but cannot be reached or read. Only a file that does not exist is "absent".
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

    private const int PermissionDenied = 13;

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

    /// <summary>Reads the file as the user this process runs as; null when there is none.</summary>
    /// <exception cref="SkyCredentialFileException">The file, or its folder, exists but cannot be used.</exception>
    public static SkyStoredCredentials? Load(string path) => Load(path, SkyFileSystem.CurrentUserId);

    /// <summary>
    /// Reads the file, or returns null when there is none: only when it, or a folder on its path, does not exist
    /// (<c>ENOENT</c>). A file that exists but cannot be used is refused, never taken for an absent one: one that cannot
    /// be reached or read, that belongs to another user than <paramref name="owner"/>, that other users can read or
    /// write, or whose folder they can change, and one that is not a valid credentials file.
    /// </summary>
    /// <exception cref="SkyCredentialFileException">The file, or its folder, exists but cannot be used.</exception>
    public static SkyStoredCredentials? Load(string path, uint owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var file = SkyFileSystem.Inspect(fullPath);
        if (file.Missing)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        if (!file.Exists)
        {
            throw Unusable(fullPath, file.Error == PermissionDenied
                ? $"it cannot be reached ({file.Reason}): a folder on its path cannot be searched. Run 'chmod 700 {directory}', then try again."
                : $"it cannot be reached ({file.Reason}).");
        }

        if (!file.IsRegularFile)
        {
            throw Unusable(fullPath, "it is not a file.");
        }

        if (file.Owner is { } fileOwner && fileOwner != owner)
        {
            throw Unusable(fullPath, $"it belongs to another user (user ID {fileOwner}). Only your own credentials file is used: run 'hvo-sky login' to save one.");
        }

        CheckDirectory(directory, SkyFileSystem.Inspect(directory), owner);
        if ((file.Mode & OthersMask) != 0)
        {
            throw new SkyCredentialFileException(
                $"{fullPath} can be read or changed by other users. Run 'chmod 600 {fullPath}', then try again.");
        }

        SkyStoredCredentials? stored;
        try
        {
            using var stream = File.OpenRead(fullPath);
            if (stream.Length > MaximumFileBytes)
            {
                throw new SkyCredentialFileException($"{fullPath} is not a valid credentials file: it is too large.");
            }

            stored = JsonSerializer.Deserialize<SkyStoredCredentials>(stream, FileJson);
        }
        catch (JsonException ex)
        {
            throw new SkyCredentialFileException($"{fullPath} is not a valid credentials file.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Unusable(fullPath, $"it cannot be read ({ex.Message}). Run 'chmod 600 {fullPath}', then try again.", ex);
        }
        catch (IOException ex)
        {
            throw Unusable(fullPath, $"it cannot be read ({ex.Message}).", ex);
        }

        if (stored is null)
        {
            throw new SkyCredentialFileException($"{fullPath} is not a valid credentials file.");
        }

        if (stored.SchemaVersion != SchemaVersion)
        {
            throw new SkyCredentialFileException(
                $"{fullPath} has credentials schema {stored.SchemaVersion}, which this hvo-sky does not read. Run 'hvo-sky login' to save them again.");
        }

        if (stored.Url is not null && !SkyAddress.TryParse(stored.Url.OriginalString, out _, out var error))
        {
            throw new SkyCredentialFileException($"The address in {fullPath} cannot be used: {error}");
        }

        if (stored.ApiKey is not null && !SkyApiKey.IsWellFormed(stored.ApiKey))
        {
            throw new SkyCredentialFileException(
                $"The API key in {fullPath} is not a CameraAgent API key. Run 'hvo-sky login' to save a key again.");
        }

        return stored;
    }

    /// <summary>Writes the file as the user this process runs as.</summary>
    /// <exception cref="SkyCredentialFileException">The folder cannot be trusted, or the file cannot be written.</exception>
    public static void Save(string path, SkyStoredCredentials credentials) => Save(path, credentials, SkyFileSystem.CurrentUserId);

    /// <summary>
    /// Writes the file atomically with mode <c>0600</c>, creating its folder with mode <c>0700</c> when missing.
    /// </summary>
    /// <exception cref="SkyCredentialFileException">
    /// The folder belongs to another user than <paramref name="owner"/> or other users can change it, or the file cannot
    /// be written.
    /// </exception>
    public static void Save(string path, SkyStoredCredentials credentials, uint owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(credentials);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var folder = SkyFileSystem.Inspect(directory);
            if (folder.Missing)
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(directory);
                }
                else
                {
                    Directory.CreateDirectory(directory, OwnerDirectoryMode);
                }
            }
            else if (!folder.Exists)
            {
                throw Unusable(fullPath, $"its folder {directory} cannot be reached ({folder.Reason}).");
            }
            else
            {
                CheckDirectory(directory, folder, owner);
            }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unusable(fullPath, $"it cannot be written ({ex.Message}).", ex);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing was written there, or the folder cannot be reached; there is nothing more to remove.
            }
        }
    }

    private static SkyCredentialFileException Unusable(string path, string problem, Exception? innerException = null)
        => innerException is null
            ? new($"The credentials file {path} cannot be used: {problem}")
            : new($"The credentials file {path} cannot be used: {problem}", innerException);

    // Another user who owns or can change the folder can replace the file, whatever the file's own mode.
    private static void CheckDirectory(string directory, SkyPathStatus folder, uint owner)
    {
        if (!folder.Exists)
        {
            throw new SkyCredentialFileException($"The credentials folder {directory} cannot be used: it cannot be reached ({folder.Reason}).");
        }

        if (!folder.IsDirectory)
        {
            throw new SkyCredentialFileException($"The credentials folder {directory} cannot be used: it is not a folder.");
        }

        if (folder.Owner is { } folderOwner && folderOwner != owner && folderOwner != 0)
        {
            throw new SkyCredentialFileException(
                $"The credentials folder {directory} cannot be used: it belongs to another user (user ID {folderOwner}), who could replace the credentials file.");
        }

        if ((folder.Mode & OthersWriteMask) != 0)
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
    /// other than this machine. This machine is its loopback, which the connection reaches directly whatever the proxy
    /// (<see cref="SkyApiClient.CreateHandler"/>, #1404), and every address bound to one of its interfaces
    /// (<paramref name="localAddresses"/>): a request to the CameraAgent host's own LAN address, made on that host, never
    /// leaves it (#1384), unless it goes through a proxy. Such an address is this machine only when
    /// <paramref name="proxy"/>, the proxy the request is sent through, bypasses it. A name is not resolved, so it is never
    /// taken for this machine.
    /// </summary>
    public static bool SendsKeyInClear(Uri address, Func<IReadOnlyCollection<IPAddress>> localAddresses, Func<IWebProxy?> proxy)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(localAddresses);
        ArgumentNullException.ThrowIfNull(proxy);
        if (address.Scheme != Uri.UriSchemeHttp || address.IsLoopback)
        {
            return false;
        }

        return address.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || !IPAddress.TryParse(address.DnsSafeHost, out var host)
            || !localAddresses().Any(local => SameAddress(local, host))
            || proxy() is { } through && !through.IsBypassed(address);
    }

    /// <summary>The addresses bound to this machine's interfaces, or none when they cannot be read.</summary>
    public static IReadOnlyCollection<IPAddress> InterfaceAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(static adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(static unicast => unicast.Address)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>The same address, whatever its IPv6 scope or IPv4-mapped form.</summary>
    private static bool SameAddress(IPAddress first, IPAddress second)
    {
        var a = first.IsIPv4MappedToIPv6 ? first.MapToIPv4() : first;
        var b = second.IsIPv4MappedToIPv6 ? second.MapToIPv4() : second;
        return a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());
    }
}
