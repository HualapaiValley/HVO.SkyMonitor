using System.Net.Http.Headers;
using System.Net;
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

    /// <summary>Uploads one leased artifact and classifies the durable settlement required by its response.</summary>
    public async Task<ArtifactUploadResult> UploadAsync(
        string storageRoot,
        ArtifactOutboxRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        ArgumentNullException.ThrowIfNull(record);
        ArtifactUploadManifest manifest;
        try
        {
            manifest = CreateCompatibilityManifest(record);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return new(ArtifactUploadDisposition.Quarantine, "invalid-manifest-projection");
        }
        var path = Path.Combine(Path.GetFullPath(storageRoot), manifest.RelativeArtifactPath);
        if (!File.Exists(path))
        {
            return new(ArtifactUploadDisposition.Quarantine, "payload-missing");
        }
        if (new FileInfo(path).Length != manifest.ByteLength)
        {
            return new(ArtifactUploadDisposition.Quarantine, "payload-length-mismatch");
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
            if (response.IsSuccessStatusCode)
            {
                ArtifactUploadAcknowledgement? acknowledgement;
                try
                {
                    using var responseBody = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    acknowledgement = await JsonSerializer.DeserializeAsync<ArtifactUploadAcknowledgement>(
                        responseBody, SerializerOptions, cancellationToken).ConfigureAwait(false);
                    acknowledgement?.Validate();
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
                {
                    return new(ArtifactUploadDisposition.Quarantine, "acknowledgement-invalid");
                }
                catch (IOException)
                {
                    return new(ArtifactUploadDisposition.Retry, "acknowledgement-read-failure");
                }

                if (acknowledgement is null || !AcknowledgementMatches(acknowledgement, manifest))
                {
                    return new(ArtifactUploadDisposition.Quarantine, "acknowledgement-mismatch");
                }

                return new(
                    ArtifactUploadDisposition.Acknowledged,
                    "acknowledged",
                    Acknowledgement: acknowledgement);
            }

            return IsRetryable(response.StatusCode)
                ? new ArtifactUploadResult(
                    ArtifactUploadDisposition.Retry,
                    string.Concat("http-", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ResolveRetryAfter(response.Headers.RetryAfter))
                : new ArtifactUploadResult(
                    ArtifactUploadDisposition.Quarantine,
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "authentication-rejected"
                        : string.Concat("http-", ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        catch (HttpRequestException exception)
        {
            return exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new(ArtifactUploadDisposition.Quarantine, "authentication-rejected")
                : new(ArtifactUploadDisposition.Retry, "transport-failure");
        }
        catch (InvalidOperationException)
        {
            return new(ArtifactUploadDisposition.Quarantine, "authentication-configuration-invalid");
        }
        catch (JsonException)
        {
            return new(ArtifactUploadDisposition.Quarantine, "authentication-response-invalid");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ArtifactUploadDisposition.Retry, "request-timeout");
        }
    }

    internal static ArtifactUploadManifest CreateCompatibilityManifest(ArtifactOutboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Manifest?.LegacyManifest is { } legacy)
        {
            legacy.Validate();
            return legacy;
        }
        if (record.Manifest?.Manifest is not { } current)
        {
            throw new InvalidDataException("Outbox record does not contain a deliverable artifact manifest.");
        }
        var validation = current.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Outbox record contains an invalid manifest v2.");
        }
        var descriptor = current.Descriptor;
        var projected = new ArtifactUploadManifest(
            ArtifactUploadManifest.CurrentSchemaVersion,
            descriptor.Capture.AgentId,
            descriptor.Artifact.ArtifactId,
            descriptor.Capture.CaptureId,
            descriptor.Artifact.Role,
            descriptor.Artifact.MediaType,
            descriptor.Layout.ByteLength,
            descriptor.Artifact.ChecksumSha256,
            descriptor.Timing.ExposureStartedUtc,
            string.Concat("v2-", record.IdempotencyKey),
            current.RelativeArtifactPath,
            current.Scene);
        projected.Validate();
        return projected;
    }

    private static bool AcknowledgementMatches(
        ArtifactUploadAcknowledgement acknowledgement,
        ArtifactUploadManifest manifest)
        => string.Equals(acknowledgement.IdempotencyKey, manifest.IdempotencyKey, StringComparison.OrdinalIgnoreCase)
            && acknowledgement.ArtifactId == manifest.ArtifactId
            && string.Equals(acknowledgement.ChecksumSha256, manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            && acknowledgement.ByteLength == manifest.ByteLength
            && string.Equals(
                acknowledgement.AcceptedManifestSchemaVersion,
                manifest.SchemaVersion,
                StringComparison.Ordinal);

    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)statusCode == 425
            || (int)statusCode >= 500;

    private TimeSpan? ResolveRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            var delay = date - timeProvider.GetUtcNow();
            return delay > TimeSpan.Zero ? delay : null;
        }
        return null;
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
