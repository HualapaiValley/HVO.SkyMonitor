using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    [DataRow("Preview")]
    [DataRow("CalibratedPreview")]
    [DataRow("CombinedPreview")]
    public void FixedTransferPanelEditsNestedValuesWithAccessibleLabelsAndRestoresNoOp(string alias)
    {
        using var context = new BunitContext();
        const string source = """{ "FixedTransfer": { "BlackLevel":1e+02, "WhiteLevel":4095, "Gamma":1.20, "future":[1e+02] }, "blackPercentile":0.25, "AsinhStrength":8, "future":true }""";
        var service = Configure(context, alias, source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        foreach (var field in new[] { "blackLevel", "whiteLevel", "gamma" })
        {
            var id = "node-options-0-" + field;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
            Assert.AreEqual("decimal", cut.Find("#" + id).GetAttribute("inputmode"));
        }
        var fixture = Path.Combine(TestContext.TestResultsDirectory!, "fixed-transfer-" + alias + ".html");
        File.WriteAllText(fixture, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Fixed transfer editor</title><body>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(fixture);
        Assert.IsEmpty(cut.FindAll("input[id*='Percentile'],input[id*='asinhStrength']"));
        cut.Find("#node-options-0-gamma").Change("2.2");
        cut.Find("#node-options-0-gamma").Change("1.2");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-blackLevel").Change("100.5");
        cut.Find("#node-options-0-whiteLevel").Change("5000");
        cut.Find("#node-options-0-gamma").Change("2.2");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual(source.Replace(":1e+02,", ":100.5,", StringComparison.Ordinal).Replace(":4095", ":5000", StringComparison.Ordinal)
            .Replace(":1.20", ":2.2", StringComparison.Ordinal), service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("blackLevel", "4095")]
    [DataRow("blackLevel", "-1")]
    [DataRow("whiteLevel", "64")]
    [DataRow("whiteLevel", "65536")]
    [DataRow("gamma", "NaN")]
    [DataRow("gamma", "Infinity")]
    [DataRow("gamma", "0.09")]
    [DataRow("gamma", "10.1")]
    [DataRow("gamma", "")]
    public void InvalidFixedTransferEditPreventsSavingPreviouslyValidDraft(string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, "Preview", """{"FixedTransfer":{}}""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-gamma").Change("1");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsEmpty(service.Created);
    }
}
