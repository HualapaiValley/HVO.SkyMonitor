using System.Net;
using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Authenticated HTTP transport for the graph-execution evidence lane. It is deliberately its own client with its own
/// routes: it never shares the fleet heartbeat channel and never carries an artifact payload, so an acknowledgement
/// here says only that the receiver durably stored evidence.
/// </summary>
internal sealed class CameraAgentExecutionEvidenceTransport(
    IDeviceIdentityStore identityStore,
    IDeviceSecretStore secretStore,
    IHttpClientFactory httpClientFactory,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IExecutionEvidenceTransport
{
    internal static readonly Uri NegotiateRoute = new("/api/device/execution-evidence/negotiate", UriKind.Relative);
    internal static readonly Uri SubmitRoute = new("/api/device/execution-evidence", UriKind.Relative);
    internal const string SubmitMediaType = ExecutionEvidenceBatchCodec.MediaType;

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
        => options.Value.CentralIntegration.Mode != CentralIntegrationMode.Disabled &&
            options.Value.ExecutionEvidenceExport.Enabled &&
            await GetEndpointAsync(cancellationToken).ConfigureAwait(false) is not null;

    public async ValueTask<ExecutionEvidenceNegotiationTransportResult> NegotiateAsync(
        ExecutionEvidenceNegotiationRequestV1 request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = await GetEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return new(
                ExecutionEvidenceTransportDisposition.AuthenticationBlocked,
                ExecutionEvidenceExportReasonCodes.TransportUnconfigured,
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        using var message = CreateRequest(
            NegotiateRoute, endpoint.Value, GraphExecutionEvidenceJson.Serialize(request), "application/json");
        try
        {
            using var timeout = CreateRequestTimeout(cancellationToken);
            using var response = await SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ToFailure(response);
                return new(failure.Disposition, failure.ReasonCode, RetryAfter: failure.RetryAfter);
            }
            var payload = await ReadBoundedAsync(
                response.Content, GraphExecutionEvidenceLimits.MaximumNegotiationBytes, timeout.Token)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return new(
                    ExecutionEvidenceTransportDisposition.Retry, GraphExecutionEvidenceReasonCodes.PayloadTooLarge);
            }
            var parsed = GraphExecutionEvidenceJson.ParseNegotiationResponse(payload);
            return parsed.Value is null
                ? new(
                    ExecutionEvidenceTransportDisposition.Retry,
                    parsed.Validation.ReasonCode ?? GraphExecutionEvidenceReasonCodes.InvalidNegotiation)
                : new(ExecutionEvidenceTransportDisposition.Completed, "completed", parsed.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ExecutionEvidenceTransportDisposition.Retry, "request-timeout");
        }
        catch (HttpRequestException)
        {
            return new(
                ExecutionEvidenceTransportDisposition.Retry,
                ExecutionEvidenceExportReasonCodes.TransportUnavailable);
        }
    }

    public async ValueTask<ExecutionEvidenceSubmitTransportResult> SubmitAsync(
        string originIdentitySha256,
        IReadOnlyList<byte[]> envelopes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originIdentitySha256);
        ArgumentNullException.ThrowIfNull(envelopes);
        var endpoint = await GetEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return new(
                ExecutionEvidenceTransportDisposition.AuthenticationBlocked,
                ExecutionEvidenceExportReasonCodes.TransportUnconfigured,
                RetryAfter: TimeSpan.FromMinutes(5));
        }
        using var message = CreateRequest(
            SubmitRoute, endpoint.Value, ExecutionEvidenceBatchCodec.Encode(envelopes), SubmitMediaType);
        message.Headers.TryAddWithoutValidation("X-HVO-Evidence-Origin", originIdentitySha256);
        try
        {
            using var timeout = CreateRequestTimeout(cancellationToken);
            using var response = await SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ToFailure(response);
                return new(failure.Disposition, failure.ReasonCode, RetryAfter: failure.RetryAfter);
            }
            var payload = await ReadBoundedAsync(
                response.Content, GraphExecutionEvidenceLimits.MaximumFeedbackBytes, timeout.Token)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return new(
                    ExecutionEvidenceTransportDisposition.Retry, GraphExecutionEvidenceReasonCodes.PayloadTooLarge);
            }
            var parsed = GraphExecutionEvidenceJson.ParseFeedback(payload);
            return parsed.Value is null
                ? new(
                    ExecutionEvidenceTransportDisposition.Retry,
                    parsed.Validation.ReasonCode ?? GraphExecutionEvidenceReasonCodes.InvalidFact)
                : new(ExecutionEvidenceTransportDisposition.Completed, "completed", parsed.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ExecutionEvidenceTransportDisposition.Retry, "request-timeout");
        }
        catch (HttpRequestException)
        {
            return new(
                ExecutionEvidenceTransportDisposition.Retry,
                ExecutionEvidenceExportReasonCodes.TransportUnavailable);
        }
    }

    private async ValueTask<(string DeviceId, string DeviceKey)?> GetEndpointAsync(CancellationToken cancellationToken)
    {
        var secrets = await secretStore.GetAsync(cancellationToken).ConfigureAwait(false);
        if (secrets is null || secrets.DevicePublicId == Guid.Empty || string.IsNullOrWhiteSpace(secrets.DeviceKey))
        {
            return null;
        }
        var identity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(identity.DeviceId) ? null : (identity.DeviceId, secrets.DeviceKey);
    }

    private static HttpRequestMessage CreateRequest(
        Uri route,
        (string DeviceId, string DeviceKey) endpoint,
        byte[] payload,
        string mediaType)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
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
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ExecutionEvidenceExport.RequestTimeoutSeconds));
        return timeout;
    }

    private (ExecutionEvidenceTransportDisposition Disposition, string ReasonCode, TimeSpan? RetryAfter) ToFailure(
        HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } date && date > timeProvider.GetUtcNow()
                ? date - timeProvider.GetUtcNow()
                : null);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (ExecutionEvidenceTransportDisposition.AuthenticationBlocked,
                    ExecutionEvidenceExportReasonCodes.AuthenticationBlocked, TimeSpan.FromMinutes(5)),
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound or
                HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType =>
                (ExecutionEvidenceTransportDisposition.Rejected, $"http-{(int)response.StatusCode}", retryAfter),
            _ => (ExecutionEvidenceTransportDisposition.Retry, $"http-{(int)response.StatusCode}", retryAfter)
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
}
