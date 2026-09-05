using System.Globalization;
using Bunit;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class AutomationsPageTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 5, 3, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Render_ListsRegisteredScheduleAndSourceTasksWithTheDurableDefinitionContract()
    {
        using var context = CreateContext();
        Register(context, Automation());

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Automations", cut.Find("h1").TextContent.Trim());
            Assert.Contains("Capture schedule", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Next scheduled activity", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("virtual-sky-temperature", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("on demand available", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Environmental acquisition trigger kinds", cut.Markup, StringComparison.Ordinal);
            // The two vocabularies on this page must not read as one.
            Assert.Contains("their own separate trigger vocabulary", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("On Demand", cut.Markup, StringComparison.Ordinal);
            Assert.AreEqual(
                "Local automation definitions", cut.Find("#automation-definitions").TextContent.Trim());
            Assert.AreEqual("Next-run calendar", cut.Find("#automation-calendar").TextContent.Trim());
            Assert.AreEqual("Run history", cut.Find("#automation-runs").TextContent.Trim());
            Assert.Contains("Sky temperature", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Capture Relative", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Capture sequence 412", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("At capture sequence 420", cut.Markup, StringComparison.Ordinal);
            // The page states plainly that no definition can express a command or a script.
            Assert.Contains("runs a command, a script, or a path", cut.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("[role='alert']"));
            // Repeated row actions carry a distinguishing accessible name, not just "Remove".
            Assert.IsNotNull(cut.Find("button[aria-label='Remove sky-temperature']"));
            Assert.IsNotNull(cut.Find("button[aria-label='Disable sky-temperature']"));
            Assert.IsNotNull(cut.Find("a[href='/operations/schedule']"));
            Assert.IsNotNull(cut.Find("a[href='/operations/environment']"));
        });
    }

    [TestMethod]
    public void Render_WhenOneSourceFails_ShowsPartialDataNotice()
    {
        using var context = CreateContext();
        Register(context, Automation(), environmentAvailable: false);

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Showing partial data", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("environmental store is offline", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Capture schedule", cut.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#automation-environment"));
            Assert.IsNotNull(cut.Find("#automation-definitions"));
        });
    }

    [TestMethod]
    public void Render_WhenTheAutomationStoreIsUnavailable_KeepsTheRestOfTheSectionUsable()
    {
        using var context = CreateContext();
        Register(context, automation: null);

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Showing partial data", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(
                "The local automation store could not be read", cut.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#automation-calendar"));
            Assert.IsEmpty(cut.FindAll("#automation-runs"));
            Assert.IsNotNull(cut.Find("#automation-schedule"));
        });
    }

    [TestMethod]
    public void Render_WithNoDefinitions_ShowsTheEmptyStatesAndTheRegistryVocabulary()
    {
        using var context = CreateContext();
        Register(context, Automation(definitions: [], calendar: [], runs: []));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No local automation is defined", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("No enabled definition uses a wall-clock trigger", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("No automation run has been recorded", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Environmental On Demand Acquisition", cut.Markup, StringComparison.Ordinal);
            // Only registered targets can be chosen; the editor never accepts free text for one.
            Assert.AreEqual("SELECT", cut.Find("#automation-target").TagName);
            Assert.AreEqual("SELECT", cut.Find("#automation-trigger").TagName);
            Assert.AreEqual("SELECT", cut.Find("#automation-task").TagName);
        });
    }

    [TestMethod]
    public void Save_ConfirmsBeforeRecordingAndSendsTheExpectedVersionAndAKey()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-id");

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find("#automation-interval").Change("7200");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        Assert.IsNotNull(cut.Find("dialog"));
        Assert.IsEmpty(service.SaveRequests);
        cut.Find("dialog .btn-primary").Click();

        var request = service.SaveRequests.Single();
        Assert.AreEqual("nightly-temperature", request.DefinitionId);
        Assert.AreEqual("Nightly temperature", request.Name);
        Assert.AreEqual(7200, request.TriggerInterval);
        Assert.AreEqual(0L, request.ExpectedVersion);
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.IdempotencyKey));
        Assert.Contains("Recorded a new immutable automation revision", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Save_RejectsAnIntervalOutsideTheContractBoundsBeforeConfirming()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-interval");

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find("#automation-interval").Change("5");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.SaveRequests);
        Assert.Contains("must be between 60 and 86400", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Save_AfterUnavailable_ReusesTheIdempotencyKeyAndExpectedVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        service.Kind = OperatorUiResultKind.Unavailable;
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-id");

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find("dialog .btn-primary").Click();
        // The modal stays open on an unavailable command so the same command can be retried.
        Assert.IsNotNull(cut.Find("dialog"));
        service.Kind = OperatorUiResultKind.Success;
        cut.Find("dialog .btn-primary").Click();

        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
        Assert.AreEqual(service.SaveRequests[0].ExpectedVersion, service.SaveRequests[1].ExpectedVersion);
    }

    [TestMethod]
    public void Save_OnConflict_ReReadsDurableStateAndRetriesWithTheRefreshedVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-sky-temperature");
        cut.Find("#automation-edit-sky-temperature").Click();
        var readsBefore = service.Reads;
        // The durable version moves while the operator is editing, which is what a conflict means.
        service.AdvanceStoredVersion();
        service.Kind = OperatorUiResultKind.Conflict;
        service.Message = "This automation changed since the page was read. Refresh before retrying.";

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find("dialog .btn-primary").Click();
        // The conflict re-read must be carried into the next attempt, or every retry resends the same
        // stale token and conflicts forever.
        service.Kind = OperatorUiResultKind.Success;
        cut.Find("#automation-name").Change("Renamed");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find("dialog .btn-primary").Click();

        Assert.IsTrue(service.Reads > readsBefore, "A conflict re-reads durable state.");
        Assert.HasCount(2, service.SaveRequests);
        Assert.AreEqual(2L, service.SaveRequests[0].ExpectedVersion);
        Assert.AreEqual(3L, service.SaveRequests[1].ExpectedVersion);
    }

    [TestMethod]
    public void Save_AnnouncesItsOutcomeEvenWhileAnotherSourceIsFailing()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []),
            environmentAvailable: false);
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-id");

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find("dialog .btn-primary").Click();

        // A degraded read of some other source must never silence the outcome of the operator's command.
        Assert.HasCount(1, service.SaveRequests);
        Assert.Contains("Recorded a new immutable automation revision", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Save_RejectsAnEmptyIdentifierNameOrTargetBeforeConfirming()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-id");

        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.SaveRequests);
        Assert.Contains("The identifier must start with a lower-case letter", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Toggle_DoesNotRewriteTheDefinitionTheOperatorIsEditing()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-edit-sky-temperature");

        cut.Find("#automation-edit-sky-temperature").Click();
        cut.Find("#automation-trigger").Change(nameof(LocalAutomationTriggerKind.Periodic));
        cut.Find("#automation-interval").Change("900");
        cut.Find("#automation-toggle-sky-temperature").Click();
        cut.Find("dialog .btn-primary").Click();

        // Toggling a row must not reseed the editor, or a later Save would record fields the operator
        // never chose.
        Assert.AreEqual("Periodic", cut.Find("#automation-trigger").GetAttribute("value"));
        Assert.AreEqual("900", cut.Find("#automation-interval").GetAttribute("value"));
        Assert.AreEqual("sky-temperature", cut.Find("#automation-id").GetAttribute("value"));
        Assert.HasCount(1, service.SaveRequests);
    }

    [TestMethod]
    public void Save_AfterUnavailable_MintsANewKeyWhenOnlyTheReasonChanges()
    {
        using var context = CreateContext();
        var service = Register(context, Automation(definitions: [], calendar: [], runs: []));
        service.Kind = OperatorUiResultKind.Unavailable;
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-id");

        cut.Find("#automation-id").Change("nightly-temperature");
        cut.Find("#automation-name").Change("Nightly temperature");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find("dialog .btn-primary").Click();
        cut.Find("dialog .btn-outline-light").Click();
        service.Kind = OperatorUiResultKind.Success;
        cut.Find("#automation-reason").Change("nightly cadence");
        cut.Find($"#{AutomationsPage.SaveTriggerId}").Click();
        cut.Find("dialog .btn-primary").Click();

        // The reason is part of the durable payload hash, so reusing the key would be an unexplainable
        // key-conflict dead end.
        Assert.HasCount(2, service.SaveRequests);
        Assert.AreNotEqual(service.SaveRequests[0].IdempotencyKey, service.SaveRequests[1].IdempotencyKey);
        Assert.AreEqual("nightly cadence", service.SaveRequests[1].Reason);
    }

    [TestMethod]
    public void Render_ShowsTheRetainedRevisionHistoryTheRemovalPromisesToKeep()
    {
        using var context = CreateContext();
        Register(context, Automation());

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Recorded revisions", cut.Markup, StringComparison.Ordinal);
            Assert.IsNotNull(cut.Find("details.revision-history"));
            Assert.Contains("nightly", cut.Markup, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void Render_WhenEveryReadFails_ShowsTheUnavailableNotice()
    {
        using var context = CreateContext();
        var service = new AutomationUiService(null);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(
            new SchedulePageTests.ScheduleUiService(null));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(new EnvironmentalUiService(null));
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(service);

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Automations unavailable", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(
                "The local automation store could not be read", cut.Markup, StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#automation-calendar"));
            Assert.IsEmpty(cut.FindAll("#automation-schedule"));
        });
    }

    [TestMethod]
    public void Render_WhenTheRegistryIsUnavailable_SaysWhyAndOffersNoTarget()
    {
        using var context = CreateContext();
        Register(context, Automation(definitions: [], calendar: [], runs: [], registryAvailable: false));

        var cut = context.Render<AutomationsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(
                "Environmental acquisition is disabled", cut.Markup, StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll("#automation-target option"));
        });
    }

    [TestMethod]
    public void Remove_ConfirmsAndSendsTheStoredExpectedVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-remove-sky-temperature");

        cut.Find("#automation-remove-sky-temperature").Click();
        Assert.Contains("Remove sky-temperature?", cut.Markup, StringComparison.Ordinal);
        cut.Find("dialog .btn-primary").Click();

        var request = service.RemoveRequests.Single();
        Assert.AreEqual("sky-temperature", request.DefinitionId);
        Assert.AreEqual(2L, request.ExpectedVersion);
    }

    [TestMethod]
    public void Toggle_SendsTheInvertedEnablementAtTheStoredVersion()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-toggle-sky-temperature");

        cut.Find("#automation-toggle-sky-temperature").Click();
        Assert.Contains("Disable sky-temperature?", cut.Markup, StringComparison.Ordinal);
        cut.Find("dialog .btn-primary").Click();

        var request = service.SaveRequests.Single();
        Assert.IsFalse(request.Enabled);
        Assert.AreEqual(2L, request.ExpectedVersion);
        Assert.AreEqual("sky-temperature", request.DefinitionId);
    }

    [TestMethod]
    public void Cancel_ClosesTheConfirmationWithoutIssuingACommand()
    {
        using var context = CreateContext();
        var service = Register(context, Automation());
        var cut = context.Render<AutomationsPage>();
        cut.WaitForElement("#automation-remove-sky-temperature");

        cut.Find("#automation-remove-sky-temperature").Click();
        cut.Find("dialog .btn-outline-light").Click();

        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(service.RemoveRequests);
        Assert.IsEmpty(service.SaveRequests);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static AutomationUiService Register(
        BunitContext context,
        LocalAutomationOperatorState? automation,
        bool environmentAvailable = true)
    {
        var service = new AutomationUiService(automation);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(
            new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        context.Services.AddSingleton<ICameraAgentEnvironmentalUiService>(
            new EnvironmentalUiService(environmentAvailable ? Status() : null));
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(service);
        return service;
    }

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

    private static EnvironmentalUiStatus Status() => new(
        true,
        DateTimeOffset.Parse("2026-09-04T03:00:00Z", CultureInfo.InvariantCulture),
        12,
        4096,
        0,
        [
            new EnvironmentalUiSource(
                "virtual-sky-temperature",
                EnvironmentalObservationKind.AirTemperature,
                true,
                true,
                "Fresh",
                null,
                null,
                DateTimeOffset.Parse("2026-09-04T02:59:00Z", CultureInfo.InvariantCulture),
                60,
                DateTimeOffset.Parse("2026-09-04T03:05:00Z", CultureInfo.InvariantCulture),
                0)
        ],
        []);

    private sealed class EnvironmentalUiService(EnvironmentalUiStatus? status) : ICameraAgentEnvironmentalUiService
    {
        public ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(status is null
                ? OperatorUiResult<EnvironmentalUiStatus>.Failure(
                    OperatorUiResultKind.Unavailable, "The environmental store is offline.")
                : OperatorUiResult<EnvironmentalUiStatus>.Success(status));

        public ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>> GetHistoryAsync(
            EnvironmentalObservationKind? kind, int pageSize, string? cursor, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>> AcquireAsync(
            string sourceId, string idempotencyKey, string reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    internal sealed class AutomationUiService(LocalAutomationOperatorState? state) : ICameraAgentAutomationUiService
    {
        private LocalAutomationOperatorState? _state = state;

        internal List<LocalAutomationSaveRequest> SaveRequests { get; } = [];

        internal List<LocalAutomationRemoveRequest> RemoveRequests { get; } = [];

        internal OperatorUiResultKind Kind { get; set; } = OperatorUiResultKind.Success;

        internal string Message { get; set; } = "The automation command failed.";

        internal int Reads { get; private set; }

        public ValueTask<OperatorUiResult<LocalAutomationOperatorState>> GetAsync(
            CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult(_state is null
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
