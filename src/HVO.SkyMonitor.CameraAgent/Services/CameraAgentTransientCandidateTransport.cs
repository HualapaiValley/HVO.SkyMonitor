using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class CameraAgentTransientCandidateTransport(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOptions<CameraAgentHostOptions> hostOptions,
    TimeProvider timeProvider) : ITransientCandidateTransport
{
    private static readonly Uri DeliveryRoute = new("/api/device/transient-candidates", UriKind.Relative);

    public async ValueTask<TransientCandidateTransportResult> SendAsync(
        TransientCandidateSubmissionEnvelopeV1 submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (secrets is null || secrets.DevicePublicId == Guid.Empty || string.IsNullOrWhiteSpace(secrets.DeviceKey))
        {
            return new(
                TransientCandidateTransportDisposition.AuthenticationBlocked,
                "credentials-unavailable",
                RetryAfter: TimeSpan.FromMinutes(5));
        }

        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(submission.Candidate.AgentId, identity.DeviceId, StringComparison.Ordinal))
        {
            return new(TransientCandidateTransportDisposition.Rejected, "agent-identity-mismatch");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, DeliveryRoute)
        {
            Content = new ByteArrayContent(TransientCandidateDeliveryJson.Serialize(submission))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-HVO-Device-Id", identity.DeviceId);
        request.Headers.TryAddWithoutValidation("X-HVO-Device-Key", secrets.DeviceKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", submission.SubmissionIdentitySha256);

        try
        {
            var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                hostOptions.Value.TransientDetection.DeliveryRequestTimeoutSeconds));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
            {
                var bytes = await ReadBoundedAsync(
                    response.Content,
                    TransientCandidateDeliveryJson.MaximumAcknowledgementBytes,
                    timeout.Token).ConfigureAwait(false);
                var acknowledgement = bytes is null
                    ? null
                    : TransientCandidateDeliveryJson.ParseAcknowledgement(bytes).Value;
                var validPair = acknowledgement is not null &&
                    bytes!.AsSpan().SequenceEqual(TransientCandidateDeliveryJson.Serialize(acknowledgement)) &&
                    TransientCandidateDeliveryJson.Matches(acknowledgement, submission) &&
                    (response.StatusCode == HttpStatusCode.Accepted &&
                        acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Accepted ||
                     response.StatusCode == HttpStatusCode.OK &&
                        acknowledgement.Disposition == TransientCandidateSubmissionDisposition.Duplicate);
                return validPair
                    ? new(
                        TransientCandidateTransportDisposition.Acknowledged,
                        acknowledgement!.Disposition == TransientCandidateSubmissionDisposition.Accepted
                            ? "accepted"
                            : "duplicate",
                        acknowledgement)
                    : new(TransientCandidateTransportDisposition.Retry, "invalid-acknowledgement");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(
                    TransientCandidateTransportDisposition.AuthenticationBlocked,
                    "credentials-rejected",
                    RetryAfter: TimeSpan.FromMinutes(5));
            }
            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode == 425 || (int)response.StatusCode >= 500)
            {
                return new(
                    TransientCandidateTransportDisposition.Retry,
                    $"http-{(int)response.StatusCode}",
                    RetryAfter: ResolveRetryAfter(response.Headers.RetryAfter));
            }
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                return new(TransientCandidateTransportDisposition.Rejected, $"http-{(int)response.StatusCode}");
            }
            return new(TransientCandidateTransportDisposition.Retry, $"http-{(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(TransientCandidateTransportDisposition.Retry, "request-timeout");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new(
                TransientCandidateTransportDisposition.AuthenticationBlocked,
                "central-authentication-failure",
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        catch (HttpRequestException)
        {
            return new(TransientCandidateTransportDisposition.Retry, "transport-failure");
        }
    }

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

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            return null;
        }
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 1024));
        var buffer = new byte[1024];
        while (destination.Length <= maximumBytes)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }
}
