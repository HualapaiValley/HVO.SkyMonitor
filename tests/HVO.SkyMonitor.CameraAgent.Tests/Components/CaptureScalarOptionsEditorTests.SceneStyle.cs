using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void SceneStyleWholeDraftUpgradeRequiresExplicitReviewAndReachesClosedEditors()
    {
        using var context = new BunitContext();
        var service = Configure(context, "ScenePresentationLayer", "{ \"markerValue\":144, \"future\":[1e+02] }");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsEmpty(cut.FindAll("#node-options-0-pointGlyph"));
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.FindAll("button").Single(button => button.TextContent == "Review whole-draft scene style upgrade").Click();
        Assert.IsNotEmpty(cut.Find(".conversion-review pre").TextContent);
        Assert.IsEmpty(service.Created);
        cut.FindAll("button").Single(button => button.TextContent == "Apply conversion to draft").Click();
        Assert.HasCount(2, cut.FindAll("#node-options-0-pointGlyph option"));
        Assert.HasCount(3, cut.FindAll("#node-options-0-labelFont option"));
        Assert.IsNotNull(cut.Find("#scene-overlay"));
        SaveFixture("scene-style-editor-default.html", cut.Markup);
        cut.Find("#node-options-0-pointGlyph").Change("Crosshair");
        cut.Find("#node-options-0-labelFont").Change("PlexRegular");
        cut.Find("#node-options-0-cardinalFont").Change("MonoRegular");
        cut.Find("#node-options-0-markerColor").Change("#ff8000");
        cut.Find("#node-options-0-labelColor").Change("#50f08c");
        cut.Find("#node-options-0-cardinalColor").Change("#78b4ff");
        cut.Find("#node-options-0-imageCircleColor").Change("#dc78c8");
        SaveFixture("scene-style-editor-changed.html", cut.Markup);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var step = service.Created.Single().Pipeline.Steps.Single();
        Assert.AreEqual(SceneStylePresentation.StepAlias, step.Type);
        Assert.AreEqual("Crosshair", step.Options!.Value.GetProperty("pointGlyph").GetString());
        Assert.AreEqual("PlexRegular", step.Options.Value.GetProperty("labelFont").GetString());
        Assert.AreEqual(255, step.Options.Value.GetProperty("markerRed").GetInt32());
        Assert.AreEqual(80, step.Options.Value.GetProperty("labelRed").GetInt32());
        Assert.AreEqual(180, step.Options.Value.GetProperty("cardinalGreen").GetInt32());
        Assert.AreEqual(200, step.Options.Value.GetProperty("imageCircleBlue").GetInt32());
        Assert.AreEqual("[1e+02]", step.Options.Value.GetProperty("future").GetRawText());
        Assert.IsEmpty(service.Commands);
        Assert.AreEqual("ScenePresentationLayer", service.RevisionDetail!.Pipeline.Steps.Single().Type);
    }

    [TestMethod]
    [DataRow("pointGlyph", "arbitrary")]
    [DataRow("labelFont", "PlexBold")]
    [DataRow("cardinalFont", "Arial")]
    [DataRow("labelColor", "#ffffff00")]
    public void SceneStyleInvalidFormCannotSaveEvenAfterPreview(string key, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, SceneStylePresentation.StepAlias, "{ \"future\":[1e+02] }");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-pointGlyph").Change("Crosshair");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + key).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void SceneStyleUnchangedAndRestoredEditorPreservesExactJson()
    {
        using var context = new BunitContext();
        const string json = "{ \"LabelFont\":\"MonoBold\", \"future\":[1e+02] }";
        var service = Configure(context, SceneStylePresentation.StepAlias, json);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-pointGlyph").Change("Crosshair");
        cut.Find("#node-options-0-pointGlyph").Change("Circle");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.AreEqual(json, service.RevisionDetail!.Pipeline.Steps.Single().Options!.Value.GetRawText());
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void SceneStyleDeniedSavePreservesSourceAndCannotActivate()
    {
        using var context = new BunitContext();
        const string source = "{ \"future\":[1e+02] }";
        var service = Configure(context, SceneStylePresentation.StepAlias, source);
        service.Create = _ => OperatorUiResult<ProcessingGraphRevisionState>.Failure(OperatorUiResultKind.Unauthorized, "Denied");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-pointGlyph").Change("Crosshair");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps.Single().Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    private void SaveFixture(string name, string markup)
    {
        var path = Path.Combine(TestContext.TestResultsDirectory!, name);
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Scene style editor</title><body data-theme='hvo-dark'>" + markup + "</body></html>");
        TestContext.AddResultFile(path);
    }
}
