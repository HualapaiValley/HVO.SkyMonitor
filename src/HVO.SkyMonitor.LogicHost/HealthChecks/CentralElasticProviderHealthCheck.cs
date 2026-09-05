using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services.Elastic;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Elastic provisioning signal (#430): disabled is healthy; degraded when provider-eligible backlog cannot be placed
/// (daily limit, cold start beyond the queue deadline) or instances had to be cleaned up as orphans.
/// </summary>
internal sealed class CentralElasticProviderHealthCheck(
    IOptions<CentralElasticProviderOptions> options,
    IElasticRunnerProvider provider,
    ElasticProviderTelemetry telemetry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Elastic provisioning is disabled.", new Dictionary<string, object> { ["enabled"] = false }));
        }
        var snapshot = telemetry.Snapshot(provider.Name);
        var data = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["provider"] = provider.Name,
            ["maxInstances"] = settings.MaxInstances,
            ["starting"] = snapshot?.Starting ?? 0,
            ["running"] = snapshot?.Running ?? 0,
            ["idle"] = snapshot?.Idle ?? 0,
            ["backlog"] = snapshot?.Backlog ?? 0,
            ["oldestBacklogSeconds"] = snapshot?.OldestBacklogAgeSeconds ?? 0,
            ["instanceMinutesToday"] = snapshot?.InstanceMinutesToday ?? 0,
            ["lastDecision"] = snapshot?.LastDecision ?? "none",
            ["orphansCleanedLastSample"] = snapshot?.OrphansCleaned ?? 0
        };
        if (snapshot is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Elastic provisioning is enabled; no sample yet.", data));
        }
        if (snapshot.Backlog > 0 && snapshot.LastDecision == ElasticScalingPolicy.ReasonDailyLimit)
        {
            return Task.FromResult(HealthCheckResult.Degraded("The daily instance-minute limit is reached; provider-eligible work is retained locally.", data: data));
        }
        if (snapshot.Backlog > 0 && snapshot.LastDecision == ElasticScalingPolicy.ReasonColdStartExceedsDeadline
            && snapshot.OldestBacklogAgeSeconds > settings.QueueDeadline.TotalSeconds)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Provider startup cannot meet the queue deadline; work is retained locally past the deadline.", data: data));
        }
        if (snapshot.OrphansCleaned > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Elastic runner instances were cleaned up as orphans in the last sample.", data: data));
        }
        return Task.FromResult(HealthCheckResult.Healthy("Elastic provisioning is within policy.", data));
    }
}
