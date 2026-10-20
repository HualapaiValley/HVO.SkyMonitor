using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>How a client reaches one CameraAgent.</summary>
internal sealed record SkyConnectionOptions
{
    /// <summary>CameraAgent's address, with a trailing slash.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>The owner API key sent as <c>Authorization: Bearer</c>; null for anonymous reads such as <c>/alive</c>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The client's own version, sent as its user agent.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>For tests: the handler of every request.</summary>
    public Func<HttpMessageHandler>? CreateHandler { get; init; }
}

/// <summary>
/// CameraAgent's operations API (<c>/api/v1/operations</c>) over HTTP. The key travels only in the
/// <c>Authorization</c> header, never in a URL, and redirects are not followed, so it is never sent anywhere but the
/// address given. A refusal or failure becomes <see cref="SkyApiException"/> (CameraAgent answered),
/// <see cref="SkyProtocolException"/> (its answer could not be read), or the transport's own exception (it did not
/// answer). The client has no timeout of its own: the caller's token, the command's deadline, bounds each request and
/// the reading of its answer's body alike.
/// </summary>
internal sealed class SkyApiClient : IDisposable
{
    public const string OperationsRoute = "api/v1/operations/";

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>The largest answer read; the operations API's largest, the schedule, is far smaller.</summary>
    private const int MaximumResponseBytes = 16 * 1024 * 1024;

    private readonly HttpClient _http;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The HttpClient owns the handler (disposeHandler: true) and is disposed with this client.")]
    public SkyApiClient(SkyConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _http = new HttpClient(options.CreateHandler?.Invoke() ?? CreateHandler(), disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hvo-sky", UserAgentVersion(options.ClientVersion)));
        if (options.ApiKey is { } key)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    public SkyConnectionOptions Options { get; }

    // Redirects are not followed, so the key is never sent anywhere but the address given, and no cookie is kept.
    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    };

    public static JsonSerializerOptions Json { get; } = CreateJson();

    /// <summary>Reads <paramref name="route"/> as a JSON document.</summary>
    public Task<JsonElement> GetAsync(string route, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, route, body: null, idempotent: false, accepted: null, cancellationToken);

    /// <summary>Reads <paramref name="route"/> as <typeparamref name="T"/>.</summary>
    public async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
        => Read<T>(await GetAsync(route, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Sends <paramref name="body"/> as JSON and reads the JSON answer. A change carries a fresh
    /// <c>Idempotency-Key</c>, which CameraAgent records with location and schedule commands.
    /// </summary>
    public Task<JsonElement> SendAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
        => SendAsync(method, route, body, idempotent: true, accepted: null, cancellationToken);

    /// <summary>
    /// Sends a change, as <see cref="SendAsync(HttpMethod, string, object?, CancellationToken)"/> does, and calls
    /// <paramref name="accepted"/> as soon as CameraAgent's success status line arrives, before its body is read: from
    /// then on CameraAgent has taken the change, whatever becomes of the body.
    /// </summary>
    public Task<JsonElement> SendAsync(HttpMethod method, string route, object? body, Action accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return SendAsync(method, route, body, idempotent: true, accepted, cancellationToken);
    }

    public static T Read<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(Json) ?? throw new SkyProtocolException("CameraAgent's answer was empty.");
        }
        catch (JsonException ex)
        {
            throw new SkyProtocolException("CameraAgent's answer could not be read.", ex);
        }
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string route,
        object? body,
        bool idempotent,
        Action? accepted,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(route, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        if (idempotent)
        {
            request.Headers.Add(IdempotencyKeyHeader, Guid.NewGuid().ToString("N"));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            accepted?.Invoke();
        }

        var text = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Refusal(response, text);
        }

        try
        {
            using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new SkyProtocolException("CameraAgent's answer could not be read.", ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new SkyProtocolException("CameraAgent's answer is larger than this client reads.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new SkyProtocolException("CameraAgent's answer is larger than this client reads.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static SkyApiException Refusal(HttpResponseMessage response, string text)
    {
        var status = response.StatusCode;
        if ((int)status is >= 300 and < 400)
        {
            // Not followed: the key would go with it. The address is the operator's to correct.
            return new SkyApiException(status,
                $"CameraAgent answered with a redirect (HTTP {(int)status}) rather than the API. Give its address as it serves the API, with --url or 'hvo-sky login --url'.");
        }

        JsonElement? problem = null;
        string? title = null;
        string? detail = null;
        string? code = null;
        var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    problem = root.Clone();
                    title = String(root, "title");
                    detail = String(root, "detail");
                    code = String(root, "code");
                    if (root.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in list.EnumerateObject())
                        {
                            errors[entry.Name] = entry.Value.ValueKind == JsonValueKind.Array
                                ? entry.Value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String)
                                    .Select(static item => item.GetString()!).ToArray()
                                : [];
                        }
                    }

                    if (root.TryGetProperty("settings", out var reasons) && reasons.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var entry in reasons.EnumerateObject().Where(static entry => entry.Value.ValueKind == JsonValueKind.String))
                        {
                            settings[entry.Name] = entry.Value.GetString()!;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not a problem document; the status says enough.
            }
        }

        return new SkyApiException(status, title ?? DescribeStatus(status), code, detail)
        {
            Errors = errors,
            Settings = settings,
            Problem = problem
        };
    }

    private static string DescribeStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "CameraAgent did not accept the API key.",
        HttpStatusCode.Forbidden => "The API key may not do this.",
        HttpStatusCode.NotFound => "CameraAgent does not have what was asked for.",
        _ => $"CameraAgent answered HTTP {(int)status}."
    };

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // A product token may not hold '+'; the commit after it is not needed to identify the client.
    private static string UserAgentVersion(string? version)
    {
        var text = version?.Split('+')[0];
        return string.IsNullOrWhiteSpace(text) || !text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            ? "unknown"
            : text;
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public void Dispose() => _http.Dispose();
}
