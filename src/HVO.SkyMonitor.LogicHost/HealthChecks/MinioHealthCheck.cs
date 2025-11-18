using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Validates connectivity to the configured MinIO endpoint.
/// </summary>
internal sealed class MinioHealthCheck : IHealthCheck
{
    private readonly MinioOptions _options;
    private readonly HttpClient _httpClient;

    public MinioHealthCheck(IOptions<MinioOptions> options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options.Value;
        _httpClient = httpClient;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return HealthCheckResult.Unhealthy("MinIO endpoint is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.AccessKey) || string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            return HealthCheckResult.Unhealthy("MinIO credentials are not configured.");
        }

        try
        {
            var scheme = _options.UseSsl ? "https" : "http";
            var uriBuilder = new UriBuilder(scheme, _options.Endpoint, _options.Port, "/minio/health/live");

            using var response = await _httpClient.GetAsync(uriBuilder.Uri, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("MinIO endpoint responded to health probe.");
            }

            return HealthCheckResult.Unhealthy($"MinIO health probe returned {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Unhealthy("Unable to reach MinIO endpoint.", ex);
        }
        catch (TaskCanceledException ex)
        {
            return HealthCheckResult.Unhealthy("MinIO health probe timed out.", ex);
        }
    }
}
