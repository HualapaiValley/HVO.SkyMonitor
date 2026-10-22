using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>#1383: unconfigured layer steps draw the original overlay colours, constellation width and opacity.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PresentationOverlayDefaultOptionsTests
{
    [TestMethod]
    public void LayerStepsDefaultToTheOriginalConstellationColourWidthAndOpacity()
    {
        var scene = new ScenePresentationLayerProcessingStepOptions();
        Assert.AreEqual((byte)96, scene.ConstellationLineRed);
        Assert.AreEqual((byte)160, scene.ConstellationLineGreen);
        Assert.AreEqual((byte)255, scene.ConstellationLineBlue);
        Assert.AreEqual(2, scene.ConstellationLineThickness);
        // Unset grey values leave the shared producer's restored colours in place.
        Assert.IsNull(scene.MarkerValue);
        Assert.IsNull(scene.LabelValue);
        Assert.IsNull(scene.ImageCircleValue);
        Assert.IsNull(scene.CardinalValue);
        Assert.IsNull(new EnvironmentPresentationLayerProcessingStepOptions().Value);
        Assert.AreEqual(800_000, new OverlayManifestProcessingStepOptions().ConstellationOpacityMillionths);
    }
}
