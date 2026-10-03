using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class NightlyProductAutomationTaskRegistryTests
{
    [TestMethod]
    public void Descriptor_OffersOnlyWindowTriggersPinnedStillTargetsAndBothSupportedPeriods()
    {
        WithRegistry((registry, _, _) =>
        {
            var descriptor = registry.Describe();
            Assert.IsTrue(descriptor.Available);
            Assert.AreEqual(LocalAutomationTaskKind.StillImageGeneration, descriptor.TaskKind);
            CollectionAssert.AreEqual(new[] { LocalAutomationTriggerKind.SourceWindowClosed }, descriptor.CompatibleTriggers.ToArray());
            Assert.HasCount(2, descriptor.Targets);
            Assert.HasCount(2, descriptor.SupportedSourceWindows);
            Assert.IsTrue(descriptor.Targets.All(target => NightlyProductPreset.TryParseTarget(target, out _)));
        });
    }

    [TestMethod]
    public void WrongSelectionAndLegacyPeriodicDefinition_AreRejected()
    {
        WithRegistry((registry, _, _) =>
        {
            var daily = NightlyProductFixture.Occurrence(NightlyProductKind.Keogram).Definition;
            Assert.IsNull(registry.Validate(daily));
            Assert.IsNotNull(registry.Validate(daily with
            {
                SourceWindow = daily.SourceWindow! with
                { Selection = LocalAutomationSourceSelection.DarkNightActualSources }
            }));
            Assert.IsNotNull(registry.Validate(daily with { TriggerKind = LocalAutomationTriggerKind.Periodic, SourceWindow = null }));
            Assert.IsNotNull(registry.Validate(daily with { TaskTarget = "keogram" }));
            Assert.IsNotNull(registry.Validate(daily with { TaskTarget = "star-trail:" + new string('0', 64) }));
        });
    }

    [TestMethod]
    public async Task Execute_PublishesTheRetainedOccurrenceAndReturnsADurableFailureForUnverifiableSource()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-adapter");
        try
        {
            var options = NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options());
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            var reader = new InMemoryNightlySourceReader();
            reader.Add(NightlyProductFixture.Frame(1, NightlyProductFixture.DayStartUtc.AddHours(16)));
            using var store = new SqliteNightlyProductStore(options, clock);
            using var generator = new NightlyProductGenerator(options, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
                reader, store, new AstronomyEnginePlanetEphemeris(), static () => null, clock);
            var registry = new NightlyProductAutomationTaskRegistry(generator, options);
            var success = await registry.ExecuteAsync(NightlyProductFixture.Occurrence(NightlyProductKind.Keogram), CancellationToken.None);
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, success.Outcome);
            reader.RestoreFailure = new InvalidDataException("expired source");
            var failure = await registry.ExecuteAsync(NightlyProductFixture.Occurrence(NightlyProductKind.StarTrail), CancellationToken.None);
            Assert.AreEqual(LocalAutomationRunOutcome.Failed, failure.Outcome);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WithRegistry(Action<NightlyProductAutomationTaskRegistry, SqliteNightlyProductStore, InMemoryNightlySourceReader> action)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-still-adapter");
        try
        {
            var options = NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options());
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            var reader = new InMemoryNightlySourceReader();
            using var store = new SqliteNightlyProductStore(options, clock);
            using var generator = new NightlyProductGenerator(options, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
                reader, store, new AstronomyEnginePlanetEphemeris(), static () => null, clock);
            action(new(generator, options), store, reader);
        }
        finally { Directory.Delete(root, true); }
    }
}
