using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    private static readonly string[] EditedAnnotationCornerTokens = ["environment", "capture-sequence", "utc", "agent-identity"];

    [TestMethod]
    public void AnnotationSelectionsRenderEditRejectExpandedCornersSaveAndReload()
    {
        using var context = new BunitContext();
        const string source = """{ "future":[1e+02], "recipeVersion":"retained" }""";
        var service = Configure(context, "Annotation", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string corner = "#node-options-0-topLeftTokens";
        const string environment = "#node-options-0-environmentalKinds";
        Assert.HasCount(3, cut.FindAll(corner + " select"));
        Assert.HasCount(6, cut.FindAll(environment + " select"));
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-topLeftTokens-0']").TextContent);
        cut.Find(corner + "-0").Change("environment");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find(corner + "-add").Click();
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        cut.Find(environment + "-5-remove").Click();
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        CollectionAssert.AreEqual(EditedAnnotationCornerTokens,
            saved.GetProperty("topLeftTokens").EnumerateArray().Select(static value => value.GetString()).ToArray());
        Assert.AreEqual(5, saved.GetProperty("environmentalKinds").GetArrayLength());
        Assert.AreEqual("[1e+02]", saved.GetProperty("future").GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
        using var reload = new BunitContext();
        Configure(reload, "Annotation", saved.GetRawText());
        var restored = reload.Render<ProcessingGraphEditorPage>();
        Assert.HasCount(4, restored.FindAll(corner + " select"));
        Assert.HasCount(5, restored.FindAll(environment + " select"));
        restored.Find("form").Submit();
        Assert.IsTrue(restored.Find("button.btn-primary").HasAttribute("disabled"));
    }
}
