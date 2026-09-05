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
    LocalProcessElasticRunnerProvider localProcessProvider,
    ElasticProviderTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<ElasticRunnerAutoscaler> logger) : BackgroundService
{
    private bool _adopted;
    private static readonly string HostName = Environment.MachineName;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        telemetry.MarkStarted(timeProvider.GetUtcNow());
        if (!options.Value.Enabled)
        {
            Log.Disabled(logger);
            await RetireInheritedInstancesAsync(stoppingToken).ConfigureAwait(false);
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
                telemetry.RecordSampleFailure(provider.Name, timeProvider.GetUtcNow(), exception.Message);
                Log.SampleFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// A host restarted with provisioning disabled still owns the instances a previous, enabled host process
    /// launched: those recorded as live and still running are drained and stopped so the feature is really off.
    /// </summary>
    internal async Task<int> RetireInheritedInstancesAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = timeProvider.GetUtcNow();
        var live = new[] { nameof(ElasticRunnerInstanceState.Starting), nameof(ElasticRunnerInstanceState.Running), nameof(ElasticRunnerInstanceState.Stopping) };
        // Only instances this host launched are reconciled: another replica's rows are its own to retire.
        var rows = await dbContext.CentralElasticRunnerInstances
            .Where(instance => instance.HostName == HostName && live.Contains(instance.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var alive = rows.Where(row => row.Provider == LocalProcessElasticRunnerProvider.ProviderName && row.ProcessId is { } processId
                && localProcessProvider.TryAdopt(row.InstanceId, row.RunnerId, processId, row.StartedAtUtc))
            .ToList();
        // Every drain request goes out first so no inherited instance keeps claiming while another drains.
        await localProcessProvider.RetireAsync(alive.Select(row => row.InstanceId).ToArray(), options.Value.RetireGrace, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var wasAlive = alive.Contains(row);
            await StopAsync(dbContext, row, now, wasAlive ? ElasticRunnerInstanceState.Stopped : ElasticRunnerInstanceState.Orphaned, "provisioning-disabled", cancellationToken).ConfigureAwait(false);
            Log.InheritedRetired(logger, row.InstanceId, wasAlive);
        }
        return alive.Count;
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
            .Where(instance => instance.Provider == provider.Name && instance.HostName == HostName && live.Contains(instance.State))
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
        // Provider instances without a durable row (a launch whose record failed to persist) are retired so they
        // never run unaccounted; the next provisioning starts from a recorded intent.
        var recorded = rows.Select(row => row.InstanceId).ToHashSet(StringComparer.Ordinal);
        foreach (var unrecorded in alive.Keys.Where(instanceId => !recorded.Contains(instanceId)).ToArray())
        {
            await provider.RetireAsync(unrecorded, settings.RetireGrace, cancellationToken).ConfigureAwait(false);
            alive.Remove(unrecorded);
            telemetry.RecordOrphanCleaned(provider.Name);
            orphans++;
        }
        var inFlight = 0;
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
                inFlight += registration.MaxConcurrency - registration.AvailableSlots;
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
        // Only backlog the instances could claim counts: the same pool predicate as the claim (a reserved pool
        // serves its own pool and shared work; unpooled instances serve only unpooled observatories).
        var entitlements = entitlementOptions.Value is { Enabled: true } enabled ? enabled : null;
        backlogRows = backlogRows.Where(row => IsPoolEligible(settings.Pool, entitlements?.ResolvePool(row.ObservatoryId))).ToList();
        var backlog = backlogRows.Sum(row => row.Count);
        var oldestAge = backlogRows.Count == 0 ? TimeSpan.Zero : now - backlogRows.Min(row => row.Oldest!.Value);
        int? entitled = null;
        if (entitlements is not null && backlogRows.Count != 0)
        {
            // Demand includes in-flight work, so the bound counts it too: occupied slots already hold entitlement
            // of observatories that may not be backlogged, and backlogged observatories keep their own limits.
            var limits = backlogRows.Select(row => entitlements.ResolveActiveJobs(row.ObservatoryId)).ToArray();
            entitled = limits.Any(limit => limit <= 0) ? null : limits.Sum() + inFlight;
        }
        var minutesToday = await InstanceMinutesTodayAsync(dbContext, now, cancellationToken).ConfigureAwait(false);
        var input = new ElasticScalingInput(backlog, oldestAge, running, starting, idle.Count(item => item.IdleFor >= settings.ScaleToZeroAfter),
            idle.Count == 0 ? TimeSpan.Zero : idle[0].IdleFor, entitled, minutesToday, inFlight);
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
        var retired = 0;
        var idleRetired = 0;
        // Idle instances first (longest idle first), then registering ones when the decision covers them (daily limit).
        var retiring = idle.Select(item => (item.Row, item.IdleFor))
            .Concat(current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Starting)).Select(row => (Row: row, IdleFor: TimeSpan.Zero)))
            .Take(decision.Retire)
            .ToList();
        await provider.RetireAsync(retiring.Select(item => item.Row.InstanceId).ToArray(), settings.RetireGrace, cancellationToken).ConfigureAwait(false);
        foreach (var item in retiring)
        {
            await StopAsync(dbContext, item.Row, now, ElasticRunnerInstanceState.Stopped, decision.Reason, cancellationToken).ConfigureAwait(false);
            telemetry.RecordRetirement(provider.Name, decision.Reason);
            retired++;
            if (item.Row.State == nameof(ElasticRunnerInstanceState.Stopped) && item.IdleFor >= settings.ScaleToZeroAfter)
            {
                idleRetired++;
            }
        }
        var startingRetired = retiring.Count(item => item.IdleFor == TimeSpan.Zero && item.Row.RegisteredAtUtc is null);
        telemetry.UpdateSnapshot(new ElasticProviderSnapshot(
            now, provider.Name, Math.Max(0, starting + decision.Provision - startingRetired), Math.Max(0, running - (retired - startingRetired)),
            Math.Max(0, input.Idle - idleRetired), backlog, (long)oldestAge.TotalSeconds, minutesToday, decision.Reason, orphans));
        return decision;
    }

    /// <summary>The claim's pool predicate: reserved instances serve their pool and shared work, unpooled instances only shared work.</summary>
    internal static bool IsPoolEligible(string? instancePool, string? observatoryPool)
        => instancePool is null ? observatoryPool is null : observatoryPool is null || string.Equals(observatoryPool, instancePool, StringComparison.Ordinal);

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
        // Instances filling the warm minimum keep no self-termination timeout; excess capacity keeps the safety net.
        var liveCount = await dbContext.CentralElasticRunnerInstances.CountAsync(instance => instance.Provider == provider.Name && instance.HostName == HostName
            && (instance.State == nameof(ElasticRunnerInstanceState.Starting) || instance.State == nameof(ElasticRunnerInstanceState.Running)), cancellationToken).ConfigureAwait(false);
        var request = new ElasticRunnerProvisionRequest(
            instanceId, runnerId, CentralElasticProviderOptions.EligibleJobClasses, labels, settings.MaxConcurrencyPerInstance, settings.Pool,
            KeepWarm: liveCount < settings.MinWarmInstances);
        ElasticWorkloadClass.EnsureEligible(request.JobClasses);
        // The durable intent is written before the launch so a launched process is always accounted for; if the
        // launch fails the intent is closed, and if recording the process fails the process is retired.
        var row = new CentralElasticRunnerInstance
        {
            Provider = provider.Name,
            HostName = HostName,
            InstanceId = instanceId,
            RunnerId = runnerId,
            ProcessArchitecture = provider.Capabilities.ProcessArchitecture,
            RuntimeImage = provider.Capabilities.RuntimeImage,
            State = nameof(ElasticRunnerInstanceState.Starting),
            StartedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.CentralElasticRunnerInstances.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ElasticRunnerInstance instance;
        try
        {
            instance = await provider.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StopAsync(dbContext, row, now, ElasticRunnerInstanceState.Stopped, "provision-failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        row.ProcessId = instance.ProcessId;
        row.StartedAtUtc = instance.StartedAtUtc;
        row.UpdatedAtUtc = now;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await provider.RetireAsync(instanceId, settings.RetireGrace, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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

        [LoggerMessage(2237, LogLevel.Information, "Elastic runner instance inherited while provisioning is disabled: Instance={Instance}, Alive={Alive}")]
        public static partial void InheritedRetired(ILogger logger, string instance, bool alive);
    }
}
