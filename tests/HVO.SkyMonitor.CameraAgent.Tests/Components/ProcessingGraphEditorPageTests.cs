using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphEditorPageTests
{
    private static readonly string[] RawOnly = ["$raw"];
    private static readonly string[] ReorderedIds = ["telemetry", "preview"];

    [TestMethod]
    public void EmptyDraft_AddsNodesPreviewsAndSavesWithARetainedKey()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService();
        CapturePipelineConfig? previewed = null;
        service.Preview = pipeline =>
        {
            previewed = pipeline;
            var nodes = pipeline.Steps.Select(static step => new CaptureProcessingPlanNode(step.Id!, step.Type, step.Enabled ?? true, step.Required, step.Order, null, step.DependsOn, null, null, null)).ToArray();
            return OperatorUiResult<CaptureProcessingPlanPreview>.Success(new CaptureProcessingPlanPreview(pipeline.SchemaVersion, pipeline.DependencyPolicy, new string('D', 64), new string('E', 64), nodes, nodes));
        };
        var attempts = 0;
        service.Create = command => ++attempts == 1
            ? OperatorUiResult<ProcessingGraphRevisionState>.Failure(OperatorUiResultKind.Unavailable, "Response was unavailable.")
            : OperatorUiResult<ProcessingGraphRevisionState>.Success(new ProcessingGraphRevisionState("rev-new", command.Name, command.Revision, ProcessingGraphRevisionLifecycle.Draft, new string('N', 64), new string('S', 64), new string('L', 64), DateTimeOffset.UtcNow, null, null, null));
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.WaitForElement(".editor-form");

        cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Graph name", StringComparison.Ordinal)).QuerySelector("input")!.Change("night");
        cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Revision label", StringComparison.Ordinal)).QuerySelector("input")!.Change("1");
        cut.Find("select[aria-label='New node type']").Change("preview");
        cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node").Click();
        cut.Find("select[aria-label='New node type']").Change("telemetry");
        cut.FindAll("button").Single(static button => button.TextContent.Trim() == "Add node").Click();
        Assert.HasCount(2, cut.FindAll(".node-row"));
        // The second node may depend on the first: tick "preview" in its inputs.
        var telemetryInputs = cut.FindAll(".node-row")[1].QuerySelectorAll(".node-row__dependencies input");
        Assert.HasCount(2, telemetryInputs);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("svg[role='group']")));
        Assert.HasCount(2, previewed!.Steps);
        Assert.AreEqual("preview", previewed.Steps[0].Id);
        CollectionAssert.AreEqual(RawOnly, previewed.Steps[1].DependsOn!.ToArray());
        Assert.IsNotNull(cut.Find("svg title"));
        Assert.IsTrue(cut.Find("svg desc").TextContent.Contains("preview", StringComparison.Ordinal));
        Assert.HasCount(2, cut.FindAll("svg g[role='button'][tabindex='0']"));
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));

        cut.Find("button.btn-primary").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Response was unavailable.", StringComparison.Ordinal)));
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("saved as an immutable revision", StringComparison.Ordinal)));
        Assert.HasCount(2, service.Created);
        Assert.AreEqual(service.Created[0].Key, service.Created[1].Key);
        Assert.AreEqual(("night", "1"), (service.Created[1].Name, service.Created[1].Revision));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/rev-new']"));
    }

    [TestMethod]
    public void DraftFromRevision_LoadsNodesAndReordersFromTheList()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            RevisionDetail = new CameraAgentProcessingGraphRevisionDetail(
                ProcessingGraphPagesTests.Registry().Revisions[1],
                new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig("Preview", "preview", 10, null, ["$raw"], Enabled: true),
                    new CaptureProcessingStepConfig("Telemetry", "telemetry", 20, null, ["preview"], Required: false, Enabled: true)
                ]),
                null,
                null)
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/operations/pipeline/graphs/new?from=rev-validated");

        var cut = context.Render<ProcessingGraphEditorPage>();

        cut.WaitForElement(".node-row");
        Assert.IsTrue(cut.Markup.Contains("Started from revision", StringComparison.Ordinal));
        Assert.AreEqual("night", cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Graph name", StringComparison.Ordinal)).QuerySelector("input")!.GetAttribute("value"));
        Assert.AreEqual("3", cut.FindAll("label").Single(static label => label.TextContent.StartsWith("Revision label", StringComparison.Ordinal)).QuerySelector("input")!.GetAttribute("value"));
        Assert.IsTrue(cut.Find("button[aria-label='Move preview up']").HasAttribute("disabled"));

        cut.Find("button[aria-label='Move telemetry up']").Click();

        var ids = cut.FindAll(".node-row input[aria-label='Node identifier']").Select(static input => input.GetAttribute("value")).ToArray();
        CollectionAssert.AreEqual(ReorderedIds, ids);
        // telemetry now sits first, so it can no longer name preview as an input.
        Assert.HasCount(1, cut.FindAll(".node-row")[0].QuerySelectorAll(".node-row__dependencies input"));
    }

    [TestMethod]
    public void InvalidDraft_ReportsProblemsWithoutCallingTheService()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService();
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.WaitForElement(".editor-form");

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".editor-banner[role='alert']").TextContent, "The graph needs a name.", StringComparison.Ordinal));
        Assert.IsEmpty(service.Created);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void Diagram_DescribesEveryNodeAndFocusesRowsByKeyboard()
    {
        using var context = new BunitContext();
        var nodes = new[]
        {
            new CaptureProcessingPlanNode("preview", "Preview", true, true, 10, null, ["$raw"], null, null, null),
            new CaptureProcessingPlanNode("telemetry", "Telemetry", false, false, 20, null, ["preview"], null, null, null)
        };
        string? selected = null;

        var cut = context.Render<GraphDependencyDiagram>(parameters => parameters
            .Add(static diagram => diagram.Nodes, nodes)
            .Add(static diagram => diagram.NodeSelected, EventCallback.Factory.Create<string>(this, id => selected = id)));

        var svg = cut.Find("svg");
        // A group keeps the focusable node buttons in the accessibility tree; an img role would prune them.
        Assert.AreEqual("group", svg.GetAttribute("role"));
        StringAssert.Contains(cut.Find("svg desc").TextContent, "telemetry depends on preview.", StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll("path.diagram-edge"));
        Assert.IsNotNull(cut.Find("g.diagram-node--disabled"));
        cut.FindAll("g[role='button']")[1].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        Assert.AreEqual("telemetry", selected);
    }
}
