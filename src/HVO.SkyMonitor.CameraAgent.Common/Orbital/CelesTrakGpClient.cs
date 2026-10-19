using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Request bounds for <see cref="CelesTrakGpClient"/>. Each bound must be positive and no larger than the
/// <see cref="CelesTrakGpSource"/> maximum; the user agent must be one well-formed printable-ASCII header value.
/// </summary>
public sealed record CelesTrakGpClientSettings(TimeSpan RequestTimeout, int MaximumWireBytes, int MaximumRawBytes, string UserAgent);

/// <summary>
/// Issues exactly one GET of <see cref="CelesTrakGpSource.RequestUri"/> per call. It never retries, never follows a
/// redirect, never sends a conditional or cookie header and never reads a non-200 body. The timeout is an internal
/// <see cref="TimeProvider"/> deadline that covers headers, body and decoding, and is reported distinctly from caller
/// cancellation.
/// </summary>
public sealed class CelesTrakGpClient : IOrbitalElementsProviderClient
{
    internal const int MaximumUserAgentLength = 256;
    private const int InitialBufferBytes = 64 * 1024;
    private const int GzipMinimumLength = 18;
    private const string ReasonRetrieved = "orbital.retrieved";

    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly CelesTrakGpClientSettings settings;

    public CelesTrakGpClient(HttpClient httpClient, TimeProvider timeProvider, CelesTrakGpClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.RequestTimeout <= TimeSpan.Zero || settings.RequestTimeout > CelesTrakGpSource.MaximumRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "RequestTimeout must be positive and at most 30 seconds.");
        }
        if (settings.MaximumWireBytes is <= 0 or > CelesTrakGpSource.MaximumWireBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "MaximumWireBytes must be positive and at most 32 MiB.");
        }
        if (settings.MaximumRawBytes is <= 0 or > CelesTrakGpSource.MaximumRawBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "MaximumRawBytes must be positive and at most 32 MiB.");
        }
        if (!IsValidUserAgent(settings.UserAgent))
        {
            throw new ArgumentException("UserAgent must be one well-formed printable-ASCII product value.", nameof(settings));
        }
        // Default headers would be merged into the request, which is how a conditional, cookie or credential header
        // could be added by configuration. The request must carry exactly the headers this client sets.
        if (httpClient.DefaultRequestHeaders.NonValidated.Count != 0)
        {
            throw new ArgumentException("The orbital-elements HttpClient must not configure default request headers.",
                nameof(httpClient));
        }
        this.httpClient = httpClient;
        this.timeProvider = timeProvider;
        this.settings = settings;
    }

    public async ValueTask<OrbitalFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = new AttemptState(timeProvider.GetUtcNow().ToUniversalTime());
        using var timeout = new CancellationTokenSource(settings.RequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await FetchCoreAsync(state, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransportOutcome(exception))
        {
            // Caller cancellation always wins, even when it races a transport failure.
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
            {
                return state.Complete(this, OrbitalFetchDisposition.TimedOut, "orbital.request-timeout",
                    FormattableString.Invariant($"timeout-ms={(long)settings.RequestTimeout.TotalMilliseconds}"));
            }
            if (exception is OperationCanceledException { InnerException: TimeoutException })
            {
                return state.Complete(this, OrbitalFetchDisposition.TimedOut, "orbital.http-client-timeout",
                    DescribeException(exception));
            }
            var reason = exception is OperationCanceledException ? "orbital.transport-cancelled" : ClassifyTransport(exception);
            return state.Complete(this, OrbitalFetchDisposition.TransportFailure, reason, DescribeException(exception));
        }
    }

    private async Task<OrbitalFetchResult> FetchCoreAsync(AttemptState state, CancellationToken token)
    {
        using var request = CreateRequest();
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        var status = (int)response.StatusCode;
        state.HttpStatusCode = status;
        state.EntityTag = CelesTrakGpSource.SanitizeValidator(
            CelesTrakGpSource.SingleHeaderValue(response.Headers, "ETag"), CelesTrakGpSource.MaximumEntityTagLength);
        state.LastModified = CelesTrakGpSource.SanitizeValidator(
            CelesTrakGpSource.SingleHeaderValue(response.Content.Headers, "Last-Modified"),
            CelesTrakGpSource.MaximumLastModifiedLength);

        // Every non-200, including 1xx leftovers, other 2xx, 304 and every 3xx, is a provider-wide halt. The body is
        // not read: the response is disposed without touching its content stream.
        if (status != (int)HttpStatusCode.OK)
        {
            return state.Complete(this, OrbitalFetchDisposition.HttpStatusHalt, "orbital.non-200-response",
                FormattableString.Invariant($"status={status}"));
        }
        if (!IsAcceptedMediaType(response.Content.Headers))
        {
            return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.media-type",
                "content-type-not-application-json");
        }
        if (!TryReadContentEncoding(response.Content.Headers, out var encoding))
        {
            return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.content-encoding",
                "content-encoding-not-identity-or-gzip");
        }
        state.ContentEncoding = encoding;

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is < 0 || declaredLength > settings.MaximumWireBytes)
        {
            return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.wire-bound",
                FormattableString.Invariant($"declared-bytes={declaredLength} limit={settings.MaximumWireBytes}"));
        }

        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        BoundedBuffer wire;
        await using (stream.ConfigureAwait(false))
        {
            wire = await ReadBoundedAsync(stream, settings.MaximumWireBytes, (int)(declaredLength ?? InitialBufferBytes), token)
                .ConfigureAwait(false);
        }
        state.WireLengthBytes = wire.Length;
        if (wire.Exceeded)
        {
            return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.wire-bound",
                FormattableString.Invariant($"read-bytes>{settings.MaximumWireBytes}"));
        }
        if (declaredLength is long declared && declared != wire.Length)
        {
            return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.length-mismatch",
                FormattableString.Invariant($"declared-bytes={declared} read-bytes={wire.Length}"));
        }

        byte[] raw;
        // A zero-byte body is empty whatever its label; only a non-empty body is held to the gzip framing rules.
        if (encoding == "gzip" && wire.Length > 0)
        {
            var decoded = DecodeGzip(wire.Bytes, settings.MaximumRawBytes, token);
            if (decoded.ReasonCode is not null)
            {
                return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, decoded.ReasonCode, decoded.Detail);
            }
            raw = decoded.Bytes!;
        }
        else
        {
            if (wire.Length > settings.MaximumRawBytes)
            {
                return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.raw-bound",
                    FormattableString.Invariant($"raw-bytes>{settings.MaximumRawBytes}"));
            }
            raw = wire.Bytes;
        }
        if (raw.Length == 0)
        {
            return state.Complete(this, OrbitalFetchDisposition.ResponseRejected, "orbital.empty-body", "raw-bytes=0");
        }

        state.RawBytes = raw;
        state.RawSha256 = Convert.ToHexString(SHA256.HashData(raw));
        return state.Complete(this, OrbitalFetchDisposition.Retrieved, ReasonRetrieved,
            FormattableString.Invariant($"status=200 raw-bytes={raw.Length} wire-bytes={wire.Length}"));
    }

    private HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, CelesTrakGpSource.RequestUri);
        // The constructor proved the value parses, so the exact configured text goes on the wire unaltered.
        request.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return request;
    }

    internal static bool IsValidUserAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUserAgentLength ||
            value.Any(static c => c is < ' ' or > '~'))
        {
            return false;
        }
        using var probe = new HttpRequestMessage();
        return probe.Headers.UserAgent.TryParseAdd(value);
    }

    /// <summary><c>application/json</c>, optionally with exactly one <c>charset=utf-8</c> parameter.</summary>
    internal static bool IsAcceptedMediaType(HttpContentHeaders headers)
    {
        var value = CelesTrakGpSource.SingleHeaderValue(headers, "Content-Type");
        if (value is null || !MediaTypeHeaderValue.TryParse(value, out var parsed) ||
            !string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (parsed.Parameters.Count == 0)
        {
            return true;
        }
        if (parsed.Parameters.Count != 1)
        {
            return false;
        }
        var parameter = parsed.Parameters.First();
        var charset = parameter.Value is { Length: >= 2 } quoted && quoted[0] == '"' && quoted[^1] == '"'
            ? quoted[1..^1]
            : parameter.Value;
        return string.Equals(parameter.Name, "charset", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Absent, or exactly one coding of <c>identity</c> or <c>gzip</c>. Returns the lowercase token.</summary>
    internal static bool TryReadContentEncoding(HttpContentHeaders headers, out string? encoding)
    {
        encoding = null;
        if (!headers.NonValidated.TryGetValues("Content-Encoding", out var values))
        {
            return true;
        }
        string? token = null;
        var count = 0;
        foreach (var value in values)
        {
            foreach (var element in value.Split(','))
            {
                count++;
                token = element.Trim(' ', '\t');
            }
        }
        if (count != 1 || token is null)
        {
            return false;
        }
        if (string.Equals(token, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            encoding = "gzip";
            return true;
        }
        if (string.Equals(token, "identity", StringComparison.OrdinalIgnoreCase))
        {
            encoding = "identity";
            return true;
        }
        return false;
    }

    private static async Task<BoundedBuffer> ReadBoundedAsync(Stream stream, int maximum, int capacityHint,
        CancellationToken token)
    {
        var buffer = new byte[Math.Clamp(capacityHint, 0, maximum)];
        var length = 0;
        var probe = new byte[1];
        while (true)
        {
            if (length == buffer.Length)
            {
                // Full: one more byte decides between an exact fit and an over-bound body.
                var extra = await stream.ReadAsync(probe, token).ConfigureAwait(false);
                if (extra == 0)
                {
                    break;
                }
                if (length == maximum)
                {
                    return new BoundedBuffer(buffer, length + 1, Exceeded: true);
                }
                Array.Resize(ref buffer, Grow(buffer.Length, maximum));
                buffer[length++] = probe[0];
                continue;
            }
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        if (length != buffer.Length)
        {
            Array.Resize(ref buffer, length);
        }
        return new BoundedBuffer(buffer, length, Exceeded: false);
    }

    /// <summary>
    /// Decodes one gzip member under <paramref name="maximum"/>. The decoder silently accepts a truncated member and
    /// ignores bytes after a member, so the trailer's ISIZE (decoded length modulo 2^32, little-endian) must equal
    /// the decoded length; a corrupt CRC or header raises <see cref="InvalidDataException"/>.
    /// </summary>
    internal static DecodeOutcome DecodeGzip(byte[] wire, int maximum, CancellationToken token)
    {
        if (wire.Length < GzipMinimumLength || wire[0] != 0x1F || wire[1] != 0x8B || wire[2] != 0x08)
        {
            return DecodeOutcome.Reject("orbital.gzip-invalid", "gzip-header");
        }
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(wire.Length - 4));
        try
        {
            using var source = new MemoryStream(wire, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            var buffer = new byte[Math.Clamp(declaredSize, 1u, (uint)maximum)];
            var length = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (length == buffer.Length)
                {
                    var extra = gzip.ReadByte();
                    if (extra < 0)
                    {
                        break;
                    }
                    if (length == maximum)
                    {
                        return DecodeOutcome.Reject("orbital.raw-bound",
                            FormattableString.Invariant($"decoded-bytes>{maximum}"));
                    }
                    Array.Resize(ref buffer, Grow(buffer.Length, maximum));
                    buffer[length++] = (byte)extra;
                    continue;
                }
                var read = gzip.Read(buffer, length, buffer.Length - length);
                if (read == 0)
                {
                    break;
                }
                length += read;
            }
            if ((uint)length != declaredSize)
            {
                return DecodeOutcome.Reject("orbital.gzip-invalid",
                    FormattableString.Invariant($"gzip-size-mismatch decoded-bytes={length}"));
            }
            if (length != buffer.Length)
            {
                Array.Resize(ref buffer, length);
            }
            return new DecodeOutcome(buffer, null, string.Empty);
        }
        catch (InvalidDataException)
        {
            return DecodeOutcome.Reject("orbital.gzip-invalid", "gzip-stream");
        }
    }

    private static int Grow(int current, int maximum) => (int)Math.Min((long)maximum, Math.Max(current * 2L, InitialBufferBytes));

    private static bool IsTransportOutcome(Exception exception) => exception is HttpRequestException or IOException or
        AuthenticationException or SocketException or OperationCanceledException;

    internal static string ClassifyTransport(Exception exception)
    {
        var error = exception switch
        {
            HttpRequestException request => request.HttpRequestError,
            HttpIOException io => io.HttpRequestError,
            _ => HttpRequestError.Unknown
        };
        switch (error)
        {
            case HttpRequestError.SecureConnectionError:
                return "orbital.transport-tls";
            case HttpRequestError.UserAuthenticationError or HttpRequestError.ProxyTunnelError:
                return "orbital.transport-authentication";
            case HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError:
                return "orbital.transport-connection";
            case HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded or
                HttpRequestError.VersionNegotiationError:
                return "orbital.transport-protocol";
            case HttpRequestError.ConfigurationLimitExceeded:
                return "orbital.transport-limit";
        }
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return "orbital.transport-tls";
            }
            if (current is SocketException)
            {
                return "orbital.transport-connection";
            }
        }
        return exception is IOException ? "orbital.transport-io" : "orbital.transport-failure";
    }

    /// <summary>Type names and enum values only. Exception messages can echo request or response headers.</summary>
    internal static string DescribeException(Exception exception)
    {
        var builder = new StringBuilder();
        var depth = 0;
        for (var current = exception; current is not null && depth < 4; current = current.InnerException, depth++)
        {
            if (depth > 0)
            {
                builder.Append(" <- ");
            }
            builder.Append(current.GetType().Name);
            switch (current)
            {
                case HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } request:
                    builder.Append(':').Append(request.HttpRequestError.ToString());
                    break;
                case HttpIOException { HttpRequestError: not HttpRequestError.Unknown } io:
                    builder.Append(':').Append(io.HttpRequestError.ToString());
                    break;
                case SocketException socket:
                    builder.Append(':').Append(socket.SocketErrorCode.ToString());
                    break;
            }
        }
        return builder.ToString();
    }

    internal readonly record struct DecodeOutcome(byte[]? Bytes, string? ReasonCode, string Detail)
    {
        public static DecodeOutcome Reject(string reasonCode, string detail) => new(null, reasonCode, detail);
    }

    private readonly record struct BoundedBuffer(byte[] Bytes, int Length, bool Exceeded);

    private sealed class AttemptState(DateTimeOffset startedUtc)
    {
        public int? HttpStatusCode { get; set; }
        public long WireLengthBytes { get; set; }
        public string? ContentEncoding { get; set; }
        public string? EntityTag { get; set; }
        public string? LastModified { get; set; }
        public byte[]? RawBytes { get; set; }
        public string? RawSha256 { get; set; }

        public OrbitalFetchResult Complete(CelesTrakGpClient client, OrbitalFetchDisposition disposition, string reasonCode,
            string detail)
        {
            var retrieved = disposition == OrbitalFetchDisposition.Retrieved;
            return new OrbitalFetchResult(disposition, HttpStatusCode, startedUtc,
                client.timeProvider.GetUtcNow().ToUniversalTime(), retrieved ? RawBytes : null, retrieved ? RawSha256 : null,
                WireLengthBytes, ContentEncoding, EntityTag, LastModified, reasonCode, detail);
        }
    }
}
