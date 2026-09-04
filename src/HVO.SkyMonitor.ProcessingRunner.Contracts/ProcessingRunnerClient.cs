using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HVO.SkyMonitor.ProcessingRunner.Contracts;

public sealed class ProcessingRunnerClientOptions
{
    public required string RunnerId { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } =
        [ProcessingRunnerProtocol.Scope, ProcessingRunnerProtocol.ArtifactReadScope];

    public string TokenPath { get; init; } = "connect/token";

    public TimeSpan TokenRefreshSkew { get; init; } = TimeSpan.FromSeconds(60);

    public long MaxTransferBytes { get; init; } = ProcessingRunnerProtocol.MaximumTransferBytes;
}

public sealed class ProcessingRunnerClientException : Exception
{
    public ProcessingRunnerClientException()
    {
    }

    public ProcessingRunnerClientException(string message) : base(message)
    {
    }

    public ProcessingRunnerClientException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ProcessingRunnerClientException(HttpStatusCode statusCode, ProcessingRunnerProblem? problem, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Problem = problem;
    }

    public HttpStatusCode StatusCode { get; }

    public ProcessingRunnerProblem? Problem { get; }

    public string? ReasonCode => Problem?.ReasonCode;

    public bool IsLeaseStale => StatusCode == HttpStatusCode.Conflict;

    public bool IsLeaseCanceled => StatusCode == HttpStatusCode.Gone;

    public bool IsUnavailable => StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;

    public bool IsAuthorizationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>
/// The runner-side HTTP client for <c>processing-runner-v1</c>. It authenticates with client credentials, sends the
/// runner identity on every call, and fetches job inputs only with the job's lease credential.
/// </summary>
public sealed class ProcessingRunnerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ProcessingRunnerClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresUtc;

    public ProcessingRunnerClient(HttpClient http, ProcessingRunnerClientOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (http.BaseAddress is null)
        {
            throw new ArgumentException("The HTTP client requires a LogicHost base address.", nameof(http));
        }
        if (!ProcessingRunnerProtocol.IsValidRunnerId(options.RunnerId))
        {
            throw new ArgumentException("The runner id is invalid.", nameof(options));
        }
        _http = http;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RunnerId => _options.RunnerId;

    public async Task<ProcessingRunnerRegistrationResponse> RegisterAsync(
        ProcessingRunnerRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = await CreateRequestAsync(HttpMethod.Put, RunnerPath(), cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(request, options: ProcessingRunnerProtocol.SerializerOptions);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ProcessingRunnerRegistrationResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessingRunnerHeartbeatResponse> HeartbeatAsync(
        ProcessingRunnerHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = await CreateRequestAsync(HttpMethod.Post, RunnerPath("heartbeat"), cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(request, options: ProcessingRunnerProtocol.SerializerOptions);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ProcessingRunnerHeartbeatResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Claims the next eligible job, or returns <see langword="null"/> when nothing is claimable.</summary>
    public async Task<ProcessingRunnerClaim?> ClaimAsync(
        ProcessingRunnerClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = await CreateRequestAsync(HttpMethod.Post, RunnerPath("claims"), cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(request, options: ProcessingRunnerProtocol.SerializerOptions);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        return await ReadAsync<ProcessingRunnerClaim>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProcessingRunnerLeaseRenewalResponse> RenewAsync(
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        using var message = await CreateRequestAsync(
            HttpMethod.Post, RunnerPath($"jobs/{jobId:D}/lease"), cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(
            new ProcessingRunnerLeaseRenewalRequest(leaseToken), options: ProcessingRunnerProtocol.SerializerOptions);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ProcessingRunnerLeaseRenewalResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches one claimed input under the job lease and verifies its length and SHA-256 before returning it.
    /// </summary>
    public async Task<byte[]> DownloadInputAsync(
        ProcessingRunnerClaim claim,
        ProcessingRunnerArtifactMetadata input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(input);
        if (input.PayloadLength < 0 || input.PayloadLength > _options.MaxTransferBytes || input.PayloadLength > int.MaxValue)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.TransferTooLarge,
                $"Input {input.ArtifactId:D} exceeds the runner transfer limit.");
        }
        using var message = await CreateRequestAsync(
            HttpMethod.Get, new Uri(input.ContentPath.TrimStart('/'), UriKind.Relative), cancellationToken).ConfigureAwait(false);
        message.Headers.TryAddWithoutValidation(ProcessingRunnerProtocol.JobIdHeader, claim.JobId.ToString("D"));
        message.Headers.TryAddWithoutValidation(ProcessingRunnerProtocol.LeaseTokenHeader, claim.LeaseToken.ToString("D"));
        using var response = await _http.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
        var buffer = new byte[(int)input.PayloadLength];
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var read = await ReadExactlyAsync(stream, buffer, input, cancellationToken).ConfigureAwait(false);
            ProcessingRunnerProjection.VerifyPayload(
                buffer.AsSpan(0, read), input.PayloadLength, input.PayloadSha256, $"input {input.ArtifactId:D}");
        }
        return buffer;
    }

    private static async Task<int> ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        ProcessingRunnerArtifactMetadata input,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        if (read == buffer.Length)
        {
            var probe = new byte[1];
            if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new ProcessingRunnerProtocolException(
                    ProcessingRunnerReasonCodes.PayloadLengthMismatch,
                    $"Input {input.ArtifactId:D} is longer than its declared length.");
            }
        }
        return read;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "MultipartFormDataContent owns and disposes every part added to it.")]
    public async Task<ProcessingRunnerCompletionResponse> CompleteAsync(
        Guid jobId,
        ProcessingRunnerCompletionRequest request,
        IReadOnlyList<ReadOnlyMemory<byte>> payloads,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloads);
        long total = 0;
        foreach (var payload in payloads)
        {
            total = checked(total + payload.Length);
        }
        if (total > _options.MaxTransferBytes)
        {
            throw new ProcessingRunnerProtocolException(
                ProcessingRunnerReasonCodes.TransferTooLarge, "The completion payloads exceed the runner transfer limit.");
        }
        using var message = await CreateRequestAsync(
            HttpMethod.Post, RunnerPath($"jobs/{jobId:D}/completion"), cancellationToken).ConfigureAwait(false);
        using var content = new MultipartFormDataContent();
        var outcome = new ByteArrayContent(ProcessingRunnerProjection.SerializeMetadata(request));
        outcome.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(outcome, ProcessingRunnerProtocol.OutcomePartName);
        for (var ordinal = 0; ordinal < payloads.Count; ordinal++)
        {
            var part = new ReadOnlyMemoryContent(payloads[ordinal]);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(
                part,
                ProcessingRunnerProtocol.PayloadPartPrefix + ordinal.ToString(CultureInfo.InvariantCulture),
                ProcessingRunnerProtocol.PayloadPartPrefix + ordinal.ToString(CultureInfo.InvariantCulture));
        }
        message.Content = content;
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<ProcessingRunnerCompletionResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(Guid jobId, ProcessingRunnerFailureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = await CreateRequestAsync(
            HttpMethod.Post, RunnerPath($"jobs/{jobId:D}/failure"), cancellationToken).ConfigureAwait(false);
        message.Content = JsonContent.Create(request, options: ProcessingRunnerProtocol.SerializerOptions);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RetireAsync(CancellationToken cancellationToken)
    {
        using var message = await CreateRequestAsync(HttpMethod.Delete, RunnerPath(), cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _tokenLock.Dispose();

    private Uri RunnerPath(string? suffix = null)
        => new(
            suffix is null
                ? $"{ProcessingRunnerProtocol.RoutePrefix}/{Uri.EscapeDataString(_options.RunnerId)}"
                : $"{ProcessingRunnerProtocol.RoutePrefix}/{Uri.EscapeDataString(_options.RunnerId)}/{suffix}",
            UriKind.Relative);

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, Uri path, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation(ProcessingRunnerProtocol.RunnerIdHeader, _options.RunnerId);
        return message;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_accessToken is { } cached && now < _accessTokenExpiresUtc - _options.TokenRefreshSkew)
        {
            return cached;
        }
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_accessToken is { } refreshed && now < _accessTokenExpiresUtc - _options.TokenRefreshSkew)
            {
                return refreshed;
            }
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret,
                ["scope"] = string.Join(' ', _options.Scopes)
            });
            using var response = await _http.PostAsync(
                new Uri(_options.TokenPath, UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProcessingRunnerClientException(
                    response.StatusCode,
                    null,
                    $"The LogicHost token endpoint rejected the runner credentials ({(int)response.StatusCode}).");
            }
            var tokenStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(tokenStream, default, cancellationToken).ConfigureAwait(false);
            var accessToken = document.RootElement.TryGetProperty("access_token", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new ProcessingRunnerClientException("The LogicHost token response did not contain an access token.");
            }
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expiresElement)
                && expiresElement.TryGetInt32(out var seconds)
                ? seconds
                : 300;
            _accessToken = accessToken;
            _accessTokenExpiresUtc = now.AddSeconds(Math.Max(30, expiresIn));
            return accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return ProcessingRunnerProjection.DeserializeMetadata<T>(bytes);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _accessToken = null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A malformed problem body must not mask the HTTP failure it accompanies.")]
    private static async Task<ProcessingRunnerClientException> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ProcessingRunnerProblem? problem = null;
        string? snippet = null;
        try
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 0 && body.Length <= ProcessingRunnerProtocol.MaximumMetadataBytes)
            {
                try
                {
                    problem = JsonSerializer.Deserialize<ProcessingRunnerProblem>(body, ProcessingRunnerProtocol.SerializerOptions);
                }
                catch (JsonException)
                {
                    var text = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512));
                    snippet = text.ReplaceLineEndings(" ");
                }
            }
        }
        catch (Exception)
        {
            problem = null;
        }
        return new ProcessingRunnerClientException(
            response.StatusCode,
            problem,
            problem is null
                ? snippet is null
                    ? $"LogicHost returned {(int)response.StatusCode} ({response.ReasonPhrase})."
                    : $"LogicHost returned {(int)response.StatusCode} ({response.ReasonPhrase}): {snippet}"
                : $"LogicHost returned {(int)response.StatusCode}: {problem.ReasonCode} - {problem.Message}");
    }
}
