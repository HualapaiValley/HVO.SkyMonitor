using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void ManifestPanelEditsNestedRowsPreservingUnknownBytesAndDisabledNode()
    {
        using var context = new BunitContext();
        const string row = "{ \"KiNd\":\"cloud-mask\", \"EnAbLed\":true, \"ZOrder\":null, \"future\":1e+02 }";
        const string untouched = "{ \"kind\":\"environment\", \"future\":\"\\u0041\" }";
        const string source = "{ \"Layers\":[" + row + ", " + untouched + "], \"UseRichFrameFacts\":false, \"OutputVariant\":\"saved\", \"future\":1e+02 }";
        var service = Configure(context, "oVeRlAyManifest", source);
        var step = service.RevisionDetail!.Pipeline.Steps[0];
        service.RevisionDetail = service.RevisionDetail with { Pipeline = service.RevisionDetail.Pipeline with { Steps = [step with { Enabled = false }] } };
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=overlay-manifest#overlay-manifest");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.AreEqual("Overlay manifest", cut.Find("#overlay-manifest").GetAttribute("aria-label"));
        foreach (var input in cut.FindAll(".node-options input, .node-options select"))
        {
            Assert.IsNotEmpty(cut.Find("label[for='" + input.Id + "']").TextContent);
            foreach (var id in input.GetAttribute("aria-describedby")!.Split(' ')) Assert.IsNotNull(cut.Find("#" + id));
        }
        cut.Find("#node-options-0-layers-0-enabled").Change(false);
        cut.Find("#node-options-0-layers-0-zOrder").Change("-1024");
        cut.Find("#node-options-0-constellationOpacityMillionths").Change("0");
        var fixture = Path.Combine(TestContext.TestResultsDirectory!, "manifest-layer-editor.html");
        File.WriteAllText(fixture, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Manifest layer editor</title><body>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(fixture);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0];
        Assert.IsFalse(saved.Enabled, "A row's enabled flag must not toggle its graph node.");
        Assert.AreEqual(source.Replace(row + ", " + untouched,
            row.Replace("true", "false", StringComparison.Ordinal).Replace("null", "-1024", StringComparison.Ordinal) + "," + untouched,
            StringComparison.Ordinal)[..^2] + ",\"constellationOpacityMillionths\":0 }", saved.Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{ \"layers\" : [] }")]
    [DataRow("{ \"Layers\":[{\"kind\":\"environment\",\"zOrder\":null},{\"kind\":\"cloud-mask\"}],\"future\":1e+02 }")]
    public void ManifestRestoredEditsDoNotCreateRevisionOrMaterializeDefaults(string? source)
    {
        using var context = new BunitContext();
        var service = Configure(context, "OverlayManifest", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-layers";
        var count = cut.FindAll(id + " > ol > li").Count;
        cut.Find(id + "-add").Click();
        cut.Find(id + "-" + count + "-remove").Click();
        if (count > 1)
        {
            cut.Find(id + "-0-zOrder").Change("1024");
            cut.Find(id + "-0-zOrder-clear").Click();
            cut.Find(id + "-0-down").Click();
            cut.Find(id + "-1-up").Click();
        }
        cut.Find("#node-options-0-constellationOpacityMillionths").Change("0");
        cut.Find("#node-options-0-constellationOpacityMillionths").Change("800000");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
    }

    [TestMethod]
    [DataRow("zOrder", "1025")]
    [DataRow("zOrder", "-1025")]
    [DataRow("zOrder", "1.5")]
    [DataRow("kind", "Environment")]
    public void ManifestInvalidRowInvalidatesPreviouslyValidPreview(string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, "OverlayManifest", "{\"layers\":[{\"kind\":\"environment\"}]}");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-constellationOpacityMillionths").Change("0");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-layers-0-" + field).Change(value);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void ManifestCanonicalCollisionAndCountAreBoundedAndDefaultActionRepairsNull()
    {
        using var context = new BunitContext();
        var service = Configure(context, "OverlayManifest", "{\"layers\":null,\"future\":1e+02}");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-layers-clear").Click();
        StringAssert.Contains(cut.Find("#node-options-0-layers").TextContent, "Using producer defaults.", StringComparison.Ordinal);
        cut.Find("#node-options-0-layers-add").Click();
        cut.Find("#node-options-0-layers-0-kind").Change("environment");
        cut.Find("#node-options-0-layers-add").Click();
        cut.Find("#node-options-0-layers-1-kind").Change("corner-annotations");
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        for (var i = 2; i < 10; i++) cut.Find("#node-options-0-layers-add").Click();
        Assert.IsTrue(cut.Find("#node-options-0-layers-add").HasAttribute("disabled"));
        cut.Find("#node-options-0-layers-clear").Click();
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("{\"layers\":[],\"future\":1e+02}", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    public void DeniedManifestSaveDoesNotActivateOrRewriteSource()
    {
        using var context = new BunitContext();
        var service = Configure(context, "OverlayManifest", null);
        service.Create = _ => OperatorUiResult<ProcessingGraphRevisionState>.Failure(OperatorUiResultKind.Unauthorized, "Denied");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-constellationOpacityMillionths").Change("0");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.IsNull(service.RevisionDetail!.Pipeline.Steps[0].Options);
        Assert.IsEmpty(service.Commands);
    }
}
