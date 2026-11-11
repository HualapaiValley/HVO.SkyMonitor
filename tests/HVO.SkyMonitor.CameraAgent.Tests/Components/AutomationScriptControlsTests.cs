using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class AutomationScriptControlsTests
{
    [TestMethod]
    public async Task WizardReviewsSafeRegistrationPolicyThenSavesTheSelectedImmutableTarget()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        using var context = Context(fixture.Service);
        var cut = context.Render<AutomationsPage>();
        await StartAsync(cut).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll("#automation-target"));
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsNotNull(cut.Find("#automation-script-step"));
        Assert.Contains("registered.digest", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("30 seconds", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("1 pinned typed arguments", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions);
        await cut.Find("#automation-script-interval").ChangeAsync(new ChangeEventArgs { Value = "120" }).ConfigureAwait(false);
        await cut.Find("#automation-back-task").ClickAsync(new()).ConfigureAwait(false);
        Assert.AreEqual("script", cut.Find("#automation-id").GetAttribute("value"));
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.AreEqual("120", cut.Find("#automation-script-interval").GetAttribute("value"));
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions);
        await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("#automation-edit-script");
        var definition = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions.Single();
        Assert.AreEqual(LocalAutomationTaskKind.CustomScript, definition.Definition.TaskKind);
        Assert.AreEqual("registered.digest", definition.Definition.TaskTarget);
        Assert.AreEqual(120, definition.Definition.TriggerInterval);
        Assert.AreEqual("script-owner", definition.Actor);
    }

    [TestMethod]
    public async Task RetiredScriptRegistrationRequiresExplicitReplacementAndPreservesTheOldRevision()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        var saved = await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var original = saved.Value!.State.Definitions.Single();
        fixture.Stub.Target = "registered.replacement";
        using var context = Context(fixture.Service);
        var cut = context.Render<AutomationsPage>();
        await cut.WaitForElement("#automation-edit-script").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsNotNull(cut.Find("#automation-script-step"));
        Assert.Contains("no longer registered", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll("#automation-confirm"));
        var unchanged = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions.Single();
        Assert.AreEqual(original.Version, unchanged.Version);
        Assert.AreEqual(original.RevisionSha256, unchanged.RevisionSha256);
        Assert.AreEqual(original.Definition, unchanged.Definition);
        await cut.Find("#automation-script-target").ChangeAsync(new ChangeEventArgs { Value = fixture.Stub.Target }).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains(fixture.Stub.Target, cut.Markup, StringComparison.Ordinal);
        await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("#automation-edit-script");
        var revised = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions.Single();
        Assert.AreEqual(2L, revised.Version);
        Assert.AreEqual(fixture.Stub.Target, revised.Definition.TaskTarget);
        Assert.AreEqual(original.Definition, revised.History.Single(item => item.Version == 1).Definition);
        Assert.AreNotEqual(original.RevisionSha256, revised.RevisionSha256);
    }

    [TestMethod]
    public async Task UnavailableAdapterHasNoCustomWizardOptionAndExistingDefinitionShowsSafeErrors()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        fixture.Stub.Available = false;
        using var context = Context(fixture.Service);
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-script");
        Assert.IsTrue(cut.Find("[data-script-action=run-now]").HasAttribute("disabled"));
        Assert.Contains("Unavailable", cut.Find("#automation-definition-script").TextContent, StringComparison.Ordinal);
        await cut.Find("#automation-create").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsFalse(cut.FindAll("#automation-task option").Any(item => item.GetAttribute("value") == "CustomScript"));
        Assert.IsEmpty(cut.FindAll("#automation-script-step"));
    }

    [TestMethod]
    [DataRow("59")]
    [DataRow("86401")]
    [DataRow("NaN")]
    public async Task ScriptWizardRejectsInvalidScheduleBeforeConfirmationOrPersistence(string interval)
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        using var context = Context(fixture.Service);
        var cut = context.Render<AutomationsPage>();
        await StartAsync(cut).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-script-interval").ChangeAsync(new ChangeEventArgs { Value = interval }).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsNotEmpty(cut.FindAll("[role=alert]"));
        Assert.IsEmpty(cut.FindAll("#automation-confirm"));
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions);
    }

    [TestMethod]
    public async Task RunNowRequiresConfirmationAndLostResponseRetriesTheSameDurableKey()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        var saved = await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var service = new LostResponseService(fixture.Service);
        using var context = Context(service);
        var cut = context.Render<AutomationScriptRunControls>(parameters => parameters
            .Add(item => item.Definition, saved.Value!.State.Definitions.Single()).Add(item => item.Available, true));
        await cut.Find("[data-script-action=run-now]").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs);
        await cut.Find("[data-script-action=cancel]").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs);
        await cut.Find("[data-script-action=run-now]").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("[data-script-action=confirm]").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("[role=alert]");
        Assert.HasCount(1, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs);
        await cut.Find("[data-script-action=confirm]").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("[role=status]");
        Assert.HasCount(2, service.Requests);
        Assert.AreEqual(service.Requests[0], service.Requests[1]);
        Assert.HasCount(1, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    public async Task QueuedScriptStopIsEnabledWithoutCurrentAdapterAndConfirmationWithdrawsDurably()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var queued = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        var run = queued.Value!.State.Runs.Single();
        fixture.Stub.Available = false;
        using var context = Context(fixture.Service);
        var changed = 0;
        var cut = context.Render<AutomationScriptRunControls>(parameters => parameters
            .Add(item => item.Run, run).Add(item => item.Available, false).Add(item => item.Changed, () => changed++));
        Assert.IsFalse(cut.Find("[data-script-action=stop]").HasAttribute("disabled"));
        await cut.Find("[data-script-action=stop]").ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("Withdraw this queued run before execution", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("process tree", cut.Markup, StringComparison.Ordinal);
        Assert.AreEqual(LocalAutomationRunOutcome.Queued, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single().Outcome);
        await cut.Find("[data-script-action=cancel]").ClickAsync(new()).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Queued, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single().Outcome);
        await cut.Find("[data-script-action=stop]").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("[data-script-action=confirm]").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("[role=status]");
        Assert.AreEqual(1, changed);
        var terminal = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, terminal.Outcome);
        Assert.IsNull(terminal.StartedAtUtc);
        Assert.IsNotNull(terminal.CompletedAtUtc);
        Assert.AreEqual(run.Occurrence, terminal.Occurrence);
    }

    [TestMethod]
    public async Task HistoryDisplaysAndStopsAnActiveRunAfterDefinitionRemoval()
    {
        using var fixture = new ScriptControlsFixture();
        var entered = ScriptControlsFixture.Signal();
        fixture.Stub.Execute = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "unreachable");
        };
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await fixture.Store.RemoveAsync(new("script", 1, "remove", "owner", null), default).ConfigureAwait(false);
        using var context = Context(fixture.Service);
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement(".automation-tabs");
        await cut.FindAll(".automation-tabs button")[2].ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("Running", cut.Find("#automation-runs").TextContent, StringComparison.Ordinal);
        await cut.Find("[data-script-action=stop]").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsFalse(fixture.Worker.WhenIdleAsync().IsCompleted);
        await cut.Find("[data-script-action=confirm]").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.Contains("Interrupted", cut.Find("#automation-runs").TextContent, StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("[data-script-action=stop]"));
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).ActiveRuns);
    }

    internal static BunitContext Context(ICameraAgentAutomationUiService service)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton(service);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        return context;
    }
    private static async Task StartAsync(IRenderedComponent<AutomationsPage> cut)
    {
        await cut.WaitForElement("#automation-create").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-id").ChangeAsync(new ChangeEventArgs { Value = "script" }).ConfigureAwait(false);
        await cut.Find("#automation-name").ChangeAsync(new ChangeEventArgs { Value = "Operator script" }).ConfigureAwait(false);
        await cut.Find("#automation-task").ChangeAsync(new ChangeEventArgs { Value = "CustomScript" }).ConfigureAwait(false);
    }
    private sealed class LostResponseService(ICameraAgentAutomationUiService inner) : ICameraAgentAutomationUiService
    {
        internal List<LocalAutomationRunNowRequest> Requests { get; } = [];
        public async ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RunScriptNowAsync(LocalAutomationRunNowRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var result = await inner.RunScriptNowAsync(request, cancellationToken).ConfigureAwait(false);
            Assert.IsTrue(result.IsSuccess, result.Message);
            return Requests.Count == 1 ? OperatorUiResult<LocalAutomationCommandResult>.Failure(
                OperatorUiResultKind.Unavailable, "Command response unavailable. Retry the same request.") : result;
        }
        public ValueTask<OperatorUiResult<LocalAutomationOperatorState>> GetAsync(CancellationToken cancellationToken) => inner.GetAsync(cancellationToken);
        public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> SaveAsync(LocalAutomationSaveRequest request, CancellationToken cancellationToken) => inner.SaveAsync(request, cancellationToken);
        public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RemoveAsync(LocalAutomationRemoveRequest request, CancellationToken cancellationToken) => inner.RemoveAsync(request, cancellationToken);
        public ValueTask<OperatorUiResult<CameraAgentAutomationWindowPreview>> PreviewWindowAsync(LocalAutomationSaveRequest request, CancellationToken cancellationToken) => inner.PreviewWindowAsync(request, cancellationToken);
    }
}
