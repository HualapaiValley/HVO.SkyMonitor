using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Integration")]
[TestProperty("Area", "Services")]
public sealed class AutomationWizardIntegrationTests
{
    [TestMethod]
    public async Task Schedule_RetiredTargetsCannotHideALaterRunnableDefinitionBeyondTheStoreCalendarLimit()
    {
        using var fixture = new AutomationWizardFixture();
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        await fixture.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        for (var index = 0; index < LocalAutomationContract.MaximumProjectedCalendarEntries; index++)
        {
            var request = AutomationWizardFixture.Request($"retired-{index}", LocalAutomationSourceWindowKind.CompletedCivilHour);
            var result = await fixture.Service.SaveAsync(request with
            { SourceWindow = request.SourceWindow! with { ProcessingSettleAllowance = TimeSpan.Zero } }, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(result.IsSuccess);
        }
        fixture.Registry.Targets.Add("current-keogram");
        var current = await fixture.Service.SaveAsync(AutomationWizardFixture.Request("current", LocalAutomationSourceWindowKind.CompletedCivilHour)
            with
        { TaskTarget = "current-keogram", Name = "Current preset" }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(current.IsSuccess);
        fixture.Registry.Targets.Remove("keogram");
        var state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(26, state.Definitions);
        Assert.AreEqual("current", state.Calendar.Single().DefinitionId);
        Assert.IsTrue(state.Definitions.Where(item => item.Definition.DefinitionId != "current")
            .All(static item => item.RegistryRejection is not null && item.NextRunUtc is null && item.NextOccurrence is null));
        Assert.AreEqual(new DateTimeOffset(2026, 10, 12, 11, 15, 0, TimeSpan.Zero),
            state.Definitions.Single(item => item.Definition.DefinitionId == "current").NextRunUtc);

        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-definition-current");
        var rail = cut.FindAll(".ops-status-rail > div");
        Assert.AreEqual("1", rail[0].QuerySelector("strong")!.TextContent);
        Assert.AreEqual("11:15 +00:00", rail[1].QuerySelector("strong")!.TextContent);
        Assert.AreEqual("Current preset", rail[1].QuerySelector("small")!.TextContent);
        await cut.FindAll(".automation-tabs button")[1].ClickAsync(new()).ConfigureAwait(false);
        var upcoming = cut.FindAll(".automation-upcoming li").Single();
        Assert.AreEqual("Current preset", upcoming.QuerySelector("span")!.TextContent);
        Assert.AreEqual("current-keogram", upcoming.QuerySelector("small")!.TextContent);
        Assert.Contains("11:15 +00:00", upcoming.QuerySelector("strong")!.TextContent, StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll(".automation-run-tick"));
        Assert.AreEqual("Current preset", cut.Find(".automation-run-marker small").TextContent);
        Assert.DoesNotContain("No available enabled automation is due", cut.Find("#automation-schedule").TextContent);
    }

    [TestMethod]
    public async Task Wizard_SelectionDefaultsToTheTargetAcceptedChoiceBeforeSave()
    {
        using var fixture = new AutomationWizardFixture();
        await fixture.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        AutomationWindowWizardTests.Start(cut, "trail");
        await cut.Find("#automation-back-task").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-target").ChangeAsync(new ChangeEventArgs { Value = "star-trail" }).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.AreEqual("Dark-night actual sources", cut.WaitForElement("#automation-source-selection option").TextContent);
        Assert.HasCount(1, cut.FindAll("#automation-source-selection option"));
        Assert.IsEmpty((await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Definitions);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("#automation-window-preview");
        await cut.Find("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForElement("#automation-edit-trail");
        var definition = (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Definitions.Single();
        Assert.AreEqual("star-trail", definition.Definition.TaskTarget);
        Assert.AreEqual(LocalAutomationSourceSelection.DarkNightActualSources, definition.Definition.SourceWindow!.Selection);
        Assert.AreEqual("wizard-owner", definition.Actor);
    }

    [TestMethod]
    public async Task RetainedAdapterRejectionKeepsHistoryAndSchedulesResumeAfterVersionedRepair()
    {
        using var fixture = new AutomationWizardFixture();
        await fixture.Store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var saved = await fixture.Service.SaveAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false);
        var original = saved.Value!.State.Definitions.Single().NextOccurrence!;
        fixture.Clock.Advance(original.ScheduledForUtc - fixture.Clock.GetUtcNow());
        Assert.IsTrue(await fixture.Store.TryBeginOccurrenceAsync(original, CancellationToken.None).ConfigureAwait(false));
        await fixture.Store.CompleteRunAsync(original.RunKey, LocalAutomationRunOutcome.Failed, "Fixture failure", CancellationToken.None).ConfigureAwait(false);

        fixture.Registry.KeogramSelection = LocalAutomationSourceSelection.DarkNightActualSources;
        var rejected = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Contains("keogram", rejected.Registry.Single().Targets);
        Assert.Contains(LocalAutomationSourceWindowKind.SunriseDay, rejected.Registry.Single().SupportedSourceWindows);
        var retained = rejected.Definitions.Single();
        Assert.AreEqual("definition.sourceWindow.selection", retained.RegistryRejection?.FieldPath);
        Assert.IsNull(retained.NextRunUtc);
        Assert.IsEmpty(rejected.Calendar);
        Assert.AreEqual(original, rejected.Runs.Single().Occurrence);
        Assert.AreEqual(1, retained.History.Single().Version);

        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        var card = cut.WaitForElement("#automation-definition-daily");
        Assert.Contains("Unavailable", card.TextContent);
        Assert.IsTrue(cut.Find("#automation-toggle-daily").HasAttribute("disabled"));
        Assert.AreEqual("0", cut.Find(".ops-status-rail strong").TextContent);
        await cut.FindAll(".automation-tabs button")[1].ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".automation-upcoming li"));
        Assert.Contains("The recorded source selection is no longer accepted", cut.Find("details").TextContent);
        await cut.FindAll(".automation-tabs button")[2].ClickAsync(new()).ConfigureAwait(false);
        Assert.HasCount(1, cut.FindAll(".automation-run-table tbody tr"));
        Assert.IsEmpty(cut.FindAll(".automation-run-control"));
        await cut.FindAll(".automation-tabs button")[0].ClickAsync(new()).ConfigureAwait(false);

        await cut.Find("#automation-edit-daily").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("Review the selected period and selection", cut.WaitForElement("[role=alert]").TextContent);
        Assert.AreEqual("Dark-night actual sources", cut.Find("#automation-source-selection option").TextContent);
        await cut.Find("#automation-save").ClickAsync(new()).ConfigureAwait(false);
        await cut.WaitForElement("#automation-confirm").ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#automation-toggle-daily").HasAttribute("disabled")));
        var repaired = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        var live = repaired.Definitions.Single();
        Assert.AreEqual(2, live.Version);
        Assert.IsNull(live.RegistryRejection);
        Assert.AreEqual(live.NextOccurrence, repaired.Calendar.Single().Occurrence);
        Assert.AreEqual(original, repaired.Runs.Single().Occurrence);
        Assert.AreEqual(original.Definition, live.History.Single(item => item.Version == 1).Definition);
        Assert.AreEqual("wizard-owner", live.Actor);
        await cut.FindAll(".automation-tabs button")[2].ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".automation-run-control"));
    }

    [TestMethod]
    public async Task Backfill_QueuesBothRepeatedHoursOnOneReportDateAfterAnAlreadyRecordedWindow()
    {
        using var fixture = new AutomationWizardFixture("America/Denver");
        fixture.Clock.Advance(new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero) - fixture.Clock.GetUtcNow());
        var save = AutomationWizardFixture.Request("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour);
        Assert.IsTrue((await fixture.Service.SaveAsync(save, CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        var date = new DateOnly(2026, 10, 31);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        var repeated = fixture.Planner.ResolveWindows(date, save.SourceWindow!)
            .Where(window => TimeZoneInfo.ConvertTime(window.StartUtc, zone).Hour == 1).ToArray();
        Assert.HasCount(2, repeated);

        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        var controls = cut.WaitForElement(".automation-run-control");
        await controls.QuerySelector("input[type=date]")!.ChangeAsync(new ChangeEventArgs { Value = "2026-10-31" }).ConfigureAwait(false);
        await controls.QuerySelector("button")!.ClickAsync(new()).ConfigureAwait(false);
        await cut.Find(".automation-run-control input[maxlength]").InputAsync(new ChangeEventArgs { Value = "Repair first gap" }).ConfigureAwait(false);
        await SelectAsync(repeated[0]).ConfigureAwait(false);
        await QueueButton().ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("One attempt is queued", cut.Find(".automation-run-control [role=status]").TextContent);
        Assert.IsTrue(QueueButton().HasAttribute("disabled"));

        await SelectAsync(null).ConfigureAwait(false);
        await SelectAsync(repeated[0]).ConfigureAwait(false);
        await cut.Find(".automation-run-control input[maxlength]").InputAsync(new ChangeEventArgs { Value = "Check recorded hour" }).ConfigureAwait(false);
        Assert.IsFalse(QueueButton().HasAttribute("disabled"));
        await QueueButton().ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("already has a recorded run", cut.Find(".automation-run-control [role=status]").TextContent);
        Assert.AreEqual(1, (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).QueuedRunCount);

        await SelectAsync(repeated[1]).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".automation-run-control [role=status]"));
        Assert.IsFalse(QueueButton().HasAttribute("disabled"));
        await QueueButton().ClickAsync(new()).ConfigureAwait(false);
        var state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("2026-10-31", cut.Find(".automation-run-control input[type=date]").GetAttribute("value"));
        Assert.AreEqual(1, state.Definitions.Single().Version);
        Assert.HasCount(2, state.ActiveRuns);
        CollectionAssert.AreEquivalent(repeated, state.ActiveRuns.Select(run => run.Occurrence!.SourceWindow).ToArray());

        AngleSharp.Dom.IElement QueueButton() => cut.Find(".automation-run-control button.primary");

        Task SelectAsync(LocalAutomationSourceWindow? window) => cut.Find(".automation-run-control select").ChangeAsync(
            new ChangeEventArgs { Value = window?.StartUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "" });
    }

    [TestMethod]
    public async Task Backfill_PreviewAgainReenablesQueueForTheAutoSelectedDailyWindow()
    {
        using var fixture = new AutomationWizardFixture();
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        Assert.IsTrue((await fixture.Service.SaveAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false)).IsSuccess);

        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        var controls = cut.WaitForElement(".automation-run-control");
        await controls.QuerySelector("input[type=date]")!.ChangeAsync(new ChangeEventArgs { Value = "2026-10-12" }).ConfigureAwait(false);
        await cut.Find(".automation-run-control button.secondary").ClickAsync(new()).ConfigureAwait(false);
        await cut.Find(".automation-run-control input[maxlength]").InputAsync(new ChangeEventArgs { Value = "Repair daily gap" }).ConfigureAwait(false);
        await cut.Find(".automation-run-control button.primary").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsTrue(cut.Find(".automation-run-control button.primary").HasAttribute("disabled"));

        await cut.Find(".automation-run-control button.secondary").ClickAsync(new()).ConfigureAwait(false);
        Assert.IsEmpty(cut.FindAll(".automation-run-control [role=status]"));
        Assert.IsFalse(cut.Find(".automation-run-control button.primary").HasAttribute("disabled"));
        await cut.Find(".automation-run-control button.primary").ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("request was already recorded", cut.Find(".automation-run-control [role=status]").TextContent);
        Assert.AreEqual(1, (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).QueuedRunCount);
    }

    [TestMethod]
    public async Task RunControls_BackfillPreviewQueuesExactWindowAndRetryPreservesItAfterEdit()
    {
        using var fixture = new AutomationWizardFixture("America/Denver");
        fixture.Clock.Advance(new DateTimeOffset(2026, 11, 2, 20, 0, 0, TimeSpan.Zero) - fixture.Clock.GetUtcNow());
        var save = AutomationWizardFixture.Request("hourly", LocalAutomationSourceWindowKind.CompletedCivilHour);
        var state = (await fixture.Service.SaveAsync(save, CancellationToken.None).ConfigureAwait(false)).Value!.State;
        var date = new DateOnly(2026, 10, 31);
        var previews = await fixture.Service.PreviewBackfillAsync("hourly", 1, date, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(previews.IsSuccess, previews.Message);
        var repeated = previews.Value!.Where(window => TimeZoneInfo.ConvertTime(window.StartUtc,
            TimeZoneInfo.FindSystemTimeZoneById("America/Denver")).Hour == 1).ToArray();
        Assert.HasCount(2, repeated);
        Assert.AreNotEqual(repeated[0].StartUtc, repeated[1].StartUtc);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs);

        using var context = Context(fixture);
        var cut = context.Render<AutomationsPage>();
        var controls = cut.WaitForElement("#automation-definition-hourly .automation-run-control");
        await controls.QuerySelector("input[type=date]")!.ChangeAsync(new ChangeEventArgs { Value = "2026-10-31" }).ConfigureAwait(false);
        await controls.QuerySelector("button")!.ClickAsync(new()).ConfigureAwait(false);
        var select = cut.WaitForElement("#automation-definition-hourly .automation-run-control select");
        Assert.Contains("-06:00", select.TextContent);
        Assert.Contains("-07:00", select.TextContent);
        await select.ChangeAsync(new ChangeEventArgs { Value = repeated[1].StartUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ConfigureAwait(false);
        await cut.Find(".automation-run-control input[maxlength]").InputAsync(new ChangeEventArgs { Value = "Repair a gap" }).ConfigureAwait(false);
        await cut.FindAll(".automation-run-control button")[1].ClickAsync(new()).ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.Contains("One attempt is queued", cut.Find(".automation-run-control [role=status]").TextContent));
        var queued = (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
        Assert.AreEqual(repeated[1], queued.Occurrence!.SourceWindow);
        Assert.AreEqual("wizard-owner", queued.Preparation!.Actor);
        Assert.AreEqual(state.Calendar.Single(), (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Calendar.Single());
        var entry = (await fixture.Store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
        Assert.IsNotNull(await fixture.Store.TryClaimQueuedAsync(entry, CancellationToken.None).ConfigureAwait(false));
        await fixture.Store.CompleteRunAsync(queued.RunKey, LocalAutomationRunOutcome.Failed, "Fixture failure", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue((await fixture.Service.SaveAsync(save with
        {
            ExpectedVersion = 1,
            Name = "Edited",
            IdempotencyKey = "edit-after-failure"
        }, CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        await cut.Find("#automation-refresh").ClickAsync(new()).ConfigureAwait(false);
        await cut.FindAll(".automation-tabs button")[2].ClickAsync(new()).ConfigureAwait(false);
        Assert.Contains("hourly", cut.Find(".automation-run-table tbody tr strong").TextContent);
        Assert.Contains("America/Denver", cut.Find(".automation-run-provenance").TextContent);
        Assert.Contains("wizard-owner", cut.Find(".automation-run-provenance").TextContent);
        await cut.Find(".automation-run-control input[maxlength]").InputAsync(new ChangeEventArgs { Value = "Retry the original window" }).ConfigureAwait(false);
        await cut.Find(".automation-run-control button").ClickAsync(new()).ConfigureAwait(false);
        var retry = (await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single(run => run.Attempt == 2);
        Assert.AreEqual(queued.Occurrence, retry.Occurrence);
        Assert.AreEqual("wizard-owner", retry.Preparation!.Actor);
        Assert.AreEqual("retry", retry.Preparation.Operation);
    }

    [TestMethod]
    public async Task Backfill_PreviewEnforcesFinalityLookbackVersionAndAuthorization()
    {
        using var fixture = new AutomationWizardFixture();
        Assert.IsTrue((await fixture.Service.SaveAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        Assert.IsEmpty((await fixture.Service.PreviewBackfillAsync("daily", 1, new(2026, 10, 12), CancellationToken.None)
            .ConfigureAwait(false)).Value!);
        Assert.IsEmpty((await fixture.Service.PreviewBackfillAsync("daily", 1, new(2026, 9, 30), CancellationToken.None)
            .ConfigureAwait(false)).Value!);
        Assert.AreEqual(OperatorUiResultKind.Conflict, (await fixture.Service.PreviewBackfillAsync("daily", 2,
            new(2026, 10, 11), CancellationToken.None).ConfigureAwait(false)).Kind);
        fixture.Authorized = false;
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await fixture.Service.PreviewBackfillAsync("daily", 1,
            new(2026, 10, 11), CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await fixture.Service.BackfillAsync(
            new("daily", new(2026, 10, 10), null, 1, "unauthorized", "untrusted", "reason"), CancellationToken.None)
            .ConfigureAwait(false)).Kind);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    public async Task Backfill_RejectsASiteChangeBetweenPreviewAndQueue()
    {
        using var fixture = new AutomationWizardFixture();
        var date = new DateOnly(2026, 10, 10);
        Assert.IsTrue((await fixture.Service.SaveAsync(AutomationWizardFixture.Request(), CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        var before = (await fixture.Service.PreviewBackfillAsync("daily", 1, date, CancellationToken.None)
            .ConfigureAwait(false)).Value!.Single();
        fixture.Calendar.Current = Common.Gallery.ObservingDayCalendar.ForDeployment(
            LocalAutomationWindowTests.Site("America/Denver", 40, -105, 2));
        var request = new LocalAutomationBackfillRequest("daily", date, null, 1, "previewed", "untrusted", "Backfill the report date")
        {
            ExpectedWindowIdentitySha256 = before.IdentitySha256
        };
        var rejected = await fixture.Service.BackfillAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Conflict, rejected.Kind);
        Assert.Contains("changed after the preview", rejected.Message!);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs);
        var after = (await fixture.Service.PreviewBackfillAsync("daily", 1, date, CancellationToken.None)
            .ConfigureAwait(false)).Value!.Single();
        Assert.AreNotEqual(before.IdentitySha256, after.IdentitySha256);
        var queued = await fixture.Service.BackfillAsync(request with { ExpectedWindowIdentitySha256 = after.IdentitySha256 },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(after, queued.Value!.State.ActiveRuns.Single().Occurrence!.SourceWindow);
    }

    [TestMethod]
    public async Task PeriodicPreviewUsesTheAdapterPhaseAndRejectsRetainedIntervalUntilRepaired()
    {
        var adapter = new FakePeriodicTaskAdapter(LocalAutomationTaskKind.TimeDataRefresh)
        { MinimumIntervalSeconds = 300, Epoch = now => now.AddMinutes(2) };
        using var registry = new PeriodicRegistryHarness(TimeProvider.System, [adapter]);
        using var fixture = new AutomationWizardFixture(registry: registry.Registry);
        var request = AutomationWizardFixture.Request("periodic") with
        {
            TaskKind = LocalAutomationTaskKind.TimeDataRefresh,
            TaskTarget = FakePeriodicTaskAdapter.Target,
            TriggerKind = LocalAutomationTriggerKind.Periodic,
            TriggerInterval = 300,
            SourceWindow = null
        };
        var preview = await fixture.Service.PreviewScheduleAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddMinutes(7), preview.Value!.DueUtc);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Definitions);
        var saved = await fixture.Service.SaveAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(preview.Value.DueUtc, saved.Value!.State.Calendar.Single().DueUtc);
        fixture.Clock.Advance(TimeSpan.FromMinutes(8));
        var edit = request with { ExpectedVersion = 1, IdempotencyKey = "edit" };
        Assert.AreEqual(preview.Value.DueUtc, (await fixture.Service.PreviewScheduleAsync(edit, CancellationToken.None)
            .ConfigureAwait(false)).Value!.DueUtc);
        adapter.MinimumIntervalSeconds = 360;
        var invalid = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(FakePeriodicTaskAdapter.IntervalReasonCode, invalid.Definitions.Single().RegistryRejection?.ReasonCode);
        Assert.IsFalse(invalid.Definitions.Single().SupportsRunNow);
        Assert.IsEmpty(invalid.Calendar);
        Assert.AreEqual(OperatorUiResultKind.Invalid, (await fixture.Service.PreviewScheduleAsync(edit, CancellationToken.None)
            .ConfigureAwait(false)).Kind);
        var repaired = await fixture.Service.SaveAsync(edit with { TriggerInterval = 360 }, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(request.DefinitionId, repaired.Value!.State.Calendar.Single().DefinitionId);
        var queued = await fixture.Service.RunNowAsync(new("periodic", 2, "run-now", "fake", "Refresh now"), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual("wizard-owner", queued.Value!.State.ActiveRuns.Single().Preparation!.Actor);
        Assert.AreEqual(repaired.Value.State.Calendar.Single(), queued.Value.State.Calendar.Single());
    }

    [TestMethod]
    public async Task CaptureRelativePreviewUsesDurableBaselineAndClearsItWhenResuming()
    {
        var registry = new Mock<ILocalAutomationTaskRegistry>();
        registry.Setup(item => item.Describe()).Returns(
            [new(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, "Fixture source",
                [LocalAutomationTriggerKind.CaptureRelative], ["source"], true, null)]);
        registry.Setup(item => item.ResolveInitialTriggerEpoch(It.IsAny<LocalAutomationDefinition>(), It.IsAny<DateTimeOffset>()))
            .Returns((LocalAutomationDefinition _, DateTimeOffset now) => now);
        using var fixture = new AutomationWizardFixture(registry: registry.Object);
        var request = AutomationWizardFixture.Request("capture") with
        {
            TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            TaskTarget = "source",
            TriggerKind = LocalAutomationTriggerKind.CaptureRelative,
            TriggerInterval = 10,
            SourceWindow = null
        };
        var create = await fixture.Service.PreviewScheduleAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(create.IsSuccess, create.Message);
        Assert.IsNull(create.Value!.CaptureSequence);
        Assert.IsNull(create.Value.DueUtc);
        Assert.IsTrue((await fixture.Service.SaveAsync(request, CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        await fixture.Store.SetCaptureBaselineAsync("capture", 42, CancellationToken.None).ConfigureAwait(false);
        var edit = request with { ExpectedVersion = 1, TriggerInterval = 12, IdempotencyKey = "edit" };
        var preview = await fixture.Service.PreviewScheduleAsync(edit, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(54, preview.Value!.CaptureSequence);
        var paused = await fixture.Service.SaveAsync(edit with { Enabled = false }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(paused.IsSuccess);
        var resume = await fixture.Service.PreviewScheduleAsync(edit with { ExpectedVersion = 2 }, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(resume.IsSuccess);
        Assert.IsNull(resume.Value!.CaptureSequence);
    }

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
