using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void MixedGainPanelRendersEditsRejectsSavesAndReloadsWithoutChangingBindings()
    {
        using var context = new BunitContext();
        const string source = "{ \"Recipe\":\"mixed-gain-normalized-mean\", \"WindowSize\":7, \"normalizationPlan\":{\"future\":1e+02} }";
        var service = Configure(context, "RollingCombination", source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.AreEqual("Mixed-gain rolling combination", cut.Find(".node-options-section h3").TextContent);
        Assert.AreEqual("7", cut.Find("#node-options-0-windowSize").GetAttribute("value"));
        foreach (var input in cut.FindAll(".node-options input, .node-options select"))
        {
            Assert.IsNotEmpty(cut.Find("label[for='" + input.Id + "']").TextContent);
            foreach (var id in input.GetAttribute("aria-describedby")!.Split(' ')) Assert.IsNotNull(cut.Find("#" + id));
        }
        cut.Find("#node-options-0-windowSize").Change("8");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-maximumAgeMilliseconds").Change("0");
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsEmpty(service.Created);
        cut.Find("#node-options-0-maximumAgeMilliseconds-clear").Click();
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual(source.Replace(":7", ":8", StringComparison.Ordinal), saved.GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands);
        var reloaded = new ProcessingNodeOptionsForm(ProcessingNodeOptionsForm.DescriptorFor("RollingCombination", saved)!, saved);
        Assert.AreEqual("8", reloaded.Get("windowSize"));
        Assert.IsTrue(reloaded.TryBuild(out var restored, out _));
        Assert.AreEqual(saved.GetRawText(), restored!.Value.GetRawText());
    }
}
