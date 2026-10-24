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
public sealed partial class CaptureScalarOptionsEditorTests
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
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><title>Capture scalar options: rendered component fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
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
    [DataRow("Annotation")]
    [DataRow("ImageQuality")]
    [DataRow("Telemetry")]
    [DataRow("VirtualSkyCloudObservation")]
    [DataRow("WeatherCloudOverlay")]
    [DataRow("CloudAssessment")]
    [DataRow("Calibration")]
    [DataRow("Preview")]
    [DataRow("CalibratedPreview")]
    [DataRow("CombinedPreview")]
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
    [DataRow("Annotation")]
    [DataRow("ImageQuality")]
    [DataRow("Telemetry")]
    [DataRow("VirtualSkyCloudObservation")]
    [DataRow("WeatherCloudOverlay")]
    [DataRow("CloudAssessment")]
    [DataRow("Calibration")]
    [DataRow("Preview")]
    [DataRow("CalibratedPreview")]
    [DataRow("CombinedPreview")]
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
    [DataRow("Annotation")]
    [DataRow("ImageQuality")]
    [DataRow("Telemetry")]
    [DataRow("VirtualSkyCloudObservation")]
    [DataRow("WeatherCloudOverlay")]
    [DataRow("CloudAssessment")]
    [DataRow("Calibration")]
    [DataRow("Preview")]
    [DataRow("CalibratedPreview")]
    [DataRow("CombinedPreview")]
    [DataRow("JpegEncoding")]
    [DataRow("ProjectedScene")]
    [DataRow("CloudPresentationLayer")]
    [DataRow("EnvironmentPresentationLayer")]
    [DataRow("ScenePresentationLayer")]
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

    [TestMethod]
    public void WeatherOverlayDeepLinkEditsVisualFieldsAndRetainsAccessibleBrowserFixture()
    {
        using var context = new BunitContext();
        var service = Configure(context, "WeatherCloudOverlay", """{ "LineThickness":1, "future":[1e+02], "outputEncoding":"Packed" }""");
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=weather-cloud-overlay#weather-cloud-overlay");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#weather-cloud-overlay[aria-label='Weather and cloud overlay']"));
        foreach (var field in new[] { "enabled", "recipeVersion", "outputVariant", "lineThickness", "drawLabels", "maximumLabelCharacters" })
        {
            var id = "node-options-0-" + field;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
        }
        StringAssert.Contains(cut.Find("#node-options-0-lineThickness-help").TextContent, "1–8 px", StringComparison.Ordinal);
        var path = Path.Combine(TestContext.TestResultsDirectory!, "weather-cloud-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><title>Weather overlay options fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-0-lineThickness").Change("4");
        cut.Find("#node-options-0-drawLabels").Change(false);
        cut.Find("#node-options-0-maximumLabelCharacters").Change("12");
        cut.Find("#node-options-0-outputVariant").Change("weather-custom");
        cut.Find("#node-options-0-recipeVersion").Change("retained-label");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var options = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(4, options.GetProperty("LineThickness").GetInt32());
        Assert.IsFalse(options.GetProperty("drawLabels").GetBoolean());
        Assert.AreEqual(12, options.GetProperty("maximumLabelCharacters").GetInt32());
        Assert.AreEqual("weather-custom", options.GetProperty("outputVariant").GetString());
        Assert.AreEqual("retained-label", options.GetProperty("recipeVersion").GetString());
        Assert.AreEqual("[1e+02]", options.GetProperty("future").GetRawText());
        Assert.AreEqual("Packed", options.GetProperty("outputEncoding").GetString());
        Assert.IsFalse(options.TryGetProperty("enabled", out _));
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow("lineThickness", "0")]
    [DataRow("lineThickness", "9")]
    [DataRow("lineThickness", "2.5")]
    [DataRow("maximumLabelCharacters", "129")]
    [DataRow("maximumLabelCharacters", "-1")]
    [DataRow("recipeVersion", " ")]
    [DataRow("outputVariant", " ")]
    public void InvalidWeatherOverlayFieldInvalidatesPreview(string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, "WeatherCloudOverlay", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-lineThickness").Change("4");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
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
