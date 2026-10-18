using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>The address and key a command uses, and where the key came from (the environment or the credentials file).</summary>
internal sealed record SkyCliConnection(Uri Url, string ApiKey, string KeySource)
{
    public string KeyId => SkyApiKey.IdOf(ApiKey) ?? "unknown";

    // The key is left out, so the record can be printed while debugging.
    public override string ToString() => $"SkyCliConnection {{ Url = {Url}, KeyId = {KeyId}, KeySource = {KeySource} }}";
}

/// <summary>The command line, a typed value or a missing answer was not usable; nothing was sent. Exit 2.</summary>
internal sealed class SkyCliUsageException : Exception
{
    public SkyCliUsageException()
    {
    }

    public SkyCliUsageException(string message)
        : base(message)
    {
    }

    public SkyCliUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The <c>--json</c> error kind: <c>Usage</c>, or <c>ConfirmationRequired</c> for a question no one could answer.</summary>
    public string Kind { get; init; } = "Usage";
}

/// <summary>There is no address or API key; nothing was sent. Exit 3.</summary>
internal sealed class SkyCliNotConfiguredException : Exception
{
    public SkyCliNotConfiguredException()
    {
    }

    public SkyCliNotConfiguredException(string message)
        : base(message)
    {
    }

    public SkyCliNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A command's own outcome, worded by the command, with the exit code and <c>--json</c> kind it gives.</summary>
internal sealed class SkyCliOutcomeException : Exception
{
    public SkyCliOutcomeException()
    {
    }

    public SkyCliOutcomeException(string message)
        : base(message)
    {
    }

    public SkyCliOutcomeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SkyCliOutcomeException(string message, SkyExitCode code, string kind, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Kind = kind;
    }

    public SkyExitCode Code { get; init; } = SkyExitCode.Refused;

    public string Kind { get; init; } = "Refused";
}

/// <summary>One run of a command: its global options, its output, and the connection to CameraAgent.</summary>
internal sealed class SkyCliContext
{
    private bool _warnedClearText;

    public SkyCliContext(SkyCliHost host, bool json, Uri? url, string? credentialsFile)
    {
        Host = host;
        Json = json;
        UrlOverride = url;
        CredentialsPath = string.IsNullOrWhiteSpace(credentialsFile)
            ? SkyCredentialStore.GetDefaultPath(host.GetEnvironmentVariable, host.UserProfile)
            : Path.GetFullPath(credentialsFile);
    }

    public SkyCliHost Host { get; }

    /// <summary>True with <c>--json</c>: results are one JSON document on standard output.</summary>
    public bool Json { get; }

    public Uri? UrlOverride { get; }

    public string CredentialsPath { get; }

    public TextWriter Out => Host.Out;

    public TextWriter Error => Host.Error;

    /// <summary>The address in use: <c>--url</c>, then <c>HVO_SKY_URL</c>, then the credentials file; null when none.</summary>
    /// <exception cref="SkyCredentialFileException">The environment or the file cannot be used.</exception>
    public Uri? ResolveUrl()
    {
        var environment = SkyCredentialStore.FromEnvironment(Host.GetEnvironmentVariable);
        return UrlOverride ?? environment.Url ?? SkyCredentialStore.Load(CredentialsPath)?.Url;
    }

    /// <summary>
    /// The address and key: the address from <c>--url</c>, the environment or the file; the key from the environment,
    /// then the file. Both are checked before anything is sent.
    /// </summary>
    public SkyCliConnection ResolveConnection()
    {
        var environment = SkyCredentialStore.FromEnvironment(Host.GetEnvironmentVariable);
        var file = SkyCredentialStore.Load(CredentialsPath);
        var url = UrlOverride ?? environment.Url ?? file?.Url
            ?? throw new SkyCliNotConfiguredException(
                $"No CameraAgent address. Run '{SkyCli.CommandName} login --url URL', set {SkyCredentialStore.UrlVariable}, or pass --url.");
        if (!SkyAddress.TryParse(url.OriginalString, out var address, out var error))
        {
            throw new SkyCredentialFileException($"The CameraAgent address cannot be used: {error}");
        }

        var (key, source) = environment.ApiKey is { } fromEnvironment
            ? (fromEnvironment, "environment (" + SkyCredentialStore.ApiKeyVariable + ")")
            : (file?.ApiKey, CredentialsPath);
        if (key is null)
        {
            throw new SkyCliNotConfiguredException(
                $"No API key. Run '{SkyCli.CommandName} login', or set {SkyCredentialStore.ApiKeyVariable}.");
        }

        return new SkyCliConnection(address!, key, source);
    }

    /// <summary>A client for <paramref name="connection"/>, warning once when the key would cross a network unencrypted.</summary>
    public SkyApiClient CreateClient(SkyCliConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        WarnIfClearText(connection.Url);
        return CreateClient(connection.Url, connection.ApiKey);
    }

    public SkyApiClient CreateClient(Uri url, string? apiKey) => new(new SkyConnectionOptions
    {
        BaseAddress = url,
        ApiKey = apiKey,
        Timeout = Host.RequestTimeout,
        ClientVersion = Host.ClientVersion,
        CreateHandler = Host.CreateHandler
    });

    /// <summary>Resolves the connection and creates a client for it.</summary>
    public SkyApiClient Connect() => CreateClient(ResolveConnection());

    public void WarnIfClearText(Uri url)
    {
        if (!_warnedClearText && SkyAddress.SendsKeyInClear(url))
        {
            _warnedClearText = true;
            Error.WriteLine(
                $"Warning: {url} is plain HTTP to another machine, so the API key crosses the network unencrypted. Use HTTPS, an SSH tunnel, or run {SkyCli.CommandName} on the CameraAgent host.");
        }
    }

    /// <summary>Writes <paramref name="value"/> as one indented JSON document.</summary>
    public void WriteJson<T>(T value) => Out.WriteLine(JsonSerializer.Serialize(value, SkyCliJson.Indented));

    /// <summary>Writes <paramref name="value"/> as one line of JSON (for <c>--watch</c>).</summary>
    public void WriteJsonLine<T>(T value) => Out.WriteLine(JsonSerializer.Serialize(value, SkyCliJson.Compact));

    /// <summary>
    /// Asks for a secret, never from the command line: from the terminal without echo, or as one line of standard input.
    /// </summary>
    /// <exception cref="SkyCliUsageException">No value was given.</exception>
    public string ReadSecret(string prompt)
        => Host.ReadLine(prompt, true)?.Trim() is { Length: > 0 } secret
            ? secret
            : throw new SkyCliUsageException($"No value was given for: {prompt.TrimEnd(' ', ':')}.");

    /// <summary>
    /// Asks a yes/no question at the terminal. Without one (or with <c>--json</c>) no one can answer it, so the command
    /// stops with exit 2, as the installer does, and says which option answers it.
    /// </summary>
    /// <exception cref="SkyCliUsageException">The question had no one to answer it.</exception>
    /// <returns>False when the person said no.</returns>
    public bool Confirm(string question, string option)
    {
        if (!Host.IsInteractive || Json)
        {
            throw new SkyCliUsageException($"{question} Nothing was sent: confirm it with {option}.") { Kind = "ConfirmationRequired" };
        }

        var answer = Host.ReadLine($"{question} [y/N] ", false)?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Error.WriteLine("Not confirmed; nothing was sent.");
        return false;
    }

    /// <summary>Reports <paramref name="error"/> and returns its exit code.</summary>
    public int Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (code, kind, message) = Classify(error);
        var refusal = error as SkyApiException;
        if (Json)
        {
            WriteJson(new
            {
                error = new
                {
                    exitCode = (int)code,
                    kind,
                    message,
                    status = refusal is null || refusal.StatusCode == 0 ? (int?)null : (int)refusal.StatusCode,
                    code = refusal?.Code,
                    detail = refusal?.Detail,
                    errors = refusal?.Errors.Count > 0 ? refusal.Errors : null,
                    settings = refusal?.Settings.Count > 0 ? refusal.Settings : null
                }
            });
            return (int)code;
        }

        Error.WriteLine(message);
        if (refusal is { Detail: { Length: > 0 } detail } && detail != message)
        {
            Error.WriteLine(detail);
        }

        if (refusal is not null)
        {
            foreach (var (key, problems) in refusal.Errors)
            {
                foreach (var problem in problems)
                {
                    Error.WriteLine(key.Length > 0 ? $"  {key}: {problem}" : $"  {problem}");
                }
            }

            foreach (var (key, reason) in refusal.Settings)
            {
                Error.WriteLine($"  {key}: {reason}");
            }
        }

        return (int)code;
    }

    internal static (SkyExitCode Code, string Kind, string Message) Classify(Exception error) => error switch
    {
        SkyCliUsageException usage => (SkyExitCode.Usage, usage.Kind, usage.Message),
        SkySettingValueException value => (SkyExitCode.Usage, "Usage", value.Message),
        SkyCliOutcomeException outcome => (outcome.Code, outcome.Kind, outcome.Message),
        SkyCliNotConfiguredException missing => (SkyExitCode.Refused, "NotConfigured", missing.Message),
        SkyCredentialFileException file => (SkyExitCode.Refused, "NotConfigured", file.Message),
        SkyApiException { StatusCode: HttpStatusCode.Unauthorized } => (SkyExitCode.Refused, "SignedOut",
            $"CameraAgent did not accept the API key (HTTP 401): it is wrong, was revoked, or the owner must replace a temporary password. Run '{SkyCli.CommandName} login' with a current key."),
        SkyApiException { StatusCode: HttpStatusCode.Forbidden } refusal => (SkyExitCode.Refused, "Forbidden",
            $"{refusal.Message} (HTTP 403). A Read key may only read; changes need a ReadWrite key."),
        SkyApiException { StatusCode: >= HttpStatusCode.InternalServerError } refusal => (SkyExitCode.Failed, "Failed",
            $"{refusal.Message} (HTTP {(int)refusal.StatusCode}). It may not have completed; try again."),
        SkyApiException refusal => (SkyExitCode.Refused, "Refused", refusal.Message),
        SkyProtocolException unreadable => (SkyExitCode.Failed, "Failed", unreadable.Message),
        // HttpClient reports its own timeout as a cancellation; only the interrupt's token is an interruption.
        TaskCanceledException { InnerException: TimeoutException } => (SkyExitCode.Failed, "Unreachable", "CameraAgent did not answer in time."),
        OperationCanceledException => (SkyExitCode.Interrupted, "Interrupted", "Interrupted."),
        HttpRequestException transport => (SkyExitCode.Failed, "Unreachable", $"CameraAgent could not be reached: {Describe(transport)}"),
        TimeoutException => (SkyExitCode.Failed, "Unreachable", "CameraAgent did not answer in time."),
        _ => (SkyExitCode.Failed, "Failed", $"The command failed: {error.Message}")
    };

    private static string Describe(HttpRequestException error)
        => error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "its host name did not resolve.",
            HttpRequestError.ConnectionError => "the connection was refused or dropped.",
            HttpRequestError.SecureConnectionError => "the TLS connection could not be made.",
            _ => error.InnerException?.Message ?? error.Message
        };
}

/// <summary>JSON that <c>--json</c> writes: camelCase, enums as names.</summary>
internal static class SkyCliJson
{
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
