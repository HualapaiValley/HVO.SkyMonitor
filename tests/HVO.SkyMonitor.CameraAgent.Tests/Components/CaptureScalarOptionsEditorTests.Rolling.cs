using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void RollingPanelPreservesSourceAndEditsFractionalNullableLimits()
    {
        using var context = new BunitContext();
        const string source = "{ \"OutputVariant\":\"kept\", \"Recipe\":null, \"future\":[1e+02] }";
        var service = Configure(context, "RollingCombination", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.HasCount(5, cut.FindAll(".node-options .option-field"));
        Assert.HasCount(2, cut.FindAll("#node-options-0-windowKind option"));
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        foreach (var key in new[] { "maximumAgeMilliseconds", "maximumIntegrationMilliseconds" })
        {
            var id = "node-options-0-" + key;
            Assert.AreEqual("decimal", cut.Find("#" + id).GetAttribute("inputmode"));
            Assert.AreEqual(id + "-help " + id + "-status", cut.Find("#" + id).GetAttribute("aria-describedby"));
            StringAssert.Contains(cut.Find("#" + id + "-status").TextContent, "Not specified", StringComparison.Ordinal);
            cut.Find("#" + id).Change("0.125");
            cut.Find("#" + id + "-clear").Click();
        }
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-windowSize").Change("3");
        cut.Find("#node-options-0-windowKind").Change("Centered");
        cut.Find("#node-options-0-maximumAgeMilliseconds").Change("0.125");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(0.125, saved.GetProperty("maximumAgeMilliseconds").GetDouble());
        Assert.AreEqual("kept", saved.GetProperty("OutputVariant").GetString());
        Assert.AreEqual("[1e+02]", saved.GetProperty("future").GetRawText());
        Assert.IsFalse(saved.TryGetProperty("maximumIntegrationMilliseconds", out _));
        Assert.IsEmpty(service.Commands);
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        var path = Path.Combine(TestContext.TestResultsDirectory!, "rolling-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Same-gain rolling options</title><body>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
    }

    [TestMethod]
    public void RollingInvalidEditsPreventSavingAndMixedGainHasNoPanel()
    {
        using var context = new BunitContext();
        var service = Configure(context, "RollingCombination", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        foreach (var invalid in new[] { "0", "-1", "NaN", "Infinity", "1e400", "null" })
        {
            cut.Find("#node-options-0-maximumIntegrationMilliseconds").Change("0.5");
            cut.Find("form").Submit();
            Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("#node-options-0-maximumIntegrationMilliseconds").Change(invalid);
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        }
        Assert.IsEmpty(service.Created);
        Assert.IsNull(HVO.SkyMonitor.CameraAgent.Services.ProcessingNodeOptionsForm.DescriptorFor("RollingCombination",
            System.Text.Json.JsonSerializer.SerializeToElement(new { Recipe = "mixed-gain-normalized-mean", normalizationPlan = new { keep = true } })));
    }
}
