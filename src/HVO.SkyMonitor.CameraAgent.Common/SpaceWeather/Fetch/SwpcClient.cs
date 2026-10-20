using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;

/// <summary>
/// One GET per call, with an internal clock deadline covering headers, body and decoding. Use a client whose
/// primary handler is created by SwpcSource and whose logging and retry handlers are disabled.
/// </summary>
public sealed class SwpcClient : ISwpcClient
{
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly string userAgent;

    /// <summary>Creates a transport with no default headers and one valid printable-ASCII User-Agent.</summary>
    public SwpcClient(HttpClient httpClient, TimeProvider timeProvider, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!IsValidUserAgent(userAgent))
            throw new ArgumentException("UserAgent must be one well-formed printable-ASCII header value.", nameof(userAgent));
        if (httpClient.DefaultRequestHeaders.NonValidated.Count != 0)
            throw new ArgumentException("The SWPC HttpClient must not configure default request headers.", nameof(httpClient));
        this.httpClient = httpClient;
        this.timeProvider = timeProvider;
        this.userAgent = userAgent;
    }

    /// <inheritdoc />
    public async ValueTask<SwpcHttpResponse> FetchAsync(SwpcProduct product, SwpcRequestValidators? validators,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = SwpcSource.RequestUri(product);
        var sentEtag = SwpcSource.SanitizeValidator(validators?.EntityTag, SwpcSource.MaximumEntityTagLength);
        var sentModified = SwpcSource.SanitizeValidator(validators?.LastModified, SwpcSource.MaximumLastModifiedLength);
        var validatorsSent = sentEtag is not null || sentModified is not null;
        var started = timeProvider.GetUtcNow().ToUniversalTime();
        using var deadline = new CancellationTokenSource(SwpcSource.RequestTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(SwpcSource.MediaType(product)));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            if (sentEtag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", sentEtag);
            if (sentModified is not null) request.Headers.TryAddWithoutValidation("If-Modified-Since", sentModified);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            var media = SwpcSource.SanitizeValidator(SwpcSource.SingleHeaderValue(response.Content.Headers, "Content-Type"),
                SwpcSource.MaximumMediaTypeLength);
            var etag = SwpcSource.SanitizeValidator(SwpcSource.SingleHeaderValue(response.Headers, "ETag"),
                SwpcSource.MaximumEntityTagLength);
            var modified = SwpcSource.SanitizeValidator(SwpcSource.SingleHeaderValue(response.Content.Headers, "Last-Modified"),
                SwpcSource.MaximumLastModifiedLength);
            var retry = SwpcSource.SingleHeaderValue(response.Headers, "Retry-After");
            if (retry?.Length > SwpcSource.MaximumRetryAfterLength) retry = null;
            BodyResult body = default;
            if (response.StatusCode == HttpStatusCode.OK)
                body = await ReadBodyAsync(response.Content, SwpcSource.MaximumDecodedBytes(product), linked.Token)
                    .ConfigureAwait(false);
            // Also settle a handler/stream that completed without observing cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
            return new SwpcHttpResponse(product, started, timeProvider.GetUtcNow().ToUniversalTime(),
                SwpcTransportFailure.None, (int)response.StatusCode, validatorsSent, media, etag, modified, retry,
                body.Bytes, body.Oversize, body.Undecodable);
        }
        catch (Exception exception) when (IsTransportOutcome(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = deadline.IsCancellationRequested || exception is OperationCanceledException { InnerException: TimeoutException }
                ? SwpcTransportFailure.Timeout : SwpcTransportFailure.NetworkError;
            return new SwpcHttpResponse(product, started, timeProvider.GetUtcNow().ToUniversalTime(), failure, null, validatorsSent);
        }
    }

    internal static bool IsValidUserAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(static c => c is < ' ' or > '~')) return false;
        using var probe = new HttpRequestMessage();
        return probe.Headers.UserAgent.TryParseAdd(value);
    }

    private static bool IsTransportOutcome(Exception exception) => exception is HttpRequestException or IOException or
        AuthenticationException or SocketException or OperationCanceledException;

    private static async Task<BodyResult> ReadBodyAsync(HttpContent content, int maximum, CancellationToken token)
    {
        var encoding = SwpcSource.SingleHeaderValue(content.Headers, "Content-Encoding");
        var hasEncoding = content.Headers.NonValidated.Contains("Content-Encoding");
        var gzip = string.Equals(encoding?.Trim(' ', '\t'), "gzip", StringComparison.OrdinalIgnoreCase);
        if (hasEncoding && !gzip && !string.Equals(encoding?.Trim(' ', '\t'), "identity", StringComparison.OrdinalIgnoreCase))
            return new(default, false, true);
        var declared = content.Headers.ContentLength;
        if (!gzip && declared > maximum) return new(default, true, false);
        var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var sourceDisposal = source.ConfigureAwait(false);
        var wire = new CountingStream(source);
        await using var wireDisposal = wire.ConfigureAwait(false);
        if (!gzip)
        {
            var body = await ReadBoundedAsync(wire, maximum, token).ConfigureAwait(false);
            if (!body.Oversize) wire.CheckLength(declared);
            return body;
        }
        try
        {
            var decoder = new GZipStream(wire, CompressionMode.Decompress, leaveOpen: true);
            await using var decoderDisposal = decoder.ConfigureAwait(false);
            var body = await ReadBoundedAsync(decoder, maximum, token).ConfigureAwait(false);
            if (body.Oversize) return body;
            wire.CheckLength(declared);
            // GZipStream can silently accept an incomplete member. Check its framing and final decoded size too.
            // No wire buffer grows with the response: only the first three and final eight bytes are retained.
            if (!wire.HasGzipFraming(body.Bytes.Length)) return new(default, false, true);
            return body;
        }
        catch (InvalidDataException)
        {
            // A connection ending before its declared length is transport failure, including inside a gzip read.
            if (wire.Ended) wire.CheckLength(declared);
            return new(default, false, true);
        }
    }

    private static async Task<BodyResult> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        var bytes = new byte[maximum + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        token.ThrowIfCancellationRequested();
        if (length > maximum) return new(default, true, false);
        return new(bytes.AsMemory(0, length), false, false);
    }

    private readonly record struct BodyResult(ReadOnlyMemory<byte> Bytes, bool Oversize, bool Undecodable);

    /// <summary>Tracks wire length and constant-space gzip framing without buffering or draining the body.</summary>
    private sealed class CountingStream(Stream source) : Stream
    {
        private readonly byte[] first = new byte[3];
        private readonly byte[] tail = new byte[8];
        private long count;
        public bool Ended { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int length) =>
            ReadAsync(buffer.AsMemory(offset, length)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) Ended = true;
            for (var index = 0; index < read && count + index < first.Length; index++)
                first[count + index] = buffer.Span[index];
            for (var index = Math.Max(0, read - tail.Length); index < read; index++)
                tail[(count + index) % tail.Length] = buffer.Span[index];
            count += read;
            return read;
        }
        public void CheckLength(long? declared)
        {
            if (declared.HasValue && count != declared.Value) throw new IOException("Incomplete HTTP body.");
        }
        public bool HasGzipFraming(int decodedLength)
        {
            if (count < 18 || first[0] != 0x1f || first[1] != 0x8b || first[2] != 8) return false;
            Span<byte> size = stackalloc byte[4];
            for (var index = 0; index < size.Length; index++) size[index] = tail[(count - 4 + index) % tail.Length];
            return BinaryPrimitives.ReadUInt32LittleEndian(size) == (uint)decodedLength;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
    }
}
