using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.Fleet.Contracts;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class CameraAgentFleetHeartbeatTransport(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IHttpClientFactory httpClientFactory) : IFleetHeartbeatTransport
{
    public async ValueTask<FleetAgentEndpoint?> GetEndpointAsync(CancellationToken cancellationToken)
    {
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (secrets is null || secrets.DevicePublicId == Guid.Empty || string.IsNullOrWhiteSpace(secrets.DeviceKey))
        {
            return null;
        }
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return new FleetAgentEndpoint(
            secrets.DevicePublicId,
            identity.DeviceId,
            secrets.DeviceKey,
            secrets.HeartbeatEndpoint,
            secrets.HeartbeatIntervalSeconds);
    }

    public async ValueTask<FleetDeliveryResult> SendAsync(
        FleetAgentEndpoint endpoint,
        FleetStatusReportV1 report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(report);
        var envelope = new FleetHeartbeatEnvelope(endpoint.DeviceId, endpoint.DeviceKey, report);
        var payload = FleetContractJson.Serialize(envelope);
        if (payload.Length > FleetContractJson.MaximumPayloadBytes)
        {
            return new FleetDeliveryResult(FleetDeliveryDisposition.Quarantine, "payload-too-large");
        }

        var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.HeartbeatEndpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var acknowledgementBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            FleetHeartbeatAcknowledgement? acknowledgement;
            try
            {
                acknowledgement = FleetContractJson.DeserializeAcknowledgement(acknowledgementBytes);
            }
            catch (JsonException)
            {
                acknowledgement = null;
            }
            return acknowledgement is null
                ? new FleetDeliveryResult(FleetDeliveryDisposition.Retry, "invalid-acknowledgement")
                : new FleetDeliveryResult(FleetDeliveryDisposition.Acknowledged, "acknowledged", acknowledgement);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge)
        {
            return new FleetDeliveryResult(FleetDeliveryDisposition.Quarantine, $"http-{(int)response.StatusCode}");
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new FleetDeliveryResult(FleetDeliveryDisposition.Blocked, "credentials-rejected", RetryAfter: TimeSpan.FromMinutes(5));
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        return new FleetDeliveryResult(FleetDeliveryDisposition.Retry, $"http-{(int)response.StatusCode}", RetryAfter: retryAfter);
    }
}
