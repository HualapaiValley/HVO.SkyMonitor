using AngleSharp.Dom;
using Bunit;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods.")]
public sealed class AutomationWindowWizardTests
{
    [TestMethod]
    public void Wizard_PreviewsBeforeSavingAndRetainsFieldsWhenSteppingBack()
    {
        using var context = Context(out var service);
        var cut = context.Render<AutomationsPage>();
        Start(cut);
        Assert.IsEmpty(service.SaveRequests);
        Assert.Contains("Step 2 of 3", cut.Markup, StringComparison.Ordinal);
        cut.Find("#automation-source-window").Change(nameof(LocalAutomationSourceWindowKind.CompletedCivilHour));
        cut.Find("#automation-settle").Change("120");
        cut.Find("#automation-back-task").Click();
        Assert.AreEqual("daily", cut.Find("#automation-id").GetAttribute("value"));
        cut.Find("#automation-save").Click();
        Assert.AreEqual("120", cut.Find("#automation-settle").GetAttribute("value"));
        cut.Find("#automation-save").Click();
        var preview = cut.Find("#automation-window-preview").TextContent;
        Assert.Contains("America/Phoenix", preview, StringComparison.Ordinal);
        Assert.Contains("-07:00", preview, StringComparison.Ordinal);
        Assert.Contains("UTC", preview, StringComparison.Ordinal);
        Assert.Contains("inclusive", preview, StringComparison.Ordinal);
        Assert.Contains("exclusive", preview, StringComparison.Ordinal);
        Assert.Contains("earliest final", preview, StringComparison.Ordinal);
        Assert.IsEmpty(service.SaveRequests);
        cut.Find("#automation-confirm").Click();
        var request = service.SaveRequests.Single();
        Assert.AreEqual(1, request.TriggerInterval);
        Assert.AreEqual(LocalAutomationTriggerKind.SourceWindowClosed, request.TriggerKind);
        Assert.AreEqual(LocalAutomationSourceWindowKind.CompletedCivilHour, request.SourceWindow!.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(120), request.SourceWindow.ProcessingSettleAllowance);
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("86401")]
    [DataRow("NaN")]
    [DataRow("0.00000001")]
    public void Wizard_RejectsInvalidSettleBeforePreview(string settle)
    {
        using var context = Context(out var service);
        var cut = context.Render<AutomationsPage>();
        Start(cut);
        cut.Find("#automation-settle").Change(settle);
        cut.Find("#automation-save").Click();
        Assert.IsEmpty(service.PreviewRequests);
        Assert.IsEmpty(service.SaveRequests);
        Assert.Contains("between 0 and 86400", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Wizard_NoEventExplainsMissingPreviewWithoutInventingATime()
    {
        using var context = Context(out var service);
        service.PreviewResult = OperatorUiResult<CameraAgentAutomationWindowPreview>.Success(
            new(DateTimeOffset.UnixEpoch, null, "solar.no-event"));
        var cut = context.Render<AutomationsPage>();
        Start(cut);
        cut.Find("#automation-save").Click();
        Assert.Contains("Source window unavailable (solar.no-event)", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#automation-window-preview dl"));
        Assert.IsEmpty(service.SaveRequests);
    }

    [TestMethod]
    public void Wizard_FailedPreviewStaysInEditorAndPolicyChangeGetsANewCommandKey()
    {
        using var context = Context(out var service);
        service.PreviewResult = OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(
            OperatorUiResultKind.Unavailable, "Preview unavailable.");
        var cut = context.Render<AutomationsPage>();
        Start(cut);
        cut.Find("#automation-save").Click();
        Assert.IsNotNull(cut.Find("#automation-settle"));
        Assert.IsEmpty(cut.FindAll("#automation-confirm"));
        service.PreviewResult = null;
        service.Kind = OperatorUiResultKind.Unavailable;
        cut.Find("#automation-save").Click();
        cut.Find("#automation-confirm").Click();
        var first = service.SaveRequests.Single();
        cut.Find("#automation-cancel").Click();
        cut.Find("#automation-settle").Change("600");
        cut.Find("#automation-save").Click();
        cut.Find("#automation-confirm").Click();
        Assert.AreNotEqual(first.IdempotencyKey, service.SaveRequests.Last().IdempotencyKey);
    }

    [TestMethod]
    public void Wizard_UnavailableTaskCannotBeCreated()
    {
        using var context = Context(out _, available: false);
        var cut = context.Render<AutomationsPage>();
        Assert.IsTrue(cut.Find("#automation-create").HasAttribute("disabled"));
        Assert.Contains("Producer unavailable", cut.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Definition_WithUnavailableProducerDoesNotAdvertiseEnabledExecution()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var definition = LocalAutomationWindowTests.Definition("daily",
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay));
        var state = LocalAutomationOperatorState.Empty with
        {
            Registry = new AutomationWizardFixture.WizardRegistry { Available = false }.Describe(),
            Definitions = [new(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition),
                DateTimeOffset.UnixEpoch, "owner", null, null, null, null, [])]
        };
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(new AutomationsPageTests.AutomationUiService(state));
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        var cut = context.Render<AutomationsPage>();
        Assert.AreEqual("Unavailable", cut.Find("#automation-definition-daily header .state-chip").TextContent);
        Assert.Contains("recorded enablement is on", cut.Find("#automation-definition-daily").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("target", "target is no longer registered")]
    [DataRow("trigger", "trigger is not supported")]
    [DataRow("source", "source period is not supported")]
    public void Definition_WithUnsupportedRetainedSettingsIsUnavailableAcrossCardsAndSchedule(string mismatch, string reason)
    {
        using var context = RetainedContext(mismatch, out _);
        var cut = context.Render<AutomationsPage>();
        var card = cut.Find("#automation-definition-daily");
        Assert.AreEqual("Unavailable", card.QuerySelector("header .state-chip")!.TextContent);
        Assert.Contains(reason, card.TextContent, StringComparison.Ordinal);
        Assert.IsTrue(cut.Find("#automation-toggle-daily").HasAttribute("disabled"));
        var rail = cut.FindAll(".ops-status-rail > div");
        Assert.AreEqual("0", rail[0].QuerySelector("strong")!.TextContent);
        Assert.AreEqual("None scheduled", rail[1].QuerySelector("strong")!.TextContent);
        var next = card.QuerySelectorAll("dl > div").Single(item => item.QuerySelector("dt")!.TextContent == "Next run");
        Assert.AreEqual("Unavailable", next.QuerySelector("dd")!.TextContent);
        cut.FindAll(".automation-tabs button")[1].Click();
        Assert.IsEmpty(cut.FindAll(".automation-upcoming li"));
        Assert.IsEmpty(cut.FindAll(".automation-run-tick"));
    }

    [TestMethod]
    [DataRow("target", LocalAutomationSourceWindowKind.SunriseDay)]
    [DataRow("source", LocalAutomationSourceWindowKind.CompletedCivilHour)]
    public void Definition_WithUnsupportedSettingsCanBeRepairedThroughTheVersionedEditor(string mismatch, LocalAutomationSourceWindowKind expectedKind)
    {
        using var context = RetainedContext(mismatch, out var service);
        var cut = context.Render<AutomationsPage>();
        cut.Find("#automation-edit-daily").Click();
        if (mismatch == "target")
        {
            cut.Find("#automation-target").Change("new-preset");
        }
        else
        {
            Assert.Contains("recorded source period is no longer supported", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        }
        cut.Find("#automation-save").Click();
        Assert.AreEqual(expectedKind.ToString(), cut.Find("#automation-source-window").GetAttribute("value"));
        cut.Find("#automation-save").Click();
        cut.Find("#automation-confirm").Click();
        var request = service.SaveRequests.Single();
        Assert.AreEqual("daily", request.DefinitionId);
        Assert.AreEqual(mismatch == "target" ? "new-preset" : "old-preset", request.TaskTarget);
        Assert.AreEqual(1, request.ExpectedVersion);
        Assert.AreEqual(expectedKind, request.SourceWindow!.Kind);
    }

    private const string GenericCombinationMessage = "That task and trigger combination is not registered on this CameraAgent.";

    [TestMethod]
    public async Task Wizard_DailySummaryWithAnHourlyPeriodNamesTheSelectedPeriodAndTheOneItNeeds()
    {
        await WithStillProductWizardAsync(async (fixture, targets) =>
        {
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            context.Services.AddSingleton<ICameraAgentAutomationUiService>(fixture.Service);
            context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
            var cut = context.Render<AutomationsPage>();
            Start(cut, "summary");
            await cut.Find("#automation-back-task").ClickAsync(new());
            await cut.Find("#automation-target").ChangeAsync(new ChangeEventArgs { Value = targets[NightlyProductKind.DailySummary] });
            await cut.Find("#automation-save").ClickAsync(new());
            await cut.Find("#automation-source-window").ChangeAsync(new ChangeEventArgs
            { Value = nameof(LocalAutomationSourceWindowKind.CompletedCivilHour) });
            await cut.Find("#automation-save").ClickAsync(new());

            var alert = cut.WaitForElement("[role=alert]").TextContent;
            StringAssert.Contains(alert, "The source period \"Completed site-local hour\" is not accepted by this task target.", StringComparison.Ordinal);
            StringAssert.Contains(alert, "Choose \"Sunrise to next sunrise\" for this producer.", StringComparison.Ordinal);
            Assert.DoesNotContain(GenericCombinationMessage, alert, StringComparison.Ordinal);
            // The rejected preview saved nothing and left the editor open on the same step.
            Assert.IsEmpty(cut.FindAll("#automation-confirm"));
            Assert.IsEmpty((await fixture.Store.GetStateAsync(CancellationToken.None)).Definitions);

            // Choosing the named period is the whole correction: the same definition then previews and saves.
            await cut.Find("#automation-source-window").ChangeAsync(new ChangeEventArgs
            { Value = nameof(LocalAutomationSourceWindowKind.SunriseDay) });
            await cut.Find("#automation-save").ClickAsync(new());
            cut.WaitForElement("#automation-window-preview");
            await cut.Find("#automation-confirm").ClickAsync(new());
            cut.WaitForElement("#automation-edit-summary");
            var saved = (await fixture.Store.GetStateAsync(CancellationToken.None)).Definitions.Single().Definition;
            Assert.AreEqual(targets[NightlyProductKind.DailySummary], saved.TaskTarget);
            Assert.AreEqual(LocalAutomationSourceWindowKind.SunriseDay, saved.SourceWindow!.Kind);
        });
    }

    [TestMethod]
    public async Task Preview_SourcePeriodMismatchIsReportedOnlyForATargetThatRejectsThePeriod()
    {
        await WithStillProductWizardAsync(async (fixture, targets) =>
        {
            LocalAutomationSaveRequest Request(NightlyProductKind kind, LocalAutomationSourceWindowKind period,
                LocalAutomationSourceSelection selection = LocalAutomationSourceSelection.AllActualSources)
                => AutomationWizardFixture.Request(kind: period) with
                {
                    TaskTarget = targets[kind],
                    SourceWindow = LocalAutomationWindowTests.Policy(period, selection)
                };

            var hourlySummary = await fixture.Service.PreviewWindowAsync(
                Request(NightlyProductKind.DailySummary, LocalAutomationSourceWindowKind.CompletedCivilHour), CancellationToken.None);
            Assert.AreEqual(OperatorUiResultKind.Invalid, hourlySummary.Kind);
            Assert.AreEqual("The source period \"Completed site-local hour\" is not accepted by this task target. "
                + "Choose \"Sunrise to next sunrise\" for this producer.", hourlySummary.Message);

            // The period is named first even when the selection is wrong too; the selection has its own message next.
            var hourlyDarkSummary = await fixture.Service.PreviewWindowAsync(Request(NightlyProductKind.DailySummary,
                LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceSelection.DarkNightActualSources), CancellationToken.None);
            Assert.AreEqual(hourlySummary.Message, hourlyDarkSummary.Message);
            var dailyDarkSummary = await fixture.Service.PreviewWindowAsync(Request(NightlyProductKind.DailySummary,
                LocalAutomationSourceWindowKind.SunriseDay, LocalAutomationSourceSelection.DarkNightActualSources), CancellationToken.None);
            Assert.AreEqual("The source selection does not match this task target. Choose All actual sources for this producer.",
                dailyDarkSummary.Message);

            // The same hourly period stays valid for the targets that take it, and a sunrise day for the daily summary.
            foreach (var (kind, period) in new[]
                     {
                         (NightlyProductKind.Keogram, LocalAutomationSourceWindowKind.CompletedCivilHour),
                         (NightlyProductKind.Keogram, LocalAutomationSourceWindowKind.SunriseDay),
                         (NightlyProductKind.DailySummary, LocalAutomationSourceWindowKind.SunriseDay)
                     })
            {
                var accepted = await fixture.Service.PreviewWindowAsync(Request(kind, period), CancellationToken.None);
                Assert.IsTrue(accepted.IsSuccess, $"{kind} over {period}: {accepted.Message}");
            }
        });
    }

    [TestMethod]
    [DataRow("definition.triggerKind")]
    [DataRow("definition.taskKind")]
    [DataRow(null)]
    public void OtherUnregisteredCombinationsKeepTheGenericMessage(string? fieldPath)
        => Assert.AreEqual(GenericCombinationMessage, CameraAgentAutomationUiService.DescribeFailure(new(
            LocalAutomationCommandStatus.Invalid, LocalAutomationContract.UnregisteredCombinationReasonCode, fieldPath,
            LocalAutomationOperatorState.Empty)));

    /// <summary>
    /// A wizard over the shipped still-product rules: the registry delegates to the installed adapter, so the
    /// rejection under test is the one a host returns rather than one a test double invents.
    /// </summary>
    private static async Task WithStillProductWizardAsync(
        Func<AutomationWizardFixture, IReadOnlyDictionary<NightlyProductKind, string>, Task> test)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-wizard-still");
        try
        {
            var options = NightlyProductFixture.HostOptions(root, NightlyProductFixture.Options());
            var clock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
            using var store = new SqliteNightlyProductStore(options, clock);
            using var generator = new NightlyProductGenerator(options, new FixedConfigurationAccessor(NightlyProductFixture.Configuration()),
                new InMemoryNightlySourceReader(), store, new AstronomyEnginePlanetEphemeris(), static () => null, clock);
            var adapter = new NightlyProductAutomationTaskRegistry(generator, options);
            using var fixture = new AutomationWizardFixture(registry: new StillProductRegistry(adapter));
            await fixture.Store.InitializeAsync(CancellationToken.None);
            var targets = adapter.Describe().Targets.ToDictionary(
                static target => NightlyProductPreset.TryParseTarget(target, out var kind) ? kind : throw new InvalidOperationException(target));
            await test(fixture, targets);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class StillProductRegistry(ILocalAutomationWindowTaskAdapter adapter) : ILocalAutomationTaskRegistry
    {
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => [adapter.Describe()];

        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => adapter.Validate(definition);

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
            => adapter.ExecuteAsync(occurrence, cancellationToken);
    }

    private static BunitContext RetainedContext(string mismatch, out AutomationsPageTests.AutomationUiService service)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var definition = LocalAutomationWindowTests.Definition("daily",
            LocalAutomationWindowTests.Policy(LocalAutomationSourceWindowKind.SunriseDay)) with
        { TaskTarget = "old-preset" };
        var due = definition.TriggerEpochUtc.AddHours(1);
        var state = LocalAutomationOperatorState.Empty with
        {
            ReadAtUtc = definition.TriggerEpochUtc,
            Registry = [new(LocalAutomationTaskKind.StillImageGeneration, "Available producer",
                mismatch == "trigger" ? [LocalAutomationTriggerKind.Periodic] : [LocalAutomationTriggerKind.SourceWindowClosed],
                mismatch == "target" ? ["new-preset"] : [definition.TaskTarget], true, null)
                { SupportedSourceWindows = mismatch == "source" ? [LocalAutomationSourceWindowKind.CompletedCivilHour] : [LocalAutomationSourceWindowKind.SunriseDay] }],
            Definitions = [new(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition),
                definition.TriggerEpochUtc, "owner", null, due, null, null, [])],
            Calendar = [new(definition.DefinitionId, definition.Name, definition.TaskKind, definition.TaskTarget, due)]
        };
        service = new(state);
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(service);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        return context;
    }

    internal static void Start(IRenderedComponent<AutomationsPage> cut, string id = "daily")
    {
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#automation-create").HasAttribute("disabled")));
        cut.Find("#automation-create").Click();
        cut.WaitForElement("#automation-id").Change(id);
        cut.Find("#automation-name").Change(id);
        cut.Find("#automation-save").Click();
    }

    private static BunitContext Context(out AutomationsPageTests.AutomationUiService service, bool available = true)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var registry = new AutomationWizardFixture.WizardRegistry { Available = available };
        service = new(LocalAutomationOperatorState.Empty with { Registry = registry.Describe() });
        context.Services.AddSingleton<ICameraAgentAutomationUiService>(service);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        return context;
    }
}
