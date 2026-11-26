using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Components.Pages;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void SatelliteLabelVisibilitySavesToADraftRevision()
    {
        const string selector = "#node-options-0-hideObjectLabels";
        using var context = new BunitContext();
        var service = Configure(context, "CaptureSatelliteTracks", """{ "catalogNumbers": ["25544"] }""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsFalse(cut.Find(selector).HasAttribute("checked"));
        cut.Find(selector).Change(true);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single();
        var options = saved.Pipeline.Steps[0].Options!.Value;
        Assert.IsTrue(options.GetProperty("hideObjectLabels").GetBoolean());
        Assert.AreEqual("25544", options.GetProperty("catalogNumbers")[0].GetString());
        Assert.IsEmpty(service.Commands);
        using var reloaded = new BunitContext();
        Configure(reloaded, "CaptureSatelliteTracks", options.GetRawText());
        Assert.IsTrue(reloaded.Render<ProcessingGraphEditorPage>().Find(selector).HasAttribute("checked"));
    }

    [TestMethod]
    [DataRow("MeasuredStellarAssociations", "outputVariant", "measured-custom", " ")]
    [DataRow("MeasuredAstrometry", "captureStride", "120", "59")]
    [DataRow("CaptureSatelliteTracks", "outputVariant", "tracks-custom", " ")]
    [DataRow("CaptureSatelliteLayer", "inputVariant", "tracks-custom", " ")]
    [DataRow("Storage", "retentionDays", "60", "0")]
    public void RemainingNodePanelRendersRejectsInvalidEditsSavesAndReloads(
        string alias, string key, string edited, string invalid)
    {
        const string source = """{ "future": [1e+02] }""";
        using var context = new BunitContext();
        var service = Configure(context, alias, source);
        var cut = context.Render<ProcessingGraphEditorPage>();
        var selector = "#node-options-0-" + key;
        Assert.IsNotEmpty(cut.Find("label[for='node-options-0-" + key + "']").TextContent);
        Assert.AreEqual("node-options-0-" + key + "-help", cut.Find(selector).GetAttribute("aria-describedby"));
        cut.Find(selector).Change(edited);
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find(selector).Change(invalid);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
        cut.Find(selector).Change(edited);
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single();
        Assert.AreEqual("2", saved.Revision);
        Assert.AreEqual("[1e+02]", saved.Pipeline.Steps[0].Options!.Value.GetProperty("future").GetRawText());
        Assert.AreEqual(source, service.RevisionDetail!.Pipeline.Steps[0].Options!.Value.GetRawText());
        Assert.IsEmpty(service.Commands, "Saving must leave activation explicit.");
        using var reloaded = new BunitContext();
        var persisted = JsonSerializer.Deserialize<CapturePipelineConfig>(JsonSerializer.Serialize(saved.Pipeline))!;
        Configure(reloaded, alias, persisted.Steps[0].Options!.Value.GetRawText());
        var restored = reloaded.Render<ProcessingGraphEditorPage>();
        Assert.AreEqual(edited, restored.Find(selector).GetAttribute("value"));
        restored.Find("form").Submit();
        Assert.IsTrue(restored.Find("button.btn-primary").HasAttribute("disabled"));
    }
}
