using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;

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

    [TestMethod]
    public void RichFrameFactsAliasRequiresExplicitSelectionAndValidatesThroughSharedContracts()
    {
        var services = new ServiceCollection();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().Build());
        var registrations = services.Where(item => item.ServiceType == typeof(CaptureProcessingStepRegistration))
            .Select(item => (CaptureProcessingStepRegistration)item.ImplementationInstance!).ToArray();
        var rich = registrations.Single(item => item.Alias == FrameFactsPresentation.StepAlias);
        Assert.IsFalse(rich.AutoInclude);
        Assert.AreEqual(typeof(FrameFactsPresentationCaptureProcessingStep), rich.ImplementationType);
        Assert.IsTrue(registrations.Any(item => item.Alias == "EnvironmentPresentationLayer"));
        var invalid = new FrameFactsPresentationProcessingStepOptions
        {
            WidthPixels = 1936,
            HeightPixels = 1216,
            TopLeftFont = "PlexRegular",
            TopLeftTemplate = "**bold**"
        };
        var results = new List<ValidationResult>();
        Assert.IsFalse(Validator.TryValidateObject(invalid, new ValidationContext(invalid), results, validateAllProperties: true));
        Assert.IsNotEmpty(results);
        Assert.AreEqual("environment-layer-v1", new EnvironmentPresentationLayerProcessingStepOptions().OutputVariant);
        Assert.AreEqual("environment-layer-v1", new OverlayManifestProcessingStepOptions().EnvironmentVariant);
        Assert.AreEqual("presentation-metadata-facts-v2", new OverlayManifestProcessingStepOptions().EnvironmentFactsVariant);
    }
}
