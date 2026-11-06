using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// A definition of the appended periodic task kind projects through the durable operator state, its JSON
/// contract, and every view of the Automations page without a kind-specific branch.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationPeriodicProjectionTests : IDisposable
{
    private const string DefinitionId = "orbital-elements-refresh";
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private string _root = null!;
    private MutableTimeProvider _time = null!;
    private FakePeriodicTaskAdapter _adapter = null!;
    private PeriodicRegistryHarness _harness = null!;
    private SqliteLocalAutomationStore _store = null!;
    private readonly StubCaptureSequenceSource _captures = new();

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-periodic-projection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _time = new(Start);
        _adapter = new() { Epoch = static now => now.AddMinutes(17) };
        _harness = new(_time, [_adapter]);
        _store = new(_harness.Registry, _captures, Options.Create(new CameraAgentHostOptions { RawIngressRoot = _root }),
            _time, NullLogger<SqliteLocalAutomationStore>.Instance);
        await _store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _store.Dispose();
        _harness.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [TestMethod]
    public async Task OperatorState_ProjectsTheNewKindThroughTheStoreAndItsJsonContract()
    {
        var state = await ProjectedStateAsync().ConfigureAwait(false);

        var descriptor = state.Registry.Single(static item => item.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh);
        Assert.AreEqual(FakePeriodicTaskAdapter.Target, descriptor.Targets.Single());
        var definition = state.Definitions.Single();
        Assert.AreEqual(LocalAutomationTaskKind.OrbitalElementsRefresh, definition.Definition.TaskKind);
        Assert.AreEqual(Start.AddMinutes(17).AddHours(2), definition.NextRunUtc);
        Assert.IsTrue(LocalAutomationRunKeys.IsRunNow(definition.LastRun!.RunKey));
        var due = state.Calendar.Single();
        Assert.AreEqual(LocalAutomationTaskKind.OrbitalElementsRefresh, due.TaskKind);
        Assert.AreEqual(definition.NextRunUtc, due.DueUtc);
        Assert.IsTrue(state.Runs.All(static run => LocalAutomationRunKeys.IsRunNow(run.RunKey)));
        CollectionAssert.AreEquivalent(new[] { LocalAutomationRunOutcome.Succeeded, LocalAutomationRunOutcome.Queued },
            state.Runs.Select(static run => run.Outcome).ToArray());
        Assert.AreEqual(1, state.QueuedRunCount);

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(state, web);
        Assert.Contains("\"taskKind\":\"OrbitalElementsRefresh\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation\":\"run-now\"", json, StringComparison.Ordinal);
        var definitionJson = JsonSerializer.Serialize(definition.Definition, web);
        Assert.AreEqual(definition.Definition, JsonSerializer.Deserialize<LocalAutomationDefinition>(definitionJson, web));
        Assert.AreEqual(LocalAutomationTaskKind.OrbitalElementsRefresh,
            JsonSerializer.Deserialize<LocalAutomationTaskKind>("\"OrbitalElementsRefresh\"", web));
    }

    [TestMethod]
    public async Task AutomationsPage_RendersTheNewKindInEveryViewAndItsEditor()
    {
        var state = await ProjectedStateAsync().ConfigureAwait(false);
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(
            new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(new AutomationsPageTests.AutomationUiService(state));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement(".automation-card");
        var card = cut.Find($"article.automation-card[aria-labelledby='automation-card-{DefinitionId}']");
        Assert.AreEqual("Orbital elements refresh", card.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("Fixture periodic refresh", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("Every 2 h", card.TextContent, StringComparison.Ordinal);
        Assert.AreEqual("Orbital Elements Refresh / revision 1", card.QuerySelector("footer > span")!.TextContent.Trim());
        Assert.IsFalse(cut.Find($"#automation-edit-{DefinitionId}").HasAttribute("disabled"));

        await cut.FindAll(".automation-tabs button").Single(static button => button.TextContent.Trim() == "Schedule")
            .ClickAsync().ConfigureAwait(false);
        Assert.Contains("Orbital elements refresh", cut.Find(".automation-upcoming").TextContent, StringComparison.Ordinal);
        await cut.FindAll(".automation-tabs button").Single(static button => button.TextContent.Trim() == "Run history")
            .ClickAsync().ConfigureAwait(false);
        var rows = cut.FindAll(".automation-run-table tbody tr");
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(static row => row.TextContent.Contains("Periodic", StringComparison.Ordinal)));
        Assert.IsTrue(rows.Any(static row => row.TextContent.Contains("Succeeded", StringComparison.Ordinal)));
        Assert.IsTrue(rows.Any(static row => row.TextContent.Contains("Queued", StringComparison.Ordinal)));

        await cut.FindAll(".automation-tabs button").Single(static button => button.TextContent.Trim() == "Definitions")
            .ClickAsync().ConfigureAwait(false);
        await cut.Find($"#automation-edit-{DefinitionId}").ClickAsync().ConfigureAwait(false);
        var task = cut.Find("#automation-task");
        Assert.IsTrue(task.QuerySelectorAll("option").Any(static option =>
            option.TextContent.Trim() == "Orbital Elements Refresh" &&
            option.GetAttribute("value") == nameof(LocalAutomationTaskKind.OrbitalElementsRefresh)));
        Assert.AreEqual(FakePeriodicTaskAdapter.Target, cut.Find("#automation-target").GetAttribute("value"));
    }

    /// <summary>One completed and one queued run-now of a saved definition of the appended kind.</summary>
    private async Task<LocalAutomationOperatorState> ProjectedStateAsync()
    {
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await _store.SaveAsync(new(DefinitionId,
            "Orbital elements refresh", true, LocalAutomationTaskKind.OrbitalElementsRefresh, FakePeriodicTaskAdapter.Target,
            LocalAutomationTriggerKind.Periodic, 7_200, 0, "save", "owner", "orbital schedule"), CancellationToken.None)
            .ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await _store.RunNowAsync(
            new(DefinitionId, 1, "run-now-1", "owner", "operator refresh"), CancellationToken.None).ConfigureAwait(false)).Status);
        using (var runner = new LocalAutomationRunnerService(_store, _harness.Registry, _captures,
            Options.Create(new CameraAgentHostOptions()), _time, NullLogger<LocalAutomationRunnerService>.Instance))
        {
            await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        }
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await _store.RunNowAsync(
            new(DefinitionId, 1, "run-now-2", "owner", "operator refresh"), CancellationToken.None).ConfigureAwait(false)).Status);
        return await _store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
