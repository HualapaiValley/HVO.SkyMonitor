using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    [DataRow("ScenePresentationLayer", "markerValue")]
    [DataRow("ScenePresentationLayer", "labelValue")]
    [DataRow("ScenePresentationLayer", "imageCircleValue")]
    [DataRow("ScenePresentationLayer", "cardinalValue")]
    [DataRow("EnvironmentPresentationLayer", "value")]
    public void IntensityPanelPreservesNoOpsAndSetsOrClearsOnlySelectedOverride(string alias, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var token in new string?[] { null, "null", "17" })
        foreach (var edit in new[] { "0", "255", "" })
        {
            using var context = new BunitContext();
            var source = "{ \"future\":[1e+02], \"constellationIds\":[\"Ori\"], \"OutputVariant\":\"kept\"" +
                (token is null ? "" : ", \"" + key.ToUpperInvariant() + "\":" + token) + " }";
            var service = Configure(context, alias, source);
            var cut = context.Render<ProcessingGraphEditorPage>();
            var id = "node-options-0-" + key;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help " + id + "-status", cut.Find("#" + id).GetAttribute("aria-describedby"));
            Assert.AreEqual("numeric", cut.Find("#" + id).GetAttribute("inputmode"));
            Assert.AreEqual("Use default palette", cut.Find("#" + id + "-clear").TextContent);
            StringAssert.Contains(cut.Find("#" + id + "-status").TextContent,
                token is null ? "Not specified" : token == "null" ? "Default palette (explicitly selected)" : "Value set", StringComparison.Ordinal);
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("#" + id).Change("255");
            cut.Find("#" + id).Change(token is null or "null" ? "" : token);
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            if (edit.Length == 0) cut.Find("#" + id + "-clear").Click();
            else cut.Find("#" + id).Change(edit);
            cut.Find("form").Submit();
            if (edit.Length == 0 && token is null or "null")
                Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            else
            {
                cut.Find("button.btn-primary").Click();
                var replacement = edit.Length == 0 ? "null" : edit;
                var expected = token is null ? source[..^2] + ",\"" + key + "\":" + replacement + " }"
                    : source.Replace("\":" + token + " }", "\":" + replacement + " }", StringComparison.Ordinal);
                Assert.AreEqual(expected, service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
            }
            Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
            Assert.IsEmpty(service.Commands);
            if (token is null && edit == "0")
            {
                var path = Path.Combine(TestContext.TestResultsDirectory!, alias + "-" + key + "-intensity.html");
                File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Layer intensities</title><body>" + cut.Markup + "</body></html>");
                TestContext.AddResultFile(path);
            }
        }
    }

    [TestMethod]
    [DataRow("ScenePresentationLayer", "markerValue")]
    [DataRow("ScenePresentationLayer", "labelValue")]
    [DataRow("ScenePresentationLayer", "imageCircleValue")]
    [DataRow("ScenePresentationLayer", "cardinalValue")]
    [DataRow("EnvironmentPresentationLayer", "value")]
    public void InvalidIntensityEditDisablesPreviouslyValidSave(string alias, string key)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        foreach (var invalid in new[] { "-1", "256", "1.5", "NaN", "Infinity", "1e400", "null" })
        {
            cut.Find("#node-options-0-" + key).Change("0");
            cut.Find("form").Submit();
            Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
            cut.Find("#node-options-0-" + key).Change(invalid);
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        }
        Assert.IsEmpty(service.Created);
    }
}
