using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services.Elastic;

/// <summary>Provider used when elastic provisioning is disabled; every call is a programming error and throws.</summary>
internal sealed class NullElasticRunnerProvider : IElasticRunnerProvider
{
    public string Name => "none";

    public ElasticProviderCapabilities Capabilities { get; } = new("none", string.Empty, string.Empty, false, []);

    public TimeSpan EstimateStartup() => TimeSpan.Zero;

    public Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Elastic provisioning is disabled.");

    public Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Elastic provisioning is disabled.");

    public Task<IReadOnlyList<ElasticRunnerInstance>> ListAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Elastic provisioning is disabled.");
}

/// <summary>
/// Host-side autoscaler over <see cref="IElasticRunnerProvider"/> (#430). Each sample: reconciles recorded
/// instances with the provider and the runner registry (orphans, registration timeouts, measured cold starts,
/// busy tracking), measures provider-eligible runner-placed backlog and the entitlement bound, applies
/// <see cref="ElasticScalingPolicy"/>, and provisions or retires. When <c>ElasticProviders</c> is disabled the
/// service never touches the provider.
/// </summary>
internal sealed partial class ElasticRunnerAutoscaler(
    IServiceScopeFactory scopeFactory,
    IOptions<CentralElasticProviderOptions> options,
    IOptions<CentralProcessingRunnerOptions> runnerOptions,
    IOptions<CentralProcessingEntitlementOptions> entitlementOptions,
    IElasticRunnerProvider provider,
    ElasticProviderTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<ElasticRunnerAutoscaler> logger) : BackgroundService
{
    private bool _adopted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            Log.Disabled(logger);
            return;
        }
        using var timer = new PeriodicTimer(options.Value.SampleInterval, timeProvider);
        do
        {
            try
            {
                await SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.SampleFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>One autoscaling pass; exposed for tests and for the evidence harness.</summary>
    internal async Task<ElasticScalingDecision> SampleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>();
        await registry.RefreshStatusesAsync(cancellationToken).ConfigureAwait(false);

        var live = new[] { nameof(ElasticRunnerInstanceState.Starting), nameof(ElasticRunnerInstanceState.Running), nameof(ElasticRunnerInstanceState.Stopping) };
        var rows = await dbContext.CentralElasticRunnerInstances
            .Where(instance => instance.Provider == provider.Name && live.Contains(instance.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (!_adopted)
        {
            AdoptRecorded(rows);
            _adopted = true;
        }
        var alive = (await provider.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(instance => instance.InstanceId, StringComparer.Ordinal);
        var runnerIds = rows.Select(row => row.RunnerId).ToArray();
        var registrations = await dbContext.CentralProcessingRunners.AsNoTracking()
            .Where(runner => runnerIds.Contains(runner.RunnerId))
            .Select(runner => new { runner.RunnerId, runner.Status, runner.RegisteredAtUtc, runner.AvailableSlots, runner.MaxConcurrency })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var registered = registrations.ToDictionary(runner => runner.RunnerId, StringComparer.Ordinal);
        var orphans = 0;
        foreach (var row in rows)
        {
            registered.TryGetValue(row.RunnerId, out var registration);
            if (!alive.ContainsKey(row.InstanceId))
            {
                // The provider no longer has the instance: it exited on its own (idle shutdown, crash, host restart).
                await StopAsync(dbContext, row, now, ElasticRunnerInstanceState.Orphaned, "process-exited", cancellationToken).ConfigureAwait(false);
                telemetry.RecordOrphanCleaned(provider.Name);
                orphans++;
                continue;
            }
            if (row.State == nameof(ElasticRunnerInstanceState.Starting))
            {
                if (registration is not null && registration.Status != CentralProcessingRunnerStatus.Retired)
                {
                    row.State = nameof(ElasticRunnerInstanceState.Running);
                    row.RegisteredAtUtc = registration.RegisteredAtUtc;
                    var coldStart = registration.RegisteredAtUtc - row.StartedAtUtc;
                    row.ColdStartMilliseconds = (int)Math.Max(0, coldStart.TotalMilliseconds);
                    row.UpdatedAtUtc = now;
                    telemetry.RecordColdStart(provider.Name, coldStart);
                    (provider as LocalProcessElasticRunnerProvider)?.RecordMeasuredStartup(coldStart);
                    Log.Registered(logger, row.InstanceId, row.RunnerId, row.ColdStartMilliseconds.Value);
                }
                else if (now - row.StartedAtUtc > settings.RegistrationTimeout)
                {
                    await provider.RetireAsync(row.InstanceId, settings.RetireGrace, cancellationToken).ConfigureAwait(false);
                    await StopAsync(dbContext, row, now, ElasticRunnerInstanceState.Stopped, "registration-timeout", cancellationToken).ConfigureAwait(false);
                    telemetry.RecordRetirement(provider.Name, "registration-timeout");
                    orphans++;
                }
                continue;
            }
            if (registration is null || registration.Status != CentralProcessingRunnerStatus.Active)
            {
                // Registry lost the runner (stale or retired) while the process is alive: retire the instance too.
                await provider.RetireAsync(row.InstanceId, settings.RetireGrace, cancellationToken).ConfigureAwait(false);
                await StopAsync(dbContext, row, now, ElasticRunnerInstanceState.Stopped, "registry-" + (registration?.Status.ToString().ToLowerInvariant() ?? "missing"), cancellationToken).ConfigureAwait(false);
                telemetry.RecordRetirement(provider.Name, "registry-lost");
                orphans++;
                continue;
            }
            if (registration.AvailableSlots < registration.MaxConcurrency)
            {
                row.LastBusyAtUtc = now;
                row.UpdatedAtUtc = now;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var current = rows.Where(row => row.State is nameof(ElasticRunnerInstanceState.Starting) or nameof(ElasticRunnerInstanceState.Running)).ToList();
        var starting = current.Count(row => row.State == nameof(ElasticRunnerInstanceState.Starting));
        var running = current.Count(row => row.State == nameof(ElasticRunnerInstanceState.Running));
        var idle = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Running))
            .Select(row => new { Row = row, IdleFor = now - (row.LastBusyAtUtc ?? row.RegisteredAtUtc ?? row.StartedAtUtc) })
            .OrderByDescending(item => item.IdleFor)
            .ToList();

        var placed = runnerOptions.Value.ResolveRunnerPlacedRecipes();
        var backlogRows = placed.Count == 0
            ? []
            : await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => (job.Status == CentralDerivativeJobStatus.Pending || job.Status == CentralDerivativeJobStatus.RetryableFailure)
                    && job.AvailableAtUtc != null && job.AvailableAtUtc <= now && placed.Contains(job.RecipeName))
                .GroupBy(job => job.SourceArtifact!.Frame!.ObservatoryId)
                .Select(group => new { ObservatoryId = group.Key, Count = group.Count(), Oldest = group.Min(job => job.AvailableAtUtc) })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        var backlog = backlogRows.Sum(row => row.Count);
        var oldestAge = backlogRows.Count == 0 ? TimeSpan.Zero : now - backlogRows.Min(row => row.Oldest!.Value);
        int? entitled = null;
        if (entitlementOptions.Value is { Enabled: true } entitlements && backlogRows.Count != 0)
        {
            var limits = backlogRows.Select(row => entitlements.ResolveActiveJobs(row.ObservatoryId)).ToArray();
            entitled = limits.Any(limit => limit <= 0) ? null : limits.Sum();
        }
        var minutesToday = await InstanceMinutesTodayAsync(dbContext, now, cancellationToken).ConfigureAwait(false);
        var input = new ElasticScalingInput(backlog, oldestAge, running, starting, idle.Count(item => item.IdleFor >= settings.ScaleToZeroAfter),
            idle.Count == 0 ? TimeSpan.Zero : idle[0].IdleFor, entitled, minutesToday);
        var decision = ElasticScalingPolicy.Decide(settings, input, provider.EstimateStartup());
        if (decision.Provision == 0 && decision.Retire == 0 && decision.Reason is not "steady" && backlog > 0)
        {
            telemetry.RecordRejectedPlacement(provider.Name, decision.Reason);
            Log.PlacementRejected(logger, decision.Reason, backlog, (long)oldestAge.TotalSeconds);
        }
        for (var i = 0; i < decision.Provision; i++)
        {
            await ProvisionAsync(dbContext, settings, now, cancellationToken).ConfigureAwait(false);
        }
        foreach (var item in idle.Take(decision.Retire))
        {
            await provider.RetireAsync(item.Row.InstanceId, settings.RetireGrace, cancellationToken).ConfigureAwait(false);
            await StopAsync(dbContext, item.Row, now, ElasticRunnerInstanceState.Stopped, ElasticScalingPolicy.ReasonIdle, cancellationToken).ConfigureAwait(false);
            telemetry.RecordRetirement(provider.Name, ElasticScalingPolicy.ReasonIdle);
        }
        telemetry.UpdateSnapshot(new ElasticProviderSnapshot(
            now, provider.Name, starting + decision.Provision, running - decision.Retire, input.Idle, backlog,
            (long)oldestAge.TotalSeconds, minutesToday, decision.Reason, orphans));
        return decision;
    }

    private void AdoptRecorded(IEnumerable<CentralElasticRunnerInstance> rows)
    {
        if (provider is not LocalProcessElasticRunnerProvider local)
        {
            return;
        }
        foreach (var row in rows.Where(row => row.ProcessId is { } && row.State != nameof(ElasticRunnerInstanceState.Stopping)))
        {
            local.TryAdopt(row.InstanceId, row.RunnerId, row.ProcessId!.Value, row.StartedAtUtc);
        }
    }

    private async Task ProvisionAsync(ApplicationDbContext dbContext, CentralElasticProviderOptions settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var instanceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var runnerId = $"elastic-{provider.Name}-{instanceId}";
        var labels = new List<string> { $"provider:{provider.Name}", $"elastic-instance:{instanceId}" };
        labels.AddRange(provider.Capabilities.Labels.Where(label => !labels.Contains(label, StringComparer.Ordinal)));
        labels.AddRange(settings.Labels);
        if (settings.Pool is { } pool)
        {
            labels.Add($"pool:{pool}");
            labels.Add("pool-mode:reserved");
        }
        var request = new ElasticRunnerProvisionRequest(
            instanceId, runnerId, CentralElasticProviderOptions.EligibleJobClasses, labels, settings.MaxConcurrencyPerInstance, settings.Pool);
        ElasticWorkloadClass.EnsureEligible(request.JobClasses);
        var instance = await provider.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        dbContext.CentralElasticRunnerInstances.Add(new CentralElasticRunnerInstance
        {
            Provider = provider.Name,
            InstanceId = instanceId,
            RunnerId = runnerId,
            ProcessId = instance.ProcessId,
            ProcessArchitecture = provider.Capabilities.ProcessArchitecture,
            RuntimeImage = provider.Capabilities.RuntimeImage,
            State = nameof(ElasticRunnerInstanceState.Starting),
            StartedAtUtc = instance.StartedAtUtc,
            UpdatedAtUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordProvision(provider.Name);
    }

    private static async Task StopAsync(
        ApplicationDbContext dbContext, CentralElasticRunnerInstance row, DateTimeOffset now, ElasticRunnerInstanceState state, string reason, CancellationToken cancellationToken)
    {
        row.State = state.ToString();
        row.Reason = reason;
        row.StoppedAtUtc = now;
        row.UpdatedAtUtc = now;
        await dbContext.CentralProcessingRunners
            .Where(runner => runner.RunnerId == row.RunnerId && runner.Status != CentralProcessingRunnerStatus.Retired)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, now)
                .SetProperty(runner => runner.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Instance minutes consumed today (UTC), the daily cost/resource accounting the policy is bounded by.</summary>
    internal static async Task<int> InstanceMinutesTodayAsync(ApplicationDbContext dbContext, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var rows = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .Where(instance => instance.StoppedAtUtc == null || instance.StoppedAtUtc >= dayStart)
            .Select(instance => new { instance.StartedAtUtc, instance.StoppedAtUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        double minutes = 0;
        foreach (var row in rows)
        {
            var from = row.StartedAtUtc > dayStart ? row.StartedAtUtc : dayStart;
            var to = row.StoppedAtUtc ?? now;
            if (to > from)
            {
                minutes += (to - from).TotalMinutes;
            }
        }
        return (int)Math.Ceiling(minutes);
    }

    private static partial class Log
    {
        [LoggerMessage(2233, LogLevel.Information, "Elastic provisioning is disabled; no provider adapter will be called.")]
        public static partial void Disabled(ILogger logger);

        [LoggerMessage(2234, LogLevel.Warning, "Elastic autoscaler sample failed.")]
        public static partial void SampleFailed(ILogger logger, Exception exception);

        [LoggerMessage(2235, LogLevel.Information, "Elastic runner instance registered: Instance={Instance}, Runner={Runner}, ColdStartMs={ColdStartMs}")]
        public static partial void Registered(ILogger logger, string instance, string runner, int coldStartMs);

        [LoggerMessage(2236, LogLevel.Information, "Elastic placement rejected, work retained locally: Reason={Reason}, Backlog={Backlog}, OldestBacklogSeconds={OldestBacklogSeconds}")]
        public static partial void PlacementRejected(ILogger logger, string reason, int backlog, long oldestBacklogSeconds);
    }
}
