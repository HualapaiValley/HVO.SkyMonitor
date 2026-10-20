using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsAutomationAdapterTests : OrbitalStoreTestBase
{
    private static LocalAutomationDefinition Definition(
        string definitionId = OrbitalElementsAutomationAdapter.DefinitionId,
        string target = CelesTrakGpSource.DatasetKey,
        LocalAutomationTriggerKind trigger = LocalAutomationTriggerKind.Periodic,
        int interval = 21_600,
        LocalAutomationSourceWindowPolicy? window = null)
        => new(definitionId, "Orbital elements refresh", true, LocalAutomationTaskKind.OrbitalElementsRefresh, target,
            trigger, interval, OrbitalElementsStoreTestData.Start, window);

    private static async Task<(OrbitalRefreshTestHarness Harness, OrbitalElementsAutomationAdapter Adapter)> CreateAsync(
        string root, bool enabled = true, bool initialize = true)
    {
        var harness = await OrbitalRefreshTestHarness.CreateAsync(root, enabled, initialize: initialize).ConfigureAwait(false);
        var availability = new OrbitalElementsAvailability(harness.Store, NullLogger<OrbitalElementsAvailability>.Instance);
        return (harness, new OrbitalElementsAutomationAdapter(availability, harness.Job));
    }

    [TestMethod]
    public async Task TheTaskIsDescribedAsAPeriodicRunNowCapableRefreshOfTheActiveGroup()
    {
        var (harness, adapter) = await CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var _ = harness;

        var descriptor = adapter.Describe();

        Assert.AreEqual(LocalAutomationTaskKind.OrbitalElementsRefresh, adapter.TaskKind);
        Assert.AreEqual(LocalAutomationTaskKind.OrbitalElementsRefresh, descriptor.TaskKind);
        CollectionAssert.AreEqual(new[] { LocalAutomationTriggerKind.Periodic }, descriptor.CompatibleTriggers.ToArray());
        CollectionAssert.AreEqual(new[] { CelesTrakGpSource.DatasetKey }, descriptor.Targets.ToArray());
        Assert.IsTrue(descriptor.Available);
        Assert.IsNull(descriptor.UnavailableReason);
        Assert.AreEqual(
            "Refreshes CelesTrak GP active-group orbital elements within the provider's two-hour floor.",
            descriptor.Description);
        Assert.IsTrue(adapter.SupportsRunNow);
    }

    [TestMethod]
    [DataRow("id", "orbital.reserved-definition-id", "definition.definitionId")]
    [DataRow("target", "automation.unregisteredTarget", "definition.taskTarget")]
    [DataRow("trigger", "automation.unregisteredCombination", "definition.triggerKind")]
    [DataRow("window", "automation.unregisteredCombination", "definition.triggerKind")]
    public async Task DefinitionsOutsideTheReservedShapeAreRejected(string defect, string reasonCode, string field)
    {
        var (harness, adapter) = await CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var _ = harness;
        var definition = defect switch
        {
            "id" => Definition(definitionId: "orbital-elements-refresh-2"),
            "target" => Definition(target: "celestrak:gp:stations"),
            "trigger" => Definition(trigger: LocalAutomationTriggerKind.CaptureRelative),
            _ => Definition(window: new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
                LocalAutomationSourceWindowKind.SunriseDay, LocalAutomationSourceSelection.AllActualSources,
                TimeSpan.FromMinutes(5)))
        };

        var rejection = adapter.Validate(definition);

        Assert.IsNotNull(rejection);
        Assert.AreEqual(reasonCode, rejection.ReasonCode);
        Assert.AreEqual(field, rejection.FieldPath);
    }

    [TestMethod]
    [DataRow(60, "orbital.interval-below-provider-floor")]
    [DataRow(7_199, "orbital.interval-below-provider-floor")]
    [DataRow(7_200, null)]
    [DataRow(86_400, null)]
    [DataRow(86_401, "orbital.interval-out-of-range")]
    public async Task TheIntervalMustRespectTheProviderFloor(int interval, string? reasonCode)
    {
        var (harness, adapter) = await CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var _ = harness;

        var rejection = adapter.Validate(Definition(interval: interval));

        Assert.AreEqual(reasonCode, rejection?.ReasonCode);
        if (rejection is not null)
        {
            Assert.AreEqual("definition.triggerInterval", rejection.FieldPath);
        }
    }

    [TestMethod]
    public async Task AnUnavailableStoreRejectsTheDefinitionButADisabledFeatureDoesNot()
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var (lockedHarness, locked) = await CreateAsync(root, initialize: false).ConfigureAwait(false);
        using (lockedHarness)
        {
            var rejection = locked.Validate(Definition());

            Assert.IsNotNull(rejection);
            Assert.AreEqual(OrbitalElementsAvailability.StoreUnavailableReasonCode, rejection.ReasonCode);
            Assert.AreEqual("definition.taskKind", rejection.FieldPath);
        }
        owner.Dispose();

        var (disabledHarness, disabled) = await CreateAsync(CreateRoot(), enabled: false).ConfigureAwait(false);
        using (disabledHarness)
        {
            Assert.IsNull(disabled.Validate(Definition()));
        }
    }

    [TestMethod]
    public async Task TheInitialEpochIsTheInstallationPhaseWithinThirtyMinutes()
    {
        var (harness, adapter) = await CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var _ = harness;
        var now = OrbitalElementsStoreTestData.Start.AddMilliseconds(1_234);

        var epoch = adapter.ResolveInitialTriggerEpoch(now);

        Assert.AreEqual(now + harness.Store.PhaseOffset, epoch);
        Assert.AreEqual(TimeSpan.Zero, epoch.Offset);
        Assert.AreEqual(0, epoch.UtcTicks % TimeSpan.TicksPerMillisecond);
        Assert.IsTrue(epoch >= now && epoch - now < TimeSpan.FromMinutes(30));
        Assert.AreEqual(epoch, adapter.ResolveInitialTriggerEpoch(now), "The phase is deterministic per installation.");
    }

    [TestMethod]
    public async Task WithoutAnInitializedStoreTheEpochIsInvalidRatherThanAnException()
    {
        var (harness, adapter) = await CreateAsync(CreateRoot(), initialize: false).ConfigureAwait(false);
        using var _ = harness;

        Assert.AreEqual(DateTimeOffset.MinValue, adapter.ResolveInitialTriggerEpoch(OrbitalElementsStoreTestData.Start));
    }

    [TestMethod]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n7", "Manual")]
    [DataRow("orbital-elements-refresh|0123456789abcdef|20261019T120000Z", "Scheduled")]
    public async Task RunNowOccurrencesAreManualAttemptsAndEveryOtherOccurrenceIsScheduled(string runKey, string trigger)
    {
        var (harness, adapter) = await CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var _ = harness;
        harness.Client.Enqueue(harness.Retrieved(OrbitalRefreshTestHarness.ValidPayload()));
        var definition = Definition();
        var occurrence = new LocalAutomationOccurrence(LocalAutomationOccurrence.CurrentVersion, definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), runKey, harness.Clock.GetUtcNow(), null, "synthetic");

        var execution = await adapter.ExecuteAsync(occurrence, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome, execution.Detail);
        Assert.AreEqual($"Activated|orbital.activated|{trigger}", harness.LastAttempt());
        Assert.AreEqual(runKey, OrbitalElementsStoreTestData.Scalar(harness.Root, "SELECT run_key FROM orbital_attempts;"));
    }
}
