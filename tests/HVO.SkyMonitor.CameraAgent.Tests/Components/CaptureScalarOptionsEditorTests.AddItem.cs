using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    [TestMethod]
    public void AnnotationOptionsAddItemOnDefaultListsShowsAnUnusedChoiceAndNoError()
    {
        using var context = new BunitContext();
        var service = Configure(context, "Annotation", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string corner = "#node-options-0-topLeftTokens";
        const string environment = "#node-options-0-environmentalKinds";
        // Both default lists already select their first allowed value.
        Assert.AreEqual("agent-identity", cut.Find(corner + "-0").GetAttribute("value"));
        Assert.AreEqual("AirTemperature", cut.Find(environment + "-0").GetAttribute("value"));

        cut.Find(corner + "-add").Click();
        cut.Find(environment + "-add").Click();

        Assert.AreEqual("schedule-profile", cut.Find(corner + "-3").GetAttribute("value"));
        Assert.AreEqual("WindDirection", cut.Find(environment + "-6").GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        Assert.IsEmpty(cut.FindAll(corner + "-add-unavailable, " + environment + "-add-unavailable"));
        Assert.IsFalse(cut.Find(corner + "-add").HasAttribute("disabled"));
        Assert.IsFalse(cut.Find(corner + "-add").HasAttribute("aria-describedby"));

        // The added items are an ordinary valid edit: it previews and saves as a draft.
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("button.btn-primary").Click();
        var saved = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual("schedule-profile", saved.GetProperty("topLeftTokens")[3].GetString());
        Assert.AreEqual("WindDirection", saved.GetProperty("environmentalKinds")[6].GetString());
    }

    [TestMethod]
    public void AddItemIsDisabledWithAStatedReasonOnceEveryAllowedValueIsSelected()
    {
        using var context = new BunitContext();
        var every = Enum.GetNames<EnvironmentalObservationKind>();
        Configure(context, "EnvironmentPresentationLayer", System.Text.Json.JsonSerializer.Serialize(new
        { environmentalKinds = every.SkipLast(1) }));
        var cut = context.Render<ProcessingGraphEditorPage>();
        const string id = "#node-options-0-environmentalKinds";
        Assert.IsFalse(cut.Find(id + "-add").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(id + "-add-unavailable"));

        cut.Find(id + "-add").Click();

        // The last unused kind was added; nothing is left, so the control says why it is off.
        Assert.AreEqual(every[^1], cut.Find(id + "-" + (every.Length - 1)).GetAttribute("value"));
        var add = cut.Find(id + "-add");
        Assert.IsTrue(add.HasAttribute("disabled"));
        Assert.AreEqual("node-options-0-environmentalKinds-add-unavailable", add.GetAttribute("aria-describedby"));
        var reason = cut.Find(id + "-add-unavailable");
        Assert.AreEqual("status", reason.GetAttribute("role"));
        Assert.AreEqual(ProcessingNodeOptionsForm.EveryChoiceSelectedReason, reason.TextContent);
        Assert.IsEmpty(cut.FindAll(".option-errors[role='alert'] li"));
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));

        // Removing a kind restores the control, and adding again returns exactly the removed kind.
        cut.Find(id + "-2-remove").Click();
        Assert.IsFalse(cut.Find(id + "-add").HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll(id + "-add-unavailable"));
        cut.Find(id + "-add").Click();
        Assert.AreEqual(every[2], cut.Find(id + "-" + (every.Length - 1)).GetAttribute("value"));
        Assert.IsEmpty(cut.FindAll(".option-errors[role='alert'] li"));
    }
}
