using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void ProjectedScenePanelEditsOnlyQueryScalarsAndExportsAccessibleFixture()
    {
        using var context = new BunitContext();
        var service = Configure(context, "pRoJeCtEdScEnE", """{ "MaximumMagnitude":6.50, "maximumResults":2000, "includeConstellationEndpointStars":false, "OutputVariant":"retained-scene", "AstronomyAlgorithmVersion":"retained-v1", "ConstellationIds":[ "ORI" ], "SolarSystemBodies":[ "Mars" ], "CuratedAnnotations":null, "future":[1e+02] }""");
        var source = service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=projected-scene#projected-scene");
        var cut = context.Render<ProcessingGraphEditorPage>();
        var panel = cut.Find("#projected-scene[aria-label='Projected scene query']");
        Assert.HasCount(4, panel.QuerySelectorAll("input"));
        foreach (var field in new[] { "maximumMagnitude", "maximumResults", "includeConstellationEndpointStars" })
        {
            var id = "node-options-0-" + field;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
            Assert.IsNotEmpty(cut.Find("#" + id + "-help").TextContent);
        }
        StringAssert.Contains(panel.TextContent, "physical predicted-scene", StringComparison.Ordinal);
        StringAssert.Contains(panel.TextContent, "VirtualSky uses its separate module settings", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("#node-options-0-maximumMagnitude-help").TextContent, "-30–30 mag", StringComparison.Ordinal);
        Assert.AreEqual("decimal", cut.Find("#node-options-0-maximumMagnitude").GetAttribute("inputmode"));
        Assert.AreEqual("numeric", cut.Find("#node-options-0-maximumResults").GetAttribute("inputmode"));
        var path = Path.Combine(TestContext.TestResultsDirectory!, "projected-scene-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Projected scene query fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-0-maximumMagnitude").Change("2");
        cut.Find("#node-options-0-maximumResults").Change("1");
        cut.Find("#node-options-0-includeConstellationEndpointStars").Change(true);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual(source.Replace("6.50", "2", StringComparison.Ordinal).Replace("2000", "1", StringComparison.Ordinal)
            .Replace("Stars\":false", "Stars\":true", StringComparison.Ordinal),
            service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands, "Saving does not validate, activate or change VirtualSky module settings.");
    }

    [TestMethod]
    [DataRow("maximumMagnitude", "-30.01")]
    [DataRow("maximumMagnitude", "30.01")]
    [DataRow("maximumMagnitude", "NaN")]
    [DataRow("maximumResults", "0")]
    [DataRow("maximumResults", "2147483647")]
    [DataRow("maximumResults", "1.5")]
    public void InvalidProjectedSceneEditInvalidatesThePreview(string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, "ProjectedScene", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-maximumResults").Change("1");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{}")]
    [DataRow("{ \"future\": [1e+02], \"CuratedAnnotations\": null }")]
    [DataRow("{ \"MaximumMagnitude\":6.50, \"maximumResults\":2000, \"includeConstellationEndpointStars\":false }")]
    public void UnchangedOrRestoredProjectedSceneQueryKeepsTheSourceAndCannotSave(string? json)
    {
        using var context = new BunitContext();
        var service = Configure(context, "ProjectedScene", json);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-maximumMagnitude").Change("2");
        cut.Find("#node-options-0-maximumMagnitude").Change("6.5");
        cut.Find("#node-options-0-maximumResults").Change("1");
        cut.Find("#node-options-0-maximumResults").Change("2000");
        cut.Find("#node-options-0-includeConstellationEndpointStars").Change(true);
        cut.Find("#node-options-0-includeConstellationEndpointStars").Change(false);
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        Assert.AreEqual(json, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
    }

    [TestMethod]
    public void ProjectedSceneDeepLinkExplainsAMissingNode()
    {
        using var context = new BunitContext();
        Configure(context, "Telemetry", null);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=projected-scene#projected-scene");
        var cut = context.Render<ProcessingGraphEditorPage>();
        StringAssert.Contains(cut.Find("p[role='status']").TextContent, "no supported projected scene node", StringComparison.Ordinal);
    }
}
