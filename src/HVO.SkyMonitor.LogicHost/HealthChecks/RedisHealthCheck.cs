using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Validates connectivity to the configured Redis endpoint.
/// </summary>
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly string? _configuration;

    public RedisHealthCheck(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration.GetValue<string>("Redis:Configuration");
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_configuration))
        {
            return HealthCheckResult.Unhealthy("Redis connection string is not configured.");
        }

        try
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(_configuration).ConfigureAwait(false);
            var database = connection.GetDatabase();
            await database.PingAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy("Redis responded to PING.");
        }
        catch (RedisConnectionException ex)
        {
            return HealthCheckResult.Unhealthy("Redis connection failed.", ex);
        }
        catch (RedisTimeoutException ex)
        {
            return HealthCheckResult.Unhealthy("Redis timed out while responding to PING.", ex);
        }
    }
}
