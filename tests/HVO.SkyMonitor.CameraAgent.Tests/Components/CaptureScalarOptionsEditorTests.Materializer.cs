using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void MaterializerPanelDistinguishesManifestDefaultsAndExplicitSelectionAndSavesDraftOnly()
    {
        using var context = new BunitContext();
        const string source = """{ "useRichFrameFacts":true, "outputVariant":"saved", "basePreviewVariant":"base", "future":1e+02 }""";
        var service = Configure(context, "PresentationMaterializer", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-enabledLayerKinds";
        StringAssert.Contains(cut.Find(id).TextContent, "Using manifest defaults.", StringComparison.Ordinal);
        cut.Find(id + "-add").Click();
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-enabledLayerKinds-0']").TextContent);
        cut.Find(id + "-0").Change("corner-annotations");
        StringAssert.Contains(cut.Find(id).TextContent, "Using an explicit selection.", StringComparison.Ordinal);
        var fixture = Path.Combine(TestContext.TestResultsDirectory!, "materializer-selection.html");
        File.WriteAllText(fixture, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Materializer selection</title><body>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(fixture);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("""{ "useRichFrameFacts":true, "outputVariant":"saved", "basePreviewVariant":"base", "future":1e+02,"enabledLayerKinds":["corner-annotations"] }""", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{ \"enabledLayerKinds\" : [ ] }")]
    [DataRow("{ \"EnabledLayerKinds\" : [ \"corner-annotations\", \"cloud-mask\" ], \"future\":1e+02 }")]
    public void MaterializerRestoredSelectionKeepsNoOpIdentity(string? source)
    {
        using var context = new BunitContext();
        var service = Configure(context, "PresentationMaterializer", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-enabledLayerKinds";
        var count = cut.FindAll(id + " > ol > li").Count;
        cut.Find(id + "-add").Click();
        cut.Find(id + "-" + count + "-remove").Click();
        if (count > 1)
        {
            cut.Find(id + "-0-down").Click();
            cut.Find(id + "-1-up").Click();
        }
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
    }

    [TestMethod]
    public void MaterializerAliasCollisionInvalidatesPreviewAndDefaultActionRepairsNull()
    {
        using var context = new BunitContext();
        var service = Configure(context, "PresentationMaterializer", """{"enabledLayerKinds":null,"future":1e+02}""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-enabledLayerKinds";
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find(id + "-clear").Click();
        cut.Find(id + "-add").Click();
        cut.Find(id + "-0").Change("environment");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find(id + "-add").Click();
        cut.Find(id + "-1").Change("corner-annotations");
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        cut.Find(id + "-clear").Click();
        StringAssert.Contains(cut.Find(id).TextContent, "Using manifest defaults.", StringComparison.Ordinal);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("""{"enabledLayerKinds":[],"future":1e+02}""", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    public void DeniedMaterializerSaveKeepsSourceAndActivationUnchanged()
    {
        using var context = new BunitContext();
        const string source = """{"enabledLayerKinds":["environment"]}""";
        var service = Configure(context, "PresentationMaterializer", source);
        service.Create = _ => OperatorUiResult<ProcessingGraphRevisionState>.Failure(OperatorUiResultKind.Unauthorized, "Denied");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-enabledLayerKinds-clear").Click();
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }
}
