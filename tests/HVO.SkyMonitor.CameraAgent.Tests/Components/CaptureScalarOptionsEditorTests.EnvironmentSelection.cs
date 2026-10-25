using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void EnvironmentSelectionPanelShowsDefaultSavesSubsetAndExplicitEmptyWithoutActivation()
    {
        using var context = new BunitContext();
        const string source = """{ "WidthPixels":1936,"HeightPixels":1216,"future":1e+02 }""";
        var service = Configure(context, "EnvironmentPresentationLayer", source);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=environment-layer#environment-layer");
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-environmentalKinds";
        string[] expected = ["AirTemperature", "RelativeHumidity", "AtmosphericPressure", "WindSpeed", "RainState", "CloudCover"];
        CollectionAssert.AreEqual(expected, cut.FindAll(id + " select").Select(static select => select.GetAttribute("value")!).ToArray());
        Assert.HasCount(12, cut.Find(id + "-0").Children);
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-environmentalKinds-0']").TextContent);
        Assert.AreEqual("node-options-0-environmentalKinds-help", cut.Find(id).GetAttribute("aria-describedby"));
        Assert.DoesNotContain("Maximum", cut.Find(id + "-help").TextContent, StringComparison.Ordinal);
        SaveEnvironmentEditorFixture("default", cut.Markup);
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find(id + "-0-down").Click();
        cut.Find(id + "-1-up").Click();
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        for (var i = 5; i > 0; i--) cut.Find(id + "-" + i + "-remove").Click();
        cut.Find(id + "-0").Change("WindDirection");
        SaveEnvironmentEditorFixture("subset", cut.Markup);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("WindDirection", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetProperty("environmentalKinds")[0].GetString());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{\"EnvironmentalKinds\":[0,\"aIrTemperature\",\"WindSpeed\"],\"future\":1e+02}")]
    [DataRow("{\"environmentalKinds\":[]}")]
    public void EnvironmentSelectionEmptyAndRestoredEditsKeepSourceIdentity(string? source)
    {
        using var context = new BunitContext();
        var service = Configure(context, "EnvironmentPresentationLayer", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-environmentalKinds";
        var count = cut.FindAll(id + " select").Count;
        cut.Find(id + "-add").Click();
        cut.Find(id + "-" + count + "-remove").Click();
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        if (count == 0) return;
        for (var i = count - 1; i >= 0; i--) cut.Find(id + "-" + i + "-remove").Click();
        SaveEnvironmentEditorFixture("empty", cut.Markup);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual(0, service.Created.Single().Pipeline.Steps[0].Options!.Value.GetProperty(source is null ? "environmentalKinds" : "EnvironmentalKinds").GetArrayLength());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
    }

    [TestMethod]
    public void EnvironmentSelectionInvalidEditInvalidatesPreviouslyValidPreview()
    {
        using var context = new BunitContext();
        var service = Configure(context, "EnvironmentPresentationLayer", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-environmentalKinds-0").Change("WindGust");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-environmentalKinds-0").Change("Undefined");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsEmpty(service.Created);
    }

    private void SaveEnvironmentEditorFixture(string state, string markup)
    {
        var path = Path.Combine(TestContext.TestResultsDirectory!, "environment-editor-" + state + ".html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Environment observation selection</title><body data-theme='hvo-dark'>" + markup + "</body></html>");
        TestContext.AddResultFile(path);
    }

    [TestMethod]
    public void EnvironmentSelectionOpeningInvalidRetainedSourceShowsErrorsWithoutRepair()
    {
        foreach (var token in new[] { "null", "[999]", "[\"Unknown\"]" })
        {
            using var context = new BunitContext();
            var source = "{\"environmentalKinds\":" + token + ",\"future\":1e+02}";
            var service = Configure(context, "EnvironmentPresentationLayer", source);
            var cut = context.Render<ProcessingGraphEditorPage>();
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsEmpty(service.Created);
            Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        }
        var form = new HVO.SkyMonitor.CameraAgent.Services.ProcessingNodeOptionsForm(HVO.SkyMonitor.Processing.RetainedLayerOptionDescriptors.Environment, null);
        form.ClearCollection("environmentalKinds");
        Assert.IsTrue(form.TryBuild(out var cleared, out _));
        Assert.AreEqual(0, cleared!.Value.GetProperty("environmentalKinds").GetArrayLength());
    }
}
