using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void CloudAssessmentPanelEditsScalarsRetainsOpaqueReferenceAndExportsAccessibleFixture()
    {
        using var context = new BunitContext();
        var service = Configure(context, "CloudAssessment", """{ "GridColumns":16, "ClearReferenceManifestPath":"retained/clear.manifest.json", "future":[1e+02], "imageCircle":{"keep":17} }""");
        var source = service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=cloud-assessment#cloud-assessment");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#cloud-assessment[aria-label='Cloud assessment']"));
        foreach (var field in new[] { "enabled", "outputVariant", "gridColumns", "gridRows", "transmissionThresholdMillionths",
            "minimumReferenceSignal", "minimumSamplesPerTile", "maximumSaturatedFractionMillionths", "includeMask" })
        {
            var id = "node-options-0-" + field;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
            Assert.IsNotEmpty(cut.Find("#" + id + "-help").TextContent);
        }
        Assert.IsEmpty(cut.FindAll("input[id*='ReferenceManifestPath']"));
        Assert.IsFalse(cut.Markup.Contains("retained/clear.manifest.json", StringComparison.Ordinal));
        StringAssert.Contains(cut.Find("#node-options-0-gridColumns-help").TextContent, "1–64 columns", StringComparison.Ordinal);
        var path = Path.Combine(TestContext.TestResultsDirectory!, "cloud-assessment-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Cloud assessment options fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-0-gridColumns").Change("2");
        cut.Find("#node-options-0-gridRows").Change("1");
        cut.Find("#node-options-0-transmissionThresholdMillionths").Change("750000");
        cut.Find("#node-options-0-minimumReferenceSignal").Change("1");
        cut.Find("#node-options-0-minimumSamplesPerTile").Change("8");
        cut.Find("#node-options-0-maximumSaturatedFractionMillionths").Change("200000");
        cut.Find("#node-options-0-includeMask").Change(false);
        cut.Find("#node-options-0-outputVariant").Change("cloud-custom");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var options = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(2, options.GetProperty("GridColumns").GetInt32());
        Assert.AreEqual(1, options.GetProperty("gridRows").GetInt32());
        Assert.AreEqual(750_000, options.GetProperty("transmissionThresholdMillionths").GetInt32());
        Assert.AreEqual(1, options.GetProperty("minimumReferenceSignal").GetInt32());
        Assert.AreEqual(8, options.GetProperty("minimumSamplesPerTile").GetInt32());
        Assert.AreEqual(200_000, options.GetProperty("maximumSaturatedFractionMillionths").GetInt32());
        Assert.IsFalse(options.GetProperty("includeMask").GetBoolean());
        Assert.AreEqual("cloud-custom", options.GetProperty("outputVariant").GetString());
        Assert.AreEqual("retained/clear.manifest.json", options.GetProperty("ClearReferenceManifestPath").GetString());
        Assert.AreEqual("[1e+02]", options.GetProperty("future").GetRawText());
        Assert.AreEqual("{\"keep\":17}", options.GetProperty("imageCircle").GetRawText());
        Assert.IsFalse(options.TryGetProperty("enabled", out _));
        Assert.IsEmpty(service.Commands, "Saving does not activate.");
        Assert.AreEqual(source, service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("gridColumns", "0")]
    [DataRow("gridRows", "65")]
    [DataRow("transmissionThresholdMillionths", "1000000")]
    [DataRow("minimumReferenceSignal", "65536")]
    [DataRow("minimumSamplesPerTile", "2147483648")]
    [DataRow("maximumSaturatedFractionMillionths", "-1")]
    [DataRow("outputVariant", " ")]
    [DataRow("gridRows", "1.5")]
    public void InvalidCloudAssessmentFieldInvalidatesPreview(string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, "CloudAssessment", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-gridRows").Change("4");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void RestoredCloudAssessmentScalarsKeepAnUnchangedDraftUnsavable()
    {
        using var context = new BunitContext();
        var service = Configure(context, "CloudAssessment", """{ "future": [1e+02], "ClearReferenceManifestPath": "retained/clear.manifest.json" }""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-gridRows").Change("1");
        cut.Find("#node-options-0-gridRows").Change("12");
        cut.Find("#node-options-0-includeMask").Change(false);
        cut.Find("#node-options-0-includeMask").Change(true);
        cut.Find("#node-options-0-outputVariant").Change("cloud-custom");
        cut.Find("#node-options-0-outputVariant").Change("cloud-assessment-v1");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }
}
