using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class CameraAgentProcessingGraphDeliveryTransport(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IProcessingGraphDeliveryTransport
{
    private static readonly Uri PullRoute = new("/api/device/processing-graphs/pull", UriKind.Relative);
    private static readonly Uri FactsRoute = new("/api/device/processing-graphs/facts", UriKind.Relative);
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public async ValueTask<ProcessingGraphProposalTransportResult> PullAsync(
        ProcessingGraphProposalPollRequestV1 request,
        CancellationToken cancellationToken)
    {
        var endpoint = await GetEndpointAsync(request.AgentId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return new(ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, "credentials-unavailable",
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        using var message = CreateRequest(PullRoute, endpoint.Value, JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions));
        try
        {
            using var timeout = CreateRequestTimeout(cancellationToken);
            using var response = await SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var payload = await ReadBoundedAsync(
                    response.Content, ProcessingGraphJson.MaximumDocumentBytes + 64 * 1024, timeout.Token)
                    .ConfigureAwait(false);
                var result = payload is null
                    ? null
                    : JsonSerializer.Deserialize<ProcessingGraphProposalPollResponseV1>(payload, SerializerOptions);
                return result is null
                    ? new(ProcessingGraphDeliveryTransportDisposition.Retry, "invalid-response")
                    : result.Proposal is { } proposal && proposal.InstallationPublicId != endpoint.Value.DevicePublicId
                        ? new(ProcessingGraphDeliveryTransportDisposition.Rejected, "installation-mismatch")
                    : new(ProcessingGraphDeliveryTransportDisposition.Acknowledged, "acknowledged", result);
            }
            return ToProposalFailure(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ProcessingGraphDeliveryTransportDisposition.Retry, "request-timeout");
        }
        catch (HttpRequestException)
        {
            return new(ProcessingGraphDeliveryTransportDisposition.Retry, "transport-unavailable");
        }
    }

    public async ValueTask<ProcessingGraphFactTransportResult> SendFactAsync(
        ProcessingGraphDeliveryFactV1 fact,
        CancellationToken cancellationToken)
    {
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = await GetEndpointAsync(identity.DeviceId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return new(ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, "credentials-unavailable",
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        using var message = CreateRequest(FactsRoute, endpoint.Value, JsonSerializer.SerializeToUtf8Bytes(fact, SerializerOptions));
        try
        {
            using var timeout = CreateRequestTimeout(cancellationToken);
            using var response = await SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var payload = await ReadBoundedAsync(response.Content, 16 * 1024, timeout.Token).ConfigureAwait(false);
                var acknowledgement = payload is null
                    ? null
                    : JsonSerializer.Deserialize<ProcessingGraphFactAcknowledgementV1>(payload, SerializerOptions);
                return acknowledgement is null
                    ? new(ProcessingGraphDeliveryTransportDisposition.Retry, "invalid-acknowledgement")
                    : new(ProcessingGraphDeliveryTransportDisposition.Acknowledged, "acknowledged", acknowledgement);
            }
            var failure = ToFailure(response);
            return new(failure.Disposition, failure.ReasonCode, RetryAfter: failure.RetryAfter);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ProcessingGraphDeliveryTransportDisposition.Retry, "request-timeout");
        }
        catch (HttpRequestException)
        {
            return new(ProcessingGraphDeliveryTransportDisposition.Retry, "transport-unavailable");
        }
    }

    private async ValueTask<(string DeviceId, string DeviceKey, Guid DevicePublicId)?> GetEndpointAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return secrets is null || secrets.DevicePublicId == Guid.Empty || string.IsNullOrWhiteSpace(secrets.DeviceKey) ||
            !string.Equals(identity.DeviceId, agentId, StringComparison.Ordinal)
                ? null
                : (identity.DeviceId, secrets.DeviceKey, secrets.DevicePublicId);
    }

    private static HttpRequestMessage CreateRequest(
        Uri route,
        (string DeviceId, string DeviceKey, Guid DevicePublicId) endpoint,
        byte[] payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-HVO-Device-Id", endpoint.DeviceId);
        request.Headers.TryAddWithoutValidation("X-HVO-Device-Key", endpoint.DeviceKey);
        request.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "1");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProcessingGraphDelivery.RequestTimeoutSeconds));
        return timeout;
    }

    private ProcessingGraphProposalTransportResult ToProposalFailure(HttpResponseMessage response)
    {
        var failure = ToFailure(response);
        return new(failure.Disposition, failure.ReasonCode, RetryAfter: failure.RetryAfter);
    }

    private (ProcessingGraphDeliveryTransportDisposition Disposition, string ReasonCode, TimeSpan? RetryAfter) ToFailure(
        HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date && date > timeProvider.GetUtcNow()
                ? date - timeProvider.GetUtcNow()
                : null);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (ProcessingGraphDeliveryTransportDisposition.AuthenticationBlocked, "credentials-rejected", TimeSpan.FromMinutes(5)),
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound =>
                (ProcessingGraphDeliveryTransportDisposition.Rejected, $"http-{(int)response.StatusCode}", retryAfter),
            _ => (ProcessingGraphDeliveryTransportDisposition.Retry, $"http-{(int)response.StatusCode}", retryAfter)
        };
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
        using var target = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        while (target.Length <= maximumBytes)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return target.ToArray();
            }
            if (target.Length + read > maximumBytes)
            {
                return null;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}
