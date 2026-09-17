using System.Diagnostics;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.HealthChecks;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Elastic;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

/// <summary>
/// Elastic provider adapters (#430) against a Kestrel-hosted LogicHost and the real self-hosted runner launched as a
/// process by the local proof adapter. Local-only mode proves zero provider calls.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class ElasticProviderIntegrationTests
{
    private static readonly byte[] SourcePayload = [1, 0, 2, 0, 3, 0, 4, 0];
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(120);

    [TestCleanup]
    public Task CleanupAsync() => DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory);

    [TestMethod]
    public async Task BacklogProvisionsALocalRunnerThatCompletesThePlacedJobThenScalesToZero()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        // No backlog: nothing is provisioned.
        (await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(ElasticScalingDecision.Steady);

        var sourceId = await SeedPreviewJobAsync("elastic-e2e").ConfigureAwait(false);
        var decision = await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1, "runner-placed backlog provisions one instance");
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonBacklog);
        var instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        var instanceId = instance.InstanceId;
        instance.State.Should().Be(nameof(ElasticRunnerInstanceState.Starting));
        instance.ProcessId.Should().NotBeNull();
        instance.RunnerId.Should().StartWith("elastic-local-process-");

        // The launched runner registers (cold start measured), claims through the runner protocol, and completes the job.
        await host.SampleUntilAsync(async () => (await host.InstanceAsync(instanceId).ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Running), CompletionTimeout).ConfigureAwait(false);
        instance = await host.InstanceAsync(instanceId).ConfigureAwait(false);
        instance.ColdStartMilliseconds.Should().BeGreaterThan(0);
        await ElasticHost.WaitUntilAsync(async () => await host.JobStatusAsync(sourceId).ConfigureAwait(false) == CentralDerivativeJobStatus.Completed, CompletionTimeout).ConfigureAwait(false);
        var jobId = await host.PlacedJobIdAsync(sourceId).ConfigureAwait(false);
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var registration = await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false);
            registration.CapabilitiesJson.Should().Contain("provider:local-process").And.Contain($"elastic-instance:{instance.InstanceId}", "provenance labels are advertised by the instance");
            var attempt = await db.CentralDerivativeJobAttempts.AsNoTracking().SingleAsync(item => item.CentralDerivativeJobId == jobId && item.AttemptNumber == 1).ConfigureAwait(false);
            attempt.WorkerId.Should().Be(instance.RunnerId, "the job was executed by the provisioned instance");
            attempt.Outcome.Should().Be(CentralDerivativeAttemptOutcome.Completed);
        }

        // Idle beyond the scale-to-zero delay retires the instance and its registration; the process is gone.
        await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await host.SampleUntilAsync(async () => (await host.InstanceAsync(instanceId).ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Stopped), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        instance = await host.InstanceAsync(instanceId).ConfigureAwait(false);
        instance.Reason.Should().Be(ElasticScalingPolicy.ReasonIdle);
        instance.StoppedAtUtc.Should().NotBeNull();
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingRunnerStatus.Retired);
            (await ElasticRunnerAutoscaler.InstanceMinutesTodayAsync(db, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false))
                .Should().BeGreaterThanOrEqualTo(1, "instance minutes are accounted");
        }
        ProcessIsAlive(instance.ProcessId!.Value).Should().BeFalse();
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task AKilledInstanceIsCleanedUpAsAnOrphanAndReportedByHealth()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        await SeedPreviewJobAsync("elastic-orphan").ConfigureAwait(false);
        (await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Provision.Should().Be(1);
        var instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        var instanceId = instance.InstanceId;
        await host.SampleUntilAsync(async () => (await host.InstanceAsync(instanceId).ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Running), CompletionTimeout).ConfigureAwait(false);

        // Simulate a crash outside the host's control.
        using (var process = Process.GetProcessById(instance.ProcessId!.Value))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        instance = await host.InstanceAsync(instanceId).ConfigureAwait(false);
        instance.State.Should().Be(nameof(ElasticRunnerInstanceState.Orphaned), "the dead instance is reaped even though the remaining backlog provisions a replacement");
        instance.Reason.Should().Be("process-exited");
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingRunnerStatus.Retired, "the registry no longer counts the orphan as capacity");
        }
        var health = await host.HealthAsync().ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Degraded);
        ((int)health.Data["orphansCleanedLastSample"]).Should().Be(1);
    }

    [TestMethod]
    public async Task LiveWorkIsRefusedByTheProviderBeforeAnyProcessStarts()
    {
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        // The provider is exercised directly here: the hosted autoscaler is stopped so its reconciliation cannot
        // retire the unrecorded instances this test launches.
        await host.Autoscaler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        var request = new ElasticRunnerProvisionRequest("livetest", "elastic-local-process-livetest",
            [ProcessingRunnerJobClass.CentralRecipe, ProcessingRunnerJobClass.CameraAgentLive], ["provider:local-process"], 1, null);
        await FluentActions.Awaiting(() => host.Provider.ProvisionAsync(request, CancellationToken.None))
            .Should().ThrowAsync<ElasticWorkloadClassException>().ConfigureAwait(false);
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty("no process was started for the refused request");

        // Provisioning is idempotent per instance id: a retried request returns the instance already launched.
        var eligible = new ElasticRunnerProvisionRequest("idem0001", "elastic-local-process-idem0001", CentralElasticProviderOptions.EligibleJobClasses, ["provider:local-process"], 1, null);
        var first = await host.Provider.ProvisionAsync(eligible, CancellationToken.None).ConfigureAwait(false);
        var second = await host.Provider.ProvisionAsync(eligible, CancellationToken.None).ConfigureAwait(false);
        second.ProcessId.Should().Be(first.ProcessId);
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().HaveCount(1);
        await host.Provider.RetireAsync("idem0001", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        (await host.Provider.ListAsync(CancellationToken.None).ConfigureAwait(false)).Should().BeEmpty();
        // Provisioned runners claim through processing-runner-v1, which refuses live classes at claim for every runner
        // (Runner_CannotClaimLiveOrReplayClassesOrInProcessPlacedRecipes); the boundary refuses them one step earlier.
    }

    [TestMethod]
    public async Task LocalOnlyModeNeverCallsAProvider()
    {
        var counting = new CountingProvider();
        using var factory = AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
            builder.ConfigureServices(services => services.AddSingleton<IElasticRunnerProvider>(counting));
        });
        await SeedPreviewJobAsync("elastic-local-only").ConfigureAwait(false);
        var autoscaler = factory.Services.GetServices<IHostedService>().OfType<ElasticRunnerAutoscaler>().Single();
        await autoscaler.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        factory.Services.GetRequiredService<IOptions<CentralElasticProviderOptions>>().Value.Enabled.Should().BeFalse("ElasticProviders is absent by default");
        counting.Calls.Should().Be(0, "a disabled host never touches a provider adapter");
        var health = await new CentralElasticProviderHealthCheck(
            factory.Services.GetRequiredService<IOptions<CentralElasticProviderOptions>>(), counting,
            factory.Services.GetRequiredService<ElasticProviderTelemetry>())
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Healthy);
        ((bool)health.Data["enabled"]).Should().BeFalse();
        await autoscaler.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public void ScriptedFactoryRemovesOnlyTheAutomaticElasticAutoscaler()
    {
        var services = new ServiceCollection();
        services.AddHostedService<UnrelatedHostedService>();
        services.AddHostedService<ElasticRunnerAutoscaler>();

        RemoveAutomaticElasticAutoscaler(services);

        services.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(HostedServiceType)
            .Should().Equal(typeof(UnrelatedHostedService));

        var alreadySuppressed = new ServiceCollection();
        alreadySuppressed.AddHostedService<UnrelatedHostedService>();
        RemoveAutomaticElasticAutoscaler(alreadySuppressed);
        alreadySuppressed.Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(HostedServiceType)
            .Should().Equal(typeof(UnrelatedHostedService));
    }

    [TestMethod]
    public async Task AReservedRetirementLeftByAPreviousHostIsCompletedNotOrphaned()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        await SeedPreviewJobAsync("elastic-resume").ConfigureAwait(false);
        (await host.Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Provision.Should().Be(1);
        var instance = await host.SingleInstanceAsync().ConfigureAwait(false);
        var instanceId = instance.InstanceId;
        await host.SampleUntilAsync(async () => (await host.InstanceAsync(instanceId).ConfigureAwait(false)).State == nameof(ElasticRunnerInstanceState.Running), CompletionTimeout).ConfigureAwait(false);

        // The host exits after the retirement reservation committed but before the drain request went out.
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CentralElasticRunnerInstances.Where(row => row.InstanceId == instanceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, nameof(ElasticRunnerInstanceState.Stopping)))
                .ConfigureAwait(false);
        }
        ProcessIsAlive(instance.ProcessId!.Value).Should().BeTrue("the reserved runner is still running when the next host process starts");

        // A fresh host process (new provider tracking, nothing adopted yet) re-adopts the reserved retirement and completes it.
        var services = host.Factory.Services;
        using var restartedProvider = new LocalProcessElasticRunnerProvider(
            services.GetRequiredService<IOptions<CentralElasticProviderOptions>>(), TimeProvider.System,
            services.GetRequiredService<ILogger<LocalProcessElasticRunnerProvider>>());
        var restarted = CreateAutoscaler(services, restartedProvider, services.GetRequiredService<IOptions<CentralElasticProviderOptions>>().Value, restartedProvider);
        await restarted.SampleAsync(CancellationToken.None).ConfigureAwait(false);

        instance = await host.InstanceAsync(instanceId).ConfigureAwait(false);
        instance.State.Should().Be(nameof(ElasticRunnerInstanceState.Stopped), "the reserved retirement is completed, not orphaned");
        instance.Reason.Should().Be(ElasticRunnerAutoscaler.ReasonRetirementResumed);
        ProcessIsAlive(instance.ProcessId!.Value).Should().BeFalse("the drain request that never went out is sent by the new host");
        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == instance.RunnerId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingRunnerStatus.Retired, "a completed retirement leaves no registration to revive");
        }
    }

    [TestMethod]
    public async Task ALaunchWhoseRecordCannotBeSavedRetiresTheProcessAndClosesItsIntent()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        // Host shutdown lands between the process start and the save of its record.
        using var shutdown = new CancellationTokenSource();
        var provider = new ScriptedProvider(_ => shutdown.Cancel());
        var autoscaler = CreateScriptedAutoscaler(provider, WarmOptions());

        var sample = () => autoscaler.SampleAsync(shutdown.Token);
        await sample.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);

        provider.Provisioned.Should().HaveCount(1);
        var instanceId = provider.Provisioned[0].InstanceId;
        provider.Retired.Should().Equal([instanceId], "a process without a record is retired");
        var row = await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false);
        row.State.Should().Be(nameof(ElasticRunnerInstanceState.Stopped), "the interrupted intent is closed, so no replica counts it until owner-stale cleanup");
        row.Reason.Should().Be("launch-aborted");
        // The capacity is free again at once: the next sample provisions the warm instance.
        (await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Provision.Should().Be(1);
    }

    [TestMethod]
    public async Task ReRegistrationIsSerializedBehindAnInFlightAbandonmentAndDenied()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        var request = ScriptedRegistration(runnerId);
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: "host-that-died", keepWarm: true).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, request, CancellationToken.None).ConfigureAwait(false);
        }

        // Another replica is abandoning the instance: it holds the per-runner lock with the abandonment not yet committed.
        await using var abandoning = factory.Services.CreateAsyncScope();
        var abandoningDb = abandoning.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var abandonment = await abandoningDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await ElasticRunnerRegistrationLock.AcquireAsync(abandoningDb, runnerId, CancellationToken.None).ConfigureAwait(false);

        // The runner re-registers concurrently; nothing but the lock can hold it back yet.
        var registration = Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, request, CancellationToken.None).ConfigureAwait(false);
        });
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        registration.IsCompleted.Should().BeFalse("registration waits behind the in-flight abandonment instead of racing its check against it");

        var now = DateTimeOffset.UtcNow;
        var row = await abandoningDb.CentralElasticRunnerInstances.SingleAsync(candidate => candidate.InstanceId == instanceId).ConfigureAwait(false);
        row.State = nameof(ElasticRunnerInstanceState.Abandoned);
        row.Reason = "owner-lost";
        row.StoppedAtUtc = now;
        row.UpdatedAtUtc = now;
        await abandoningDb.SaveChangesAsync().ConfigureAwait(false);
        await abandoningDb.CentralProcessingRunners.Where(runner => runner.RunnerId == runnerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, now)
                .SetProperty(runner => runner.UpdatedAtUtc, now))
            .ConfigureAwait(false);
        await abandonment.CommitAsync().ConfigureAwait(false);

        var awaitRegistration = async () => await registration.ConfigureAwait(false);
        (await awaitRegistration.Should().ThrowAsync<CentralProcessingRunnerRejectedException>().ConfigureAwait(false))
            .Which.ReasonCode.Should().Be(ProcessingRunnerReasonCodes.RegistrationDenied, "the registration that waited sees the committed abandonment");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.CentralProcessingRunners.AsNoTracking().SingleAsync(runner => runner.RunnerId == runnerId).ConfigureAwait(false))
                .Status.Should().Be(CentralProcessingRunnerStatus.Retired, "the retired registration is never revived");
        }
    }

    [TestMethod]
    public async Task WarmReplacementWaitsForAnExcessInstanceWithNoWorkInFlight()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        var provider = new ScriptedProvider();
        provider.MarkAlive(instanceId, runnerId);
        // One excess (non-warm) instance fills the pool and is busy with a job.
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId), CancellationToken.None).ConfigureAwait(false);
        }
        await SetAvailableSlotsAsync(factory, runnerId, 0).ConfigureAwait(false);
        var autoscaler = CreateScriptedAutoscaler(provider, WarmOptions());

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonWarmMinimum);
        decision.Retire.Should().Be(1, "the policy asks for one excess instance to make room for the warm replacement");
        provider.Retired.Should().BeEmpty("busy work is never force-terminated to restore the warm designation");
        (await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false)).State.Should().Be(nameof(ElasticRunnerInstanceState.Running));

        // The heartbeat-reported slots lag an accepted claim: an unexpired lease keyed to the runner still counts as busy.
        await SetAvailableSlotsAsync(factory, runnerId, 1).ConfigureAwait(false);
        var sourceId = await SeedPreviewJobAsync("elastic-lease-busy").ConfigureAwait(false);
        await SetLeaseAsync(factory, sourceId, runnerId, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);
        await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        provider.Retired.Should().BeEmpty("a runner holding an unexpired lease is busy whatever its last heartbeat reported");
        (await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false)).State.Should().Be(nameof(ElasticRunnerInstanceState.Running));

        // Once the excess instance has nothing in flight it is retired and the warm replacement follows.
        await SetLeaseAsync(factory, sourceId, runnerId, DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);
        await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        provider.Retired.Should().Equal([instanceId]);
        var row = await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false);
        row.State.Should().Be(nameof(ElasticRunnerInstanceState.Stopped));
        row.Reason.Should().Be(ElasticScalingPolicy.ReasonWarmMinimum);
        decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1);
        provider.Provisioned.Single().KeepWarm.Should().BeTrue("the replacement carries the warm designation");
    }

    [TestMethod]
    public async Task ABlockingDrainKeepsTheOwnerHeartbeatFreshAndStampsTheRealStopTime()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        var provider = new ScriptedProvider { RetireDelay = TimeSpan.FromSeconds(3) };
        provider.MarkAlive(instanceId, runnerId);
        // A reserved retirement whose drain never went out: the sample completes it, and the drain blocks for three seconds.
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false, state: nameof(ElasticRunnerInstanceState.Stopping)).ConfigureAwait(false);
        var settings = WarmOptions(minWarm: 0);
        ElasticRunnerAutoscaler.OwnerHeartbeatInterval(settings).Should().Be(TimeSpan.FromMilliseconds(500), "half the one-second retire grace");
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        var sampleStarted = DateTimeOffset.UtcNow;
        var sample = autoscaler.SampleAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
        var first = (await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false)).OwnerHeartbeatAtUtc;
        await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
        var second = (await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false)).OwnerHeartbeatAtUtc;
        sample.IsCompleted.Should().BeFalse("the drain is still blocking");
        first.Should().BeAfter(sampleStarted.AddSeconds(-1));
        second.Should().BeAfter(first, "the owner heartbeat is renewed on its own connection while the drain blocks, so no peer can declare this owner lost");

        await sample.ConfigureAwait(false);
        var row = await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false);
        row.State.Should().Be(nameof(ElasticRunnerInstanceState.Stopped));
        row.StoppedAtUtc.Should().BeOnOrAfter(sampleStarted.AddSeconds(3), "the stop is stamped when the drain completed, so instance minutes cover the drain");
    }

    [TestMethod]
    public async Task AClaimIsRefusedWhileTheInstanceRetirementIsReserved()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false, state: nameof(ElasticRunnerInstanceState.Stopping)).ConfigureAwait(false);
        await SeedPreviewJobAsync("elastic-claim-refused").ConfigureAwait(false);
        await using var scope = factory.Services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>();
        var request = ScriptedRegistration(runnerId);
        await registry.RegisterAsync(ScriptedSubject, request, CancellationToken.None).ConfigureAwait(false);
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runner = await db.CentralProcessingRunners.SingleAsync(candidate => candidate.RunnerId == runnerId).ConfigureAwait(false);
        var eligible = factory.Services.GetRequiredService<IOptions<CentralProcessingRunnerOptions>>().Value.ResolveEligibleRecipes(request.Capabilities);
        eligible.Should().Contain(BuiltInProcessingRecipes.EncodedPreview, "the seeded backlog is claimable by this runner in principle");
        var jobs = scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerJobService>();

        var claim = await jobs.ClaimAsync(new CentralProcessingRunnerContext(runner, request.Capabilities, eligible),
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None).ConfigureAwait(false);
        claim.Should().BeNull("an instance whose retirement is reserved takes no new work it would be forced to abandon");
        (await db.CentralDerivativeJobs.AsNoTracking().CountAsync(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseOwner == runnerId).ConfigureAwait(false))
            .Should().Be(0);

        // A registration resolved just before the autoscaler closed the instance (process exit, registration timeout)
        // is refused too: every state but Starting and Running is closed for claiming.
        var closedId = Guid.NewGuid().ToString("N")[..16];
        var closedRunnerId = $"elastic-scripted-{closedId}";
        await SeedScriptedInstanceAsync(factory, closedId, closedRunnerId, hostName: Environment.MachineName, keepWarm: false, state: nameof(ElasticRunnerInstanceState.Orphaned)).ConfigureAwait(false);
        var closedRequest = ScriptedRegistration(closedRunnerId);
        await registry.RegisterAsync(ScriptedSubject, closedRequest, CancellationToken.None).ConfigureAwait(false);
        var closedRunner = await db.CentralProcessingRunners.SingleAsync(candidate => candidate.RunnerId == closedRunnerId).ConfigureAwait(false);
        (await jobs.ClaimAsync(new CentralProcessingRunnerContext(closedRunner, closedRequest.Capabilities, eligible),
                new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0), CancellationToken.None).ConfigureAwait(false))
            .Should().BeNull("a closed instance never leases work through a stale registration");
    }

    [TestMethod]
    public async Task AnExpiredRunnerLeaseCountsAsBacklogAndProvisionsAReplacement()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var autoscaler = CreateScriptedAutoscaler(provider, WarmOptions(minWarm: 0));
        (await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(ElasticScalingDecision.Steady);
        // The only runner crashed holding the job: its lease expires, and nothing else would ever provision a runner for it.
        var sourceId = await SeedPreviewJobAsync("elastic-expired-lease").ConfigureAwait(false);
        await SetLeaseAsync(factory, sourceId, "elastic-scripted-crashed", DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1, "an expired lease is reclaimable backlog for the claim, so it is backlog for the autoscaler");
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonBacklog);
        provider.Provisioned.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task OwnerLossCleanupIsReportedInTheHealthSnapshot()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: "host-that-died", keepWarm: false, ownerHeartbeatAtUtc: DateTimeOffset.UtcNow.AddHours(-7)).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var settings = WarmOptions(minWarm: 0);
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        (await ScriptedInstanceAsync(factory, instanceId).ConfigureAwait(false)).State.Should().Be(nameof(ElasticRunnerInstanceState.Abandoned));
        var health = await new CentralElasticProviderHealthCheck(
            Options.Create(settings), provider,
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<ElasticProviderTelemetry>())
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Degraded, "the sample abandoned an instance and retired its registration");
        ((int)health.Data["orphansCleanedLastSample"]).Should().Be(1);
    }

    [TestMethod]
    public async Task BacklogAProvisionedInstanceCouldNotClaimNeverProvisions()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var autoscaler = CreateScriptedAutoscaler(provider, WarmOptions(minWarm: 0), RunnerOptions(requiresGpu: true));
        await SeedPreviewJobAsync("elastic-gpu-required").ConfigureAwait(false);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Should().Be(ElasticScalingDecision.Steady, "a local instance registers without a GPU, so the GPU-only recipe is not elastic backlog");
        provider.Provisioned.Should().BeEmpty("no instance minutes are spent on work no instance could claim");
    }

    [TestMethod]
    public async Task IdleScaleDownKeepsEnoughRegisteredCapacityForTheDemand()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        // Two adopted instances registered with different concurrency (1 and 4 slots) under a 4-slot configuration.
        var small = Guid.NewGuid().ToString("N")[..16];
        var large = Guid.NewGuid().ToString("N")[..16];
        foreach (var (instanceId, slots) in new[] { (small, 1), (large, 4) })
        {
            var runnerId = $"elastic-scripted-{instanceId}";
            provider.MarkAlive(instanceId, runnerId);
            await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId, slots), CancellationToken.None).ConfigureAwait(false);
        }
        for (var i = 0; i < 4; i++)
        {
            await SeedPreviewJobAsync("elastic-capacity").ConfigureAwait(false);
        }
        var settings = WarmOptions(minWarm: 0);
        settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 2,
            MinWarmInstances = 0,
            MaxConcurrencyPerInstance = 4,
            ScaleToZeroAfter = TimeSpan.FromSeconds(1),
            SampleInterval = settings.SampleInterval,
            RetireGrace = settings.RetireGrace
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonIdle, "four queued jobs need one configured instance, so one idle instance may go");
        provider.Retired.Should().Equal([small], "retiring the four-slot instance would leave one registered slot for four jobs; the single-slot instance is the excess");
        (await ScriptedInstanceAsync(factory, large).ConfigureAwait(false)).State.Should().Be(nameof(ElasticRunnerInstanceState.Running));
    }

    [TestMethod]
    public async Task AnAdoptedInstanceThatCannotClaimTheBacklogDoesNotCoverIt()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        // An adopted ten-slot instance registered without the placed recipe: plenty of slots, none of them usable.
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        provider.MarkAlive(instanceId, runnerId);
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId, 10, withoutRecipe: BuiltInProcessingRecipes.EncodedPreview), CancellationToken.None).ConfigureAwait(false);
        }
        for (var i = 0; i < 5; i++)
        {
            await SeedPreviewJobAsync("elastic-incompatible").ConfigureAwait(false);
        }
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 3,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromMinutes(10),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonBacklog);
        decision.Provision.Should().Be(2, "the ten registered slots cannot claim encoded-preview, so the five jobs need new one-slot instances up to the maximum");
        provider.Provisioned.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task AnAdoptedInstanceThatCannotClaimCleanupOrOversizedInputsDoesNotCoverThem()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 2,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromMinutes(10),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        // Cleanup-only demand for a recipe the adopted instance never registered: it needs an instance that can claim it.
        var withoutRecipe = Guid.NewGuid().ToString("N")[..16];
        var withoutRecipeRunner = $"elastic-scripted-{withoutRecipe}";
        provider.MarkAlive(withoutRecipe, withoutRecipeRunner);
        await SeedScriptedInstanceAsync(factory, withoutRecipe, withoutRecipeRunner, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(withoutRecipeRunner, 10, withoutRecipe: BuiltInProcessingRecipes.EncodedPreview), CancellationToken.None).ConfigureAwait(false);
        }
        var cleanupSource = await SeedPreviewJobAsync("elastic-cleanup-incompatible").ConfigureAwait(false);
        await SetLeaseAsync(factory, cleanupSource, "elastic-scripted-crashed", DateTimeOffset.UtcNow.AddMinutes(-1), exhausted: true).ConfigureAwait(false);
        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1, "terminal cleanup needs an instance registered with the recipe; the adopted instance without it covers nothing");
        provider.Provisioned.Should().HaveCount(1);

        // A registration whose transfer limit is below the queued job's inputs cannot claim it either.
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        provider = new ScriptedProvider();
        autoscaler = CreateScriptedAutoscaler(provider, settings);
        var small = Guid.NewGuid().ToString("N")[..16];
        var smallRunner = $"elastic-scripted-{small}";
        provider.MarkAlive(small, smallRunner);
        await SeedScriptedInstanceAsync(factory, small, smallRunner, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(smallRunner, 10, maxTransferBytes: 1), CancellationToken.None).ConfigureAwait(false);
        }
        await SeedPreviewJobAsync("elastic-oversized").ConfigureAwait(false);
        decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1, "the adopted instance's one-byte transfer limit excludes the queued job's inputs, so its ten slots cover nothing");

        // An instance that serves the smaller inputs keeps its capacity: only the job it cannot claim provisions more.
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        provider = new ScriptedProvider();
        autoscaler = CreateScriptedAutoscaler(provider, settings);
        var medium = Guid.NewGuid().ToString("N")[..16];
        var mediumRunner = $"elastic-scripted-{medium}";
        provider.MarkAlive(medium, mediumRunner);
        await SeedScriptedInstanceAsync(factory, medium, mediumRunner, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(mediumRunner, 10, maxTransferBytes: 16), CancellationToken.None).ConfigureAwait(false);
        }
        for (var i = 0; i < 5; i++)
        {
            await SeedPreviewJobAsync("elastic-small").ConfigureAwait(false);
        }
        await SeedPreviewJobAsync("elastic-large", new byte[64]).ConfigureAwait(false);
        decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Should().Be(new ElasticScalingDecision(1, 0, ElasticScalingPolicy.ReasonBacklog), "the sixteen-byte limit serves the five small jobs on ten slots; only the 64-byte job needs a new instance");
        provider.Retired.Should().BeEmpty("the useful instance is never retired for the one job it cannot claim");
    }

    [TestMethod]
    public async Task RegisteredSlotsAreAllocatedByWhatEachInstanceCanClaim()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        // A ten-slot instance that takes only small inputs and a one-slot instance that takes anything.
        foreach (var (slots, limit) in new[] { (10, 16L), (1, ProcessingRunnerProtocol.MaximumTransferBytes) })
        {
            var instanceId = Guid.NewGuid().ToString("N")[..16];
            var runnerId = $"elastic-scripted-{instanceId}";
            provider.MarkAlive(instanceId, runnerId);
            await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId, slots, maxTransferBytes: limit), CancellationToken.None).ConfigureAwait(false);
        }
        await SeedPreviewJobAsync("elastic-class-small").ConfigureAwait(false);
        for (var i = 0; i < 10; i++)
        {
            await SeedPreviewJobAsync("elastic-class-large", new byte[64]).ConfigureAwait(false);
        }
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 5,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromMinutes(10),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Should().Be(new ElasticScalingDecision(3, 0, ElasticScalingPolicy.ReasonBacklog), "eleven aggregate slots do not cover ten large jobs: one fits the one-slot instance, nine are uncovered, three more instances fit the limit");
        provider.Retired.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CapabilityMatchingReassignsFlexibleCapacityInsteadOfProvisioning()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var registrations = new[]
        {
            (Id: Guid.NewGuid().ToString("N")[..16], Recipes: new[] { BuiltInProcessingRecipes.EncodedPreview, BuiltInProcessingRecipes.JpegEncoding }, Transfer: 128L),
            (Id: Guid.NewGuid().ToString("N")[..16], Recipes: new[] { BuiltInProcessingRecipes.EncodedPreview }, Transfer: 256L)
        };
        foreach (var registration in registrations)
        {
            var runnerId = $"elastic-scripted-{registration.Id}";
            provider.MarkAlive(registration.Id, runnerId);
            await SeedScriptedInstanceAsync(factory, registration.Id, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId, maxTransferBytes: registration.Transfer), CancellationToken.None).ConfigureAwait(false);
            await SetEligibleRecipesAsync(factory, runnerId, registration.Recipes).ConfigureAwait(false);
        }
        var aSource = await SeedPreviewJobAsync("elastic-augment-a", new byte[100]).ConfigureAwait(false);
        var bSource = await SeedPreviewJobAsync("elastic-augment-b").ConfigureAwait(false);
        await RetainSingleJobAsync(factory, aSource, BuiltInProcessingRecipes.EncodedPreview).ConfigureAwait(false);
        await RetainSingleJobAsync(factory, bSource, BuiltInProcessingRecipes.JpegEncoding).ConfigureAwait(false);
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 2,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromHours(2),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        };
        var runnerSettings = RunnerOptions();
        runnerSettings.Placement[BuiltInProcessingRecipes.JpegEncoding] = CentralProcessingRunnerPlacement.Runner;
        var autoscaler = CreateScriptedAutoscaler(provider, settings, runnerSettings);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);

        decision.Should().Be(ElasticScalingDecision.Steady,
            "the large A job moves to the A-only runner so the flexible runner remains available for B");
        provider.Provisioned.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FullyOccupiedRegistrationDoesNotCoverQueuedWork()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        provider.MarkAlive(instanceId, runnerId);
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId), CancellationToken.None).ConfigureAwait(false);
        }
        await SetAvailableSlotsAsync(factory, runnerId, 0).ConfigureAwait(false);
        await SeedPreviewJobAsync("elastic-occupied-backlog").ConfigureAwait(false);
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 2,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromMinutes(10),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);

        decision.Should().Be(new ElasticScalingDecision(1, 0, ElasticScalingPolicy.ReasonBacklog));
        provider.Provisioned.Should().ContainSingle("the occupied slot is unavailable to the queued job");
    }

    [TestMethod]
    public async Task LockedDecisionRebuildsDemandAfterAConcurrentClaim()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var runnerId = $"ordinary-runner-{Guid.NewGuid():N}";
        await using (var registrationScope = factory.Services.CreateAsyncScope())
        {
            await registrationScope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId), CancellationToken.None).ConfigureAwait(false);
        }
        var sourceId = await SeedPreviewJobAsync("elastic-claim-race").ConfigureAwait(false);
        var autoscaler = CreateScriptedAutoscaler(provider, new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 2,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromHours(2),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        });
        await using var lockScope = factory.Services.CreateAsyncScope();
        await using var capacityLock = await CentralObjectApplicationLock.AcquireAsync(
            lockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            $"processing-runner-claim/{runnerId}",
            CancellationToken.None).ConfigureAwait(false);
        await using var claimScope = factory.Services.CreateAsyncScope();
        var claimDb = claimScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runner = await claimDb.CentralProcessingRunners.SingleAsync(candidate => candidate.RunnerId == runnerId).ConfigureAwait(false);
        var request = ScriptedRegistration(runnerId);
        var eligible = factory.Services.GetRequiredService<IOptions<CentralProcessingRunnerOptions>>().Value.ResolveEligibleRecipes(request.Capabilities);
        using var claimCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var claim = claimScope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerJobService>().ClaimAsync(
            new CentralProcessingRunnerContext(runner, request.Capabilities, eligible),
            new ProcessingRunnerClaimRequest(ProcessingRunnerJobClass.CentralRecipe, 0),
            claimCancellation.Token);
        await WaitForClaimBarrierAsync(factory).ConfigureAwait(false);

        var barrierWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        autoscaler.ClaimBarrierWaitStarted = () => barrierWait.TrySetResult();
        var sample = autoscaler.SampleAsync(CancellationToken.None);
        await barrierWait.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        sample.IsCompleted.Should().BeFalse("the exclusive fleet snapshot waits for the real ordinary-runner claim");
        await capacityLock.DisposeAsync().ConfigureAwait(false);
        (await claim.ConfigureAwait(false)).Should().NotBeNull("the real claim commits the queued job before the fleet snapshot proceeds");

        var decision = await sample.ConfigureAwait(false);

        decision.Should().Be(ElasticScalingDecision.Steady,
            "the locked rebuild sees the newly leased job instead of treating the stale queued row as uncovered");
        provider.Provisioned.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnIncompatibleInstanceAtTheLimitIsReplacedByOneThatCanClaim()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var instanceId = Guid.NewGuid().ToString("N")[..16];
        var runnerId = $"elastic-scripted-{instanceId}";
        provider.MarkAlive(instanceId, runnerId);
        await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId, 10, withoutRecipe: BuiltInProcessingRecipes.EncodedPreview), CancellationToken.None).ConfigureAwait(false);
        }
        await SeedPreviewJobAsync("elastic-replace-incompatible").ConfigureAwait(false);
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 1,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromMinutes(10),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(1)
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        // The manually driven autoscaler owns no dependency from this disposable host.
        // Disposing it before the sample reproduces the lifetime boundary that failed in #632.
        await factory.DisposeAsync().ConfigureAwait(false);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Should().Be(new ElasticScalingDecision(0, 1, ElasticScalingPolicy.ReasonIncompatibleReplacement), "the only instance cannot claim the queued recipe and fills the limit");
        provider.Retired.Should().Equal([instanceId]);
        var row = await ScriptedInstanceAsync(AssemblyHooks.Fixture.Factory, instanceId).ConfigureAwait(false);
        row.State.Should().Be(nameof(ElasticRunnerInstanceState.Stopped));
        row.Reason.Should().Be(ElasticScalingPolicy.ReasonIncompatibleReplacement);
        decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(1, "the freed slot goes to an instance that registers with the placed recipe");
    }

    [TestMethod]
    public async Task ConcurrentRetirementsStampTheirOwnStopTimes()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider();
        var quick = Guid.NewGuid().ToString("N")[..16];
        var slow = Guid.NewGuid().ToString("N")[..16];
        provider.RetireDelays[slow] = TimeSpan.FromSeconds(3);
        foreach (var instanceId in new[] { quick, slow })
        {
            var runnerId = $"elastic-scripted-{instanceId}";
            provider.MarkAlive(instanceId, runnerId);
            await SeedScriptedInstanceAsync(factory, instanceId, runnerId, hostName: Environment.MachineName, keepWarm: false).ConfigureAwait(false);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICentralProcessingRunnerRegistry>()
                .RegisterAsync(ScriptedSubject, ScriptedRegistration(runnerId), CancellationToken.None).ConfigureAwait(false);
        }
        var settings = new CentralElasticProviderOptions
        {
            Enabled = true,
            Provider = CentralElasticProviderKind.LocalProcess,
            MaxInstances = 2,
            MaxConcurrencyPerInstance = 1,
            ScaleToZeroAfter = TimeSpan.FromSeconds(1),
            SampleInterval = TimeSpan.FromHours(1),
            RetireGrace = TimeSpan.FromSeconds(10)
        };
        var autoscaler = CreateScriptedAutoscaler(provider, settings);

        var started = DateTimeOffset.UtcNow;
        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - started;
        decision.Retire.Should().Be(2);
        provider.Retired.Should().BeEquivalentTo([quick, slow]);
        var quickRow = await ScriptedInstanceAsync(factory, quick).ConfigureAwait(false);
        var slowRow = await ScriptedInstanceAsync(factory, slow).ConfigureAwait(false);
        quickRow.StoppedAtUtc.Should().BeBefore(started.AddSeconds(2), "the instance that exited at once is not charged for its sibling's drain");
        slowRow.StoppedAtUtc.Should().BeOnOrAfter(started.AddSeconds(3));
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "drains run concurrently");
    }

    [TestMethod]
    public async Task AProviderThatCannotDescribeAnInstanceProvisionsNothingAndDegradesHealth()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        using var factory = RunnerEnabledFactory();
        await ClearScriptedRowsAsync(factory).ConfigureAwait(false);
        var provider = new ScriptedProvider { Describable = false };
        var settings = WarmOptions();
        var autoscaler = CreateScriptedAutoscaler(provider, settings);
        await SeedPreviewJobAsync("elastic-undescribed").ConfigureAwait(false);

        var decision = await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
        decision.Provision.Should().Be(0, "an instance that cannot be described would abort before registration; the warm minimum waits too");
        decision.Reason.Should().Be(ElasticScalingPolicy.ReasonInstanceUndescribed);
        provider.Provisioned.Should().BeEmpty();
        var health = await new CentralElasticProviderHealthCheck(
            Options.Create(settings), provider,
            AssemblyHooks.Fixture.Factory.Services.GetRequiredService<ElasticProviderTelemetry>())
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        health.Status.Should().Be(HealthStatus.Degraded);
        ((string)health.Data["lastDecision"]).Should().Be(ElasticScalingPolicy.ReasonInstanceUndescribed);

        // Once the provider can describe an instance again, provisioning resumes.
        provider.Describable = true;
        (await autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false)).Provision.Should().Be(1);
    }

    [TestMethod]
    public async Task TheLocalAdapterDescribesInstancesFromTheConfiguredRunner()
    {
        await DisableClaimableJobsAsync(AssemblyHooks.Fixture.Factory).ConfigureAwait(false);
        await using var host = await ElasticHost.StartAsync(maxInstances: 1, scaleToZeroAfter: TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        var provider = (LocalProcessElasticRunnerProvider)host.Provider;
        var probed = await provider.ProbeConfiguredRunnerAsync(CancellationToken.None).ConfigureAwait(false);
        probed.Should().NotBeNull("the configured runner executable advertises its own capabilities");
        probed!.ProtocolVersion.Should().Be(ProcessingRunnerProtocol.Version);
        probed.BuiltInRecipes.Should().Contain(recipe => recipe.Name == BuiltInProcessingRecipes.EncodedPreview);
        var described = await provider.DescribeInstanceAsync(2, ["provider:local-process", "elastic-instance:probe"], CancellationToken.None).ConfigureAwait(false);
        described.Should().NotBeNull();
        described!.MaxConcurrency.Should().Be(2);
        described.Labels.Should().BeEquivalentTo(["elastic-instance:probe", "provider:local-process"]);
        described.RuntimeIdentifier.Should().Be(probed.RuntimeIdentifier, "the description comes from the probed runner, with the instance's own concurrency and labels");
    }

    private const string ScriptedSubject = "scripted-runner-subject";

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> RunnerEnabledFactory()
        => AssemblyHooks.Fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ProcessingRunners:Enabled", "true");
            builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
            builder.ConfigureTestServices(RemoveAutomaticElasticAutoscaler);
        });

    private static void RemoveAutomaticElasticAutoscaler(IServiceCollection services)
    {
        var autoscalers = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Where(static descriptor => HostedServiceType(descriptor) == typeof(ElasticRunnerAutoscaler))
            .ToArray();
        autoscalers.Should().HaveCountLessThanOrEqualTo(1,
            "the production host has at most one automatic elastic autoscaler registration");
        foreach (var descriptor in autoscalers)
        {
            services.Remove(descriptor);
        }
    }

    private static Type? HostedServiceType(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is not null)
        {
            return descriptor.ImplementationType;
        }
        return descriptor.ImplementationInstance?.GetType()
            ?? descriptor.ImplementationFactory?.Method.ReturnType;
    }

    /// <summary>Warm minimum of one within a pool of one: the shape every scripted scenario reasons about.</summary>
    private static CentralElasticProviderOptions WarmOptions(int minWarm = 1) => new()
    {
        Enabled = true,
        Provider = CentralElasticProviderKind.LocalProcess,
        MaxInstances = 1,
        MinWarmInstances = minWarm,
        MaxConcurrencyPerInstance = 1,
        ScaleToZeroAfter = TimeSpan.FromMinutes(10),
        SampleInterval = TimeSpan.FromHours(1),
        RetireGrace = TimeSpan.FromSeconds(1)
    };

    /// <summary>An autoscaler as a fresh host process would construct it: nothing adopted, the given provider in front.</summary>
    private static ElasticRunnerAutoscaler CreateAutoscaler(
        IServiceProvider services, IElasticRunnerProvider provider, CentralElasticProviderOptions settings, LocalProcessElasticRunnerProvider? localProcessProvider = null)
        => new(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            services.GetRequiredService<IOptions<CentralProcessingRunnerOptions>>(),
            services.GetRequiredService<IOptions<CentralProcessingEntitlementOptions>>(),
            provider,
            localProcessProvider ?? services.GetRequiredService<LocalProcessElasticRunnerProvider>(),
            services.GetRequiredService<ElasticProviderTelemetry>(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<ElasticRunnerAutoscaler>>());

    private static ElasticRunnerAutoscaler CreateScriptedAutoscaler(
        IElasticRunnerProvider provider,
        CentralElasticProviderOptions settings,
        CentralProcessingRunnerOptions? runnerSettings = null)
    {
        var services = AssemblyHooks.Fixture.Factory.Services;
        return new ElasticRunnerAutoscaler(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            Options.Create(runnerSettings ?? RunnerOptions()),
            services.GetRequiredService<IOptions<CentralProcessingEntitlementOptions>>(),
            provider,
            services.GetRequiredService<LocalProcessElasticRunnerProvider>(),
            services.GetRequiredService<ElasticProviderTelemetry>(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<ElasticRunnerAutoscaler>>());
    }

    private static CentralProcessingRunnerOptions RunnerOptions(bool requiresGpu = false)
        => new()
        {
            Enabled = true,
            Placement = new Dictionary<string, CentralProcessingRunnerPlacement>(StringComparer.Ordinal)
            {
                [BuiltInProcessingRecipes.EncodedPreview] = CentralProcessingRunnerPlacement.Runner
            },
            Requirements = requiresGpu
                ? new Dictionary<string, CentralProcessingRunnerRequirementOptions>(StringComparer.Ordinal)
                {
                    [BuiltInProcessingRecipes.EncodedPreview] = new() { RequiresGpu = true }
                }
                : new Dictionary<string, CentralProcessingRunnerRequirementOptions>(StringComparer.Ordinal)
        };

    private static ProcessingRunnerRegistrationRequest ScriptedRegistration(string runnerId, int maxConcurrency = 1, string? withoutRecipe = null, long? maxTransferBytes = null)
    {
        var capabilities = ProcessingRunnerCapabilities.CreateForCurrentProcess(maxConcurrency, maxTransferBytes ?? ProcessingRunnerProtocol.MaximumTransferBytes, null, null, null, null);
        if (withoutRecipe is not null)
        {
            capabilities = capabilities with { BuiltInRecipes = capabilities.BuiltInRecipes.Where(recipe => recipe.Name != withoutRecipe).ToArray() };
        }
        return new ProcessingRunnerRegistrationRequest(runnerId, "Scripted elastic runner", capabilities, Environment.ProcessId, DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private static async Task SeedScriptedInstanceAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, string instanceId, string runnerId, string hostName, bool keepWarm,
        string? state = null, DateTimeOffset? ownerHeartbeatAtUtc = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.CentralElasticRunnerInstances.Add(new CentralElasticRunnerInstance
        {
            Provider = ScriptedProvider.ProviderName,
            HostName = hostName,
            InstanceId = instanceId,
            RunnerId = runnerId,
            KeepWarm = keepWarm,
            OwnerHeartbeatAtUtc = ownerHeartbeatAtUtc ?? now,
            ProcessArchitecture = "x64",
            RuntimeImage = "test",
            State = state ?? nameof(ElasticRunnerInstanceState.Running),
            StartedAtUtc = now.AddHours(-1),
            RegisteredAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SetAvailableSlotsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, string runnerId, int availableSlots)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralProcessingRunners.Where(runner => runner.RunnerId == runnerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(runner => runner.AvailableSlots, availableSlots))
            .ConfigureAwait(false);
    }

    private static async Task SetEligibleRecipesAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        string runnerId,
        IReadOnlyCollection<string> recipes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var json = System.Text.Json.JsonSerializer.Serialize(recipes.Order(StringComparer.Ordinal));
        await db.CentralProcessingRunners.Where(runner => runner.RunnerId == runnerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(runner => runner.EligibleRecipesJson, json))
            .ConfigureAwait(false);
    }

    private static async Task WaitForClaimBarrierAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var probe = await CentralObjectApplicationLock.TryAcquireAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                CentralProcessingRunnerJobService.ClaimBarrier,
                CancellationToken.None).ConfigureAwait(false);
            if (probe is null)
            {
                return;
            }
            await probe.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }
        Assert.Fail("the real runner claim did not acquire the shared fleet barrier");
    }

    private static async Task RetainSingleJobAsync(
        WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory,
        Guid sourceArtifactId,
        string recipe)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var jobs = await db.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == sourceArtifactId)
            .OrderBy(job => job.Id)
            .ToListAsync()
            .ConfigureAwait(false);
        jobs.Should().NotBeEmpty();
        jobs[0].RecipeName = recipe;
        foreach (var job in jobs.Skip(1))
        {
            job.Status = CentralDerivativeJobStatus.TerminalFailure;
            job.AvailableAtUtc = null;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SetLeaseAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, Guid sourceArtifactId, string owner, DateTimeOffset leaseExpiresAtUtc, bool exhausted = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs
            .Where(job => job.SourceCentralArtifactId == sourceArtifactId && job.RecipeName == BuiltInProcessingRecipes.EncodedPreview)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.AttemptCount, job => exhausted ? job.MaxAttempts : job.AttemptCount)
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Leased)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseOwner, owner)
                .SetProperty(job => job.LeaseToken, Guid.NewGuid())
                .SetProperty(job => job.LeaseExpiresAtUtc, leaseExpiresAtUtc))
            .ConfigureAwait(false);
    }

    private static async Task<CentralElasticRunnerInstance> ScriptedInstanceAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, string instanceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CentralElasticRunnerInstances.AsNoTracking().SingleAsync(instance => instance.InstanceId == instanceId).ConfigureAwait(false);
    }

    private static async Task ClearScriptedRowsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralElasticRunnerInstances.Where(instance => instance.Provider == ScriptedProvider.ProviderName).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private static async Task<Guid> SeedPreviewJobAsync(string scenario, byte[]? payload = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
            $"{scenario}-{suffix}", Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-5), payload ?? SourcePayload, $"{scenario}-profile").ConfigureAwait(false);
    }

    private static async Task DisableClaimableJobsAsync(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CentralDerivativeJobs.Where(job => job.Status != CentralDerivativeJobStatus.Completed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null))
            .ConfigureAwait(false);
        await db.CentralProcessingRunners.Where(runner => runner.Status != CentralProcessingRunnerStatus.Retired)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(runner => runner.Status, CentralProcessingRunnerStatus.Retired)
                .SetProperty(runner => runner.RetiredAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(runner => runner.UpdatedAtUtc, DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
    }

    private static bool ProcessIsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class CountingProvider : IElasticRunnerProvider
    {
        public int Calls { get; private set; }
        public string Name => "counting";
        public ElasticProviderCapabilities Capabilities { get; } = new("counting", "x64", "test", true, []);
        public TimeSpan EstimateStartup() { Calls++; return TimeSpan.Zero; }
        public ValueTask<ProcessingRunnerCapabilities?> DescribeInstanceAsync(int maxConcurrency, IReadOnlyList<string> labels, CancellationToken cancellationToken) { Calls++; return ValueTask.FromResult<ProcessingRunnerCapabilities?>(ProcessingRunnerCapabilities.CreateForCurrentProcess(maxConcurrency, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, labels, null)); }
        public Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException(); }
        public Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<ElasticRunnerInstance>> ListAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult<IReadOnlyList<ElasticRunnerInstance>>([]); }
    }

    private sealed class UnrelatedHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>An in-memory provider whose instances live only in the test: provisions succeed, retirements are recorded.</summary>
    private sealed class ScriptedProvider(Action<ElasticRunnerProvisionRequest>? onProvisioned = null) : IElasticRunnerProvider
    {
        public const string ProviderName = "scripted";
        private readonly Dictionary<string, ElasticRunnerInstance> _alive = new(StringComparer.Ordinal);
        public List<ElasticRunnerProvisionRequest> Provisioned { get; } = [];
        public List<string> Retired { get; } = [];
        public string Name => ProviderName;
        public ElasticProviderCapabilities Capabilities { get; } = new(ProviderName, "x64", "test", true, []);
        public TimeSpan EstimateStartup() => TimeSpan.Zero;

        public bool Describable { get; set; } = true;

        public ValueTask<ProcessingRunnerCapabilities?> DescribeInstanceAsync(int maxConcurrency, IReadOnlyList<string> labels, CancellationToken cancellationToken)
            => ValueTask.FromResult(Describable ? ProcessingRunnerCapabilities.CreateForCurrentProcess(maxConcurrency, ProcessingRunnerProtocol.MaximumTransferBytes, null, null, labels, null) : null);

        public void MarkAlive(string instanceId, string runnerId)
            => _alive[instanceId] = new ElasticRunnerInstance(instanceId, runnerId, ElasticRunnerInstanceState.Running, DateTimeOffset.UtcNow.AddHours(-1), null);

        public Task<ElasticRunnerInstance> ProvisionAsync(ElasticRunnerProvisionRequest request, CancellationToken cancellationToken)
        {
            Provisioned.Add(request);
            var instance = new ElasticRunnerInstance(request.InstanceId, request.RunnerId, ElasticRunnerInstanceState.Starting, DateTimeOffset.UtcNow, null);
            _alive[request.InstanceId] = instance;
            onProvisioned?.Invoke(request);
            return Task.FromResult(instance);
        }

        public TimeSpan RetireDelay { get; set; }

        public Dictionary<string, TimeSpan> RetireDelays { get; } = new(StringComparer.Ordinal);

        public async Task RetireAsync(string instanceId, TimeSpan grace, CancellationToken cancellationToken)
        {
            var delay = RetireDelays.TryGetValue(instanceId, out var own) ? own : RetireDelay;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            lock (Retired)
            {
                Retired.Add(instanceId);
                _alive.Remove(instanceId);
            }
        }

        public Task<IReadOnlyList<ElasticRunnerInstance>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ElasticRunnerInstance>>(_alive.Values.ToList());
    }

    /// <summary>A Kestrel-hosted LogicHost with the local-process adapter enabled against the real runner binary in the test output.</summary>
    internal sealed class ElasticHost : IAsyncDisposable
    {
        private readonly string _secretFile;

        private ElasticHost(WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> factory, string secretFile)
        {
            Factory = factory;
            _secretFile = secretFile;
            Autoscaler = factory.Services.GetServices<IHostedService>().OfType<ElasticRunnerAutoscaler>().Single();
            Provider = factory.Services.GetRequiredService<IElasticRunnerProvider>();
        }

        public WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> Factory { get; }

        public ElasticRunnerAutoscaler Autoscaler { get; }

        public IElasticRunnerProvider Provider { get; }

        public static async Task<ElasticHost> StartAsync(int maxInstances, TimeSpan scaleToZeroAfter)
        {
            // The runner is launched from its own build output (the copy in the test output carries no framework
            // reference): the apphost when present, otherwise the dll through the current dotnet host.
            var (executable, arguments) = ResolveRunnerLaunch();
            var secretFile = Path.Combine(Path.GetTempPath(), $"hvo-elastic-secret-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(secretFile, TestClients.SystemProcessingRunner.ClientSecret).ConfigureAwait(false);
            // The runner needs the host address before the host starts, so a free loopback port is chosen up front.
            var port = FreePort();
            var factory = AssemblyHooks.Fixture.CreateKestrelFactory()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ProcessingRunners:Enabled", "true");
                    builder.UseSetting($"ProcessingRunners:Placement:{BuiltInProcessingRecipes.EncodedPreview}", "Runner");
                    builder.UseSetting("ProcessingRunners:HeartbeatInterval", "00:00:01");
                    builder.UseSetting("ProcessingRunners:StaleAfter", "00:00:30");
                    builder.UseSetting("ProcessingRunners:ClaimBackoff", "00:00:01");
                    builder.UseSetting("ElasticProviders:Enabled", "true");
                    builder.UseSetting("ElasticProviders:Provider", "LocalProcess");
                    builder.UseSetting("ElasticProviders:MaxInstances", maxInstances.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    builder.UseSetting("ElasticProviders:ScaleToZeroAfter", scaleToZeroAfter.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
                    builder.UseSetting("ElasticProviders:SampleInterval", "01:00:00");
                    builder.UseSetting("ElasticProviders:RetireGrace", "00:00:10");
                    builder.UseSetting("ElasticProviders:LocalProcess:Executable", executable);
                    for (var i = 0; i < arguments.Length; i++)
                    {
                        builder.UseSetting($"ElasticProviders:LocalProcess:Arguments:{i}", arguments[i]);
                    }
                    builder.UseSetting("ElasticProviders:LocalProcess:WorkingDirectory", Path.GetDirectoryName(arguments.Length == 0 ? executable : arguments[0])!);
                    builder.UseSetting("ElasticProviders:LocalProcess:LogicHostUrl", $"http://127.0.0.1:{port}/");
                    builder.UseSetting("ElasticProviders:LocalProcess:ClientId", TestClients.SystemProcessingRunner.ClientId);
                    builder.UseSetting("ElasticProviders:LocalProcess:ClientSecretFile", secretFile);
                    builder.UseSetting("ElasticProviders:LocalProcess:IdleShutdown", "00:00:00");
                    builder.UseSetting("ElasticProviders:LocalProcess:AllowInsecureHttp", "true");
                });
            factory.UseKestrel(options => options.ListenLocalhost(port));
            factory.CreateClient();
            var published = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault();
            new Uri(published ?? "http://127.0.0.1:0/").Port.Should().Be(port, "the runner was told the address the host actually listens on");
            // The hosted loop samples once at startup (finding no backlog because callers reset the database first)
            // and then waits for the one-hour interval; the tests drive every further sample explicitly.
            return new ElasticHost(factory, secretFile);
        }

        /// <summary>Locates the runner in its own build output for the test's configuration; exposed for the evidence harness.</summary>
        internal static (string Executable, string[] Arguments) ResolveRunnerLaunch()
        {
            var testBin = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = testBin.Parent?.Name ?? "Debug";
            var root = testBin;
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                root = root.Parent;
            }
            root.Should().NotBeNull("the repository root is discoverable from the test output");
            // The runner is self-contained per runtime identifier, so its output sits under net10.0/<rid>/.
            var frameworkBin = Path.Combine(root!.FullName, "src", "HVO.SkyMonitor.ProcessingRunner", "bin", configuration, "net10.0");
            var appHostName = OperatingSystem.IsWindows() ? "HVO.SkyMonitor.ProcessingRunner.exe" : "HVO.SkyMonitor.ProcessingRunner";
            foreach (var runnerBin in new[] { Path.Combine(frameworkBin, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier), frameworkBin })
            {
                var appHost = Path.Combine(runnerBin, appHostName);
                if (File.Exists(appHost))
                {
                    return (appHost, []);
                }
            }
            var dll = Path.Combine(frameworkBin, "HVO.SkyMonitor.ProcessingRunner.dll");
            File.Exists(dll).Should().BeTrue($"the runner build output exists under {frameworkBin}");
            return (Environment.ProcessPath ?? "dotnet", [dll]);
        }

        private static int FreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async Task<CentralElasticRunnerInstance> SingleInstanceAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralElasticRunnerInstances.AsNoTracking().OrderByDescending(instance => instance.StartedAtUtc).FirstAsync().ConfigureAwait(false);
        }

        public async Task<CentralElasticRunnerInstance> InstanceAsync(string instanceId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralElasticRunnerInstances.AsNoTracking().SingleAsync(instance => instance.InstanceId == instanceId).ConfigureAwait(false);
        }

        /// <summary>The seed helper returns the raw source artifact id; the runner-placed job is the preview job of that source.</summary>
        public async Task<Guid> PlacedJobIdAsync(Guid sourceArtifactId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == sourceArtifactId && job.RecipeName == BuiltInProcessingRecipes.EncodedPreview)
                .Select(job => job.Id).SingleAsync().ConfigureAwait(false);
        }

        public async Task<CentralDerivativeJobStatus> JobStatusAsync(Guid sourceArtifactId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == sourceArtifactId && job.RecipeName == BuiltInProcessingRecipes.EncodedPreview)
                .Select(job => job.Status).SingleAsync().ConfigureAwait(false);
        }

        public async Task SampleUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!await condition().ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("The elastic condition was not reached in time.");
                }
                await Task.Delay(500).ConfigureAwait(false);
                await Autoscaler.SampleAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!await condition().ConfigureAwait(false))
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("The condition was not reached in time.");
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        public async Task<HealthCheckResult> HealthAsync()
            => await new CentralElasticProviderHealthCheck(
                    Factory.Services.GetRequiredService<IOptions<CentralElasticProviderOptions>>(), Provider,
                    Factory.Services.GetRequiredService<ElasticProviderTelemetry>())
                .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            foreach (var instance in await Provider.ListAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await Provider.RetireAsync(instance.InstanceId, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            await Factory.DisposeAsync().ConfigureAwait(false);
            File.Delete(_secretFile);
        }
    }
}
