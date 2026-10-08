using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// Covers the closed registry: it publishes only registered task kinds, compatible triggers, and
/// on-demand-capable targets, and it maps every acquisition disposition onto a bounded run outcome.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class EnvironmentalLocalAutomationTaskRegistryTests
{
    [TestMethod]
    public void CompositeWithoutInstalledWindowAdapter_DoesNotAdvertiseProducerCapabilities()
    {
        using var coordinator = CreateCoordinator();
        var registry = new LocalAutomationTaskRegistry(CreateRegistry(coordinator, enabled: true), [], TimeProvider.System);
        Assert.AreEqual(1, registry.Describe().Count);
        var unsupported = LocalAutomationWindowTests.Definition("daily",
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay));
        Assert.AreEqual(LocalAutomationContract.UnregisteredCombinationReasonCode, registry.Validate(unsupported)!.ReasonCode);
    }

    [TestMethod]
    public async Task InstalledWindowAdapter_RequiresSupportedTargetAndRetainedFinality()
    {
        using var coordinator = CreateCoordinator();
        var adapter = new WindowAdapter();
        var calendar = ObservingDayCalendar.ForDeployment(LocalAutomationWindowTests.Site());
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(calendar));
        var policy = LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay);
        var window = planner.ResolveWindows(new DateOnly(2026, 10, 12), policy).Single();
        var time = new MutableTimeProvider(window.EarliestFinalUtc.AddTicks(-1));
        var registry = new LocalAutomationTaskRegistry(CreateRegistry(coordinator, enabled: true), [adapter], time);
        var definition = LocalAutomationWindowTests.Definition("daily", policy);
        var occurrence = LocalAutomationWindowPlanner.CreateOccurrence(
            new(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition), null, null), window);
        Assert.AreEqual(2, registry.Describe().Count);
        Assert.IsNull(registry.Validate(definition));
        Assert.AreEqual(LocalAutomationContract.UnregisteredTargetReasonCode,
            registry.Validate(definition with { TaskTarget = "missing-rig" })!.ReasonCode);
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped,
            (await registry.ExecuteAsync(occurrence, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.IsEmpty(adapter.Executed);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded,
            (await registry.ExecuteAsync(occurrence, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(occurrence, adapter.Executed.Single());
    }

    private sealed class WindowAdapter : ILocalAutomationWindowTaskAdapter
    {
        public List<LocalAutomationOccurrence> Executed { get; } = [];
        public LocalAutomationTaskDescriptor Describe() => new(LocalAutomationTaskKind.StillImageGeneration,
            "Installed fixture", [LocalAutomationTriggerKind.SourceWindowClosed], ["test-rig"], true, null)
        { SupportedSourceWindows = [LocalAutomationSourceWindowKind.SunriseDay] };
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => null;
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            Executed.Add(occurrence);
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "Fixture"));
        }
    }

    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly LocalAutomationTriggerKind[] ExpectedTriggers =
    [
        LocalAutomationTriggerKind.Periodic,
        LocalAutomationTriggerKind.CaptureRelative
    ];
    private static readonly string[] ExpectedTargets = ["temperature"];

    [TestMethod]
    public void Describe_PublishesOnlyOnDemandCapableTargetsAndCompatibleTriggers()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: true);

        var descriptor = registry.Describe().Single();

        Assert.AreEqual(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, descriptor.TaskKind);
        Assert.IsTrue(descriptor.Available);
        CollectionAssert.AreEqual(ExpectedTriggers, descriptor.CompatibleTriggers.ToArray());
        // "polling-only" declares Periodic but not OnDemand, so it can never be an automation target.
        CollectionAssert.AreEqual(ExpectedTargets, descriptor.Targets.ToArray());
    }

    [TestMethod]
    public void Describe_ReportsUnavailableWhenEnvironmentalAcquisitionIsDisabled()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: false);

        var descriptor = registry.Describe().Single();

        Assert.IsFalse(descriptor.Available);
        Assert.IsEmpty(descriptor.Targets);
        Assert.IsNotNull(descriptor.UnavailableReason);
    }

    [TestMethod]
    public void Validate_RejectsAnUnregisteredTargetAndAcceptsARegisteredOne()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: true);

        Assert.IsNull(registry.Validate(Definition("temperature")));
        var rejection = registry.Validate(Definition("polling-only"));
        Assert.IsNotNull(rejection);
        Assert.AreEqual(LocalAutomationContract.UnregisteredTargetReasonCode, rejection.ReasonCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_MapsAProducedAcquisitionOntoASucceededRun()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: true, new RecordingCommandStore());

        var execution = await registry
            .ExecuteAsync(Definition("temperature"), "run-key-1", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome);
    }

    [TestMethod]
    public async Task ExecuteAsync_SkipsWhenTheDurableCommandStoreReportsTheSourceBusy()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: true, new BusyCommandStore());

        var execution = await registry
            .ExecuteAsync(Definition("temperature"), "run-key-1", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, execution.Outcome);
    }

    [TestMethod]
    public async Task ExecuteAsync_SkipsAnUnregisteredTargetWithoutContactingTheCoordinator()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: false, new ThrowingCommandStore());

        var execution = await registry
            .ExecuteAsync(Definition("temperature"), "run-key-1", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, execution.Outcome);
        Assert.IsTrue(execution.Detail.Contains(
            LocalAutomationContract.UnregisteredTargetReasonCode, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteAsync_RecordsAFailureWithoutLeakingTheExceptionMessage()
    {
        using var coordinator = CreateCoordinator();
        var registry = CreateRegistry(coordinator, enabled: true, new ThrowingCommandStore());

        var execution = await registry
            .ExecuteAsync(Definition("temperature"), "run-key-1", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Failed, execution.Outcome);
        Assert.IsFalse(execution.Detail.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ActorFor_NamesTheDefinitionRatherThanAnOperator()
        => Assert.AreEqual(
            "automation:sky-temperature",
            EnvironmentalLocalAutomationTaskRegistry.ActorFor("sky-temperature"));

    private static LocalAutomationDefinition Definition(string target)
        => new(
            "sky-temperature",
            "Sky temperature",
            true,
            LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            target,
            LocalAutomationTriggerKind.Periodic,
            3600,
            Epoch);

    private static EnvironmentalLocalAutomationTaskRegistry CreateRegistry(
        EnvironmentalAcquisitionCoordinator coordinator,
        bool enabled,
        IEnvironmentalOnDemandCommandStore? commands = null)
        => new(
            coordinator,
            new EnvironmentalOnDemandAcquisitionService(
                coordinator,
                commands ?? new RecordingCommandStore(),
                HostOptions(enabled),
                TimeProvider.System),
            HostOptions(enabled));

    private static IOptions<CameraAgentHostOptions> HostOptions(bool enabled)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.GetTempPath(),
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = enabled,
                MaximumConcurrency = 4,
                SourceTimeoutMilliseconds = 5_000,
                Sources = enabled ? [OnDemandSource(), PollingOnlySource()] : []
            }
        });

    private static EnvironmentalAcquisitionCoordinator CreateCoordinator()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new EnvironmentalSourceFactory(
            provider,
            [new EnvironmentalSourceRegistration(
                "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
        return new EnvironmentalAcquisitionCoordinator(
            factory,
            new RecordingPublisher(),
            new FixedDeploymentLocationStore(Location()),
            HostOptions(enabled: true),
            TimeProvider.System);
    }

    private static EnvironmentalSourceConfiguration OnDemandSource() => new()
    {
        Id = "temperature",
        Type = "VirtualEnvironment",
        Kind = EnvironmentalObservationKind.AirTemperature,
        Required = true,
        Triggers = [EnvironmentalAcquisitionTrigger.OnDemand, EnvironmentalAcquisitionTrigger.Periodic],
        ScheduleEpochUtc = Epoch,
        PeriodSeconds = 30,
        EveryNthCapture = 3,
        ValidForSeconds = 120,
        StaleAfterSeconds = 45,
        Options = CaptureContractJson.SerializeToElement(
            new VirtualEnvironmentalSourceOptions(209, Epoch, 12.5, null, 0.1, 0.2))
    };

    private static EnvironmentalSourceConfiguration PollingOnlySource() => new()
    {
        Id = "polling-only",
        Type = "VirtualEnvironment",
        Kind = EnvironmentalObservationKind.AirTemperature,
        Required = false,
        Triggers = [EnvironmentalAcquisitionTrigger.Periodic],
        ScheduleEpochUtc = Epoch,
        PeriodSeconds = 30,
        EveryNthCapture = 3,
        ValidForSeconds = 120,
        StaleAfterSeconds = 45,
        Options = CaptureContractJson.SerializeToElement(
            new VirtualEnvironmentalSourceOptions(210, Epoch, 11.5, null, 0.1, 0.2))
    };

    private static DeploymentLocationSnapshot Location()
        => DeploymentLocationSnapshot.Create(
            "location", 1, "test", null, Epoch.AddDays(-1), null, 35.5599378, -113.9119818, 520, "America/Phoenix");

    private sealed class RecordingPublisher : IEnvironmentalObservationPublisher
    {
        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new EnvironmentalObservationPublishResult(
                EnvironmentalObservationPublishDisposition.Enqueued, null));
    }

    private sealed class FixedDeploymentLocationStore(DeploymentLocationSnapshot active) : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; } = active;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => Active!;
    }

    private sealed class RecordingCommandStore : IEnvironmentalOnDemandCommandStore
    {
        public ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
            string root, string idempotencyKey, string payloadSha256, string sourceId, string actorId,
            string? reason, DateTimeOffset observedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalOnDemandCommandClaim(
                EnvironmentalOnDemandClaimDisposition.Claimed, "lease-1", observedAtUtc, null));

        public ValueTask CompleteOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, EnvironmentalAcquisitionReceipt receipt,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class BusyCommandStore : IEnvironmentalOnDemandCommandStore
    {
        public ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
            string root, string idempotencyKey, string payloadSha256, string sourceId, string actorId,
            string? reason, DateTimeOffset observedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalOnDemandCommandClaim(
                EnvironmentalOnDemandClaimDisposition.Busy, string.Empty, observedAtUtc, null));

        public ValueTask CompleteOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, EnvironmentalAcquisitionReceipt receipt,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class ThrowingCommandStore : IEnvironmentalOnDemandCommandStore
    {
        public ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
            string root, string idempotencyKey, string payloadSha256, string sourceId, string actorId,
            string? reason, DateTimeOffset observedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => ValueTask.FromException<EnvironmentalOnDemandCommandClaim>(
                new IOException("the durable secret path is unavailable"));

        public ValueTask CompleteOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, EnvironmentalAcquisitionReceipt receipt,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ReleaseOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
