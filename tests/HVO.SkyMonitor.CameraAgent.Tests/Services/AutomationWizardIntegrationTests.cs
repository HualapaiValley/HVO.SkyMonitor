using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Integration")]
public sealed class AutomationWizardIntegrationTests
{
    [TestMethod]
    public async Task Wizard_CreateHourlyAndDailyThenEditAndRestart_PreservesIndependentRevisionsAndActor()
    {
        using var fixture = new AutomationWizardFixture();
        await fixture.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using (var context = Context(fixture))
        {
            var cut = context.Render<AutomationsPage>();
            foreach (var (id, kind) in new[] { ("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour),
                         ("daily", LocalAutomationSourceWindowKind.SunriseDay) })
            {
                AutomationWindowWizardTests.Start(cut, id);
                await cut.Find("#automation-source-window").ChangeAsync(new ChangeEventArgs { Value = kind.ToString() }).ConfigureAwait(false);
                await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
                cut.WaitForElement("#automation-window-preview");
                var before = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
                Assert.IsFalse(before.Definitions.Any(item => item.Definition.DefinitionId == id), "Preview must not save.");
                await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
                cut.WaitForElement($"#automation-edit-{id}");
            }
            await cut.Find("#automation-edit-daily").ClickAsync(new()).ConfigureAwait(false);
            await cut.Find("#automation-name").ChangeAsync(new ChangeEventArgs { Value = "Daily revised" }).ConfigureAwait(false);
            await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
            Assert.AreEqual("900", cut.Find("#automation-settle").GetAttribute("value"));
            await cut.Find("#automation-settle").ChangeAsync(new ChangeEventArgs { Value = "1800.0000001" }).ConfigureAwait(false);
            await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForElement("#automation-window-preview");
            await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => Assert.Contains("Daily revised", cut.Markup, StringComparison.Ordinal));
            await cut.Find("#automation-toggle-daily").ClickAsync(new()).ConfigureAwait(false);
            await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
            cut.WaitForAssertion(() => Assert.Contains("Disabled", cut.Find("#automation-definition-daily").TextContent, StringComparison.Ordinal));
        }
        await fixture.RestartAsync().ConfigureAwait(false);
        var state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var hourly = state.Definitions.Single(item => item.Definition.DefinitionId == "hourly");
        var daily = state.Definitions.Single(item => item.Definition.DefinitionId == "daily");
        Assert.AreEqual(1, hourly.Version);
        Assert.AreEqual(3, daily.Version);
        Assert.IsFalse(daily.Definition.Enabled);
        Assert.AreEqual("wizard-owner", daily.Actor);
        Assert.AreEqual(3, daily.History.Count);
        Assert.AreEqual(TimeSpan.FromMinutes(15), hourly.Definition.SourceWindow!.ProcessingSettleAllowance);
        Assert.AreEqual(TimeSpan.FromMinutes(30).Add(TimeSpan.FromTicks(1)), daily.Definition.SourceWindow!.ProcessingSettleAllowance);
        Assert.AreEqual(TimeSpan.FromMinutes(15), daily.History.Single(item => item.Version == 1).Definition.SourceWindow!.ProcessingSettleAllowance);
        Assert.AreNotEqual(hourly.RevisionSha256, daily.RevisionSha256);
        Assert.IsEmpty(state.Runs);
    }

    [TestMethod]
    public async Task PreviewAndSave_UseTheSameConcreteWindowAndRetainItAcrossEdits()
    {
        using var fixture = new AutomationWizardFixture();
        await fixture.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var request = AutomationWizardFixture.Request();
        var preview = await fixture.Service.PreviewWindowAsync(request, CancellationToken.None).ConfigureAwait(false);
        var saved = await fixture.Service.SaveAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(saved.IsSuccess, saved.Message);
        var occurrence = saved.Value!.State.Definitions.Single().NextOccurrence!;
        Assert.AreEqual(preview.Value!.NextWindow, occurrence.SourceWindow);
        Assert.AreEqual(occurrence.SourceWindow!.EndUtc.AddMinutes(15), occurrence.ScheduledForUtc);
        fixture.Clock.Advance(occurrence.ScheduledForUtc - fixture.Clock.GetUtcNow());
        Assert.IsTrue(await fixture.Store.TryBeginOccurrenceAsync(occurrence, CancellationToken.None).ConfigureAwait(false));
        await fixture.Store.CompleteRunAsync(occurrence.RunKey, LocalAutomationRunOutcome.Failed, "Synthetic failure", CancellationToken.None).ConfigureAwait(false);
        var changed = request with
        {
            ExpectedVersion = 1,
            IdempotencyKey = "edit",
            SourceWindow = request.SourceWindow! with
            { Kind = LocalAutomationSourceWindowKind.CompletedCivilHour }
        };
        Assert.IsTrue((await fixture.Service.SaveAsync(changed, CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        await fixture.RestartAsync().ConfigureAwait(false);
        var state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(occurrence, state.Runs.Single().Occurrence);
        Assert.AreEqual(LocalAutomationSourceWindowKind.CompletedCivilHour, state.Definitions.Single().Definition.SourceWindow!.Kind);
        var stale = await fixture.Service.SaveAsync(changed with { ExpectedVersion = 1, IdempotencyKey = "stale" }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Conflict, stale.Kind);
    }

    [TestMethod]
    public async Task Wizard_AReplacedProducerTargetIsUnavailableUntilAnExplicitRevisionRepairsIt()
    {
        using var fixture = new AutomationWizardFixture();
        await fixture.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var saved = await fixture.Service.SaveAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false);
        var original = saved.Value!.State.Definitions.Single();
        var occurrence = original.NextOccurrence!;
        fixture.Clock.Advance(occurrence.ScheduledForUtc - fixture.Clock.GetUtcNow());
        fixture.Registry.Targets.Remove("keogram");
        fixture.Registry.Targets.Add("replacement-keogram");
        Assert.IsFalse(await fixture.Store.TryBeginOccurrenceAsync(occurrence, CancellationToken.None).ConfigureAwait(false),
            "The old target must be rejected even when its original window has reached finality.");

        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("Unavailable", cut.Find("#automation-definition-daily header .state-chip").TextContent));
        await cut.Find("#automation-edit-daily").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-target").ChangeAsync(new ChangeEventArgs { Value = "replacement-keogram" }).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("#automation-window-preview");
        await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.AreEqual("Enabled", cut.Find("#automation-definition-daily header .state-chip").TextContent));
        var repaired = (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Definitions.Single();
        Assert.AreEqual(2, repaired.Version);
        Assert.AreEqual("replacement-keogram", repaired.Definition.TaskTarget);
        Assert.AreEqual("keogram", repaired.History.Single(item => item.Version == 1).Definition.TaskTarget);
        Assert.AreEqual(original.Definition.SourceWindow, repaired.Definition.SourceWindow);
        Assert.AreEqual("wizard-owner", repaired.Actor);
    }

    private static BunitContext Context(AutomationWizardFixture fixture)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(fixture.Service);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        return context;
    }
}
