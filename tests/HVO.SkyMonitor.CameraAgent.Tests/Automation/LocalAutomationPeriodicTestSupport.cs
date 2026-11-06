using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// A test-only periodic task adapter. It owns one task kind, publishes only the periodic trigger, and lets
/// the test control its phase origin, run-now support, minimum interval, and execution outcome.
/// </summary>
internal sealed class FakePeriodicTaskAdapter(
    LocalAutomationTaskKind taskKind = LocalAutomationTaskKind.OrbitalElementsRefresh) : ILocalAutomationPeriodicTaskAdapter
{
    public const string Target = "fixture-active";
    public const string IntervalReasonCode = "automation.fixture-interval";

    public LocalAutomationTaskKind TaskKind { get; } = taskKind;

    /// <summary>The descriptor this adapter publishes; a test replaces it to register an invalid shape.</summary>
    public LocalAutomationTaskDescriptor Descriptor { get; set; } = new(taskKind, "Fixture periodic refresh",
        [LocalAutomationTriggerKind.Periodic], [Target], true, null);

    /// <summary>The adapter's own minimum, longer than the shared periodic bound.</summary>
    public int MinimumIntervalSeconds { get; set; } = 7_200;

    public Func<DateTimeOffset, DateTimeOffset>? Epoch { get; set; }

    public int EpochCalls { get; private set; }

    public int ValidateCalls { get; private set; }

    public bool SupportsRunNow { get; set; } = true;

    public List<LocalAutomationOccurrence> Executed { get; } = [];

    public Func<LocalAutomationOccurrence, CancellationToken, ValueTask<LocalAutomationExecution>>? Executor { get; set; }

    public LocalAutomationTaskDescriptor Describe() => Descriptor;

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ValidateCalls++;
        return definition.TriggerInterval < MinimumIntervalSeconds
            ? new(IntervalReasonCode, "definition.triggerInterval")
            : null;
    }

    public DateTimeOffset ResolveInitialTriggerEpoch(DateTimeOffset nowUtc)
    {
        EpochCalls++;
        return Epoch?.Invoke(nowUtc) ?? nowUtc;
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        Executed.Add(occurrence);
        return Executor?.Invoke(occurrence, cancellationToken) ?? ValueTask.FromResult(new LocalAutomationExecution(
            LocalAutomationRunOutcome.Succeeded, "Fixture periodic refresh completed."));
    }
}

/// <summary>A test-only source-window adapter, used to prove the periodic family never shares its kinds.</summary>
internal sealed class FakeWindowTaskAdapter(
    LocalAutomationTaskKind taskKind = LocalAutomationTaskKind.StillImageGeneration) : ILocalAutomationWindowTaskAdapter
{
    public LocalAutomationTaskDescriptor Describe() => new(taskKind, "Fixture producer",
        [LocalAutomationTriggerKind.SourceWindowClosed], ["test-rig"], true, null)
    { SupportedSourceWindows = [LocalAutomationSourceWindowKind.SunriseDay] };

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => null;

    public ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "Fixture"));
}

/// <summary>
/// The delivered composite registry over a disabled environmental registry, so a test exercises the real
/// periodic, source-window, and environmental routing without any environmental source or network.
/// </summary>
internal sealed class PeriodicRegistryHarness : IDisposable
{
    private readonly EnvironmentalAcquisitionCoordinator _coordinator;

    public PeriodicRegistryHarness(TimeProvider timeProvider,
        IEnumerable<ILocalAutomationPeriodicTaskAdapter> periodicAdapters,
        IEnumerable<ILocalAutomationWindowTaskAdapter>? windowAdapters = null)
    {
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.GetTempPath(),
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions { Enabled = false, Sources = [] }
        });
        _coordinator = new EnvironmentalAcquisitionCoordinator(
            new EnvironmentalSourceFactory(new ServiceCollection().BuildServiceProvider(), []),
            new UnusedPublisher(), new UnusedDeploymentLocationStore(), options, timeProvider);
        Environmental = new EnvironmentalLocalAutomationTaskRegistry(_coordinator,
            new EnvironmentalOnDemandAcquisitionService(_coordinator, new UnusedCommandStore(), options, timeProvider),
            options);
        try
        {
            Registry = new LocalAutomationTaskRegistry(Environmental, windowAdapters ?? [], timeProvider, periodicAdapters);
        }
        catch
        {
            _coordinator.Dispose();
            throw;
        }
    }

    public EnvironmentalLocalAutomationTaskRegistry Environmental { get; }

    public LocalAutomationTaskRegistry Registry { get; }

    public void Dispose() => _coordinator.Dispose();

    private sealed class UnusedPublisher : IEnvironmentalObservationPublisher
    {
        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Environmental acquisition is disabled in this fixture.");
    }

    private sealed class UnusedDeploymentLocationStore : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active => null;

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => throw new NotSupportedException("Environmental acquisition is disabled in this fixture.");

        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException("Environmental acquisition is disabled in this fixture.");
    }

    private sealed class UnusedCommandStore : IEnvironmentalOnDemandCommandStore
    {
        public ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
            string root, string idempotencyKey, string payloadSha256, string sourceId, string actorId,
            string? reason, DateTimeOffset observedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
            => throw new NotSupportedException("Environmental acquisition is disabled in this fixture.");

        public ValueTask CompleteOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, EnvironmentalAcquisitionReceipt receipt,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Environmental acquisition is disabled in this fixture.");

        public ValueTask ReleaseOnDemandAsync(
            string root, string idempotencyKey, string leaseToken, CancellationToken cancellationToken)
            => throw new NotSupportedException("Environmental acquisition is disabled in this fixture.");
    }
}
