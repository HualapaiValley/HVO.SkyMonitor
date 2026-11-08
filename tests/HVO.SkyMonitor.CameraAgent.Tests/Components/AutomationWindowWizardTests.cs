using AngleSharp.Dom;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
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
