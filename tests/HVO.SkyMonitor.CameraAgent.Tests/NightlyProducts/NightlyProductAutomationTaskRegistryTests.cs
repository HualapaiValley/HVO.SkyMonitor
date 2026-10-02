using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods do not require context-free continuations.")]
public sealed class NightlyProductAutomationTaskRegistryTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-registry");

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void Describe_OffersBothStillProductTargetsOnlyWhenEnabled()
    {
        using var enabled = Registry(NightlyProductFixture.Options());
        using var disabled = Registry(NightlyProductFixture.Options(enabled: false));

        var offered = enabled.Registry.Describe().Single();
        var withheld = disabled.Registry.Describe().Single();

        Assert.AreEqual(LocalAutomationTaskKind.NightlyProductGeneration, offered.TaskKind);
        Assert.IsTrue(offered.Available);
        CollectionAssert.AreEqual(new[] { LocalAutomationTriggerKind.Periodic }, offered.CompatibleTriggers.ToArray());
        CollectionAssert.AreEqual(
            new[] { NightlyProductContract.KeogramTarget, NightlyProductContract.StarTrailTarget },
            offered.Targets.ToArray());
        Assert.IsFalse(withheld.Available);
        Assert.IsEmpty(withheld.Targets);
        Assert.IsNotNull(withheld.UnavailableReason);
    }

    [TestMethod]
    public void Validate_RejectsOtherTriggersKindsAndTargets()
    {
        using var harness = Registry(NightlyProductFixture.Options());

        Assert.IsNull(harness.Registry.Validate(Definition(NightlyProductContract.StarTrailTarget)));
        Assert.AreEqual(
            LocalAutomationContract.UnregisteredCombinationReasonCode,
            harness.Registry.Validate(Definition(NightlyProductContract.KeogramTarget) with
            {
                TriggerKind = LocalAutomationTriggerKind.CaptureRelative
            })!.ReasonCode);
        Assert.AreEqual(
            LocalAutomationContract.UnregisteredCombinationReasonCode,
            harness.Registry.Validate(Definition(NightlyProductContract.KeogramTarget) with
            {
                TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition
            })!.ReasonCode);
        Assert.AreEqual(
            LocalAutomationContract.UnregisteredTargetReasonCode,
            harness.Registry.Validate(Definition("time-lapse"))!.ReasonCode);
    }

    [TestMethod]
    public async Task Execute_RunsTheGeneratorAndReportsItsOutcome()
    {
        using var harness = Registry(NightlyProductFixture.Options());

        var execution = await harness.Registry.ExecuteAsync(
            Definition(NightlyProductContract.KeogramTarget), "run-1", CancellationToken.None);

        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome);
        StringAssert.StartsWith(execution.Detail, "keogram: 24 windows evaluated", StringComparison.Ordinal);
        Assert.IsLessThanOrEqualTo(LocalAutomationContract.MaximumDetailLength, execution.Detail.Length);
    }

    [TestMethod]
    public async Task Execute_WhenDisabledOrForeign_SkipsOrFailsWithoutRunning()
    {
        using var disabled = Registry(NightlyProductFixture.Options(enabled: false));
        using var enabled = Registry(NightlyProductFixture.Options());

        var skipped = await disabled.Registry.ExecuteAsync(
            Definition(NightlyProductContract.KeogramTarget), "run-1", CancellationToken.None);
        var foreign = await enabled.Registry.ExecuteAsync(
            Definition(NightlyProductContract.KeogramTarget) with
            {
                TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition
            },
            "run-2",
            CancellationToken.None);

        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, skipped.Outcome);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, foreign.Outcome);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, SqliteNightlyProductStore.DirectoryName)));
    }

    [TestMethod]
    public void Outcome_MapsNotReadyFailedAndPendingRuns()
    {
        var ready = new NightlyProductRunReport(NightlyProductKind.StarTrail, true, 24, 0, 1, 0, 3, 0, 0, null);

        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, NightlyProductAutomationTaskRegistry.Outcome(ready));
        Assert.AreEqual(
            LocalAutomationRunOutcome.Failed,
            NightlyProductAutomationTaskRegistry.Outcome(ready with { FailedWindows = 1 }));
        Assert.AreEqual(
            LocalAutomationRunOutcome.Skipped,
            NightlyProductAutomationTaskRegistry.Outcome(ready with { Ready = false }));
        StringAssert.Contains(ready.Describe(), "3 pending", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Composite_DispatchesEachKindToItsOwnerAndRefusesDuplicateKinds()
    {
        using var harness = Registry(NightlyProductFixture.Options());
        var environmental = new StubAutomationTaskRegistry();
        var composite = new CompositeLocalAutomationTaskRegistry(environmental, harness.Registry);

        CollectionAssert.AreEqual(
            new[] { LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, LocalAutomationTaskKind.NightlyProductGeneration },
            composite.Describe().Select(static descriptor => descriptor.TaskKind).ToArray());
        Assert.IsNull(composite.Validate(Definition(NightlyProductContract.StarTrailTarget)));
        Assert.AreEqual(
            LocalAutomationRunOutcome.Succeeded,
            (await composite.ExecuteAsync(Definition(NightlyProductContract.StarTrailTarget), "run-1", CancellationToken.None))
                .Outcome);
        Assert.IsEmpty(environmental.ExecutedRunKeys);

        var alone = new CompositeLocalAutomationTaskRegistry(harness.Registry);
        var foreign = Definition("weather") with { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition };
        Assert.AreEqual(LocalAutomationContract.UnregisteredCombinationReasonCode, alone.Validate(foreign)!.ReasonCode);
        Assert.AreEqual(
            LocalAutomationRunOutcome.Failed,
            (await alone.ExecuteAsync(foreign, "run-2", CancellationToken.None)).Outcome);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new CompositeLocalAutomationTaskRegistry(harness.Registry, harness.Registry));
    }

    private static LocalAutomationDefinition Definition(string target) => new(
        "nightly-" + target,
        "Nightly " + target,
        true,
        LocalAutomationTaskKind.NightlyProductGeneration,
        target,
        LocalAutomationTriggerKind.Periodic,
        900,
        NightlyProductFixture.DayStartUtc);

    private RegistryHarness Registry(NightlyProductOptions nightly)
    {
        var options = NightlyProductFixture.HostOptions(_root, nightly);
        var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
        var store = new SqliteNightlyProductStore(options, clock);
        var generator = new NightlyProductGenerator(
            options,
            new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
            new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar),
            new InMemoryNightlySourceReader(),
            store,
            new AstronomyEnginePlanetEphemeris(),
            static () => null,
            clock);
        return new RegistryHarness(store, generator, new NightlyProductAutomationTaskRegistry(generator, options));
    }

    private sealed record RegistryHarness(
        SqliteNightlyProductStore Store,
        NightlyProductGenerator Generator,
        NightlyProductAutomationTaskRegistry Registry) : IDisposable
    {
        public void Dispose()
        {
            Generator.Dispose();
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}
