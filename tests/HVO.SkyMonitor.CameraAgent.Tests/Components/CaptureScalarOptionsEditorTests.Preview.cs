using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    [DataRow("Preview", "preview")]
    [DataRow("CalibratedPreview", "calibrated-preview")]
    [DataRow("CombinedPreview", "combined-preview")]
    public void PreviewPanelEditsEffectiveScalarsAndRetainsAccessibleFixture(string alias, string section)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, """{ "FixedTransfer":null, "BlackPercentile":0.25, "future":[1e+02] }""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#" + section));
        foreach (var field in new[] { "enabled", "recipeVersion", "outputVariant", "blackPercentile", "whitePercentile", "asinhStrength" })
        {
            var id = "node-options-0-" + field;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
        }
        var path = Path.Combine(TestContext.TestResultsDirectory!, section + "-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Preview options fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-0-blackPercentile").Change("0.1");
        cut.Find("#node-options-0-whitePercentile").Change("0.99");
        cut.Find("#node-options-0-asinhStrength").Change("8");
        cut.Find("#node-options-0-recipeVersion").Change("retained-label");
        cut.Find("#node-options-0-outputVariant").Change("preview-custom");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var options = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(0.1, options.GetProperty("BlackPercentile").GetDouble());
        Assert.AreEqual(0.99, options.GetProperty("whitePercentile").GetDouble());
        Assert.AreEqual(8, options.GetProperty("asinhStrength").GetDouble());
        Assert.AreEqual("retained-label", options.GetProperty("recipeVersion").GetString());
        Assert.AreEqual("preview-custom", options.GetProperty("outputVariant").GetString());
        Assert.AreEqual("null", options.GetProperty("FixedTransfer").GetRawText());
        Assert.AreEqual("[1e+02]", options.GetProperty("future").GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow("Preview")]
    [DataRow("CalibratedPreview")]
    [DataRow("CombinedPreview")]
    public void FixedPreviewPanelNeverOffersIneffectiveControlsOrChangesMode(string alias)
    {
        using var context = new BunitContext();
        const string source = """{ "FixedTransfer": { "BlackLevel":1e+02, "WhiteLevel":4095, "Gamma":1.2, "future":[1e+02] }, "blackPercentile":0.25, "AsinhStrength":8, "future":true }""";
        var service = Configure(context, alias, source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        StringAssert.Contains(cut.Find(".node-options-help").TextContent, "Fixed transfer is active", StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("input[id*='Percentile'],input[id*='asinhStrength'],input[id*='fixedTransfer']"));
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-outputVariant").Change("fixed-custom");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var options = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(source[..^2] + ",\"outputVariant\":\"fixed-custom\" }", options.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow("Preview", "blackPercentile", "0.999999")]
    [DataRow("Preview", "whitePercentile", "0.5")]
    [DataRow("Preview", "asinhStrength", "NaN")]
    [DataRow("Preview", "asinhStrength", "1001")]
    [DataRow("Preview", "recipeVersion", " ")]
    [DataRow("JpegEncoding", "jpegQuality", "0")]
    [DataRow("JpegEncoding", "jpegQuality", "101")]
    [DataRow("JpegEncoding", "jpegQuality", "75.5")]
    [DataRow("JpegEncoding", "outputVariant", " ")]
    public void InvalidPreviewOrJpegInputInvalidatesPreviouslyValidSave(string alias, string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-outputVariant").Change("edited");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("null")]
    [DataRow("4096")]
    public void JpegPanelPreservesResizeAndRestoredAbsentQuality(string? resize)
    {
        using var context = new BunitContext();
        var source = "{ \"future\":[1e+02]" + (resize is null ? "" : ", \"MaximumDimension\":" + resize) + " }";
        var service = Configure(context, "JpegEncoding", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#jpeg-encoding"));
        Assert.IsEmpty(cut.FindAll("input[id*='maximumDimension'],input[id*='enabled']"));
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-jpegQuality']").TextContent);
        cut.Find("#node-options-0-jpegQuality").Change("60");
        cut.Find("#node-options-0-jpegQuality").Change("80");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-jpegQuality").Change("60");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual(source[..^2] + ",\"jpegQuality\":60 }", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }
}
