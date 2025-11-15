using System.Net.Http.Headers;
using HVO.SkyMonitor.CameraAgent.Authentication;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Http;

/// <summary>
/// Delegating handler that attaches either a bearer token or API key supplied by the central identity service.
/// </summary>
public sealed class CentralIdentityDelegatingHandler(
    ICentralAuthenticationService authenticationService,
    ILogger<CentralIdentityDelegatingHandler> logger) : DelegatingHandler
{
    private const string ApiKeyHeader = "X-API-Key";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(ApiKeyHeader) && request.Headers.Authorization is null)
        {
            var token = await authenticationService.GetAccessTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                logger.LogTrace("Attached bearer token for {RequestUri}", request.RequestUri);
            }
            else
            {
                var apiKey = authenticationService.GetApiKey();
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
                    logger.LogTrace("Attached API key for {RequestUri}", request.RequestUri);
                }
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}