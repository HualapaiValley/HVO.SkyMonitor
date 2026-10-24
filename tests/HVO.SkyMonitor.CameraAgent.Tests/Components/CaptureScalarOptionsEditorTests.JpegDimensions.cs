using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    [DataRow(null, "Not specified")]
    [DataRow("null", "Explicit null")]
    [DataRow("4096", "Limit set")]
    public void JpegDimensionPanelPreservesNoOpAndSetsOrClearsOnlyTheLimit(string? token, string status)
    {
        using var context = new BunitContext();
        var source = "{ \"JpegQuality\":90, \"OutputVariant\":\"retained\", \"future\":[1e+02]" +
            (token is null ? "" : ", \"MaximumDimension\":" + token) + " }";
        var service = Configure(context, "jPeGeNcOdInG", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "node-options-0-maximumDimension";
        StringAssert.Contains(cut.Find("#" + id + "-status").TextContent, status, StringComparison.Ordinal);
        Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
        Assert.AreEqual(id + "-help " + id + "-status", cut.Find("#" + id).GetAttribute("aria-describedby"));
        Assert.AreEqual("numeric", cut.Find("#" + id).GetAttribute("inputmode"));
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#" + id).Change("16384");
        cut.Find("#" + id).Change(token == "4096" ? "4096" : "");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#" + id).Change("4");
        if (token == "4096") cut.Find("#" + id + "-clear").Click();
        var path = Path.Combine(TestContext.TestResultsDirectory!, "jpeg-dimensions-" + (token ?? "absent") + ".html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>JPEG dimensions</title><body>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var expected = token == "4096" ? source.Replace("4096", "null", StringComparison.Ordinal) : token is null
            ? source[..^2] + ",\"maximumDimension\":4 }" : source.Replace("null", "4", StringComparison.Ordinal);
        Assert.AreEqual(expected, service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("16385")]
    [DataRow("1.5")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("1e400")]
    [DataRow("null")]
    public void InvalidJpegDimensionDisablesPreviouslyValidSave(string input)
    {
        using var context = new BunitContext();
        var service = Configure(context, "JpegEncoding", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-maximumDimension").Change("4");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-maximumDimension").Change(input);
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void ClearingAlreadyAbsentJpegLimitDoesNotMaterializeAnyOptions()
    {
        var form = new ProcessingNodeOptionsForm(ProcessingNodeOptionsForm.DescriptorFor("JpegEncoding")!, null);
        form.Set("maximumDimension", "");
        Assert.IsTrue(form.TryBuild(out var options, out _));
        Assert.IsNull(options);
        var explicitNull = JsonSerializer.Deserialize<JsonElement>("{ \"MaximumDimension\":null }");
        form = new ProcessingNodeOptionsForm(form.Descriptor, explicitNull);
        form.Set("maximumDimension", "");
        Assert.IsTrue(form.TryBuild(out options, out _));
        Assert.AreEqual(explicitNull.GetRawText(), options!.Value.GetRawText());
    }
}
