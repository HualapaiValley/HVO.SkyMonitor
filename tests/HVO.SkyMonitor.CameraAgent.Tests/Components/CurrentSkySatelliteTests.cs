using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CurrentSkyPageTests
{
    [TestMethod]
    public void MissingSatellitePredictionIsUnavailableWithoutInventingARecordedLayer()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<CurrentSkyPage>();
        var satellite = cut.FindAll(".overlay-group[aria-label='Diagnostics layers'] label")
            .Single(label => label.QuerySelector("strong")?.TextContent == "Predicted satellite tracks");

        Assert.IsTrue(satellite.QuerySelector("input")!.HasAttribute("disabled"));
        StringAssert.Contains(satellite.TextContent, "No compatible prediction retained", StringComparison.Ordinal);
        Assert.IsFalse(satellite.QuerySelector("input")!.HasAttribute("data-layer-identity"));
    }

    [TestMethod]
    public async Task RetainedSatellitePredictionAndMeasuredTrailHaveIndependentControlsAndFrozenSaveSelection()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CurrentImageHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentCurrentImagePresentation>.Success(WithStructuredBase()));
        var predictedIdentity = new string('D', 64);
        var measuredIdentity = new string('E', 64);
        var presentation = Layered(OperatorUiTestData.CurrentImage().DisplayCapture!.CaptureId, predictedIdentity);
        presentation = presentation with
        {
            ManifestArtifactId = Guid.NewGuid(),
            Svg = System.Text.Encoding.UTF8.GetBytes(
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 640 480\"><g id=\"hvo-layer-0\"></g><g id=\"hvo-layer-1\"></g></svg>"),
            Layers =
            [
                presentation.Layers[0] with
                {
                    Kind = OrbitalTrackPresentation.LayerKind,
                    EnabledByDefault = false
                },
                presentation.Layers[0] with
                {
                    IdentitySha256 = measuredIdentity,
                    Kind = "MeasuredTransientTrails",
                    DomGroupId = "hvo-layer-1",
                    EnabledByDefault = false
                }
            ]
        };
        service.PresentationHandler = (_, _) => ValueTask.FromResult(
            OperatorUiResult<CameraAgentLayeredPresentation>.Success(presentation));
        var saved = false;
        service.FrozenMaterializationHandler = (id, manifestIdentity, selected, _) =>
        {
            Assert.AreEqual(presentation.CaptureId, id);
            Assert.AreEqual(presentation.ManifestIdentitySha256, manifestIdentity);
            CollectionAssert.AreEqual(new[] { predictedIdentity }, selected.ToArray());
            saved = true;
            return ValueTask.FromResult(OperatorUiResult<CameraAgentPresentationMaterializationReceipt>.Failure(
                OperatorUiResultKind.Conflict, "The retained composition changed; reload before saving."));
        };
        context.JSInterop.SetupModule("./Components/Pages/CurrentSkyPage.razor.js")
            .Setup<string>("bindLayerToggles", _ => true).SetResult("valid");

        var cut = context.Render<CurrentSkyPage>();
        var predicted = cut.WaitForElement($".sky-layer-controls input[data-layer-identity='{predictedIdentity}']");
        var measured = cut.Find($".sky-layer-controls input[data-layer-identity='{measuredIdentity}']");

        Assert.IsFalse(predicted.HasAttribute("checked"));
        Assert.IsFalse(measured.HasAttribute("checked"));
        var predictedGroup = cut.FindAll(".overlay-group[aria-label='Diagnostics layers'] label")
            .Where(label => label.QuerySelector("strong")?.TextContent == "Predicted satellite tracks").ToArray();
        Assert.HasCount(1, predictedGroup);
        StringAssert.Contains(predictedGroup[0].TextContent, "exact exposure segment", StringComparison.Ordinal);
        StringAssert.Contains(predictedGroup[0].TextContent, "not a detection or visibility claim", StringComparison.Ordinal);
        Assert.HasCount(1, cut.FindAll(".overlay-group[aria-label='Measured layers'] input[data-layer-identity]"));

        await predicted.ChangeAsync(true).ConfigureAwait(false);

        Assert.IsTrue(cut.Find($".sky-layer-controls input[data-layer-identity='{predictedIdentity}']").HasAttribute("checked"));
        Assert.IsFalse(cut.Find($".sky-layer-controls input[data-layer-identity='{measuredIdentity}']").HasAttribute("checked"));
        await cut.Find(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        cut.WaitForAssertion(() => Assert.IsTrue(saved));
    }
}
