using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using Microsoft.Data.SqlClient;
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
        foreach (var row in rows)
        {
            row.OwnerHeartbeatAtUtc = now;
        }
        // Rows whose owning host stopped reconciling them (replica removed, renamed, or dead) are reaped so they stop
        // holding capacity and accruing minutes; their registrations are retired.
        var ownerStaleBefore = now - OwnerStaleAfter(settings);
        var stale = await dbContext.CentralElasticRunnerInstances
            .Where(instance => instance.HostName != HostName && live.Contains(instance.State) && instance.OwnerHeartbeatAtUtc < ownerStaleBefore)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in stale)
        {
            // Abandoned rather than orphaned: the registry denies any re-registration of this runner id, so a process
            // that outlived its host stops on its own instead of reviving the retired registration.
            await StopAsync(dbContext, row, now, ElasticRunnerInstanceState.Abandoned, "owner-lost", cancellationToken).ConfigureAwait(false);
            telemetry.RecordOrphanCleaned(provider.Name);
            Log.OwnerLost(logger, row.InstanceId, row.HostName);
        }
        if (!_adopted)
        {
            AdoptRecorded(rows);
            _adopted = true;
        }
        var alive = (await provider.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(instance => instance.InstanceId, StringComparer.Ordinal);
        // Occupied slots are deployment-wide: every replica's live instances contribute to in-flight demand.
        var liveRunnerIds = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .Where(instance => instance.Provider == provider.Name && live.Contains(instance.State))
            .Select(instance => instance.RunnerId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var registrations = await dbContext.CentralProcessingRunners.AsNoTracking()
            .Where(runner => liveRunnerIds.Contains(runner.RunnerId))
            .Select(runner => new { runner.RunnerId, runner.Status, runner.RegisteredAtUtc, runner.AvailableSlots, runner.MaxConcurrency })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var registered = registrations.ToDictionary(runner => runner.RunnerId, StringComparer.Ordinal);
        var localRunnerIds = rows.Select(row => row.RunnerId).ToHashSet(StringComparer.Ordinal);
        var inFlight = registrations.Where(runner => !localRunnerIds.Contains(runner.RunnerId) && runner.Status == CentralProcessingRunnerStatus.Active)
            .Sum(runner => Math.Max(0, runner.MaxConcurrency - runner.AvailableSlots));
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
        // Capacity is bounded deployment-wide: every replica's live instances count toward MaxInstances, while only
        // this host's instances are reconciled or retired here.
        var liveStates = new[] { nameof(ElasticRunnerInstanceState.Starting), nameof(ElasticRunnerInstanceState.Running) };
        var otherHosts = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .Where(instance => instance.Provider == provider.Name && instance.HostName != HostName && liveStates.Contains(instance.State))
            .GroupBy(instance => instance.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var starting = current.Count(row => row.State == nameof(ElasticRunnerInstanceState.Starting))
            + otherHosts.Where(item => item.State == nameof(ElasticRunnerInstanceState.Starting)).Sum(item => item.Count);
        var running = current.Count(row => row.State == nameof(ElasticRunnerInstanceState.Running))
            + otherHosts.Where(item => item.State == nameof(ElasticRunnerInstanceState.Running)).Sum(item => item.Count);
        // Idle retirement candidates are the running instances idle beyond the scale-to-zero delay; excess instances
        // (with a safety timeout) retire before instances kept for the warm minimum.
        var idle = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Running))
            .Select(row => new { Row = row, IdleFor = now - (row.LastBusyAtUtc ?? row.RegisteredAtUtc ?? row.StartedAtUtc) })
            .Where(item => item.IdleFor >= settings.ScaleToZeroAfter)
            .OrderBy(item => item.Row.KeepWarm)
            .ThenByDescending(item => item.IdleFor)
            .ToList();
        var warmLive = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .CountAsync(instance => instance.Provider == provider.Name && instance.KeepWarm && liveStates.Contains(instance.State), cancellationToken).ConfigureAwait(false);

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
            // The bound is what the claim could still admit: each backlogged observatory's remaining headroom
            // (limit minus its unexpired leases from any worker) plus the work already executing on our instances.
            var observatoryIds = backlogRows.Select(row => row.ObservatoryId).ToArray();
            var activeByObservatory = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now
                    && observatoryIds.Contains(job.SourceArtifact!.Frame!.ObservatoryId))
                .GroupBy(job => job.SourceArtifact!.Frame!.ObservatoryId)
                .Select(group => new { ObservatoryId = group.Key, Active = group.Count() })
                .ToDictionaryAsync(item => item.ObservatoryId, item => item.Active, cancellationToken).ConfigureAwait(false);
            var limits = backlogRows.Select(row => entitlements.ResolveActiveJobs(row.ObservatoryId)).ToArray();
            entitled = limits.Any(limit => limit <= 0)
                ? null
                : backlogRows.Sum(row => Math.Max(0, entitlements.ResolveActiveJobs(row.ObservatoryId) - (activeByObservatory.TryGetValue(row.ObservatoryId, out var active) ? active : 0))) + inFlight;
        }
        var minutesToday = await InstanceMinutesTodayAsync(dbContext, now, cancellationToken).ConfigureAwait(false);
        var input = new ElasticScalingInput(backlog, oldestAge, running, starting, idle.Count,
            idle.Count == 0 ? TimeSpan.Zero : idle.Max(item => item.IdleFor), entitled, minutesToday, inFlight, warmLive);
        // Deployment-wide decisions are serialized: the decision and its durable intents commit under one
        // application lock so replicas sampling the same backlog cannot both fill the same shortfall.
        await using var scaling = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var lockResult = new SqlParameter("@result", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
        await dbContext.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 5000;",
            [new SqlParameter("@resource", $"hvo-elastic-scaling:{provider.Name}"), lockResult], cancellationToken).ConfigureAwait(false);
        if (lockResult.Value is not int acquired || acquired < 0)
        {
            await scaling.RollbackAsync(cancellationToken).ConfigureAwait(false);
            Log.ScalingLockBusy(logger);
            telemetry.UpdateSnapshot(new ElasticProviderSnapshot(now, provider.Name, starting, running, idle.Count, backlog, (long)oldestAge.TotalSeconds, minutesToday, "scaling-lock-busy", orphans));
            return ElasticScalingDecision.Steady;
        }
        // Re-read the deployment-wide counts under the lock: another replica may have decided since the sample began.
        var lockedOthers = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .Where(instance => instance.Provider == provider.Name && instance.HostName != HostName && liveStates.Contains(instance.State))
            .GroupBy(instance => instance.State).Select(group => new { State = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        starting = current.Count(row => row.State == nameof(ElasticRunnerInstanceState.Starting)) + lockedOthers.Where(item => item.State == nameof(ElasticRunnerInstanceState.Starting)).Sum(item => item.Count);
        running = current.Count(row => row.State == nameof(ElasticRunnerInstanceState.Running)) + lockedOthers.Where(item => item.State == nameof(ElasticRunnerInstanceState.Running)).Sum(item => item.Count);
        warmLive = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .CountAsync(instance => instance.Provider == provider.Name && instance.KeepWarm && liveStates.Contains(instance.State), cancellationToken).ConfigureAwait(false);
        input = input with { Running = running, Starting = starting, WarmInstances = warmLive };
        var decision = ElasticScalingPolicy.Decide(settings, input, provider.EstimateStartup());
        if (decision.Provision == 0 && decision.Retire == 0 && decision.Reason is not "steady" && backlog > 0)
        {
            telemetry.RecordRejectedPlacement(provider.Name, decision.Reason);
            Log.PlacementRejected(logger, decision.Reason, backlog, (long)oldestAge.TotalSeconds);
        }
        // Durable intents and retirement reservations are recorded under the lock; processes launch and drain after
        // it commits, so another replica's decision already sees the reserved capacity change.
        var intents = new List<(CentralElasticRunnerInstance Row, ElasticRunnerProvisionRequest Request)>();
        for (var i = 0; i < decision.Provision; i++)
        {
            intents.Add(CreateIntent(dbContext, settings, now, warmLive + i));
        }
        // Idle scale-down retires only instances idle beyond the delay; a warm-minimum replacement retires one excess
        // instance (idle first); the daily-limit drain covers every active instance (idle, then busy, then registering).
        var idleRows = idle.Select(item => (item.Row, item.IdleFor)).ToList();
        var busyRunning = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Running) && !idleRows.Any(item => item.Row == row)).Select(row => (Row: row, IdleFor: TimeSpan.Zero));
        var startingRows = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Starting)).Select(row => (Row: row, IdleFor: TimeSpan.Zero));
        var retiring = (decision.Reason switch
        {
            ElasticScalingPolicy.ReasonDailyLimit => idleRows.Concat(busyRunning).Concat(startingRows),
            ElasticScalingPolicy.ReasonWarmMinimum => idleRows.Where(item => !item.Row.KeepWarm).Concat(busyRunning.Where(item => !item.Row.KeepWarm)),
            _ => idleRows.AsEnumerable()
        })
            .Take(decision.Retire)
            .ToList();
        foreach (var item in retiring)
        {
            item.Row.State = nameof(ElasticRunnerInstanceState.Stopping);
            item.Row.UpdatedAtUtc = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await scaling.CommitAsync(cancellationToken).ConfigureAwait(false);
        await LaunchAllAsync(dbContext, settings, intents, now, cancellationToken).ConfigureAwait(false);
        var retired = 0;
        var idleRetired = 0;
        await provider.RetireAsync(retiring.Select(item => item.Row.InstanceId).ToArray(), settings.RetireGrace, cancellationToken).ConfigureAwait(false);
        foreach (var item in retiring)
        {
            await StopAsync(dbContext, item.Row, now, ElasticRunnerInstanceState.Stopped, decision.Reason, cancellationToken).ConfigureAwait(false);
            telemetry.RecordRetirement(provider.Name, decision.Reason);
            retired++;
            if (item.IdleFor >= settings.ScaleToZeroAfter)
            {
                idleRetired++;
            }
        }
        var startingRetired = retiring.Count(item => item.Row.RegisteredAtUtc is null);
        telemetry.UpdateSnapshot(new ElasticProviderSnapshot(
            now, provider.Name, Math.Max(0, starting + decision.Provision - startingRetired), Math.Max(0, running - (retired - startingRetired)),
            Math.Max(0, input.Idle - idleRetired), backlog, (long)oldestAge.TotalSeconds, minutesToday, decision.Reason, orphans));
        return decision;
    }

    /// <summary>An owner that has not reconciled a row for this long is considered gone.</summary>
    internal static TimeSpan OwnerStaleAfter(CentralElasticProviderOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var byInterval = settings.SampleInterval * 6;
        return byInterval > TimeSpan.FromMinutes(5) ? byInterval : TimeSpan.FromMinutes(5);
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

    /// <summary>Records the durable intent for one instance (under the scaling lock); the launch follows the commit.</summary>
    private (CentralElasticRunnerInstance Row, ElasticRunnerProvisionRequest Request) CreateIntent(
        ApplicationDbContext dbContext, CentralElasticProviderOptions settings, DateTimeOffset now, int warmSoFar)
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
        // Instances carrying the warm designation never self-terminate; the designation follows the live count of
        // warm instances, so a lost warm instance is replaced by a warm one even while excess capacity runs.
        var request = new ElasticRunnerProvisionRequest(
            instanceId, runnerId, CentralElasticProviderOptions.EligibleJobClasses, labels, settings.MaxConcurrencyPerInstance, settings.Pool,
            KeepWarm: warmSoFar < settings.MinWarmInstances);
        ElasticWorkloadClass.EnsureEligible(request.JobClasses);
        var row = new CentralElasticRunnerInstance
        {
            Provider = provider.Name,
            HostName = HostName,
            InstanceId = instanceId,
            RunnerId = runnerId,
            KeepWarm = request.KeepWarm,
            OwnerHeartbeatAtUtc = now,
            ProcessArchitecture = provider.Capabilities.ProcessArchitecture,
            RuntimeImage = provider.Capabilities.RuntimeImage,
            State = nameof(ElasticRunnerInstanceState.Starting),
            StartedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.CentralElasticRunnerInstances.Add(row);
        return (row, request);
    }

    /// <summary>
    /// Launches every committed intent in order; when a launch throws or the host cancels, the remaining intents (and
    /// the interrupted one) are closed through a non-cancelable path so no replica counts an instance that never ran.
    /// </summary>
    private async Task LaunchAllAsync(
        ApplicationDbContext dbContext, CentralElasticProviderOptions settings,
        List<(CentralElasticRunnerInstance Row, ElasticRunnerProvisionRequest Request)> intents, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var i = 0; i < intents.Count; i++)
        {
            try
            {
                await LaunchAsync(dbContext, settings, intents[i].Row, intents[i].Request, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                foreach (var (row, _) in intents.Skip(i + 1))
                {
                    await StopAsync(dbContext, row, now, ElasticRunnerInstanceState.Stopped, "launch-aborted", CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
        }
    }

    /// <summary>Launches a recorded intent; a failed or cancelled launch closes the intent and a failed record retires the process.</summary>
    private async Task LaunchAsync(
        ApplicationDbContext dbContext, CentralElasticProviderOptions settings, CentralElasticRunnerInstance row, ElasticRunnerProvisionRequest request,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ElasticRunnerInstance instance;
        try
        {
            instance = await provider.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
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
            await provider.RetireAsync(row.InstanceId, settings.RetireGrace, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        telemetry.RecordProvision(provider.Name);
    }

    /// <summary>
    /// Closes an instance row and retires its registration in one transaction, so a runner can never observe the
    /// registration retired while the row still reads live (or the reverse) between the two writes.
    /// </summary>
    private static async Task StopAsync(
        ApplicationDbContext dbContext, CentralElasticRunnerInstance row, DateTimeOffset now, ElasticRunnerInstanceState state, string reason, CancellationToken cancellationToken)
    {
        row.State = state.ToString();
        row.Reason = reason;
        row.StoppedAtUtc = now;
        row.UpdatedAtUtc = now;
        var ownTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using (ownTransaction)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await dbContext.CentralProcessingRunners
                .Where(runner => runner.RunnerId == row.RunnerId && runner.Status != CentralProcessingRunnerStatus.Retired)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                    .SetProperty(runner => runner.RetiredAtUtc, now)
                    .SetProperty(runner => runner.UpdatedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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

        [LoggerMessage(2239, LogLevel.Warning, "Elastic runner instance abandoned because its owning host stopped reconciling it: Instance={Instance}, Owner={Owner}")]
        public static partial void OwnerLost(ILogger logger, string instance, string owner);

        [LoggerMessage(2240, LogLevel.Debug, "Elastic scaling decision skipped this sample: another replica holds the scaling lock.")]
        public static partial void ScalingLockBusy(ILogger logger);
    }
}
