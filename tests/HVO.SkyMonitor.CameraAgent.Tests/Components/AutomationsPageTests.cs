using AngleSharp.Dom;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class AutomationsPageTests
{
    [TestMethod]
    public void SourceWindowDefinition_IsTruthfullyReadOnlyHereAndTogglePreservesItsPolicy()
    {
        using var context = CreateContext();
        var initial = Automation();
        var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
            LocalAutomationSourceWindowKind.SunriseDay, LocalAutomationSourceSelection.DarkNightActualSources, TimeSpan.FromMinutes(15));
        var definition = initial.Definitions.Single().Definition with
        {
            TaskKind = LocalAutomationTaskKind.StillImageGeneration,
            TriggerKind = LocalAutomationTriggerKind.SourceWindowClosed,
            TriggerInterval = 1,
            SourceWindow = policy
        };
        var state = initial with
        {
            Definitions = [initial.Definitions.Single() with { Definition = definition }],
            Registry = [new(LocalAutomationTaskKind.StillImageGeneration, "Installed producer",
                [LocalAutomationTriggerKind.SourceWindowClosed], [definition.TaskTarget], true, null)
                { SupportedSourceWindows = [LocalAutomationSourceWindowKind.SunriseDay] }],
            RunningRunCount = 3
        };
        var service = Register(context, state);
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement(".automation-card");
        Assert.Contains("Sunrise to sunrise / dark-night sources", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Source-window settings are read-only", cut.Markup, StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("#automation-create").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("#automation-edit-sky-temperature").HasAttribute("disabled"));
        Assert.AreEqual("3", cut.FindAll(".ops-status-rail > div")[2].QuerySelector("strong")!.TextContent.Trim());
        cut.Find("#automation-toggle-sky-temperature").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();
        Assert.AreEqual(policy, service.SaveRequests.Single().SourceWindow);
    }

    /// <summary>20:00 on 4 September in America/Phoenix, which has no daylight saving.</summary>
    private static readonly DateTimeOffset Instant = new(2026, 9, 5, 3, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Render_ShowsDefinitionCardsStatusRailAndTheUnsupportedRestartTarget()
    {
        using var context = CreateContext();
        Register(context, Automation());

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement(".automation-card");
        Assert.AreEqual("Automations", cut.Find("h1").TextContent.Trim());
        Assert.IsNotNull(cut.Find("a[href='/operations/schedule']"));
        Assert.AreEqual("Definitions", cut.Find(".automation-tabs button.active").TextContent.Trim());
        Assert.AreEqual("true", cut.Find(".automation-tabs button.active").GetAttribute("aria-pressed"));

        var rail = cut.FindAll(".ops-status-rail > div");
        Assert.AreEqual("1", rail[0].QuerySelector("strong")!.TextContent.Trim());
        Assert.AreEqual("of 1 definition", rail[0].QuerySelector("small")!.TextContent.Trim());
        // Without the schedule calendar the page falls back to UTC, so the next run is 04:00.
        Assert.AreEqual("04:00 +00:00", rail[1].QuerySelector("strong")!.TextContent.Trim());
        Assert.AreEqual("Sky temperature", rail[1].QuerySelector("small")!.TextContent.Trim());
        Assert.AreEqual("0", rail[2].QuerySelector("strong")!.TextContent.Trim());
        Assert.AreEqual("1 / 1", rail[3].QuerySelector("strong")!.TextContent.Trim());

        var card = cut.Find("article.automation-card:not(.unsupported)");
        Assert.AreEqual("Sky temperature", card.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("Acquires one observation", card.TextContent, StringComparison.Ordinal);
        Assert.AreEqual("Enabled", card.QuerySelector(".state-chip")!.TextContent.Trim());
        Assert.AreEqual("Every 10 captures", Fact(card, "Trigger"));
        Assert.AreEqual("virtual-sky-temperature", Fact(card, "Target"));
        Assert.AreEqual("Succeeded / Sat 5 Sep 02:40 +00:00", Fact(card, "Last run"));
        Assert.AreEqual("At capture sequence 420", Fact(card, "Next run"));
        Assert.Contains("Environmental On Demand Acquisition / revision 2", card.QuerySelector("footer > span")!.TextContent, StringComparison.Ordinal);
        // Repeated row actions carry a distinguishing accessible name, not just "Remove".
        Assert.IsNotNull(card.QuerySelector("button[aria-label='Edit sky-temperature']"));
        Assert.IsNotNull(card.QuerySelector("button[aria-label='Disable sky-temperature']"));
        Assert.IsNotNull(card.QuerySelector("button[aria-label='Remove sky-temperature']"));

        var unsupported = cut.Find("article.automation-card.unsupported");
        Assert.AreEqual("Scheduled CameraAgent Restart", unsupported.QuerySelector("h3")!.TextContent.Trim());
        Assert.AreEqual("Not registered", Fact(unsupported, "Task type"));
        Assert.Contains("Arbitrary command execution is never allowed.", unsupported.TextContent, StringComparison.Ordinal);
        Assert.IsTrue(unsupported.QuerySelector("button")!.HasAttribute("disabled"));

        Assert.IsFalse(cut.Find("#automation-create").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("[role='alert']"));
    }

    [TestMethod]
    public void Render_ShowsTheRetainedRevisionHistory()
    {
        using var context = CreateContext();
        Register(context, Automation());

        var cut = context.Render<AutomationsPage>();

        var history = cut.WaitForElement("details.revision-history");
        Assert.AreEqual("1 recorded revision", history.QuerySelector("summary")!.TextContent.Trim());
        Assert.Contains("Revision 2", history.TextContent, StringComparison.Ordinal);
        Assert.Contains("owner", history.TextContent, StringComparison.Ordinal);
        Assert.Contains("nightly", history.TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_WithNoDefinitions_ShowsTheEmptyStateAndStillOffersCreate()
    {
        using var context = CreateContext();
        Register(context, Automation(definitions: [], calendar: [], runs: []));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement(".automation-empty");
        Assert.Contains("No local automation is defined on this CameraAgent.", cut.Find(".automation-empty").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("None scheduled", cut.FindAll(".ops-status-rail > div")[1].QuerySelector("strong")!.TextContent.Trim());
        Assert.HasCount(1, cut.FindAll("article.automation-card.unsupported"));
        Assert.IsFalse(cut.Find("#automation-create").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Render_WhenTheRegistryIsUnavailable_DisablesCreateAndSaysWhy()
    {
        using var context = CreateContext();
        Register(context, Automation(definitions: [], calendar: [], runs: [], registryAvailable: false));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement("#automation-create[disabled]");
        const string Reason = "Environmental acquisition is disabled in this CameraAgent's startup configuration.";
        Assert.AreEqual(Reason, cut.Find("#automation-create-reason").TextContent.Trim());
        Assert.AreEqual("automation-create-reason", cut.Find("#automation-create").GetAttribute("aria-describedby"));
        Assert.Contains("No automation task is available.", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_AtTheDefinitionLimit_DisablesCreate()
    {
        using var context = CreateContext();
        var template = Automation().Definitions[0];
        var definitions = Enumerable.Range(0, LocalAutomationContract.MaximumDefinitions)
            .Select(index => template with
            {
                Definition = template.Definition with { DefinitionId = $"sky-{index}" }
            })
            .ToList();
        Register(context, Automation(definitions: definitions));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement("#automation-create[disabled]");
        Assert.Contains("maximum of 32 automation definitions", cut.Find("#automation-create-reason").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Render_WhenEveryReadFails_ShowsTheUnavailableNotice()
    {
        using var context = CreateContext();
        Register(context, null);

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Automations unavailable.", cut.Find("[role='alert']").TextContent, StringComparison.Ordinal);
            Assert.Contains("Local automation state is unavailable.", cut.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".automation-tabs"));
            Assert.IsTrue(cut.Find("#automation-create").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void Refresh_WhenTheReadFails_KeepsTheLastValidSnapshot()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement(".automation-card");

        service.FailReads = true;
        cut.Find("#automation-refresh").Click();

        Assert.Contains("Showing the last valid snapshot.", cut.Find("[role='alert']").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("Sky temperature", cut.Find("article.automation-card h3").TextContent.Trim());
    }

    [TestMethod]
    public void Create_ConfirmsBeforeRecordingAndSendsVersionZeroAndAKey()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find("#automation-trigger").Change(nameof(LocalAutomationTriggerKind.Periodic));
        cut.Find("#automation-interval").Change("7200");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        // The confirmation summarises exactly what will be recorded before anything is sent.
        Assert.IsEmpty(service.SaveRequests);
        Assert.AreEqual("Record automation nightly-temperature?", cut.Find("#automation-dialog-heading").TextContent.Trim());
        Assert.AreEqual("Every 2 h", Fact(cut.Find("dialog"), "Trigger"));
        Assert.AreEqual("virtual-sky-temperature", Fact(cut.Find("dialog"), "Target"));
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        var request = service.SaveRequests.Single();
        Assert.AreEqual("nightly-temperature", request.DefinitionId);
        Assert.AreEqual("Nightly temperature", request.Name);
        Assert.AreEqual(LocalAutomationTriggerKind.Periodic, request.TriggerKind);
        Assert.AreEqual(7200, request.TriggerInterval);
        Assert.AreEqual("virtual-sky-temperature", request.TaskTarget);
        Assert.AreEqual(0L, request.ExpectedVersion);
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.IdempotencyKey));
        Assert.IsEmpty(cut.FindAll("dialog"));
        // The calendar read failing in this fixture must never silence the outcome of the operator's command.
        Assert.Contains("Recorded a new immutable automation revision.", cut.Find(".automation-message").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Create_RejectsAnIntervalOutsideTheContractBoundsInsideTheEditor()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find("#automation-interval").Change("5");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        Assert.IsEmpty(service.SaveRequests);
        Assert.IsNotNull(cut.Find("#automation-interval"), "The editor stays open so the value can be corrected.");
        Assert.Contains("The interval must be between 60 and 86400.", cut.Find(".automation-dialog-message").TextContent, StringComparison.Ordinal);
        Assert.Contains("Accepted range: 60 to 86400 seconds.", cut.Find("#automation-interval-help").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Create_RejectsAnEmptyIdentifierInsideTheEditor()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        Assert.IsEmpty(service.SaveRequests);
        Assert.IsEmpty(cut.FindAll($"#{AutomationsPage.ConfirmId}"));
        Assert.Contains("The identifier must start with a lower-case letter", cut.Find(".automation-dialog-message").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Create_SwitchingTheTriggerMovesAnUntouchedDefaultToTheNewUnit()
    {
        using var context = CreateContext();
        Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);

        Assert.AreEqual("3600", cut.Find("#automation-interval").GetAttribute("value"));
        cut.Find("#automation-trigger").Change(nameof(LocalAutomationTriggerKind.CaptureRelative));
        Assert.AreEqual("10", cut.Find("#automation-interval").GetAttribute("value"));
        Assert.Contains("Every 10 captures", cut.Find("#automation-interval-help").TextContent, StringComparison.Ordinal);

        // A value the operator typed is theirs and survives a trigger change.
        cut.Find("#automation-interval").Change("25");
        cut.Find("#automation-trigger").Change(nameof(LocalAutomationTriggerKind.Periodic));
        Assert.AreEqual("25", cut.Find("#automation-interval").GetAttribute("value"));
    }

    [TestMethod]
    public void Create_AfterUnavailable_ReusesTheIdempotencyKeyAndExpectedVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        service.Kind = OperatorUiResultKind.Unavailable;
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);
        FillCreate(cut);

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();
        // The confirmation stays open on an unavailable command so the same command can be retried.
        Assert.Contains("The automation command failed.", cut.Find(".automation-dialog-message").TextContent, StringComparison.Ordinal);
        service.Kind = OperatorUiResultKind.Success;
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
        Assert.AreEqual(service.SaveRequests[0].ExpectedVersion, service.SaveRequests[1].ExpectedVersion);
    }

    [TestMethod]
    public void Create_AfterUnavailable_MintsANewKeyWhenOnlyTheReasonChanges()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        service.Kind = OperatorUiResultKind.Unavailable;
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);
        FillCreate(cut);

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();
        cut.Find($"#{AutomationsPage.CancelId}").Click();
        // Back in the editor, with everything the operator entered still there.
        Assert.AreEqual("nightly-temperature", cut.Find("#automation-id").GetAttribute("value"));
        service.Kind = OperatorUiResultKind.Success;
        cut.Find("#automation-reason").Change("nightly cadence");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        // The reason is part of the durable payload hash, so reusing the key would be an unexplainable
        // key-conflict dead end.
        Assert.HasCount(2, service.SaveRequests);
        Assert.AreNotEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
        Assert.AreEqual("nightly cadence", service.SaveRequests[1].Reason);
    }

    [TestMethod]
    public void Edit_KeepsTheStoredDefinitionRatherThanTheRegistryDefaults()
    {
        using var context = CreateContext();
        Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-sky-temperature").Click();

        // The definition is Capture Relative while the registry's first compatible trigger is Periodic, so a
        // reseed would visibly rewrite it.
        Assert.AreEqual("Edit sky-temperature", cut.Find("#automation-dialog-heading").TextContent.Trim());
        Assert.AreEqual(nameof(LocalAutomationTriggerKind.CaptureRelative), cut.Find("#automation-trigger").GetAttribute("value"));
        Assert.AreEqual("10", cut.Find("#automation-interval").GetAttribute("value"));
        Assert.IsTrue(cut.Find("#automation-id").HasAttribute("readonly"));

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        Assert.AreEqual("Record revision 3 of sky-temperature?", cut.Find("#automation-dialog-heading").TextContent.Trim());
    }

    [TestMethod]
    public void Edit_OnConflict_ReReadsAndRetriesWithTheRefreshedVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-sky-temperature").Click();
        var readsBefore = service.Reads;
        // The durable version moves while the operator is editing, which is what a conflict means.
        service.AdvanceStoredVersion();
        service.Kind = OperatorUiResultKind.Conflict;
        service.Message = "This automation changed since the page was read. Refresh before retrying.";

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();
        // A rejected save returns to the editor with the reason rather than discarding the edit.
        Assert.Contains("changed since the page was read", cut.Find(".automation-dialog-message").TextContent, StringComparison.Ordinal);
        // The conflict re-read must be carried into the next attempt, or every retry resends the same
        // stale token and conflicts forever.
        service.Kind = OperatorUiResultKind.Success;
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        Assert.IsGreaterThan(readsBefore, service.Reads, "A conflict re-reads durable state.");
        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(2L, service.SaveRequests[0].ExpectedVersion);
        Assert.AreEqual(3L, service.SaveRequests[1].ExpectedVersion);
        Assert.AreNotEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
    }

    [TestMethod]
    public void Edit_WhenTheConflictReReadFails_KeepsTheEditRatherThanDemotingItToACreate()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-sky-temperature").Click();

        service.Kind = OperatorUiResultKind.Conflict;
        service.FailReads = true;
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        // The conflict re-read failed, so nothing is known about the version. Zeroing it would silently
        // turn the edit into a create that can never succeed.
        Assert.Contains("Showing the last valid snapshot.", cut.Markup, StringComparison.Ordinal);
        service.FailReads = false;
        service.Kind = OperatorUiResultKind.Success;
        Assert.AreEqual("Edit sky-temperature", cut.Find("#automation-dialog-heading").TextContent.Trim());
        Assert.IsTrue(cut.Find("#automation-id").HasAttribute("readonly"));
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(2L, service.SaveRequests[1].ExpectedVersion);
    }

    [TestMethod]
    public void Edit_WhenTheDefinitionWasRemoved_KeepsTheEditAndRecreatesOnlyOnRequest()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-sky-temperature").Click();
        // Another operator removes the definition while this one is editing it.
        service.RemoveStoredDefinitions();
        service.Kind = OperatorUiResultKind.NotFound;
        service.Message = "The automation no longer exists.";

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        // The re-read finds nothing, and the editor still edits: the next save is not quietly a create.
        Assert.AreEqual("Edit sky-temperature", cut.Find("#automation-dialog-heading").TextContent.Trim());
        Assert.IsTrue(cut.Find("#automation-id").HasAttribute("readonly"));
        Assert.Contains("was removed", cut.Find(".automation-removed-note").TextContent, StringComparison.Ordinal);
        service.Kind = OperatorUiResultKind.Success;
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        Assert.Contains("nothing to edit", cut.Find(".automation-dialog-message").TextContent, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll($"#{AutomationsPage.ConfirmId}"));
        Assert.HasCount(1, service.SaveRequests);

        // Recreating is the operator's explicit choice, and only then is the same definition sent as a create.
        cut.Find("#automation-recreate").Click();
        Assert.AreEqual("Define a local automation", cut.Find("#automation-dialog-heading").TextContent.Trim());
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual("sky-temperature", service.SaveRequests[1].DefinitionId);
        Assert.AreEqual(0L, service.SaveRequests[1].ExpectedVersion);
    }

    [TestMethod]
    public void Create_AfterUnavailableAndClosing_SendsTheSamePayloadUnderANewKey()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        service.Kind = OperatorUiResultKind.Unavailable;
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);
        FillCreate(cut);
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        // Stepping back to the editor keeps the retry open; closing the editor abandons it.
        cut.Find($"#{AutomationsPage.CancelId}").Click();
        cut.Find("#automation-editor-cancel").Click();
        service.Kind = OperatorUiResultKind.Success;
        OpenCreate(cut);
        FillCreate(cut);
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(service.SaveRequests[0].DefinitionId, service.SaveRequests[1].DefinitionId);
        Assert.AreNotEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
    }

    [TestMethod]
    public void Toggle_AfterUnavailableAndClosing_SendsTheNextToggleUnderANewKey()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        service.Kind = OperatorUiResultKind.Unavailable;
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-toggle-sky-temperature").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();
        cut.Find($"#{AutomationsPage.CancelId}").Click();

        service.Kind = OperatorUiResultKind.Success;
        cut.Find("#automation-toggle-sky-temperature").Click();
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(service.SaveRequests[0].ExpectedVersion, service.SaveRequests[1].ExpectedVersion);
        Assert.AreNotEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
    }

    [TestMethod]
    public void Remove_ConfirmsAndSendsTheStoredExpectedVersionAndReason()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-remove-sky-temperature").Click();

        Assert.AreEqual("Remove sky-temperature?", cut.Find("#automation-dialog-heading").TextContent.Trim());
        Assert.AreEqual("Remove automation", cut.Find($"#{AutomationsPage.ConfirmId}").TextContent.Trim());
        Assert.IsEmpty(service.RemoveRequests);
        cut.Find("#automation-command-reason").Change("  sensor retired  ");
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        var request = service.RemoveRequests.Single();
        Assert.AreEqual("sky-temperature", request.DefinitionId);
        Assert.AreEqual(2L, request.ExpectedVersion);
        Assert.AreEqual("sensor retired", request.Reason);
        Assert.IsEmpty(cut.FindAll("dialog"));
    }

    [TestMethod]
    public void Toggle_SendsTheInvertedEnablementAtTheStoredVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-toggle-sky-temperature").Click();

        Assert.AreEqual("Disable sky-temperature?", cut.Find("#automation-dialog-heading").TextContent.Trim());
        cut.Find($"#{AutomationsPage.ConfirmId}").Click();

        var request = service.SaveRequests.Single();
        Assert.IsFalse(request.Enabled);
        Assert.AreEqual(2L, request.ExpectedVersion);
        Assert.AreEqual("sky-temperature", request.DefinitionId);
        Assert.AreEqual(LocalAutomationTriggerKind.CaptureRelative, request.TriggerKind);
        Assert.AreEqual(10, request.TriggerInterval);
        Assert.IsNull(request.Reason);
    }

    [TestMethod]
    public void Cancel_ClosesTheConfirmationWithoutIssuingACommand()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-remove-sky-temperature").Click();

        cut.Find($"#{AutomationsPage.CancelId}").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.RemoveRequests);
        Assert.IsEmpty(service.SaveRequests);
    }

    [TestMethod]
    public void Escape_OnASaveConfirmationReturnsToTheEditor_AndOnTheEditorCloses()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        OpenCreate(cut);
        FillCreate(cut);
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        cut.Find("dialog").TriggerEvent("oncancel", EventArgs.Empty);
        Assert.AreEqual("nightly-temperature", cut.Find("#automation-id").GetAttribute("value"));

        cut.Find("dialog").TriggerEvent("oncancel", EventArgs.Empty);
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.SaveRequests);

        // Closing discards the draft; the next create starts clean.
        OpenCreate(cut);
        Assert.AreEqual(string.Empty, cut.Find("#automation-id").GetAttribute("value") ?? string.Empty);
    }

    [TestMethod]
    public void ScheduleView_DrawsTheCaptureWindowNowAndDueRunsInSiteTime()
    {
        using var context = CreateContext();
        Register(context, Automation(), Calendar());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement(".automation-tabs");

        cut.FindAll(".automation-tabs button")[1].Click();

        Assert.AreEqual("Schedule", cut.Find(".automation-tabs button.active").TextContent.Trim());
        Assert.Contains("America/Phoenix", cut.Find("#automation-schedule .ops-panel-heading p").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("20:00 -07:00 – 20:00 -07:00 local", cut.Find("#automation-schedule .ops-panel-heading > span").TextContent.Trim());
        var band = cut.Find(".automation-night");
        Assert.AreEqual("Capture window / 19:08 -07:00 – 05:08 -07:00", band.TextContent.Trim());
        // The window opened before the track starts, so the band is clipped to the left edge.
        Assert.Contains("left:0%", band.GetAttribute("style")!, StringComparison.Ordinal);
        Assert.AreEqual("Now 20:00 -07:00", cut.Find(".automation-now small").TextContent.Trim());
        var marker = cut.Find(".automation-run-marker");
        Assert.AreEqual("21:00 -07:00", marker.QuerySelector("strong")!.TextContent.Trim());
        Assert.AreEqual("Sky temperature", marker.QuerySelector("small")!.TextContent.Trim());
        // The card is clamped inside the track; its tick stays at the exact time.
        Assert.Contains("left:11%", marker.GetAttribute("style")!, StringComparison.Ordinal);
        Assert.Contains("left:4.17%", cut.Find(".automation-run-tick").GetAttribute("style")!, StringComparison.Ordinal);

        var upcoming = cut.FindAll(".automation-upcoming li");
        Assert.HasCount(2, upcoming);
        Assert.AreEqual("Fri 4 Sep 21:00 -07:00", upcoming[0].QuerySelector("strong")!.TextContent.Trim());
        Assert.AreEqual("At capture sequence 420", upcoming[1].QuerySelector("strong")!.TextContent.Trim());
        Assert.Contains("Capture relative.", cut.Find(".ops-validation-list").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("1 of 32", FactText(cut, "Definitions"));
        Assert.IsEmpty(cut.FindAll(".automation-utc"));
    }

    [TestMethod]
    public void ScheduleView_AWindowOpenAllDaySaysSoRatherThanRepeatingOneClockTime()
    {
        using var context = CreateContext();
        var nightStart = new DateTimeOffset(2026, 9, 4, 19, 0, 0, TimeSpan.Zero);
        var calendar = new CameraAgentScheduleCalendar(
            "America/Phoenix",
            Instant,
            [
                new CameraAgentScheduleNight(new DateOnly(2026, 9, 4), nightStart, nightStart.AddDays(1),
                    [new CameraAgentScheduleSegment(nightStart, nightStart.AddDays(1), true, CaptureScheduleAdmissionReason.LegacyCompatibility, null, null)]),
                new CameraAgentScheduleNight(new DateOnly(2026, 9, 5), nightStart.AddDays(1), nightStart.AddDays(2),
                    [new CameraAgentScheduleSegment(nightStart.AddDays(1), nightStart.AddDays(2), true, CaptureScheduleAdmissionReason.LegacyCompatibility, null, null)])
            ]);
        Register(context, Automation(), calendar);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/automations?view=schedule");

        var cut = context.Render<AutomationsPage>();

        var band = cut.WaitForElement(".automation-night");
        Assert.HasCount(1, cut.FindAll(".automation-night"), "Adjacent admitted nights merge into one window.");
        Assert.AreEqual("Capture window / open all 24 hours", band.TextContent.Trim());
        Assert.Contains("width:100%", band.GetAttribute("style")!, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ScheduleView_CrowdedRunsKeepTheirTicksAndStayListed()
    {
        using var context = CreateContext();
        var calendar = Enumerable.Range(1, 4)
            .Select(minutes => new LocalAutomationCalendarEntry(
                $"run-{minutes}", $"Run {minutes}", LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
                "virtual-sky-temperature", Instant.AddHours(6).AddMinutes(minutes)))
            .ToList();
        Register(context, Automation(calendar: calendar), Calendar());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/automations?view=schedule");

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement(".automation-day-track");
        Assert.HasCount(4, cut.FindAll(".automation-run-tick"));
        Assert.HasCount(2, cut.FindAll(".automation-run-marker"), "Only two cards fit side by side; the rest keep a tick.");
        Assert.HasCount(1, cut.FindAll(".automation-run-marker.row-0"));
        Assert.HasCount(1, cut.FindAll(".automation-run-marker.row-1"));
        Assert.HasCount(5, cut.FindAll(".automation-upcoming li"));
    }

    [TestMethod]
    public void ScheduleView_WhenTheCalendarFails_ShowsTimesInUtcAndSaysSo()
    {
        using var context = CreateContext();
        Register(context, Automation());
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/automations?view=schedule");

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement(".automation-day-track");
        Assert.Contains("Times are shown in UTC because the site timezone could not be read.", cut.Find(".automation-utc").TextContent, StringComparison.Ordinal);
        Assert.Contains("The schedule calendar is unavailable.", cut.Find(".automation-utc").TextContent, StringComparison.Ordinal);
        Assert.AreEqual("03:00 +00:00 – 03:00 +00:00 UTC", cut.Find("#automation-schedule .ops-panel-heading > span").TextContent.Trim());
        Assert.IsEmpty(cut.FindAll(".automation-night"));
        Assert.AreEqual("04:00 +00:00", cut.Find(".automation-run-marker strong").TextContent.Trim());
    }

    [TestMethod]
    public void RunsView_ShowsEachOutcomeWithItsChip()
    {
        using var context = CreateContext();
        LocalAutomationRunOutcome[] outcomes =
        [
            LocalAutomationRunOutcome.Running,
            LocalAutomationRunOutcome.Succeeded,
            LocalAutomationRunOutcome.Skipped,
            LocalAutomationRunOutcome.Failed,
            LocalAutomationRunOutcome.Missed,
            LocalAutomationRunOutcome.Interrupted
        ];
        var runs = outcomes.Select((outcome, index) => Run(10 - index, outcome, index == 5 ? "retired" : "sky-temperature")).ToList();
        Register(context, Automation(runs: runs));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/automations?view=runs");

        var cut = context.Render<AutomationsPage>();

        cut.WaitForElement(".automation-run-table");
        var rows = cut.FindAll(".automation-run-table tbody tr");
        Assert.HasCount(6, rows);
        string[] chips = ["running", "success", "skipped", "failure", "warning", "warning"];
        for (var index = 0; index < rows.Count; index++)
        {
            var chip = rows[index].QuerySelector(".state-chip")!;
            Assert.IsTrue(chip.ClassList.Contains(chips[index]), $"{outcomes[index]} uses the {chips[index]} chip.");
        }
        Assert.Contains("Sky temperature", rows[0].TextContent, StringComparison.Ordinal);
        // A removed definition's runs stay listed under their identifier.
        Assert.Contains("retired", rows[5].QuerySelector("td:nth-child(2) strong")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Capture 410", rows[0].TextContent, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".ops-status-rail"), "The status rail belongs to the definitions view.");
    }

    [TestMethod]
    public void RunsView_WithNoRuns_SaysSo()
    {
        using var context = CreateContext();
        Register(context, Automation(runs: []));
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/automations?view=runs");

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() => Assert.Contains(
            "No automation run has been recorded on this CameraAgent.", cut.Find(".automation-empty").TextContent, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Tabs_WriteTheViewToTheQueryAndDefinitionsClearsIt()
    {
        using var context = CreateContext();
        Register(context, Automation());
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement(".automation-tabs");

        cut.FindAll(".automation-tabs button")[2].Click();
        Assert.EndsWith("view=runs", navigation.Uri);
        Assert.IsNotNull(cut.Find("#automation-runs"));

        cut.FindAll(".automation-tabs button")[1].Click();
        Assert.EndsWith("view=schedule", navigation.Uri);

        cut.FindAll(".automation-tabs button")[0].Click();
        Assert.DoesNotContain("view=", navigation.Uri, StringComparison.Ordinal);
        Assert.IsNotNull(cut.Find("#automation-definitions"));
    }

    [TestMethod]
    [DataRow(null, "Definitions")]
    [DataRow("schedule", "Schedule")]
    [DataRow("RUNS", "Run history")]
    [DataRow("unknown", "Definitions")]
    public void ViewQuery_SelectsTheMatchingTab(string? view, string expected)
    {
        Assert.AreEqual(
            expected switch
            {
                "Schedule" => AutomationsPage.AutomationView.Schedule,
                "Run history" => AutomationsPage.AutomationView.Runs,
                _ => AutomationsPage.AutomationView.Definitions
            },
            AutomationsPage.ParseView(view));
    }

    [TestMethod]
    [DataRow(LocalAutomationTriggerKind.Periodic, 60, "Every 1 min")]
    [DataRow(LocalAutomationTriggerKind.Periodic, 90, "Every 90 s")]
    [DataRow(LocalAutomationTriggerKind.Periodic, 7200, "Every 2 h")]
    [DataRow(LocalAutomationTriggerKind.CaptureRelative, 1, "Every capture")]
    [DataRow(LocalAutomationTriggerKind.CaptureRelative, 25, "Every 25 captures")]
    public void DescribeTrigger_UsesTheLargestWholeUnit(LocalAutomationTriggerKind trigger, int interval, string expected)
        => Assert.AreEqual(expected, AutomationsPage.DescribeTrigger(trigger, interval));

    private static void OpenCreate(IRenderedComponent<AutomationsPage> cut)
    {
        cut.WaitForElement("#automation-create:not([disabled])").Click();
        cut.WaitForElement("#automation-id");
    }

    private static void FillCreate(IRenderedComponent<AutomationsPage> cut)
    {
        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
    }

    private static string Fact(IElement container, string term)
        => container.QuerySelectorAll("dt")
            .Single(dt => string.Equals(dt.TextContent.Trim(), term, StringComparison.Ordinal))
            .NextElementSibling!.TextContent.Trim();

    private static string FactText(IRenderedComponent<AutomationsPage> cut, string term)
        => Fact(cut.Find(".ops-facts"), term);

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static AutomationUiService Register(
        BunitContext context,
        LocalAutomationOperatorState? automation,
        CameraAgentScheduleCalendar? calendar = null)
    {
        var service = new AutomationUiService(automation);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(
            new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()) { Calendar = calendar });
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(service);
        return service;
    }

    /// <summary>
    /// The observing night of 4 September in Phoenix: local noon to local noon, with one admitted capture window
    /// from 19:08 to 05:08 local.
    /// </summary>
    private static CameraAgentScheduleCalendar Calendar()
    {
        var nightStart = new DateTimeOffset(2026, 9, 4, 19, 0, 0, TimeSpan.Zero);
        var windowStart = new DateTimeOffset(2026, 9, 5, 2, 8, 0, TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(2026, 9, 5, 12, 8, 0, TimeSpan.Zero);
        var nightEnd = nightStart.AddDays(1);
        return new CameraAgentScheduleCalendar(
            "America/Phoenix",
            Instant,
            [
                new CameraAgentScheduleNight(
                    new DateOnly(2026, 9, 4),
                    nightStart,
                    nightEnd,
                    [
                        new CameraAgentScheduleSegment(nightStart, windowStart, false, CaptureScheduleAdmissionReason.DefaultClosed, null, null),
                        new CameraAgentScheduleSegment(windowStart, windowEnd, true, CaptureScheduleAdmissionReason.WeeklyWindow, "night", null),
                        new CameraAgentScheduleSegment(windowEnd, nightEnd, false, CaptureScheduleAdmissionReason.DefaultClosed, null, null)
                    ])
            ]);
    }

    private static LocalAutomationRun Run(long sequence, LocalAutomationRunOutcome outcome, string definitionId = "sky-temperature")
        => new(
            sequence,
            definitionId,
            $"{definitionId}|{sequence}",
            new string('a', 64),
            LocalAutomationTriggerKind.CaptureRelative,
            Instant.AddMinutes(-20 - sequence),
            Instant.AddMinutes(-20 - sequence),
            outcome == LocalAutomationRunOutcome.Running ? null : Instant.AddMinutes(-19 - sequence),
            outcome,
            $"{outcome} detail",
            410);

    private static LocalAutomationOperatorState Automation(
        IReadOnlyList<LocalAutomationDefinitionState>? definitions = null,
        IReadOnlyList<LocalAutomationCalendarEntry>? calendar = null,
        IReadOnlyList<LocalAutomationRun>? runs = null,
        bool registryAvailable = true)
    {
        var definition = new LocalAutomationDefinition(
            "sky-temperature",
            "Sky temperature",
            true,
            LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
            "virtual-sky-temperature",
            LocalAutomationTriggerKind.CaptureRelative,
            10,
            Instant.AddHours(-4));
        var state = new LocalAutomationDefinitionState(
            definition,
            2,
            new string('a', 64),
            Instant.AddHours(-1),
            "owner",
            "nightly",
            null,
            420,
            new LocalAutomationRun(
                7,
                "sky-temperature",
                "sky-temperature|aaaaaaaaaaaaaaaa|c410",
                new string('a', 64),
                LocalAutomationTriggerKind.CaptureRelative,
                Instant.AddMinutes(-20),
                Instant.AddMinutes(-20),
                Instant.AddMinutes(-19),
                LocalAutomationRunOutcome.Succeeded,
                "Acquired an observation.",
                410),
            [
                new LocalAutomationRevision(
                    2, new string('a', 64), definition, false, Instant.AddHours(-1), "owner", "nightly")
            ]);
        return new LocalAutomationOperatorState(
            StoreVersion: 2,
            ReadAtUtc: Instant,
            ObservedCaptureSequence: 412,
            Registry:
            [
                new LocalAutomationTaskDescriptor(
                    LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
                    "Acquires one observation from a registered environmental source.",
                    [LocalAutomationTriggerKind.Periodic, LocalAutomationTriggerKind.CaptureRelative],
                    registryAvailable ? ["virtual-sky-temperature"] : [],
                    registryAvailable,
                    registryAvailable
                        ? null
                        : "Environmental acquisition is disabled in this CameraAgent's startup configuration.")
            ],
            Definitions: definitions ?? [state],
            Calendar: calendar ??
            [
                new LocalAutomationCalendarEntry(
                    "sky-temperature",
                    "Sky temperature",
                    LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
                    "virtual-sky-temperature",
                    Instant.AddHours(1))
            ],
            Runs: runs ?? [state.LastRun!]);
    }

    internal sealed class AutomationUiService(LocalAutomationOperatorState? state) : ICameraAgentAutomationUiService
    {
        private LocalAutomationOperatorState? _state = state;

        internal List<LocalAutomationSaveRequest> SaveRequests { get; } = [];

        internal List<LocalAutomationRemoveRequest> RemoveRequests { get; } = [];

        internal OperatorUiResultKind Kind { get; set; } = OperatorUiResultKind.Success;

        internal string Message { get; set; } = "The automation command failed.";

        internal int Reads { get; private set; }

        internal bool FailReads { get; set; }

        public ValueTask<OperatorUiResult<LocalAutomationOperatorState>> GetAsync(
            CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(_state is null || FailReads
                ? OperatorUiResult<LocalAutomationOperatorState>.Failure(
                    OperatorUiResultKind.Unavailable, "Local automation state is unavailable.")
                : OperatorUiResult<LocalAutomationOperatorState>.Success(_state));
        }

        public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> SaveAsync(
            LocalAutomationSaveRequest request,
            CancellationToken cancellationToken)
        {
            SaveRequests.Add(request);
            return ValueTask.FromResult(Result());
        }

        public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RemoveAsync(
            LocalAutomationRemoveRequest request,
            CancellationToken cancellationToken)
        {
            RemoveRequests.Add(request);
            return ValueTask.FromResult(Result());
        }

        /// <summary>Removes every definition, as another operator's remove would.</summary>
        internal void RemoveStoredDefinitions()
        {
            if (_state is not null)
            {
                _state = _state with { Definitions = [] };
            }
        }

        /// <summary>Advances the durable version the way the real store does, so a stale token is visible.</summary>
        internal void AdvanceStoredVersion()
        {
            if (_state is null)
            {
                return;
            }
            _state = _state with
            {
                Definitions =
                [
                    .. _state.Definitions.Select(static definition => definition with
                    {
                        Version = definition.Version + 1
                    })
                ]
            };
        }

        private OperatorUiResult<LocalAutomationCommandResult> Result()
            => Kind == OperatorUiResultKind.Success
                ? OperatorUiResult<LocalAutomationCommandResult>.Success(
                    new LocalAutomationCommandResult(
                        LocalAutomationCommandStatus.Applied,
                        null,
                        null,
                        _state ?? LocalAutomationOperatorState.Empty))
                : OperatorUiResult<LocalAutomationCommandResult>.Failure(Kind, Message);
    }
}
