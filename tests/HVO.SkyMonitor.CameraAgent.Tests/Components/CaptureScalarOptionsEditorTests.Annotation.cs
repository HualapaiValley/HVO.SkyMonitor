using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void AnnotationPanelExposesEffectiveControlsWithAccessibleLabelsAndPreservesProtectedSource()
    {
        using var context = new BunitContext();
        var service = Configure(context, "aNnOtAtIoN", """{ "MaRkRadius":6, "RecipeVersion":"retained", "OutputVariant":"retained-output", "RequireProjectedSceneDependency":true, "ExpectedPositionDiagnostics":true, "ConstellationLineRed":1, "ConstellationLineGreen":2, "ConstellationLineBlue":3, "ConstellationIds":["ORI"], "TopLeftTokens":["utc"], "TopRightTokens":[], "BottomLeftTokens":[], "BottomRightTokens":[], "EnvironmentalKinds":[], "future":null, "Future":[1e+02] }""");
        var source = service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=legacy-annotation#legacy-annotation");
        var cut = context.Render<ProcessingGraphEditorPage>();
        var panel = cut.Find("#legacy-annotation[aria-label='Legacy annotation']");
        Assert.HasCount(22, panel.QuerySelectorAll("input"));
        foreach (var option in AnnotationOptionDescriptors.Descriptor.Options)
        {
            var id = "node-options-0-" + option.Key;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
            Assert.IsNotEmpty(cut.Find("#" + id + "-help").TextContent);
        }
        StringAssert.Contains(panel.TextContent, "constellation color affects RGB previews", StringComparison.Ordinal);
        Assert.HasCount(1, panel.QuerySelectorAll("input[type=color]"));
        Assert.IsEmpty(panel.QuerySelectorAll("select"));
        Assert.AreEqual("numeric", cut.Find("#node-options-0-metadataInset").GetAttribute("inputmode"));
        Assert.AreEqual("decimal", cut.Find("#node-options-0-constellationLineOpacity").GetAttribute("inputmode"));
        var path = Path.Combine(TestContext.TestResultsDirectory!, "legacy-annotation-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Legacy Annotation fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-0-markRadius").Change("12");
        cut.Find("#node-options-0-constellationLineColor").Change("#ef3210");
        cut.Find("#node-options-0-drawMetadataCorners").Change(true);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText();
        Assert.AreEqual(source.Replace("Radius\":6", "Radius\":12", StringComparison.Ordinal).Replace("Red\":1", "Red\":239", StringComparison.Ordinal).Replace("Green\":2", "Green\":50", StringComparison.Ordinal).Replace("Blue\":3", "Blue\":16", StringComparison.Ordinal).Replace(" }", ",\"drawMetadataCorners\":true }", StringComparison.Ordinal), saved);
        Assert.AreEqual(source, service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands, "Saving must leave validation and activation explicit.");
    }

    [TestMethod]
    public void InvalidAnnotationEditCannotSaveAStalePreview()
    {
        foreach (var (field, value) in new[] { ("markRadius", "33"), ("markerValue", "256"), ("labelScale", "0"),
            ("maximumLabelMagnitude", "30.01"), ("constellationLineValue", "-1"), ("constellationLineOpacity", "NaN"),
            ("constellationLineThickness", "9"), ("imageCircleValue", "1.5"), ("cardinalScale", "9"),
            ("metadataValue", "256"), ("metadataScale", "5"), ("metadataInset", "65"), ("metadataLineSpacing", "17") })
        {
            using var context = new BunitContext();
            var service = Configure(context, "Annotation", null);
            var cut = context.Render<ProcessingGraphEditorPage>();
            cut.Find("#node-options-0-markRadius").Change("12");
            cut.Find("form").Submit();
            Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("#node-options-0-" + field).Change(value);
            cut.Find("form").Submit();
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"), field);
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"), field);
            Assert.IsEmpty(service.Created);
        }
    }

    [TestMethod]
    public void UnchangedOrRestoredAnnotationStylingKeepsAbsentExplicitDefaultAndUnknownNullJson()
    {
        foreach (var json in new[] { null, "{}", "{ \"future\":null, \"TopLeftTokens\":[] }", "{ \"MarkRadius\":6, \"ConstellationLineOpacity\":8e-1 }" })
        {
            using var context = new BunitContext();
            var service = Configure(context, "Annotation", json);
            var cut = context.Render<ProcessingGraphEditorPage>();
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("#node-options-0-markRadius").Change("12");
            cut.Find("#node-options-0-markRadius").Change("6");
            cut.Find("#node-options-0-constellationLineOpacity").Change("0.5");
            cut.Find("#node-options-0-constellationLineOpacity").Change("0.8");
            cut.Find("#node-options-0-drawMetadataCorners").Change(true);
            cut.Find("#node-options-0-drawMetadataCorners").Change(false);
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsEmpty(service.Created);
            Assert.AreEqual(json, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
        }
    }

    [TestMethod]
    public void AnnotationDeepLinkExplainsMissingOrUnsupportedNodesWithoutConversion()
    {
        foreach (var alias in new[] { "Telemetry", "AnnotationV5" })
        {
            using var context = new BunitContext();
            var service = Configure(context, alias, "{\"future\":null}");
            context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=legacy-annotation#legacy-annotation");
            var cut = context.Render<ProcessingGraphEditorPage>();
            StringAssert.Contains(cut.Find("p[role='status']").TextContent, "no supported legacy Annotation node", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#legacy-annotation, .conversion-review"));
            Assert.IsEmpty(service.Created);
        }
    }
}
