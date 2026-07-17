using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class CameraAgentEnvironmentalObservationBridge(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IEnvironmentalObservationTargetResolver, IEnvironmentalObservationTransport, IDisposable
{
    private static readonly Uri DeliveryRoute = new("/api/device/environmental-observations", UriKind.Relative);
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    public async ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
    {
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        return secrets is null || secrets.ObservatoryId == Guid.Empty || secrets.DevicePublicId == Guid.Empty
            ? null
            : new EnvironmentalObservationResolvedTarget(secrets.ObservatoryId, secrets.DevicePublicId);
    }

    public async ValueTask<EnvironmentalObservationTransportResult> SendAsync(
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (secrets is null || secrets.DevicePublicId == Guid.Empty || string.IsNullOrWhiteSpace(secrets.DeviceKey))
        {
            return new(
                EnvironmentalObservationTransportDisposition.AuthenticationBlocked,
                "credentials-unavailable",
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var envelope = new EnvironmentalObservationDeliveryEnvelope(
            EnvironmentalObservationDeliveryEnvelope.CurrentSchemaVersion,
            identity.DeviceId,
            secrets.DeviceKey,
            observation);
        var validation = EnvironmentalObservationDeliveryJson.Validate(envelope);
        if (!validation.IsValid)
        {
            return new(EnvironmentalObservationTransportDisposition.Quarantine, "invalid-envelope");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, DeliveryRoute)
        {
            Content = new ByteArrayContent(EnvironmentalObservationDeliveryJson.Serialize(envelope))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.EnvironmentalDelivery.RequestTimeoutSeconds));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var bytes = await ReadBoundedAsync(
                response.Content,
                EnvironmentalObservationDeliveryJson.MaximumAcknowledgementBytes,
                timeout.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return new(EnvironmentalObservationTransportDisposition.Retry, "invalid-acknowledgement");
            }
            var parsed = EnvironmentalObservationDeliveryJson.ParseAcknowledgement(bytes);
            if (parsed.Value is not { } acknowledgement ||
                !EnvironmentalObservationDeliveryJson.Matches(acknowledgement, observation) ||
                response.StatusCode == HttpStatusCode.Accepted !=
                    (acknowledgement.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted) ||
                response.StatusCode == HttpStatusCode.OK !=
                    (acknowledgement.Disposition == EnvironmentalObservationDeliveryDisposition.Duplicate) ||
                response.StatusCode is not HttpStatusCode.Accepted and not HttpStatusCode.OK)
            {
                return new(EnvironmentalObservationTransportDisposition.Retry, "invalid-acknowledgement");
            }
            return new(
                EnvironmentalObservationTransportDisposition.Acknowledged,
                acknowledgement.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted
                    ? "accepted"
                    : "duplicate",
                acknowledgement);
        }
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
        {
            return new(EnvironmentalObservationTransportDisposition.Quarantine, $"http-{(int)response.StatusCode}");
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new(EnvironmentalObservationTransportDisposition.Terminal, "http-409");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new(
                EnvironmentalObservationTransportDisposition.AuthenticationBlocked,
                "credentials-rejected",
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        if ((int)response.StatusCode is >= 400 and < 500 &&
            response.StatusCode is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests &&
            (int)response.StatusCode != 425)
        {
            return new(EnvironmentalObservationTransportDisposition.Terminal, $"http-{(int)response.StatusCode}");
        }
        return new(
            EnvironmentalObservationTransportDisposition.Retry,
            $"http-{(int)response.StatusCode}",
            RetryAfter: ResolveRetryAfter(response.Headers.RetryAfter));
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

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient(SkyMonitorClientOptions.HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    public void Dispose() => _client.Dispose();

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
