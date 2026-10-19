using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    public async Task MeasuredEvidenceRefreshPreservesOptOutWhenLayerIdentityChanges()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        var firstIdentity = new string('D', 64);
        var secondIdentity = new string('E', 64);
        var reads = 0;
        service.PresentationHandler = (id, _) =>
        {
            var refreshed = ++reads > 1;
            var presentation = Layered(id, refreshed ? secondIdentity : firstIdentity);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(presentation with
            {
                Layers = [presentation.Layers[0] with { Kind = "MeasuredTransientTrails", EnabledByDefault = refreshed }],
                ManifestIdentitySha256 = refreshed ? new string('F', 64) : presentation.ManifestIdentitySha256
            }));
        };
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        var toggle = cut.WaitForElement(".sky-layer-controls input[data-layer-identity]");
        Assert.IsFalse(toggle.HasAttribute("checked"));

        await cut.Find(".refresh-measured-evidence").ClickAsync().ConfigureAwait(false);

        cut.WaitForAssertion(() => Assert.IsTrue(reads > 1));
        Assert.AreEqual(secondIdentity, cut.Find(".sky-layer-controls input[data-layer-identity]").GetAttribute("data-layer-identity"));
        Assert.IsFalse(cut.Find(".sky-layer-controls input[data-layer-identity]").HasAttribute("checked"));
        Assert.AreEqual("0 selected", cut.Find(".scene-panel header > span").TextContent);
    }

    [TestMethod]
    [DataRow("NoRecordedCandidate", "No recorded candidate for this capture")]
    [DataRow("Incomplete", "Measured geometry is incomplete.")]
    [DataRow("Unavailable", "Source mapping is unavailable.")]
    public void MeasuredTrailControlExplainsMissingEvidenceWithoutInventingADetection(string state, string explanation)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        service.PresentationHandler = (id, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(
            Layered(id, new string('D', 64)) with
            {
                TrailEvidence = new(id, state, [], state != "NoRecordedCandidate", state == "NoRecordedCandidate" ? null : explanation)
            }));
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");

        var cut = context.Render<CurrentSkyPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, explanation, StringComparison.Ordinal));
        var measured = cut.FindAll(".overlay-group[aria-label='Measured layers'] label")
            .Single(label => label.TextContent.Contains("Measured transient trails", StringComparison.Ordinal));
        Assert.IsTrue(measured.QuerySelector("input")!.HasAttribute("disabled"));
        Assert.IsEmpty(cut.FindAll("a[href^='/transients/']"));
    }

    [TestMethod]
    public async Task MeasuredTrailsStartOffAndSaveTheShownCompositionIdentity()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        var identity = new string('D', 64);
        var presentation = Layered(OperatorUiTestData.CurrentImage().DisplayCapture!.CaptureId, identity);
        presentation = presentation with
        {
            ManifestArtifactId = Guid.NewGuid(),
            Layers = [presentation.Layers[0] with { Kind = "MeasuredTransientTrails", EnabledByDefault = false }]
        };
        service.PresentationHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<CameraAgentLayeredPresentation>.Success(presentation));
        var saved = false;
        service.FrozenMaterializationHandler = (id, manifestIdentity, selected, _) =>
        {
            Assert.AreEqual(presentation.CaptureId, id);
            Assert.AreEqual(presentation.ManifestIdentitySha256, manifestIdentity);
            CollectionAssert.AreEqual(new[] { identity }, selected.ToArray());
            saved = true;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Failure(
                OperatorUiResultKind.Conflict, "Evidence changed; reload before saving."));
        };
        var module = context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js");
        module.Setup<string>("bindLayerToggles", _ => true).SetResult("valid");
        var cut = context.Render<CurrentSkyPage>();
        var toggle = cut.WaitForElement(".sky-layer-controls input[data-layer-target]");
        Assert.IsFalse(toggle.HasAttribute("checked"));
        await toggle.ChangeAsync(true).ConfigureAwait(false);
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsTrue(saved));
        StringAssert.Contains(cut.Find(".sky-layer-result--error").TextContent, "Evidence changed", StringComparison.Ordinal);
    }
}
