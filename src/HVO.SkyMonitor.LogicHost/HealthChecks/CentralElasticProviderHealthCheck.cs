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
    ElasticProviderTelemetry telemetry,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    /// <summary>Consecutive sampling failures after which enabled provisioning is reported unhealthy.</summary>
    internal const int UnhealthyAfterConsecutiveFailures = 3;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Elastic provisioning is disabled.", new Dictionary<string, object> { ["enabled"] = false }));
        }
        var snapshot = telemetry.Snapshot(provider.Name);
        var failures = telemetry.SampleFailures(provider.Name);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var data = new Dictionary<string, object>
        {
            ["consecutiveSampleFailures"] = failures.Consecutive,
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
        if (failures.Consecutive >= UnhealthyAfterConsecutiveFailures)
        {
            // The exception is logged privately (event 2234); the anonymous health response stays generic.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The elastic autoscaler failed {failures.Consecutive} consecutive samples; enabled capacity is unavailable (see log event 2234).", data: data));
        }
        if (snapshot is null)
        {
            var grace = settings.SampleInterval * 3;
            var startedAtUtc = telemetry.StartedAtUtc;
            return Task.FromResult(startedAtUtc is { } started && now - started > grace
                ? HealthCheckResult.Degraded("Elastic provisioning is enabled but no sample has completed since startup.", data: data)
                : HealthCheckResult.Healthy("Elastic provisioning is enabled; no sample yet.", data));
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
        if (snapshot.LastDecision == ElasticScalingPolicy.ReasonInstanceUndescribed)
        {
            return Task.FromResult(HealthCheckResult.Degraded("The provider cannot describe an instance (the configured runner failed its capability probe); nothing is provisioned until a probe succeeds.", data: data));
        }
        if (snapshot.OrphansCleaned > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Elastic runner instances were cleaned up as orphans in the last sample.", data: data));
        }
        return Task.FromResult(HealthCheckResult.Healthy("Elastic provisioning is within policy.", data));
    }
}
