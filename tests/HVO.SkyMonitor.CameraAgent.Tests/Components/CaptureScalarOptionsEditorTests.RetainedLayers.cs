using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    [DataRow("cLoUdPresentationLayer", "cloud-layer", "Cloud layer", "lineThickness", "4")]
    [DataRow("eNvIrOnMeNtPresentationLayer", "environment-layer", "Environment layer", "inset", "16")]
    public void RetainedLayerPanelPreservesSourceAndDisabledGraphWithAccessibleControls(string alias, string section, string title, string field, string value)
    {
        using var context = new BunitContext();
        const string source = """{ "WidthPixels":1936, "HeightPixels":1216, "Value":null, "EnvironmentalKinds":[], "StackPreviewVariant":"retained-stack", "OutputVariant":"retained-output", "FactsOutputVariant":"retained-facts", "MaskOutputVariant":"retained-mask", "LabelOutputVariant":"retained-label", "future":[1e+02], "Future":null }""";
        var service = Configure(context, alias, source);
        var step = service.RevisionDetail!.Pipeline.Steps[0];
        service.RevisionDetail = service.RevisionDetail with { Pipeline = service.RevisionDetail.Pipeline with { Steps = [step with { Enabled = false, DependsOn = ["$raw"] }] } };
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=" + section + "#" + section);
        var cut = context.Render<ProcessingGraphEditorPage>();
        var panel = cut.Find("#" + section + "[aria-label='" + title + "']");
        Assert.HasCount(section == "cloud-layer" ? 2 : 3, panel.QuerySelectorAll("input"));
        foreach (var input in panel.QuerySelectorAll("input"))
        {
            Assert.IsNotEmpty(cut.Find("label[for='" + input.Id + "']").TextContent);
            Assert.AreEqual(input.Id + "-help", input.GetAttribute("aria-describedby"));
        }
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        var inputField = cut.Find("#node-options-0-" + field);
        var original = inputField.GetAttribute("value")!;
        inputField.Change(value);
        inputField.Change(original);
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        inputField.Change(value);
        if (section == "cloud-layer") cut.Find("#node-options-0-drawLabels").Change(false);
        else
        {
            cut.Find("#node-options-0-scale").Change("2");
            cut.Find("#node-options-0-lineSpacing").Change("8");
        }
        var path = Path.Combine(TestContext.TestResultsDirectory!, section + "-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Retained layer options</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0];
        Assert.IsFalse(saved.Enabled);
        string[] expectedDependencies = ["$raw"];
        CollectionAssert.AreEqual(expectedDependencies, saved.DependsOn!.ToArray());
        Assert.AreEqual(source[..^2] + (section == "cloud-layer" ? ",\"drawLabels\":false,\"lineThickness\":4 }" : ",\"scale\":2,\"inset\":16,\"lineSpacing\":8 }"), saved.Options!.Value.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
    }

    [TestMethod]
    [DataRow("CloudPresentationLayer", "lineThickness", "0")]
    [DataRow("CloudPresentationLayer", "lineThickness", "9")]
    [DataRow("EnvironmentPresentationLayer", "scale", "0")]
    [DataRow("EnvironmentPresentationLayer", "scale", "5")]
    [DataRow("EnvironmentPresentationLayer", "inset", "65")]
    [DataRow("EnvironmentPresentationLayer", "lineSpacing", "17")]
    [DataRow("EnvironmentPresentationLayer", "lineSpacing", "1.5")]
    public void RetainedLayerInvalidEditInvalidatesPreview(string alias, string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, alias, null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-" + field).Change("3");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    [DataRow("cloud-layer", "CloudPresentationLayerV6")]
    [DataRow("environment-layer", "EnvironmentPresentationLayerV7")]
    public void RetainedLayerMissingAndUnsupportedNodesStayUnchanged(string section, string unsupported)
    {
        foreach (var alias in new[] { "Telemetry", unsupported })
        {
            using var context = new BunitContext();
            var service = Configure(context, alias, "{\"future\":null}");
            context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=" + section + "#" + section);
            var cut = context.Render<ProcessingGraphEditorPage>();
            StringAssert.Contains(cut.Find("p[role='status']").TextContent, "no supported", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#" + section + ", .conversion-review"));
            Assert.IsEmpty(service.Created);
        }
    }
}
