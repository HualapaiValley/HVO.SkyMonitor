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

    public ValueTask<ProcessingRunnerCapabilities?> DescribeInstanceAsync(int maxConcurrency, IReadOnlyList<string> labels, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Elastic provisioning is disabled.");

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
    internal const string ReasonRetirementResumed = "retirement-resumed";
    private const string ClaimLockPrefix = "processing-runner-claim/";
    private static readonly string[] LiveStates = [nameof(ElasticRunnerInstanceState.Starting), nameof(ElasticRunnerInstanceState.Running), nameof(ElasticRunnerInstanceState.Stopping)];
    private bool _adopted;
    private string? _lastExcludedRecipes;
    private static readonly string HostName = Environment.MachineName;
    internal Action? ClaimBarrierWaitStarted { get; set; }

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
        var live = LiveStates;
        // Only instances this host launched are reconciled: another replica's rows are its own to retire.
        var rows = await dbContext.CentralElasticRunnerInstances
            .Where(instance => instance.HostName == HostName && live.Contains(instance.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var alive = rows.Where(row => row.Provider == LocalProcessElasticRunnerProvider.ProviderName && row.ProcessId is { } processId
                && localProcessProvider.TryAdopt(row.InstanceId, row.RunnerId, processId, row.StartedAtUtc))
            .ToList();
        // Every drain request goes out at once so no inherited instance keeps claiming while another drains.
        var (stoppedAt, retireFailures) = await RetireEachAsync(alive.Select(row => row.InstanceId).ToArray(), options.Value.RetireGrace, cancellationToken, localProcessProvider).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var wasAlive = alive.Contains(row);
            if (wasAlive && !stoppedAt.ContainsKey(row.InstanceId))
            {
                continue;
            }
            await StopAsync(dbContext, row, wasAlive ? ElasticRunnerInstanceState.Stopped : ElasticRunnerInstanceState.Orphaned, "provisioning-disabled",
                retireFailures.Count == 0 ? cancellationToken : CancellationToken.None, stoppedAt.TryGetValue(row.InstanceId, out var stopped) ? stopped : null).ConfigureAwait(false);
            Log.InheritedRetired(logger, row.InstanceId, wasAlive);
        }
        ThrowRetireFailures(retireFailures, cancellationToken);
        return stoppedAt.Count;
    }

    /// <summary>
    /// One autoscaling pass; exposed for tests and for the evidence harness. The owner heartbeat on this host's rows is
    /// persisted before reconciliation and renewed on its own connection while the pass runs, so a drain that blocks
    /// for the whole retirement grace never lets a peer mistake this live replica for a lost owner.
    /// </summary>
    internal async Task<ElasticScalingDecision> SampleAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await HeartbeatOwnerAsync(cancellationToken).ConfigureAwait(false);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Renewal failures are retried every tick; when the heartbeat has not been persisted for half the owner-stale
        // window the in-progress sample is abandoned (a cancelled drain keeps its instance tracked for the next
        // sample), so a peer can never declare this host lost while it is merely waiting on a long drain.
        using var sampleStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var abandonSample = () => sampleStop.CancelAsync();
        var renewal = RenewOwnerHeartbeatsAsync(OwnerHeartbeatInterval(settings), OwnerStaleAfter(settings) / 2, abandonSample, renewalStop.Token);
        try
        {
            return await SampleCoreAsync(settings, sampleStop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sampleStop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The autoscaler sample was abandoned because the owner heartbeat could not be renewed.");
        }
        finally
        {
            await renewalStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Renewal cadence: well inside the owner-stale window and inside half the retirement grace, bounded to [250 ms, 1 min].</summary>
    internal static TimeSpan OwnerHeartbeatInterval(CentralElasticProviderOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var interval = settings.SampleInterval < settings.RetireGrace / 2 ? settings.SampleInterval : settings.RetireGrace / 2;
        return TimeSpan.FromTicks(Math.Clamp(interval.Ticks, TimeSpan.FromMilliseconds(250).Ticks, TimeSpan.FromMinutes(1).Ticks));
    }

    private async Task HeartbeatOwnerAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var live = LiveStates;
        var beat = timeProvider.GetUtcNow();
        await dbContext.CentralElasticRunnerInstances
            .Where(instance => instance.Provider == provider.Name && instance.HostName == HostName && live.Contains(instance.State))
            .ExecuteUpdateAsync(setters => setters.SetProperty(instance => instance.OwnerHeartbeatAtUtc, beat), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RenewOwnerHeartbeatsAsync(TimeSpan interval, TimeSpan abandonAfter, Func<Task> abandonSample, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, timeProvider);
        var lastPersisted = timeProvider.GetUtcNow();
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await HeartbeatOwnerAsync(cancellationToken).ConfigureAwait(false);
                lastPersisted = timeProvider.GetUtcNow();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var unrenewedFor = timeProvider.GetUtcNow() - lastPersisted;
                Log.OwnerHeartbeatFailed(logger, (long)unrenewedFor.TotalSeconds, exception);
                if (unrenewedFor >= abandonAfter)
                {
                    await abandonSample().ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    private async Task<ElasticScalingDecision> SampleCoreAsync(CentralElasticProviderOptions settings, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>();
        await registry.RefreshStatusesAsync(cancellationToken).ConfigureAwait(false);

        var live = LiveStates;
        var rows = await dbContext.CentralElasticRunnerInstances
            .Where(instance => instance.Provider == provider.Name && instance.HostName == HostName && live.Contains(instance.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var orphans = 0;
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
            await StopAsync(dbContext, row, ElasticRunnerInstanceState.Abandoned, "owner-lost", cancellationToken).ConfigureAwait(false);
            telemetry.RecordOrphanCleaned(provider.Name);
            orphans++;
            Log.OwnerLost(logger, row.InstanceId, row.HostName);
        }
        if (!_adopted)
        {
            AdoptRecorded(rows);
            _adopted = true;
        }
        var alive = (await provider.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(instance => instance.InstanceId, StringComparer.Ordinal);
        // Occupied slots are deployment-wide: every replica's live instances contribute to in-flight demand. Busy
        // tracking uses the durable unexpired leases keyed to each runner as well as the heartbeat-reported slots, so a
        // claim accepted since the last heartbeat is never mistaken for idleness.
        var liveRows = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .Where(instance => instance.Provider == provider.Name && live.Contains(instance.State))
            .Select(instance => new { instance.RunnerId, instance.State })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var liveRunnerIds = liveRows.Select(item => item.RunnerId).ToList();
        var registrations = await dbContext.CentralProcessingRunners.AsNoTracking()
            .Where(runner => liveRunnerIds.Contains(runner.RunnerId))
            .Select(runner => new { runner.RunnerId, runner.Status, runner.RegisteredAtUtc, runner.AvailableSlots, runner.MaxConcurrency, runner.EligibleRecipesJson, runner.MaxTransferBytes })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var registered = registrations.ToDictionary(runner => runner.RunnerId, StringComparer.Ordinal);
        var leasesByRunner = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now && job.LeaseOwner != null && liveRunnerIds.Contains(job.LeaseOwner))
            .GroupBy(job => job.LeaseOwner!)
            .Select(group => new { RunnerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.RunnerId, item => item.Count, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        int Occupied(string runnerId, int maxConcurrency, int availableSlots)
            => Math.Max(Math.Max(0, maxConcurrency - availableSlots), leasesByRunner.TryGetValue(runnerId, out var leases) ? leases : 0);
        var localRunnerIds = rows.Select(row => row.RunnerId).ToHashSet(StringComparer.Ordinal);
        var inFlight = registrations.Where(runner => !localRunnerIds.Contains(runner.RunnerId) && runner.Status == CentralProcessingRunnerStatus.Active)
            .Sum(runner => Occupied(runner.RunnerId, runner.MaxConcurrency, runner.AvailableSlots));
        // Registered concurrency of the running instances (deployment-wide) sizes demand against what they can really
        // claim; instances without an active registration count at the configured value.
        var runningRunnerIds = liveRows.Where(item => item.State == nameof(ElasticRunnerInstanceState.Running)).Select(item => item.RunnerId).ToHashSet(StringComparer.Ordinal);
        var registeredRunning = registrations.Where(runner => runningRunnerIds.Contains(runner.RunnerId) && runner.Status == CentralProcessingRunnerStatus.Active).ToList();
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
        var busyNow = new HashSet<CentralElasticRunnerInstance>();
        foreach (var row in rows)
        {
            registered.TryGetValue(row.RunnerId, out var registration);
            if (row.State == nameof(ElasticRunnerInstanceState.Stopping))
            {
                // A retirement reserved by an earlier sample or a previous host process whose drain never completed
                // (host exit between the reservation and the provider call, or a cancelled drain) is finished here, so
                // the reserved instance can never keep claiming outside capacity accounting.
                if (alive.ContainsKey(row.InstanceId))
                {
                    await provider.RetireAsync(row.InstanceId, settings.RetireGrace, cancellationToken).ConfigureAwait(false);
                }
                await StopAsync(dbContext, row, ElasticRunnerInstanceState.Stopped, ReasonRetirementResumed, cancellationToken).ConfigureAwait(false);
                telemetry.RecordRetirement(provider.Name, ReasonRetirementResumed);
                Log.RetirementResumed(logger, row.InstanceId);
                continue;
            }
            if (!alive.ContainsKey(row.InstanceId))
            {
                // The provider no longer has the instance: it exited on its own (idle shutdown, crash, host restart).
                await StopAsync(dbContext, row, ElasticRunnerInstanceState.Orphaned, "process-exited", cancellationToken).ConfigureAwait(false);
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
                    await StopAsync(dbContext, row, ElasticRunnerInstanceState.Stopped, "registration-timeout", cancellationToken).ConfigureAwait(false);
                    telemetry.RecordRetirement(provider.Name, "registration-timeout");
                    orphans++;
                }
                continue;
            }
            if (registration is null || registration.Status != CentralProcessingRunnerStatus.Active)
            {
                // Registry lost the runner (stale or retired) while the process is alive: retire the instance too.
                await provider.RetireAsync(row.InstanceId, settings.RetireGrace, cancellationToken).ConfigureAwait(false);
                await StopAsync(dbContext, row, ElasticRunnerInstanceState.Stopped, "registry-" + (registration?.Status.ToString().ToLowerInvariant() ?? "missing"), cancellationToken).ConfigureAwait(false);
                telemetry.RecordRetirement(provider.Name, "registry-lost");
                orphans++;
                continue;
            }
            var occupied = Occupied(row.RunnerId, registration.MaxConcurrency, registration.AvailableSlots);
            if (occupied > 0)
            {
                inFlight += occupied;
                row.LastBusyAtUtc = now;
                row.UpdatedAtUtc = now;
                busyNow.Add(row);
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

        // Only backlog a provisioned instance could claim counts: runner-placed recipes filtered through the
        // capabilities such an instance registers (resource class, GPU, architecture, labels), and the claim's own
        // predicate for reclaimable work (pending, retryable, or a lease that expired, e.g. with a crashed runner).
        var placedAll = runnerOptions.Value.ResolveRunnerPlacedRecipes();
        // A provider that cannot describe an instance (the configured runner failed its probe) gets nothing
        // provisioned: no backlog is counted for it and even the warm minimum waits until a probe succeeds.
        var template = await provider.DescribeInstanceAsync(settings.MaxConcurrencyPerInstance, BuildLabels(settings, "template"), cancellationToken).ConfigureAwait(false);
        var placed = template is null ? [] : runnerOptions.Value.ResolveEligibleRecipes(template).ToHashSet(StringComparer.Ordinal);
        var excluded = string.Join(',', placedAll.Where(recipe => !placed.Contains(recipe)).OrderBy(recipe => recipe, StringComparer.Ordinal));
        if (template is not null && excluded.Length != 0 && !string.Equals(excluded, _lastExcludedRecipes, StringComparison.Ordinal))
        {
            Log.RecipesExcluded(logger, provider.Name, excluded);
        }
        _lastExcludedRecipes = template is null ? null : excluded;
        // The claimable set exactly as the claim sees it (recipe filter, the probed runner's transfer limit, readiness of
        // inputs and graph execution), so no instance is provisioned for work no runner could claim; the pool predicate
        // is applied here, as the claim does. Terminal cleanup (expired leases with exhausted attempts) is exempt from
        // pool, entitlement, and fairness bounds in the claim, so it is counted apart from executable backlog.
        var claimable = placed.Count == 0 || template is null
            ? []
            : await QueryClaimableAsync(dbContext, placed, template.MaxTransferBytes, now, cancellationToken).ConfigureAwait(false);
        var entitlements = entitlementOptions.Value is { Enabled: true } enabled ? enabled : null;
        var executable = claimable.Where(row => !row.IsCleanup && IsPoolEligible(settings.Pool, entitlements?.ResolvePool(row.ObservatoryId))).ToList();
        var backlogRows = executable.GroupBy(row => row.ObservatoryId)
            .Select(group => new { ObservatoryId = group.Key, Count = group.Count(), Oldest = group.Min(row => row.AvailableSince) })
            .ToList();
        var backlog = backlogRows.Sum(row => row.Count);
        var oldestAge = backlogRows.Count == 0 ? TimeSpan.Zero : now - backlogRows.Min(row => row.Oldest ?? now);
        var cleanupBacklog = claimable.Count(row => row.IsCleanup);
        var cleanupOldest = cleanupBacklog == 0 ? TimeSpan.Zero : now - claimable.Where(row => row.IsCleanup).Min(row => row.AvailableSince ?? now);
        // The age published outward covers everything retained; the policy's deadline rule sees executable work only.
        var reportedAge = oldestAge > cleanupOldest ? oldestAge : cleanupOldest;
        // Registered capacity counts per job: an instance counts when it can claim at least one queued job for this
        // provider (executable work in its pool scope and terminal cleanup, which the claim hands to any pool) by
        // recipe and by its own transfer limit; an instance that can claim none of it (adopted before a placement,
        // other capabilities, limit below every input) is incompatible and covers nothing. Jobs no active instance can
        // claim are an uncovered shortfall that only new instances (registering with the probed template) serve.
        var relevant = executable.Concat(claimable.Where(row => row.IsCleanup)).ToList();
        bool CanClaim(string eligibleRecipesJson, long maxTransferBytes, ClaimableBacklogRow row)
            => maxTransferBytes >= row.InputBytes && ParseEligibleRecipes(eligibleRecipesJson).Contains(row.RecipeName, StringComparer.Ordinal);
        bool CanClaimBacklog(string eligibleRecipesJson, long maxTransferBytes)
            => relevant.Count == 0 || relevant.Any(row => CanClaim(eligibleRecipesJson, maxTransferBytes, row));
        // Executable work is allocated by maximum matching over currently available slots. Terminal cleanup remains
        // instance-level demand: it needs one compatible instance rather than one slot per cleanup row.
        (ElasticFleetAllocator.Result Executable, bool CleanupUncovered) Allocate(
            IEnumerable<(string RunnerId, string EligibleRecipesJson, long MaxTransferBytes, int MaxConcurrency, int OccupiedSlots)> activeRegistrations)
        {
            var registrationsForAllocation = activeRegistrations.Select(registration => new ElasticFleetAllocator.Registration(
                registration.RunnerId,
                ParseEligibleRecipes(registration.EligibleRecipesJson).ToHashSet(StringComparer.Ordinal),
                registration.MaxTransferBytes,
                registration.MaxConcurrency,
                registration.OccupiedSlots)).ToArray();
            var allocation = ElasticFleetAllocator.Allocate(
                executable.Select(row => new ElasticFleetAllocator.Job(row.JobId, row.RecipeName, row.InputBytes, row.AvailableSince)).ToArray(),
                registrationsForAllocation);
            var cleanupRows = relevant.Where(row => row.IsCleanup).ToList();
            var cleanupUncovered = cleanupRows.Count != 0 && !cleanupRows.Any(row => activeRegistrations.Any(registration => CanClaim(registration.EligibleRecipesJson, registration.MaxTransferBytes, row)));
            return (allocation, cleanupUncovered);
        }
        var compatibleRunning = registeredRunning.Where(runner => CanClaimBacklog(runner.EligibleRecipesJson, runner.MaxTransferBytes)).ToList();
        var registeredRunningCount = registeredRunning.Count;
        var compatibleRunningCount = compatibleRunning.Count;
        var compatibleRunningConcurrency = compatibleRunning.Sum(runner => runner.MaxConcurrency);
        var incompatibleRunning = registeredRunningCount - compatibleRunningCount;
        var (allocation, cleanupUncovered) = Allocate(registeredRunning.Select(runner => (
            runner.RunnerId, runner.EligibleRecipesJson, runner.MaxTransferBytes, runner.MaxConcurrency,
            Occupied(runner.RunnerId, runner.MaxConcurrency, runner.AvailableSlots))));
        telemetry.RecordAllocation(provider.Name, "sample", allocation);
        async Task<ElasticProvisionableShortfall> ProvisionableAsync(ElasticFleetAllocator.Result currentAllocation)
        {
            var unmatchedIds = currentAllocation.UnmatchedJobIds.ToHashSet();
            var unmatched = executable.Where(row => unmatchedIds.Contains(row.JobId))
                .Select(row => new ElasticProvisionableShortfallSelector.Job(row.JobId, row.ObservatoryId, row.AvailableSince))
                .ToArray();
            if (entitlements is null || unmatched.Length == 0)
            {
                return ElasticProvisionableShortfallSelector.Select(unmatched, null, now);
            }
            var observatoryIds = unmatched.Select(row => row.ObservatoryId).Distinct().ToArray();
            var activeByObservatory = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now
                    && observatoryIds.Contains(job.SourceArtifact!.Frame!.ObservatoryId))
                .GroupBy(job => job.SourceArtifact!.Frame!.ObservatoryId)
                .Select(group => new { ObservatoryId = group.Key, Active = group.Count() })
                .ToDictionaryAsync(item => item.ObservatoryId, item => item.Active, cancellationToken).ConfigureAwait(false);
            var remaining = observatoryIds.ToDictionary(observatoryId => observatoryId, observatoryId =>
            {
                var limit = entitlements.ResolveActiveJobs(observatoryId);
                return limit == 0 ? int.MaxValue : Math.Max(0, limit - (activeByObservatory.TryGetValue(observatoryId, out var active) ? active : 0));
            });
            return ElasticProvisionableShortfallSelector.Select(unmatched, remaining, now);
        }
        var provisionable = await ProvisionableAsync(allocation).ConfigureAwait(false);
        var minutesToday = await InstanceMinutesTodayAsync(dbContext, now, cancellationToken).ConfigureAwait(false);
        var perInstance = Math.Max(1, settings.MaxConcurrencyPerInstance);
        // Compatible registered slots, plus the configured size for running instances not yet registered and for
        // starting ones (they register with the probed template); incompatible registered instances count nothing.
        int CapacityOf(int runningCount, int startingCount)
            => compatibleRunningConcurrency + Math.Max(0, runningCount - registeredRunningCount) * perInstance + startingCount * perInstance;
        var input = new ElasticScalingInput(backlog, running, starting, idle.Count,
            idle.Count == 0 ? TimeSpan.Zero : idle.Max(item => item.IdleFor), provisionable, minutesToday, inFlight, warmLive,
            CapacityOf(running, starting), cleanupBacklog, incompatibleRunning, allocation.MatchedJobIds.Count, cleanupUncovered);
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
            telemetry.UpdateSnapshot(new ElasticProviderSnapshot(now, provider.Name, starting, running, idle.Count, backlog + cleanupBacklog, (long)reportedAge.TotalSeconds, minutesToday, "scaling-lock-busy", orphans));
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
        // Registered capacity is re-read for the locked set of running rows: another replica may have reserved a
        // heterogeneous instance since the sample's registration snapshot.
        var lockedRunningIds = await dbContext.CentralElasticRunnerInstances.AsNoTracking()
            .Where(instance => instance.Provider == provider.Name && instance.State == nameof(ElasticRunnerInstanceState.Running))
            .Select(instance => instance.RunnerId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var claimBarrierResult = new SqlParameter("@result", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
        await dbContext.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;",
            [new SqlParameter("@resource", CentralObjectApplicationLock.CreateResource(CentralProcessingRunnerJobService.ClaimBarrier)), claimBarrierResult], cancellationToken).ConfigureAwait(false);
        if (claimBarrierResult.Value is int { } immediateClaimBarrier && immediateClaimBarrier == -1)
        {
            ClaimBarrierWaitStarted?.Invoke();
            claimBarrierResult = new SqlParameter("@result", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
            await dbContext.Database.ExecuteSqlRawAsync(
                "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 5000;",
                [new SqlParameter("@resource", CentralObjectApplicationLock.CreateResource(CentralProcessingRunnerJobService.ClaimBarrier)), claimBarrierResult], cancellationToken).ConfigureAwait(false);
        }
        if (claimBarrierResult.Value is not int claimBarrierAcquired || claimBarrierAcquired < 0)
        {
            throw new InvalidOperationException("The processing runner claim barrier was not acquired.");
        }
        // Every runner claim is now blocked until this transaction commits. Rebuild every demand fact so queued jobs,
        // leases, occupied slots, entitlement headroom, and age all describe one coherent decision snapshot.
        claimable = placed.Count == 0 || template is null
            ? []
            : await QueryClaimableAsync(dbContext, placed, template.MaxTransferBytes, now, cancellationToken).ConfigureAwait(false);
        executable = claimable.Where(row => !row.IsCleanup && IsPoolEligible(settings.Pool, entitlements?.ResolvePool(row.ObservatoryId))).ToList();
        backlogRows = executable.GroupBy(row => row.ObservatoryId)
            .Select(group => new { ObservatoryId = group.Key, Count = group.Count(), Oldest = group.Min(row => row.AvailableSince) })
            .ToList();
        backlog = backlogRows.Sum(row => row.Count);
        oldestAge = backlogRows.Count == 0 ? TimeSpan.Zero : now - backlogRows.Min(row => row.Oldest ?? now);
        cleanupBacklog = claimable.Count(row => row.IsCleanup);
        cleanupOldest = cleanupBacklog == 0 ? TimeSpan.Zero : now - claimable.Where(row => row.IsCleanup).Min(row => row.AvailableSince ?? now);
        reportedAge = oldestAge > cleanupOldest ? oldestAge : cleanupOldest;
        relevant = executable.Concat(claimable.Where(row => row.IsCleanup)).ToList();
        var lockedRegistered = await dbContext.CentralProcessingRunners.AsNoTracking()
            .Where(runner => lockedRunningIds.Contains(runner.RunnerId) && runner.Status == CentralProcessingRunnerStatus.Active)
            .Select(runner => new { runner.RunnerId, runner.MaxConcurrency, runner.AvailableSlots, runner.EligibleRecipesJson, runner.MaxTransferBytes })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lockedLeasesByRunner = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now && job.LeaseOwner != null && lockedRunningIds.Contains(job.LeaseOwner))
            .GroupBy(job => job.LeaseOwner!)
            .Select(group => new { RunnerId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.RunnerId, item => item.Count, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        int LockedOccupied(string runnerId, int maxConcurrency, int availableSlots)
            => Math.Max(Math.Max(0, maxConcurrency - availableSlots), lockedLeasesByRunner.TryGetValue(runnerId, out var leases) ? leases : 0);
        inFlight = lockedRegistered.Sum(runner => LockedOccupied(runner.RunnerId, runner.MaxConcurrency, runner.AvailableSlots));
        registeredRunningCount = lockedRegistered.Count;
        var lockedCompatible = lockedRegistered.Where(runner => CanClaimBacklog(runner.EligibleRecipesJson, runner.MaxTransferBytes)).ToList();
        compatibleRunningCount = lockedCompatible.Count;
        compatibleRunningConcurrency = lockedCompatible.Sum(runner => runner.MaxConcurrency);
        incompatibleRunning = registeredRunningCount - compatibleRunningCount;
        (allocation, cleanupUncovered) = Allocate(lockedRegistered.Select(runner => (
            runner.RunnerId, runner.EligibleRecipesJson, runner.MaxTransferBytes, runner.MaxConcurrency,
            LockedOccupied(runner.RunnerId, runner.MaxConcurrency, runner.AvailableSlots))));
        telemetry.RecordAllocation(provider.Name, "locked", allocation);
        provisionable = await ProvisionableAsync(allocation).ConfigureAwait(false);
        input = input with
        {
            Backlog = backlog,
            Running = running,
            Starting = starting,
            ProvisionableShortfall = provisionable,
            InFlight = inFlight,
            WarmInstances = warmLive,
            Capacity = CapacityOf(running, starting),
            CleanupBacklog = cleanupBacklog,
            IncompatibleActive = incompatibleRunning,
            MatchedBacklog = allocation.MatchedJobIds.Count,
            CleanupUncovered = cleanupUncovered
        };
        var decision = ElasticScalingPolicy.Decide(settings, input, provider.EstimateStartup());
        if (template is null && decision.Reason != ElasticScalingPolicy.ReasonDailyLimit)
        {
            // With no instance description the backlog is unknown, not zero: no provisioning and no idle retirement
            // (which could remove the only instance able to claim queued work) until a probe succeeds; the daily-budget
            // drain still applies.
            decision = new ElasticScalingDecision(0, 0, ElasticScalingPolicy.ReasonInstanceUndescribed);
            telemetry.RecordRejectedPlacement(provider.Name, decision.Reason);
            Log.InstanceUndescribed(logger, provider.Name);
        }
        // Retained work reported outward (snapshot, health, rejection log) includes cleanup demand, so a daily limit
        // that blocks the one instance an exhausted lease needs is visible.
        var retained = backlog + cleanupBacklog;
        if (decision.Provision == 0 && decision.Retire == 0 && decision.Reason is not "steady" && retained > 0)
        {
            telemetry.RecordRejectedPlacement(provider.Name, decision.Reason);
            Log.PlacementRejected(logger, decision.Reason, retained, (long)reportedAge.TotalSeconds);
        }
        // Durable intents and retirement reservations are recorded under the lock; processes launch and drain after
        // it commits, so another replica's decision already sees the reserved capacity change.
        var intents = new List<(CentralElasticRunnerInstance Row, ElasticRunnerProvisionRequest Request)>();
        for (var i = 0; i < decision.Provision; i++)
        {
            intents.Add(CreateIntent(dbContext, settings, now, warmLive + i));
        }
        // Idle scale-down retires only instances idle beyond the delay; a warm-minimum replacement retires one excess
        // instance with no work in flight (longest idle first) and otherwise waits for one to go idle, because
        // restoring the warm designation never force-terminates active work; the daily-limit drain covers every
        // active instance (idle, then busy, then registering).
        var idleRows = idle.Select(item => (item.Row, item.IdleFor)).ToList();
        var busyRunning = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Running) && !idleRows.Any(item => item.Row == row)).Select(row => (Row: row, IdleFor: TimeSpan.Zero));
        var startingRows = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Starting)).Select(row => (Row: row, IdleFor: TimeSpan.Zero));
        var excessNotBusy = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Running) && !row.KeepWarm && !busyNow.Contains(row))
            .Select(row => (Row: row, IdleFor: now - (row.LastBusyAtUtc ?? row.RegisteredAtUtc ?? row.StartedAtUtc)))
            .OrderByDescending(item => item.IdleFor);
        var incompatibleNotBusy = current.Where(row => row.State == nameof(ElasticRunnerInstanceState.Running) && !busyNow.Contains(row)
                && registered.TryGetValue(row.RunnerId, out var incompatibleRegistration) && incompatibleRegistration.Status == CentralProcessingRunnerStatus.Active
                && !CanClaimBacklog(incompatibleRegistration.EligibleRecipesJson, incompatibleRegistration.MaxTransferBytes))
            .Select(row => (Row: row, IdleFor: now - (row.LastBusyAtUtc ?? row.RegisteredAtUtc ?? row.StartedAtUtc)))
            .OrderByDescending(item => item.IdleFor);
        var candidates = decision.Reason switch
        {
            ElasticScalingPolicy.ReasonDailyLimit => idleRows.Concat(busyRunning).Concat(startingRows),
            ElasticScalingPolicy.ReasonWarmMinimum => excessNotBusy,
            ElasticScalingPolicy.ReasonIncompatibleReplacement => incompatibleNotBusy,
            _ => idleRows.AsEnumerable()
        };
        // Each reservation is taken under the runner's claim lock with a fresh check of its unexpired leases: a claim
        // accepted after the sample's snapshot keeps the instance out of this retirement, and once the reservation
        // commits the claim path (which takes the same lock) refuses the instance new work.
        var retiring = new List<(CentralElasticRunnerInstance Row, TimeSpan IdleFor)>();
        // Idle scale-down keeps enough registered capacity for the demand: with heterogeneous adopted instances the
        // policy's count is a lower bound, so a candidate whose registered slots the demand still needs is skipped.
        var remainingCapacity = CapacityOf(running, starting);
        var demand = Math.Max(EffectiveDemand(allocation.MatchedJobIds.Count, provisionable.Count, inFlight), cleanupBacklog > 0 ? 1 : 0);
        // A warm-minimum replacement is provisioned at the configured size on the next sample, so its capacity counts
        // toward what may be retired now.
        var capacityFloor = decision.Reason == ElasticScalingPolicy.ReasonWarmMinimum ? demand - perInstance : demand;
        foreach (var item in candidates)
        {
            if (retiring.Count == decision.Retire)
            {
                break;
            }
            var candidateConcurrency = registered.TryGetValue(item.Row.RunnerId, out var candidateRegistration) && candidateRegistration.Status == CentralProcessingRunnerStatus.Active
                ? (CanClaimBacklog(candidateRegistration.EligibleRecipesJson, candidateRegistration.MaxTransferBytes) ? candidateRegistration.MaxConcurrency : 0)
                : perInstance;
            if (decision.Reason is ElasticScalingPolicy.ReasonIdle or ElasticScalingPolicy.ReasonWarmMinimum
                && remainingCapacity - candidateConcurrency < capacityFloor)
            {
                continue;
            }
            if (decision.Reason != ElasticScalingPolicy.ReasonDailyLimit
                && lockedLeasesByRunner.TryGetValue(item.Row.RunnerId, out var activeLeases) && activeLeases > 0)
            {
                continue;
            }
            item.Row.State = nameof(ElasticRunnerInstanceState.Stopping);
            item.Row.UpdatedAtUtc = now;
            retiring.Add(item);
            remainingCapacity -= candidateConcurrency;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await scaling.CommitAsync(cancellationToken).ConfigureAwait(false);
        await LaunchAllAsync(dbContext, settings, intents, now, decision.Reason, cancellationToken).ConfigureAwait(false);
        var retired = 0;
        var idleRetired = 0;
        // Every drain request goes out at once and each instance is stamped the moment its own drain completes, so an
        // instance that exits quickly is never charged for a slower sibling's grace.
        var (stoppedAt, retireFailures) = await RetireEachAsync(retiring.Select(item => item.Row.InstanceId).ToArray(), settings.RetireGrace, cancellationToken).ConfigureAwait(false);
        foreach (var item in retiring)
        {
            if (!stoppedAt.TryGetValue(item.Row.InstanceId, out var stopped))
            {
                continue;
            }
            // Successful retirements are closed even when a sibling failed (non-cancelable), so their rows never stay
            // `Stopping` with an active registration until the next sample.
            await StopAsync(dbContext, item.Row, ElasticRunnerInstanceState.Stopped, decision.Reason, retireFailures.Count == 0 ? cancellationToken : CancellationToken.None, stopped).ConfigureAwait(false);
            telemetry.RecordRetirement(provider.Name, decision.Reason);
            retired++;
            if (item.IdleFor >= settings.ScaleToZeroAfter)
            {
                idleRetired++;
            }
        }
        ThrowRetireFailures(retireFailures, cancellationToken);
        var startingRetired = retiring.Count(item => item.Row.RegisteredAtUtc is null);
        telemetry.UpdateSnapshot(new ElasticProviderSnapshot(
            now, provider.Name, Math.Max(0, starting + decision.Provision - startingRetired), Math.Max(0, running - (retired - startingRetired)),
            Math.Max(0, input.Idle - idleRetired), retained, (long)reportedAge.TotalSeconds, minutesToday, decision.Reason, orphans));
        return decision;
    }

    /// <summary>One claimable runner-placed job as the claim's own readiness query reports it.</summary>
    internal sealed class ClaimableBacklogRow
    {
        public Guid JobId { get; init; }
        public Guid ObservatoryId { get; init; }
        public string RecipeName { get; init; } = string.Empty;
        public bool IsCleanup { get; init; }
        public DateTimeOffset? AvailableSince { get; init; }
        public long InputBytes { get; init; }
    }

#pragma warning disable CA2100 // The text is the constant CreateClaimableSql template; every runtime value is a SqlParameter.
    private static async Task<List<ClaimableBacklogRow>> QueryClaimableAsync(
        ApplicationDbContext dbContext, IReadOnlySet<string> recipes, long maximumInputBytes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database.SqlQueryRaw<ClaimableBacklogRow>(
                CentralDerivativeJobService.CreateClaimableSql(),
                new SqlParameter("@now", now),
                new SqlParameter("@includeRecipes", System.Data.SqlDbType.NVarChar, -1) { Value = string.Join(',', recipes.OrderBy(recipe => recipe, StringComparer.Ordinal)) },
                new SqlParameter("@excludeRecipes", System.Data.SqlDbType.NVarChar, -1) { Value = string.Empty },
                new SqlParameter("@maximumInputBytes", maximumInputBytes),
                new SqlParameter("@candidateLimit", ElasticFleetAllocator.MaximumJobs + 1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > ElasticFleetAllocator.MaximumJobs)
        {
            throw new InvalidOperationException($"Elastic allocation candidate limit exceeded: more than {ElasticFleetAllocator.MaximumJobs} claimable jobs.");
        }
        return rows;
    }
#pragma warning restore CA2100

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> EligibleRecipeCache = new(StringComparer.Ordinal);

    /// <summary>The recipes a registration may claim, as the registry recorded them.</summary>
    internal static string[] ParseEligibleRecipes(string eligibleRecipesJson)
        => EligibleRecipeCache.GetOrAdd(eligibleRecipesJson ?? "[]", static json =>
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        });

    /// <summary>The concurrency the demand can really use: queued plus in-flight work, bounded by the entitlement the policy applied.</summary>
    internal static int EffectiveDemand(int matchedBacklog, int provisionableBacklog, int inFlight)
    {
        return Math.Max(0, matchedBacklog) + Math.Max(0, provisionableBacklog) + Math.Max(0, inFlight);
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
        // Rows reserved as Stopping are adopted too: a retirement whose drain request never went out (the host exited
        // between the reservation commit and the provider call) is completed by this host instead of being orphaned
        // while its process keeps running.
        foreach (var row in rows.Where(row => row.ProcessId is { }))
        {
            local.TryAdopt(row.InstanceId, row.RunnerId, row.ProcessId!.Value, row.StartedAtUtc);
        }
    }

    /// <summary>The labels an instance registers: provenance, provider, configured labels, and the reserved pool.</summary>
    private List<string> BuildLabels(CentralElasticProviderOptions settings, string instanceId)
    {
        var labels = new List<string> { $"provider:{provider.Name}", $"elastic-instance:{instanceId}" };
        labels.AddRange(provider.Capabilities.Labels.Where(label => !labels.Contains(label, StringComparer.Ordinal)));
        labels.AddRange(settings.Labels);
        if (settings.Pool is { } pool)
        {
            labels.Add($"pool:{pool}");
            labels.Add("pool-mode:reserved");
        }
        return labels;
    }

    /// <summary>Records the durable intent for one instance (under the scaling lock); the launch follows the commit.</summary>
    private (CentralElasticRunnerInstance Row, ElasticRunnerProvisionRequest Request) CreateIntent(
        ApplicationDbContext dbContext, CentralElasticProviderOptions settings, DateTimeOffset now, int warmSoFar)
    {
        var instanceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var runnerId = $"elastic-{provider.Name}-{instanceId}";
        var labels = BuildLabels(settings, instanceId);
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
    /// Launches every committed intent in order; when a launch throws or the host cancels, the remaining intents are
    /// closed through a non-cancelable path (the interrupted one closes itself) so no replica counts an instance that never ran.
    /// </summary>
    private async Task LaunchAllAsync(
        ApplicationDbContext dbContext, CentralElasticProviderOptions settings,
        List<(CentralElasticRunnerInstance Row, ElasticRunnerProvisionRequest Request)> intents, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        for (var i = 0; i < intents.Count; i++)
        {
            try
            {
                await LaunchAsync(dbContext, settings, intents[i].Row, intents[i].Request, now, reason, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                foreach (var (row, _) in intents.Skip(i + 1))
                {
                    await StopAsync(dbContext, row, ElasticRunnerInstanceState.Stopped, "launch-aborted", CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
        }
    }

    /// <summary>Launches a recorded intent; a failed or cancelled launch closes the intent, and a failed record retires the process and closes the intent too.</summary>
    private async Task LaunchAsync(
        ApplicationDbContext dbContext, CentralElasticProviderOptions settings, CentralElasticRunnerInstance row, ElasticRunnerProvisionRequest request,
        DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        ElasticRunnerInstance instance;
        try
        {
            instance = await provider.ProvisionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await StopAsync(dbContext, row, ElasticRunnerInstanceState.Stopped, "provision-failed", CancellationToken.None).ConfigureAwait(false);
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
            // The process exists but its record does not: retire the process and close the committed intent through
            // the non-cancelable path, so no replica counts capacity that never ran until owner-stale cleanup.
            await provider.RetireAsync(row.InstanceId, settings.RetireGrace, CancellationToken.None).ConfigureAwait(false);
            await StopAsync(dbContext, row, ElasticRunnerInstanceState.Stopped, "launch-aborted", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        telemetry.RecordProvision(provider.Name, reason);
    }

    /// <summary>
    /// Closes an instance row and retires its registration in one transaction, so a runner can never observe the
    /// registration retired while the row still reads live (or the reverse) between the two writes. The transaction
    /// holds the per-runner registration lock, so a re-registration racing the close is serialized behind it and then
    /// sees the closed row (denied when abandoned) instead of reviving the retired registration.
    /// </summary>
    private async Task StopAsync(
        ApplicationDbContext dbContext, CentralElasticRunnerInstance row, ElasticRunnerInstanceState state, string reason, CancellationToken cancellationToken,
        DateTimeOffset? stoppedAtUtc = null)
    {
        // Every close (stopped, orphaned, abandoned) is written while holding the runner's claim lock, the critical
        // section the claim path checks the instance state in, so a claim can never lease work to an instance whose
        // close is committing.
        await using var claimLock = await CentralObjectApplicationLock.AcquireAsync(dbContext, ClaimLockPrefix + row.RunnerId, cancellationToken).ConfigureAwait(false);
        // Stamped when the instance actually stopped (after any drain), so instance minutes and the daily limit are exact.
        var now = timeProvider.GetUtcNow();
        row.State = state.ToString();
        row.Reason = reason;
        row.StoppedAtUtc = stoppedAtUtc ?? now;
        row.UpdatedAtUtc = now;
        var ownTransaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        await using (ownTransaction)
        {
            await ElasticRunnerRegistrationLock.AcquireAsync(dbContext, row.RunnerId, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Retires the instances concurrently; returns when each one's own drain completed (or failed) so the caller
    /// closes every successful retirement before the failures are raised.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every failure is collected and rethrown by the caller after the successful retirements are persisted.")]
    private async Task<(IReadOnlyDictionary<string, DateTimeOffset> StoppedAt, IReadOnlyList<Exception> Failures)> RetireEachAsync(
        IReadOnlyCollection<string> instanceIds, TimeSpan grace, CancellationToken cancellationToken, IElasticRunnerProvider? target = null)
    {
        var retireVia = target ?? provider;
        var stoppedAt = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        await Task.WhenAll(instanceIds.Select(async instanceId =>
        {
            try
            {
                await retireVia.RetireAsync(instanceId, grace, cancellationToken).ConfigureAwait(false);
                stoppedAt[instanceId] = timeProvider.GetUtcNow();
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        })).ConfigureAwait(false);
        return (stoppedAt, failures.ToArray());
    }

    private static void ThrowRetireFailures(IReadOnlyList<Exception> failures, CancellationToken cancellationToken)
    {
        if (failures.Count == 0)
        {
            return;
        }
        if (cancellationToken.IsCancellationRequested && failures.All(failure => failure is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        throw failures.Count == 1 ? new InvalidOperationException("An elastic runner retirement failed.", failures[0]) : new AggregateException(failures);
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

        [LoggerMessage(2241, LogLevel.Warning, "Elastic runner instance retirement resumed: a reserved retirement had not completed. Instance={Instance}")]
        public static partial void RetirementResumed(ILogger logger, string instance);

        [LoggerMessage(2242, LogLevel.Warning, "Runner-placed recipes excluded from elastic backlog because provisioned instances could not claim them: Provider={Provider}, Recipes={Recipes}")]
        public static partial void RecipesExcluded(ILogger logger, string provider, string recipes);

        [LoggerMessage(2245, LogLevel.Warning, "Elastic owner heartbeat renewal failed; retrying each tick. UnrenewedSeconds={UnrenewedSeconds}")]
        public static partial void OwnerHeartbeatFailed(ILogger logger, long unrenewedSeconds, Exception exception);

        [LoggerMessage(2246, LogLevel.Warning, "Elastic provisioning withheld: the provider cannot describe an instance (see the capability probe). Provider={Provider}")]
        public static partial void InstanceUndescribed(ILogger logger, string provider);
    }
}
