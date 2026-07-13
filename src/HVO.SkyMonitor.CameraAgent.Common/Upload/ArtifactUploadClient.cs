using System.Net.Http.Headers;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Streams queued artifacts to the versioned central multipart endpoint.</summary>
public sealed class ArtifactUploadClient(
    IHttpClientFactory httpClientFactory,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider)
{
    public const string CentralClientName = "SkyMonitor.Api";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Uploads one manifest and payload, returning false for retryable unsuccessful responses.</summary>
    public async Task<bool> UploadAsync(string storageRoot, ArtifactUploadManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        var path = Path.Combine(Path.GetFullPath(storageRoot), manifest.RelativeArtifactPath);
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        using var uploadStream = hostOptions.Value.UploadBandwidthLimitBytesPerSecond > 0
            ? new BandwidthLimitedReadStream(stream, hostOptions.Value.UploadBandwidthLimitBytesPerSecond, timeProvider)
            : null;
        Stream uploadSource = uploadStream is null ? stream : uploadStream;
        using var content = new MultipartFormDataContent();
        using var manifestContent = new StringContent(JsonSerializer.Serialize(manifest, SerializerOptions));
        content.Add(manifestContent, "manifest");
        using var payload = new StreamContent(uploadSource);
        payload.Headers.ContentType = MediaTypeHeaderValue.Parse(manifest.MediaType);
        content.Add(payload, "payload", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1.0/artifacts") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", manifest.IdempotencyKey);
        var httpClient = httpClientFactory.CreateClient(CentralClientName);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // Network loss is expected for an offline agent; retain the outbox manifest for a later retry.
            return false;
        }
    }

    internal sealed class BandwidthLimitedReadStream(Stream inner, int bytesPerSecond, TimeProvider timeProvider) : Stream
    {
        private readonly long _startedTimestamp = timeProvider.GetTimestamp();
        private readonly int _maximumReadSize = Math.Max(1, bytesPerSecond / 10);
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            WaitForBudgetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var read = inner.Read(buffer, offset, Math.Min(count, _maximumReadSize));
            _bytesRead += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            WaitForBudgetAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var read = inner.Read(buffer[..Math.Min(buffer.Length, _maximumReadSize)]);
            _bytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await WaitForBudgetAsync(cancellationToken).ConfigureAwait(false);
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maximumReadSize)], cancellationToken).ConfigureAwait(false);
            _bytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private async ValueTask WaitForBudgetAsync(CancellationToken cancellationToken)
        {
            var expectedElapsed = TimeSpan.FromSeconds((double)_bytesRead / bytesPerSecond);
            var actualElapsed = timeProvider.GetElapsedTime(_startedTimestamp);
            if (expectedElapsed > actualElapsed)
            {
                await Task.Delay(expectedElapsed - actualElapsed, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
