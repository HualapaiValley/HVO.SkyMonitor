using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>#1383: unconfigured layer steps draw the original overlay colours, constellation width and opacity.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class PresentationOverlayDefaultOptionsTests
{
    [TestMethod]
    public async Task LayerStepsRestoreOverlayDefaultsAndExpectedStarNames()
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
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var position = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(90, 0), utc, 0, 0), utc);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(
            [new CelestialCatalogObject("star:vega", "Vega", position.RightAscensionHours, position.DeclinationDegrees, 1)]))
            .BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
                new ProjectionContext(ProjectionModel.Perspective, 300, 200, 400, 400, 800, 600,
                    ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
                new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                    new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1")).ConfigureAwait(false);
        var projected = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(800, 600),
            new ProjectedSceneSource(Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Guid.Parse("00000000-0000-0000-0000-000000000002"), new string('D', 64)), "calibration-v1", "perspective-v1");
        var style = new PresentationAnnotationStyleV1(LabelScale: scene.LabelScale);
        var expected = PresentationLayerProducers.FromProjectedSceneGroupsV2(projected, style,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false,
            expectedPositionDiagnostics: scene.ExpectedPositionDiagnostics).StarAnnotations;
        Assert.HasCount(1, expected.Markers);
        Assert.AreEqual("Vega (expected)", expected.TextBlocks.Single().Lines.Single());
        var explicitOff = JsonSerializer.Deserialize<ScenePresentationLayerProcessingStepOptions>(
            """{"expectedPositionDiagnostics":false}""", JsonSerializerOptions.Web)!;
        Assert.IsEmpty(PresentationLayerProducers.FromProjectedSceneGroupsV2(projected, style,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false,
            expectedPositionDiagnostics: explicitOff.ExpectedPositionDiagnostics).StarAnnotations.TextBlocks);
        Assert.IsEmpty(PresentationLayerProducers.FromProjectedSceneGroupsV2(projected, style).StarAnnotations.TextBlocks,
            "The shared measured-source producer remains strict unless its caller explicitly requests expected labels.");
        Assert.IsFalse(new AnnotationProcessingStepOptions().ExpectedPositionDiagnostics);
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
        var legacyOptions = new OverlayManifestProcessingStepOptions();
        Assert.IsFalse(JsonSerializer.SerializeToElement(legacyOptions).TryGetProperty("UseRichFrameFacts", out _),
            "An absent opt-in must not change legacy effective options or graph identity.");
        var legacy = new OverlayManifestCaptureProcessingStep(new("manifest", "OverlayManifest", 80), legacyOptions);
        var optedIn = new OverlayManifestCaptureProcessingStep(new("manifest", "OverlayManifest", 80), new() { UseRichFrameFacts = true });
        Assert.IsFalse(legacy.DependencyRequirements.Any(requirement => requirement.SchemaVersions?.Contains(
            HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.RichTextSchemaVersion) == true));
        Assert.IsTrue(optedIn.DependencyRequirements.Any(requirement => requirement.SchemaVersions?.Contains(
            HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.RichTextSchemaVersion) == true));
        var legacyMaterializerOptions = new PresentationMaterializerProcessingStepOptions();
        Assert.IsFalse(JsonSerializer.SerializeToElement(legacyMaterializerOptions).TryGetProperty("UseRichFrameFacts", out _));
        var legacyMaterializer = new PresentationMaterializerCaptureProcessingStep(
            new("materializer", "PresentationMaterializer", 90), legacyMaterializerOptions);
        var richMaterializer = new PresentationMaterializerCaptureProcessingStep(
            new("materializer", "PresentationMaterializer", 90), new() { UseRichFrameFacts = true });
        Assert.IsFalse(legacyMaterializer.DependencyRequirements.Any(requirement => requirement.SchemaVersions?.Contains(
            HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.RichTextSchemaVersion) == true));
        Assert.IsTrue(richMaterializer.DependencyRequirements.Any(requirement => requirement.SchemaVersions?.Contains(
            HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.RichTextSchemaVersion) == true));
    }
}
