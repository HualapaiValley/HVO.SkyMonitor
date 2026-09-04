using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphPagesTests
{
    private static readonly DateTimeOffset Now = ProcessingExecutionPagesTests.Now;

    [TestMethod]
    public void GraphsPage_ListsRevisionsWithLifecycleActions()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService { Registry = Registry() });

        var cut = context.Render<ProcessingGraphsPage>();

        cut.WaitForElement(".revision-table");
        Assert.AreEqual("Named graphs", cut.Find("h1").TextContent.Trim());
        Assert.IsTrue(cut.Markup.Contains("Named", StringComparison.Ordinal));
        Assert.HasCount(4, cut.FindAll(".revision-table tbody tr"));
        Assert.IsNotNull(cut.Find("#graph-VALIDATE-rev-draft"));
        Assert.IsNotNull(cut.Find("#graph-ACTIVATE-rev-validated"));
        Assert.IsNotNull(cut.Find("#graph-ROLLBACK-rev-retired"));
        Assert.IsEmpty(cut.FindAll("#graph-RETIRE-rev-active"));
        Assert.IsEmpty(cut.FindAll("#graph-ACTIVATE-rev-active"));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/new']"));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/rev-draft']"));
    }

    [TestMethod]
    public void GraphsPage_ActivateConfirmation_UsesTheRegistryVersionAndReusesTheKeyOnRetry()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService { Registry = Registry() };
        var attempts = 0;
        service.RegistryMutation = _ => ++attempts == 1
            ? OperatorUiResult<ProcessingGraphRegistryState>.Failure(OperatorUiResultKind.Unavailable, "Response was unavailable.")
            : OperatorUiResult<ProcessingGraphRegistryState>.Success(Registry(activeId: "rev-validated", version: 8));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphsPage>();
        cut.WaitForElement("#graph-ACTIVATE-rev-validated");

        cut.Find("#graph-ACTIVATE-rev-validated").Click();
        var dialog = cut.Find("dialog");
        StringAssert.Contains(dialog.TextContent, "Activate this revision?", StringComparison.Ordinal);
        StringAssert.Contains(dialog.TextContent, "7", StringComparison.Ordinal);
        cut.Find("dialog input").Change("night graph");
        cut.Find("dialog .btn-primary").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Response was unavailable.", StringComparison.Ordinal)));
        Assert.IsNotNull(cut.Find("dialog"));
        cut.Find("dialog .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        Assert.HasCount(2, service.Commands);
        Assert.AreEqual(("activate", "rev-validated", (long?)7, service.Commands[0].Key, "night graph"), service.Commands[0]);
        Assert.AreEqual(service.Commands[0].Key, service.Commands[1].Key);
        Assert.AreEqual(7, service.Commands[1].ExpectedVersion);
        Assert.IsTrue(cut.Markup.Contains("Activate applied", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GraphsPage_Conflict_ClosesTheDialogRefreshesAndReportsIt()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService { Registry = Registry() };
        service.RegistryMutation = _ => OperatorUiResult<ProcessingGraphRegistryState>.Failure(OperatorUiResultKind.Conflict, "Graph registry state changed. Refresh and review the command again.");
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphsPage>();
        cut.WaitForElement("#graph-RETIRE-rev-draft");

        cut.Find("#graph-RETIRE-rev-draft").Click();
        cut.Find("dialog .btn-danger").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            StringAssert.Contains(cut.Find(".graphs-banner[role='alert']").TextContent, "Refresh and review the command again.", StringComparison.Ordinal);
        });
        Assert.HasCount(1, service.Commands);
        Assert.AreEqual("retire", service.Commands[0].Action);
    }

    [TestMethod]
    public void GraphsPage_Validate_DoesNotCarryAnExpectedVersion()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService { Registry = Registry() };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphsPage>();
        cut.WaitForElement("#graph-VALIDATE-rev-draft");

        cut.Find("#graph-VALIDATE-rev-draft").Click();
        cut.Find("dialog .btn-primary").Click();

        cut.WaitForAssertion(() => Assert.HasCount(1, service.Commands));
        Assert.AreEqual("validate", service.Commands[0].Action);
        Assert.IsNull(service.Commands[0].ExpectedVersion);
    }

    [TestMethod]
    public void DetailPage_ShowsDefinitionIdentitiesAndCompiledPlan()
    {
        using var context = new BunitContext();
        var pipeline = new CapturePipelineConfig(
        [
            new CaptureProcessingStepConfig("Preview", "preview", 10, null, ["$raw"], Enabled: true),
            new CaptureProcessingStepConfig("Telemetry", "telemetry", 20, null, ["preview"], Required: false, Enabled: false)
        ]);
        var plan = new CaptureProcessingPlanPreview(CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent, new string('D', 64), new string('E', 64),
            [new CaptureProcessingPlanNode("preview", "Preview", true, true, 10, null, ["$raw"], "encoded-preview", FrameArtifactRole.Preview, "display")],
            [new CaptureProcessingPlanNode("preview", "Preview", true, true, 10, null, ["$raw"], "encoded-preview", FrameArtifactRole.Preview, "display")]);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService
        {
            RevisionDetail = new CameraAgentProcessingGraphRevisionDetail(Registry().Revisions.Single(static revision => revision.RevisionId == "rev-validated"), pipeline, plan, null)
        });

        var cut = context.Render<ProcessingGraphDetailPage>(parameters => parameters.Add(static page => page.RevisionId, "rev-validated"));

        cut.WaitForElement(".definition-list");
        StringAssert.Contains(cut.Find("h1").TextContent, "night 2", StringComparison.Ordinal);
        Assert.HasCount(2, cut.FindAll(".definition-list li"));
        Assert.IsNotNull(cut.Find(".definition-node--disabled"));
        Assert.IsTrue(cut.Markup.Contains("depends on preview", StringComparison.Ordinal));
        Assert.IsTrue(cut.Markup.Contains("Effective graph", StringComparison.Ordinal));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/new?from=rev-validated']"));
    }

    [TestMethod]
    public void DetailPage_WhenThePlanDoesNotCompile_ShowsTheSanitizedReason()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService
        {
            RevisionDetail = new CameraAgentProcessingGraphRevisionDetail(Registry().Revisions[0], CapturePipelineConfig.Empty, null, "The desired graph contains a dependency cycle.")
        });

        var cut = context.Render<ProcessingGraphDetailPage>(parameters => parameters.Add(static page => page.RevisionId, "rev-active"));

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("does not compile today", StringComparison.Ordinal)));
        Assert.IsTrue(cut.Markup.Contains("dependency cycle", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Pages_NotFoundAndUnauthorized_AreExplicit()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService
        {
            RevisionDetailFailure = OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>.Failure(OperatorUiResultKind.NotFound, "The graph revision was not found."),
            RegistryFailure = OperatorUiResult<ProcessingGraphRegistryState>.Failure(OperatorUiResultKind.Unauthorized, "denied")
        });
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        var detail = context.Render<ProcessingGraphDetailPage>(parameters => parameters.Add(static page => page.RevisionId, "missing"));
        detail.WaitForAssertion(() => Assert.IsTrue(detail.Markup.Contains("Graph revision not found", StringComparison.Ordinal)));
        _ = context.Render<ProcessingGraphsPage>();

        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
    }

    internal static ProcessingGraphRegistryState Registry(string activeId = "rev-active", long version = 7) => new(
        ProcessingGraphRegistryMode.Named, activeId, "rev-basic", version,
        [
            new ProcessingGraphRevisionState("rev-active", "night", "1", ProcessingGraphRevisionLifecycle.Active, new string('A', 64), new string('S', 64), new string('L', 64), Now.AddDays(-3), Now.AddDays(-3), Now.AddDays(-2), null),
            new ProcessingGraphRevisionState("rev-validated", "night", "2", ProcessingGraphRevisionLifecycle.Validated, new string('B', 64), new string('S', 64), new string('L', 64), Now.AddDays(-1), Now.AddDays(-1), null, null),
            new ProcessingGraphRevisionState("rev-draft", "night", "3", ProcessingGraphRevisionLifecycle.Draft, new string('C', 64), new string('S', 64), new string('L', 64), Now.AddHours(-1), null, null, null),
            new ProcessingGraphRevisionState("rev-retired", "day", "1", ProcessingGraphRevisionLifecycle.Retired, new string('E', 64), new string('S', 64), new string('L', 64), Now.AddDays(-10), Now.AddDays(-10), Now.AddDays(-9), Now.AddDays(-4))
        ]);
}
