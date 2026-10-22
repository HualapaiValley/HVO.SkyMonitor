using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ConstellationOptionsEditorTests
{
    private static readonly string[] InvalidColours = ["#gg0000", "# 12345", "#12345 "];

    [TestMethod]
    public void GenericFormUsesDescriptorKeysDefaultsAndBounds()
    {
        using var context = new BunitContext();
        var descriptor = new ProcessingNodeOptionsDescriptor("fixture-options-v1", "Fixture", "fixture-v1", "Fixture", "Fixture options.",
        [
            ProcessingOptionDescriptor.WholeNumber("count", "count", "Sample count", "Choose sample count.", 10, 5, 20, "samples"),
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable samples", "Enable sampling.", false),
            ProcessingOptionDescriptor.Choice("mode", "mode", "Sample mode", "Choose a mode.", "mean", ["mean", "median"])
        ]);
        var form = new ProcessingNodeOptionsForm(descriptor, null);
        var cut = context.Render<ProcessingNodeOptionsEditor>(parameters => parameters
            .Add(editor => editor.IdPrefix, "fixture").Add(editor => editor.Form, form));
        Assert.AreEqual("10", cut.Find("#fixture-count").GetAttribute("value"));
        StringAssert.Contains(cut.Find("#fixture-count-help").TextContent, "5–20 samples", StringComparison.Ordinal);
        cut.Find("#fixture-count").Change("20");
        cut.Find("#fixture-enabled").Change(true);
        cut.Find("#fixture-mode").Change("median");
        Assert.IsTrue(form.TryBuild(out var options, out _));
        Assert.AreEqual(20, options!.Value.GetProperty("count").GetInt32());
        Assert.IsTrue(options.Value.GetProperty("enabled").GetBoolean());
        Assert.AreEqual("median", options.Value.GetProperty("mode").GetString());
    }

    [TestMethod]
    public void InvalidSourceCanBeRepairedWithoutChangingOtherDisplayedOptions()
    {
        using var context = new BunitContext();
        Configure(context, """{"constellationLineRed":255,"constellationLineGreen":0,"constellationLineBlue":0,"constellationLineThickness":0}""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.AreEqual("#ff0000", cut.Find("#node-options-0-constellationLineColor").GetAttribute("value"));
        Assert.AreEqual(ProcessingOptionReasonCodes.OutOfRange, cut.Find(".option-errors li").GetAttribute("data-reason-code"));
        cut.Find("#node-options-0-constellationLineThickness").Change("3");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(".option-errors"));
    }

    [TestMethod]
    public void EditPreviewAndSave_ChangesOnlyDescribedValuesInANewRevision()
    {
        using var context = new BunitContext();
        var service = Configure(context, """{ "ConstellationLineRed":96, "future":{"keep":[1, 2]}, "constellationLineThickness":2 }""");
        var source = service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText();
        var cut = context.Render<ProcessingGraphEditorPage>();

        Assert.AreEqual("#60a0ff", cut.Find("input[type=color]").GetAttribute("value"));
        cut.Find("input[type=color]").Change("#12ab34");
        Assert.AreEqual("#12ab34", cut.Find("#node-options-0-constellationLineColor").GetAttribute("value"));
        cut.Find("#node-options-0-constellationLineThickness").Change("5");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();

        var created = service.Created.Single();
        Assert.AreEqual("2", created.Revision);
        Assert.AreEqual("sCeNePrEsEnTaTiOnLaYeR", created.Pipeline.Steps[0].Type);
        var options = created.Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(18, options.GetProperty("ConstellationLineRed").GetInt32());
        Assert.AreEqual(171, options.GetProperty("constellationLineGreen").GetInt32());
        Assert.AreEqual(52, options.GetProperty("constellationLineBlue").GetInt32());
        Assert.AreEqual(5, options.GetProperty("constellationLineThickness").GetInt32());
        Assert.AreEqual("{\"keep\":[1, 2]}", options.GetProperty("future").GetRawText());
        Assert.AreEqual(source, service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands, "Saving must not validate or activate the draft implicitly.");
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotNull(cut.Find("a[href='/operations/pipeline/graphs/rev-new']"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{ \"future\": [1, 2] }")]
    public void OpeningAndPreviewingOrRestoringDefaults_DoesNotCreateARevision(string? json)
    {
        using var context = new BunitContext();
        var service = Configure(context, json);
        var cut = context.Render<ProcessingGraphEditorPage>();

        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        StringAssert.Contains(cut.Find(".editor-banner").TextContent, "No graph changes.", StringComparison.Ordinal);
        cut.Find("#node-options-0-constellationLineThickness").Change("7");
        cut.Find("#node-options-0-constellationLineThickness").Change("2");
        cut.Find("#node-options-0-constellationLineColor").Change("#60A0FF");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        Assert.AreEqual(json, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
    }

    [TestMethod]
    [DataRow("0", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("9", ProcessingOptionReasonCodes.OutOfRange)]
    [DataRow("2.5", ProcessingOptionReasonCodes.WrongType)]
    [DataRow("", ProcessingOptionReasonCodes.WrongType)]
    public void InvalidThickness_HasStableDiagnosticsAndCannotSaveAStalePreview(string value, string code)
    {
        using var context = new BunitContext();
        var service = Configure(context, null);
        var previews = 0;
        service.Preview = pipeline => { previews++; return Plan(pipeline); };
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-constellationLineThickness").Change("4");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));

        cut.Find("#node-options-0-constellationLineThickness").Change(value);
        cut.Find("form").Submit();

        Assert.AreEqual(code, cut.Find(".option-errors li").GetAttribute("data-reason-code"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.AreEqual(1, previews);
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void InvalidColour_CanBeCorrectedUsingTheHexInput()
    {
        using var context = new BunitContext();
        Configure(context, null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        foreach (var invalid in InvalidColours)
        {
            cut.Find("#node-options-0-constellationLineColor").Change(invalid);
            cut.Find("form").Submit();
            Assert.AreEqual(ProcessingOptionReasonCodes.WrongType, cut.Find(".option-errors li").GetAttribute("data-reason-code"));
        }
        cut.Find("#node-options-0-constellationLineColor").Change("#ff0000");
        Assert.IsEmpty(cut.FindAll(".option-errors"));
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
    }

    [TestMethod]
    public void CurrentSkyDeepLink_LoadsTheActiveGraphAndNamedSection()
    {
        using var context = new BunitContext();
        Configure(context, null);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=constellations#constellations");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#constellations input[type=color]"));
        StringAssert.Contains(cut.Find("p[role=note]").TextContent, "rev-active", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ExistingLifecycle_RollbackUsesTheRetainedRevisionAndCurrentRegistryVersion()
    {
        using var context = new BunitContext();
        var service = Configure(context, null);
        var cut = context.Render<ProcessingGraphsPage>();
        cut.Find("#graph-ROLLBACK-rev-retired").Click();
        cut.Find("dialog .btn-primary").Click();
        var command = service.Commands.Single();
        Assert.AreEqual("rollback", command.Action);
        Assert.AreEqual("rev-retired", command.RevisionId);
        Assert.AreEqual(7L, command.ExpectedVersion);
    }

    private static ProcessingExecutionPagesTests.GraphUiService Configure(BunitContext context, string? json)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = ProcessingGraphPagesTests.Registry(),
            StepAliases = ["ScenePresentationLayer", "ProjectedScene"],
            RevisionDetail = new(ProcessingGraphPagesTests.Registry().Revisions[0],
                new CapturePipelineConfig([new CaptureProcessingStepConfig("sCeNePrEsEnTaTiOnLaYeR", "scene", 10,
                    json is null ? null : JsonDocument.Parse(json).RootElement.Clone(), ["$raw"])]), null, null),
            Preview = Plan,
            Create = command => OperatorUiResult<ProcessingGraphRevisionState>.Success(new("rev-new", command.Name, command.Revision,
                ProcessingGraphRevisionLifecycle.Draft, new string('A', 64), new string('B', 64), new string('C', 64),
                DateTimeOffset.UtcNow, null, null, null))
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");
        return service;
    }

    private static OperatorUiResult<CaptureProcessingPlanPreview> Plan(CapturePipelineConfig pipeline)
        => OperatorUiResult<CaptureProcessingPlanPreview>.Success(new(pipeline.SchemaVersion, pipeline.DependencyPolicy,
            new string('D', 64), new string('E', 64), [], []));
}
