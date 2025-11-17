using System.Diagnostics;
using System.Net;
using System.Net.Http;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>
/// Verifies that the central Logic Host is reachable since our auth stack depends on it.
/// </summary>
public sealed class LogicHostHealthCheck(
    IHttpClientFactory httpClientFactory,
    ILogger<LogicHostHealthCheck> logger) : IHealthCheck
{
    private const string AlivePath = "/alive";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient(SkyMonitorClientOptions.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, AlivePath);
            request.Headers.TryAddWithoutValidation(CentralIdentityDelegatingHandler.SkipAuthHeader, "true");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var description = $"Logic Host responded {(int)response.StatusCode} in {stopwatch.ElapsedMilliseconds} ms";
                logger.LogDebug("Logic Host health check succeeded with status {StatusCode}", response.StatusCode);
                return HealthCheckResult.Healthy(description);
            }

            var unhealthyDescription = $"Logic Host returned status {(int)response.StatusCode}";
            logger.LogWarning("Logic Host health check failed with status {StatusCode}", response.StatusCode);
            return HealthCheckResult.Unhealthy(unhealthyDescription);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "Logic Host health check encountered an HTTP error");
            return HealthCheckResult.Unhealthy("Unable to reach Logic Host", ex);
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            logger.LogWarning(ex, "Logic Host health check timed out");
            return HealthCheckResult.Unhealthy("Logic Host request timed out", ex);
        }
    }
}
