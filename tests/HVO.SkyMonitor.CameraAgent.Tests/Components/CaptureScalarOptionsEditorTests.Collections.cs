using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void CollectionPanelEditsOccurrencesWithNativeAccessibleButtonsAndPreservesOtherBytes()
    {
        using var context = new BunitContext();
        const string source = """{ "ConstellationIds":[ "\u004fRI", "retained-id", "Ori" ], "SolarSystemBodies":[ 4, "mArS", "Moon" ], "future":[1e+02] }""";
        var service = Configure(context, "ProjectedScene", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        var ids = "#node-options-0-constellationIds";
        var bodies = "#node-options-0-solarSystemBodies";
        Assert.AreEqual("retained-id", cut.Find(ids + "-1").GetAttribute("value"));
        Assert.AreEqual("node-options-0-constellationIds-help", cut.Find(ids).GetAttribute("aria-describedby"));
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-constellationIds-1']").TextContent);
        Assert.IsTrue(cut.Find(ids + "-0-up").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find(ids + "-2-down").HasAttribute("disabled"));
        Assert.IsTrue(cut.FindAll(".collection-actions button").All(static button => button.GetAttribute("type") == "button" && !string.IsNullOrWhiteSpace(button.GetAttribute("aria-label"))));
        cut.Find(ids + "-0-down").Click();
        cut.Find(ids + "-2-remove").Click();
        cut.Find(ids + "-add").Click();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"), "A new blank ID must prevent saving until filled.");
        cut.Find(ids + "-2").Change("CAS");
        cut.Find(bodies + "-0-down").Click();
        cut.Find(bodies + "-2").Change("Sun");
        var fixture = Path.Combine(TestContext.TestResultsDirectory!, "selection-collections.html");
        File.WriteAllText(fixture, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Selection collection editor</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(fixture);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("""{ "ConstellationIds":["retained-id","\u004fRI","CAS"], "SolarSystemBodies":["mArS",4,"Sun"], "future":[1e+02] }""", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands, "A collection save creates a draft only.");
    }

    [TestMethod]
    [DataRow(" ")]
    [DataRow("12345678901234567")]
    public void InvalidCollectionItemClearsAPreviouslyValidPreview(string invalid)
    {
        using var context = new BunitContext();
        var service = Configure(context, "ProjectedScene", """{"constellationIds":["ORI"]}""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-constellationIds-0").Change("CAS");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-constellationIds-0").Change(invalid);
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("{\"constellationIds\":[],\"solarSystemBodies\":[]}")]
    [DataRow("{\"constellationIds\":[\"ORI\",\"retained-id\"],\"solarSystemBodies\":[4,\"mArS\"]}")]
    public void RestoredCollectionsKeepNoOpAndDefaultIdentity(string? source)
    {
        using var context = new BunitContext();
        var service = Configure(context, "ProjectedScene", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        foreach (var key in new[] { "constellationIds", "solarSystemBodies" })
        {
            var id = "#node-options-0-" + key;
            var length = cut.FindAll(id + " > ol > li").Count;
            cut.Find(id + "-add").Click();
            cut.Find(id + "-" + length + "-remove").Click();
            if (length > 1)
            {
                cut.Find(id + "-0-down").Click();
                cut.Find(id + "-1-up").Click();
            }
        }
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options?.GetRawText());
    }

    [TestMethod]
    [DataRow("projected-scene-presentation-v12-plain-circle-markers")]
    [DataRow("projected-scene-presentation-v13-plain-circle-bounded-expected-labels")]
    public void BothSceneLayerVersionsUseTheSameOrderedSelectionControls(string version)
    {
        using var context = new BunitContext();
        var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve("ScenePresentationLayer", version).Descriptor!;
        const string source = """{ "ConstellationIds":[ "retained-id", "Ori", "Ori" ], "future":1e+02 }""";
        var form = new ProcessingNodeOptionsForm(descriptor, System.Text.Json.JsonDocument.Parse(source).RootElement.Clone());
        var cut = context.Render<HVO.SkyMonitor.CameraAgent.Components.Operations.ProcessingNodeOptionsEditor>(parameters =>
            parameters.Add(static component => component.Form, form).Add(static component => component.IdPrefix, "layer"));
        StringAssert.Contains(cut.Find("#layer-constellationIds-help").TextContent, "empty list draws all", StringComparison.Ordinal);
        cut.Find("#layer-constellationIds-0-down").Click();
        cut.Find("#layer-constellationIds-2-remove").Click();
        Assert.IsTrue(form.TryBuild(out var changed, out _));
        Assert.AreEqual("""{ "ConstellationIds":["Ori","retained-id"], "future":1e+02 }""", changed!.Value.GetRawText());
        cut.Find("#layer-constellationIds-1-remove").Click();
        cut.Find("#layer-constellationIds-0-remove").Click();
        Assert.IsTrue(form.TryBuild(out changed, out _));
        Assert.AreEqual("[]", changed!.Value.GetProperty("ConstellationIds").GetRawText());
    }

    [TestMethod]
    public void ExplicitNullSelectionRequiresAnExplicitRepairBeforeSaving()
    {
        using var context = new BunitContext();
        var service = Configure(context, "ProjectedScene", """{"constellationIds":null,"future":1e+02}""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        cut.Find("#node-options-0-constellationIds-add").Click();
        cut.Find("#node-options-0-constellationIds-0-remove").Click();
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        Assert.AreEqual("""{"constellationIds":[],"future":1e+02}""", service.Created.Single().Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    public void DeniedCollectionSaveLeavesSourceAndActivationUnchanged()
    {
        using var context = new BunitContext();
        const string source = """{"constellationIds":["ORI"]}""";
        var service = Configure(context, "ProjectedScene", source);
        service.Create = _ => OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.ProcessingGraphRevisionState>
            .Failure(OperatorUiResultKind.Unauthorized, "Denied");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-constellationIds-0").Change("CAS");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        StringAssert.EndsWith(context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri,
            "/Account/AccessDenied", StringComparison.Ordinal);
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    public void CollectionControlsAndSharedFormEnforceCountsAndClosedEnumChoices()
    {
        using var context = new BunitContext();
        Configure(context, "ProjectedScene", System.Text.Json.JsonSerializer.Serialize(new
        { constellationIds = Enumerable.Repeat("ORI", 128), solarSystemBodies = Enumerable.Repeat(4, 9) }));
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsTrue(cut.Find("#node-options-0-constellationIds-add").HasAttribute("disabled"));
        Assert.IsTrue(cut.Find("#node-options-0-solarSystemBodies-add").HasAttribute("disabled"));
        cut.Find("#node-options-0-solarSystemBodies-0").Change("Pluto");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        var form = new ProcessingNodeOptionsForm(ProjectedSceneOptionDescriptors.Descriptor, null);
        for (var index = 0; index < 10; index++) form.AddCollectionItem("solarSystemBodies");
        Assert.IsFalse(form.TryBuild(out _, out _), "The shared validation also rejects count overflow outside the UI.");
    }
}
