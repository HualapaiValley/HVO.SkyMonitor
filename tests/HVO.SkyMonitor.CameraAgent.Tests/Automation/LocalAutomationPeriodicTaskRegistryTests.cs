using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// Covers the periodic adapter family of the delivered registry: it is consulted first by task kind, it never
/// shares a kind with a source-window adapter or the environmental registry, and its own rules apply only after
/// the shared bounds, the periodic trigger, the absent source window, and a published target.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class LocalAutomationPeriodicTaskRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Describe_PublishesEnvironmentalThenWindowThenPeriodicDescriptors()
    {
        var periodic = new FakePeriodicTaskAdapter();
        using var harness = new PeriodicRegistryHarness(TimeProvider.System, [periodic], [new FakeWindowTaskAdapter()]);

        var kinds = harness.Registry.Describe().Select(static descriptor => descriptor.TaskKind).ToArray();

        CollectionAssert.AreEqual(new[]
        {
            LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            LocalAutomationTaskKind.StillImageGeneration,
            LocalAutomationTaskKind.OrbitalElementsRefresh
        }, kinds);
        Assert.AreSame(periodic.Descriptor, harness.Registry.Describe()[^1]);
    }

    [TestMethod]
    public void Validate_AcceptsThePeriodicKindOnlyAfterTheSharedRulesThenDelegatesItsOwnBounds()
    {
        var periodic = new FakePeriodicTaskAdapter();
        using var harness = new PeriodicRegistryHarness(TimeProvider.System, [periodic]);

        Assert.IsNull(harness.Registry.Validate(Definition()));
        Assert.AreEqual(1, periodic.ValidateCalls);
        var shorter = harness.Registry.Validate(Definition() with { TriggerInterval = 3_600 });
        Assert.AreEqual(FakePeriodicTaskAdapter.IntervalReasonCode, shorter!.ReasonCode);
        Assert.AreEqual("definition.triggerInterval", shorter.FieldPath);
        Assert.AreEqual(2, periodic.ValidateCalls);
        var belowShared = harness.Registry.Validate(Definition() with { TriggerInterval = 59 });
        Assert.AreEqual(LocalAutomationContract.InvalidCommandReasonCode, belowShared!.ReasonCode);
        Assert.AreEqual(2, periodic.ValidateCalls, "The shared bound rejects before the adapter is consulted.");
    }

    [TestMethod]
    [DataRow("capture-trigger", LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind")]
    [DataRow("source-window-trigger", LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind")]
    [DataRow("periodic-with-window", LocalAutomationContract.InvalidCommandReasonCode, "definition.sourceWindow")]
    [DataRow("unpublished-target", LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget")]
    [DataRow("unavailable-descriptor", LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind")]
    public void Validate_RejectsAPeriodicKindWithTheWrongShapeBeforeTheAdapter(string scenario, string reasonCode, string fieldPath)
    {
        var periodic = new FakePeriodicTaskAdapter();
        using var harness = new PeriodicRegistryHarness(TimeProvider.System, [periodic]);
        var policy = LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay);
        var definition = scenario switch
        {
            "capture-trigger" => Definition() with { TriggerKind = LocalAutomationTriggerKind.CaptureRelative, TriggerInterval = 10 },
            "source-window-trigger" => Definition() with
            {
                TriggerKind = LocalAutomationTriggerKind.SourceWindowClosed,
                TriggerInterval = 1,
                SourceWindow = policy
            },
            "periodic-with-window" => Definition() with { SourceWindow = policy },
            "unpublished-target" => Definition() with { TaskTarget = "missing-group" },
            _ => Definition()
        };
        if (scenario == "unavailable-descriptor")
        {
            periodic.Descriptor = periodic.Descriptor with { Available = false, UnavailableReason = "Disabled." };
        }

        var rejection = harness.Registry.Validate(definition);

        Assert.AreEqual(reasonCode, rejection!.ReasonCode);
        Assert.AreEqual(fieldPath, rejection.FieldPath);
        Assert.AreEqual(0, periodic.ValidateCalls);
    }

    [TestMethod]
    [DataRow("duplicate-kind")]
    [DataRow("window-kind")]
    [DataRow("environmental-kind")]
    [DataRow("undefined-kind")]
    [DataRow("descriptor-kind-mismatch")]
    [DataRow("source-window-trigger")]
    [DataRow("additional-trigger")]
    [DataRow("supported-source-window")]
    [DataRow("invalid-target")]
    public void Constructor_RejectsAnInvalidDuplicatedOrSharedPeriodicRegistration(string scenario)
    {
        var first = new FakePeriodicTaskAdapter();
        ILocalAutomationPeriodicTaskAdapter[] adapters = scenario switch
        {
            "duplicate-kind" => [first, new FakePeriodicTaskAdapter()],
            "window-kind" => [new FakePeriodicTaskAdapter(LocalAutomationTaskKind.StillImageGeneration)],
            "environmental-kind" => [new FakePeriodicTaskAdapter(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition)],
            "undefined-kind" => [new FakePeriodicTaskAdapter((LocalAutomationTaskKind)99)],
            _ => [first]
        };
        first.Descriptor = scenario switch
        {
            "descriptor-kind-mismatch" => first.Descriptor with { TaskKind = LocalAutomationTaskKind.TimeLapseGeneration },
            "source-window-trigger" => first.Descriptor with { CompatibleTriggers = [LocalAutomationTriggerKind.SourceWindowClosed] },
            "additional-trigger" => first.Descriptor with
            {
                CompatibleTriggers = [LocalAutomationTriggerKind.Periodic, LocalAutomationTriggerKind.CaptureRelative]
            },
            "supported-source-window" => first.Descriptor with { SupportedSourceWindows = [LocalAutomationSourceWindowKind.SunriseDay] },
            "invalid-target" => first.Descriptor with { Targets = [new string('t', 129)] },
            _ => first.Descriptor
        };

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new PeriodicRegistryHarness(TimeProvider.System, adapters, [new FakeWindowTaskAdapter()]).Dispose());
    }

    [TestMethod]
    public void WithoutAPeriodicAdapter_TheNewKindIsUnregisteredAndExistingRoutingIsUnchanged()
    {
        using var harness = new PeriodicRegistryHarness(TimeProvider.System, []);
        var registry = harness.Registry;

        Assert.AreEqual(1, registry.Describe().Count);
        Assert.AreEqual(LocalAutomationContract.UnregisteredCombinationReasonCode, registry.Validate(Definition())!.ReasonCode);
        Assert.AreEqual(Now, registry.ResolveInitialTriggerEpoch(Definition(), Now));
        Assert.IsFalse(registry.SupportsRunNow(Definition()));
    }

    [TestMethod]
    public void InitialEpochAndRunNowSupport_RouteOnlyToThePeriodicAdapterOfTheDefinitionKind()
    {
        var periodic = new FakePeriodicTaskAdapter { Epoch = static now => now.AddMinutes(17) };
        using var harness = new PeriodicRegistryHarness(TimeProvider.System, [periodic]);
        var registry = harness.Registry;
        var environmental = Definition() with
        {
            TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            TaskTarget = "temperature"
        };

        Assert.AreEqual(Now.AddMinutes(17), registry.ResolveInitialTriggerEpoch(Definition(), Now));
        Assert.AreEqual(Now, registry.ResolveInitialTriggerEpoch(environmental, Now));
        Assert.AreEqual(1, periodic.EpochCalls);
        Assert.IsTrue(registry.SupportsRunNow(Definition()));
        Assert.IsFalse(registry.SupportsRunNow(environmental));
        periodic.SupportsRunNow = false;
        Assert.IsFalse(registry.SupportsRunNow(Definition()));
    }

    [TestMethod]
    public async Task ExecuteAsync_RunsOnlyARetainedValidPeriodicOccurrenceThroughItsAdapter()
    {
        var periodic = new FakePeriodicTaskAdapter();
        using var harness = new PeriodicRegistryHarness(TimeProvider.System, [periodic]);
        var registry = harness.Registry;
        var definition = Definition();
        var entry = new LocalAutomationRunnerEntry(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition), null, null);
        var occurrence = LocalAutomationOccurrence.Create(entry, LocalAutomationRunKeys.CreateRunNow(entry, 7), Now);

        var legacy = await registry.ExecuteAsync(definition, occurrence.RunKey, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, legacy.Outcome);
        Assert.IsEmpty(periodic.Executed);
        var executed = await registry.ExecuteAsync(occurrence, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, executed.Outcome);
        Assert.AreEqual(occurrence, periodic.Executed.Single());
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(periodic.Executed.Single().RunKey));
        periodic.MinimumIntervalSeconds = 10_800;
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped,
            (await registry.ExecuteAsync(occurrence, CancellationToken.None).ConfigureAwait(false)).Outcome);
        Assert.HasCount(1, periodic.Executed);
    }

    [TestMethod]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n1", true)]
    [DataRow("orbital-elements-refresh|0123456789ABCDEF|n42", true)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n9223372036854775807", true)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n9223372036854775808", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n0", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n01", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n-1", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n1 ", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|p1", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|m1", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|c1", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n1:retry2", false)]
    [DataRow("orbital-elements-refresh|0123456789abcde|n1", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdeg|n1", false)]
    [DataRow("Orbital|0123456789abcdef|n1", false)]
    [DataRow("orbital-elements-refresh|0123456789abcdef|n1|n2", false)]
    [DataRow("w:0123456789abcdef", false)]
    [DataRow("", false)]
    public void IsRunNow_RecognizesOnlyAnExplicitRunNowOccurrenceKey(string runKey, bool expected)
        => Assert.AreEqual(expected, LocalAutomationRunKeys.IsRunNow(runKey));

    [TestMethod]
    public void CreateRunNow_IsARecognizedKeyAndARetryJournalKeyMapsBackToIt()
    {
        var definition = Definition();
        var entry = new LocalAutomationRunnerEntry(definition, 3, LocalAutomationContract.ComputeRevisionSha256(definition), null, null);

        var runKey = LocalAutomationRunKeys.CreateRunNow(entry, 12);

        Assert.AreEqual($"orbital-elements-refresh|{entry.RevisionSha256[..16]}|n12", runKey);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(runKey));
        Assert.AreEqual(runKey, LocalAutomationRunKeys.OccurrenceRunKey(runKey + ":retry3"));
        Assert.AreEqual(runKey, LocalAutomationRunKeys.OccurrenceRunKey(runKey));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LocalAutomationRunKeys.CreateRunNow(entry, 0));
    }

    private static LocalAutomationDefinition Definition()
        => new("orbital-elements-refresh", "Orbital elements refresh", true, LocalAutomationTaskKind.OrbitalElementsRefresh,
            FakePeriodicTaskAdapter.Target, LocalAutomationTriggerKind.Periodic, 7_200, Now);
}
