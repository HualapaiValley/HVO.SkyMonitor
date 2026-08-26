using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
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
        ArtifactDeliveryDescriptor delivery;
        try
        {
            delivery = ResolveDelivery(record);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return new(ArtifactUploadDisposition.Quarantine, "invalid-manifest-projection");
        }
        var httpClient = httpClientFactory.CreateClient(CentralClientName);
        if (record.AttemptCount > 1)
        {
            var status = await CheckCentralStatusAsync(
                httpClient, record.ManifestBytes, delivery, cancellationToken).ConfigureAwait(false);
            if (status is not null)
            {
                return status;
            }
        }
        var path = Path.Combine(Path.GetFullPath(storageRoot), delivery.RelativeArtifactPath);
        if (!File.Exists(path))
        {
            return new(ArtifactUploadDisposition.Quarantine, "payload-missing");
        }
        if (new FileInfo(path).Length != delivery.ByteLength)
        {
            return new(ArtifactUploadDisposition.Quarantine, "payload-length-mismatch");
        }

        using var stream = File.OpenRead(path);
        using var uploadStream = hostOptions.Value.UploadBandwidthLimitBytesPerSecond > 0
            ? new BandwidthLimitedReadStream(stream, hostOptions.Value.UploadBandwidthLimitBytesPerSecond, timeProvider)
            : null;
        Stream uploadSource = uploadStream is null ? stream : uploadStream;
        using var content = new MultipartFormDataContent();
        using var manifestContent = new ByteArrayContent(record.ManifestBytes.ToArray());
        manifestContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(manifestContent, "manifest");
        using var payload = new StreamContent(uploadSource);
        payload.Headers.ContentType = MediaTypeHeaderValue.Parse(delivery.MediaType);
        content.Add(payload, "payload", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1.0/artifacts") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.IdempotencyKey);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await ReadAcknowledgementAsync(response, delivery, cancellationToken).ConfigureAwait(false);
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

    private async Task<ArtifactUploadResult?> CheckCentralStatusAsync(
        HttpClient httpClient,
        ReadOnlyMemory<byte> manifestBytes,
        ArtifactDeliveryDescriptor delivery,
        CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(manifestBytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1.0/artifacts/status") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.IdempotencyKey);
        try
        {
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await ReadAcknowledgementAsync(response, delivery, cancellationToken).ConfigureAwait(false);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
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
                : new(ArtifactUploadDisposition.Retry, "status-transport-failure");
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
            return new(ArtifactUploadDisposition.Retry, "status-request-timeout");
        }
    }

    private static async Task<ArtifactUploadResult> ReadAcknowledgementAsync(
        HttpResponseMessage response,
        ArtifactDeliveryDescriptor delivery,
        CancellationToken cancellationToken)
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

        if (acknowledgement is null || !AcknowledgementMatches(acknowledgement, delivery))
        {
            return new(ArtifactUploadDisposition.Quarantine, "acknowledgement-mismatch");
        }
        return new(ArtifactUploadDisposition.Acknowledged, "acknowledged", Acknowledgement: acknowledgement);
    }

    internal static ArtifactDeliveryDescriptor ResolveDelivery(ArtifactOutboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ProductManifest is { } productManifest)
        {
            var productValidation = productManifest.Validate();
            if (!productValidation.IsValid)
            {
                throw new InvalidDataException("Outbox record contains an invalid structured product manifest.");
            }
            var productDescriptor = productManifest.Descriptor;
            return new(
                productManifest.SchemaVersion,
                productManifest.IdempotencyKey,
                productDescriptor.Artifact.ArtifactId,
                productDescriptor.Artifact.Role,
                productManifest.RelativeArtifactPath,
                productDescriptor.Artifact.ChecksumSha256,
                productDescriptor.ByteLength,
                productDescriptor.Artifact.MediaType,
                productDescriptor.SourceCapture.Timing.ExposureStartedUtc);
        }
        if (record.Manifest?.LegacyManifest is { } legacy)
        {
            legacy.Validate();
            return new(
                legacy.SchemaVersion,
                legacy.IdempotencyKey,
                legacy.ArtifactId,
                legacy.Role,
                legacy.RelativeArtifactPath,
                legacy.ChecksumSha256,
                legacy.ByteLength,
                legacy.MediaType,
                legacy.CapturedAtUtc);
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
        return new(
            current.SchemaVersion,
            current.IdempotencyKey,
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            current.RelativeArtifactPath,
            descriptor.Artifact.ChecksumSha256,
            descriptor.Layout.ByteLength,
            descriptor.Artifact.MediaType,
            descriptor.Timing.ExposureStartedUtc);
    }

    private static bool AcknowledgementMatches(
        ArtifactUploadAcknowledgement acknowledgement,
        ArtifactDeliveryDescriptor delivery)
        => string.Equals(acknowledgement.IdempotencyKey, delivery.IdempotencyKey, StringComparison.OrdinalIgnoreCase)
            && acknowledgement.ArtifactId == delivery.ArtifactId
            && string.Equals(acknowledgement.ChecksumSha256, delivery.ChecksumSha256, StringComparison.OrdinalIgnoreCase)
            && acknowledgement.ByteLength == delivery.ByteLength
            && string.Equals(
                acknowledgement.AcceptedManifestSchemaVersion,
                delivery.SchemaVersion,
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
