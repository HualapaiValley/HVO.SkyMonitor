using System.Text.Json;
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
public sealed class CaptureScalarOptionsEditorTests
{
    public Microsoft.VisualStudio.TestTools.UnitTesting.TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ScalarPanelsRetainBrowserFixtureAndExplicitFalseWithoutRewritingSource()
    {
        using var context = new BunitContext();
        var service = Configure(context, "ImageQuality", null);
        var original = service.RevisionDetail!.Pipeline.Steps[0];
        service.RevisionDetail = service.RevisionDetail with
        {
            Pipeline = new CapturePipelineConfig([
                original with { Order = 17 },
                original with { Type = "Telemetry", Id = "telemetry", Order = 23, Enabled = false },
                original with { Type = "VirtualSkyCloudObservation", Id = "cloud", Order = 91 }
            ])
        };
        service.StepAliases = ["ImageQuality", "Telemetry", "VirtualSkyCloudObservation"];
        var cut = context.Render<ProcessingGraphEditorPage>();
        var ids = cut.FindAll("[id]").Select(static element => element.Id).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count(), "Every panel must have a unique section anchor.");
        Assert.IsFalse(cut.Find("#node-options-1-enabled").HasAttribute("checked"));
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        var path = Path.Combine(TestContext.TestResultsDirectory!, "capture-scalar-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><title>Capture scalar options: rendered component fixture</title><body>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-1-enabled").Change(true);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var steps = service.Created.Single().Pipeline.Steps;
        CollectionAssert.AreEqual(new int?[] { 17, 23, 91 }, steps.Select(static step => step.Order).ToArray());
        Assert.IsTrue(steps[1].Enabled);
        Assert.IsTrue(steps.All(static step => step.Options is null));
    }

    [TestMethod]
    [DataRow("ImageQuality")]
    [DataRow("Telemetry")]
    [DataRow("VirtualSkyCloudObservation")]
    public void TogglePanelSavesOnlyTheEditedMemberWithAccessibleLabels(string alias)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, """{ "future": [1e+02] }""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        var toggle = cut.Find("#node-options-0-enabled");
        Assert.IsTrue(toggle.HasAttribute("checked"));
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-enabled']").TextContent);
        Assert.AreEqual("node-options-0-enabled-help", toggle.GetAttribute("aria-describedby"));
        if (alias == "VirtualSkyCloudObservation")
            StringAssert.Contains(cut.Find("#node-options-0-enabled-help").TextContent, "simulated", StringComparison.Ordinal);
        toggle.Change(false);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var created = service.Created.Single();
        Assert.AreEqual("2", created.Revision);
        Assert.IsFalse(created.Pipeline.Steps[0].Enabled);
        Assert.AreEqual("{ \"future\": [1e+02] }", created.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands, "A save must leave activation explicit.");
        Assert.AreEqual("{ \"future\": [1e+02] }", service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("ImageQuality")]
    [DataRow("Telemetry")]
    [DataRow("VirtualSkyCloudObservation")]
    public void UnchangedSaveAndRestoredToggleDoNotMaterializeDefaults(string alias)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-enabled").Change(false);
        cut.Find("#node-options-0-enabled").Change(true);
        if (alias == "ImageQuality")
        {
            cut.Find("#node-options-0-outputVariant").Change("custom-quality");
            cut.Find("#node-options-0-outputVariant").Change("image-quality-v1");
        }
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        Assert.IsNull(service.RevisionDetail!.Pipeline.Steps[0].Options);
    }

    [TestMethod]
    public void QualityRouteCanChangeAndInvalidInputCannotSaveAStalePreview()
    {
        using var context = new BunitContext();
        var service = Configure(context, "ImageQuality", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-outputVariant").Change("quality-custom");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-outputVariant").Change(" ");
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        cut.Find("#node-options-0-outputVariant").Change("quality-custom");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("quality-custom", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetProperty("outputVariant").GetString());
    }

    [TestMethod]
    [DataRow("ImageQuality")]
    [DataRow("Telemetry")]
    [DataRow("VirtualSkyCloudObservation")]
    public void DeniedRevisionShowsNoOptionsOrSave(string alias)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, null);
        service.RevisionDetailFailure = OperatorUiResult<CameraAgentProcessingGraphRevisionDetail>.Failure(OperatorUiResultKind.Unauthorized, "Denied");
        var cut = context.Render<ProcessingGraphEditorPage>();
        StringAssert.EndsWith(context.Services.GetRequiredService<NavigationManager>().Uri, "/Account/AccessDenied", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll(".node-options"));
        Assert.IsEmpty(service.Created);
    }

    private static ProcessingExecutionPagesTests.GraphUiService Configure(BunitContext context, string alias, string? json)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new ProcessingExecutionPagesTests.GraphUiService
        {
            Registry = ProcessingGraphPagesTests.Registry(),
            StepAliases = [alias],
            RevisionDetail = new(ProcessingGraphPagesTests.Registry().Revisions[0],
                new CapturePipelineConfig([new CaptureProcessingStepConfig(alias, "node", 10,
                    json is null ? null : JsonDocument.Parse(json).RootElement.Clone(), ["$raw"])]), null, null),
            Preview = pipeline => OperatorUiResult<CaptureProcessingPlanPreview>.Success(new(pipeline.SchemaVersion, pipeline.DependencyPolicy,
                new string('D', 64), new string('E', 64), [], [])),
            Create = command => OperatorUiResult<ProcessingGraphRevisionState>.Success(new("rev-new", command.Name, command.Revision,
                ProcessingGraphRevisionLifecycle.Draft, new string('A', 64), new string('B', 64), new string('C', 64),
                DateTimeOffset.UtcNow, null, null, null))
        };
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?from=rev-active");
        return service;
    }
}
