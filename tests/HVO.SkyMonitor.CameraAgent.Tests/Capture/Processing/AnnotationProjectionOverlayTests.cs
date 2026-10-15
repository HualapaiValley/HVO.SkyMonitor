using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class AnnotationProjectionOverlayTests
{
    private static readonly ProjectionContext Projection = new(ProjectionModel.EquidistantFisheye, 500, 400, 200, 200,
        1000, 800, ProjectionAperture.Circular, 200, 90);

    [TestMethod]
    [DataRow(ProjectedSceneQuarterRotation.Degrees90, false)]
    [DataRow(ProjectedSceneQuarterRotation.Degrees270, true)]
    public async Task ProjectedSceneOverlayLandmarksUseTheEmittedImagePixels(
        ProjectedSceneQuarterRotation rotation, bool horizontalMirror)
    {
        var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            1000, 800, 0, 0, 1000, 800, 2, 4, horizontalMirror, false, rotation, 200, 500);
        var scene = await SceneAsync(transform, resolved: true).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, scene.SchemaVersion);

        var (objects, _, overlay) = CreateStep().CreateProjectedSceneInputs(scene);

        // Regression for #518: the landmarks used to stay in source pixels, centre (500, 400) with one radius of 200,
        // while the objects they frame had already taken the 2x4 bin and the quarter rotation.
        Assert.IsNotNull(overlay);
        Assert.AreEqual(100, overlay.Center.X, 1e-9);
        Assert.AreEqual(250, overlay.Center.Y, 1e-9);
        Assert.AreEqual(50, overlay.ImageCircleRadius, 1e-9);
        Assert.AreEqual(100, overlay.ImageCircleRadiusY!.Value, 1e-9);
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(Projection)!;
        foreach (var (expected, actual) in new[]
        {
            (landmarks.North, overlay.North), (landmarks.East, overlay.East),
            (landmarks.South, overlay.South), (landmarks.West, overlay.West)
        })
        {
            var mapped = ProjectedSceneImageTransform.Apply(transform, expected);
            Assert.AreEqual(mapped.X, actual.X, 1e-9);
            Assert.AreEqual(mapped.Y, actual.Y, 1e-9);
        }

        var preview = AnnotationRenderer.AnnotateMono8WithSegments(new byte[200 * 500], 200, 500, [], [],
            new PreviewTransform(1, 1), new AnnotationOptions { DrawImageCircle = true }, overlay, null);
        Assert.IsTrue(Lit(preview.Pixels.Span, 150, 250), "The circle must meet the emitted horizontal radius.");
        Assert.IsTrue(Lit(preview.Pixels.Span, 100, 350), "The circle must meet the emitted vertical radius.");
        Assert.IsFalse(Lit(preview.Pixels.Span, 100, 450), "A single source radius would overshoot the binned axis.");
        Assert.AreSame(scene.ResolvedFootprints!.Single().Parts,
            objects.Single(static item => item.Id == "solar-system:Sun").FootprintParts);
        // The edge maps the scene through the same shared adapter the central host uses for the same artifact.
        CollectionAssert.AreEqual(ProjectedSceneAnnotation.CreateObjects(scene,
            new AnnotationProcessingStepOptions().MaximumLabelMagnitude).ToArray(), objects.ToArray());
    }

    [TestMethod]
    public async Task ProjectedSceneV1OverlayKeepsTheReleasedSourcePixelLandmarks()
    {
        var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            1000, 800, 0, 0, 1000, 800, 2, 4, false, false, ProjectedSceneQuarterRotation.Degrees90, 200, 500);
        var scene = await SceneAsync(transform, resolved: false).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, scene.SchemaVersion);

        var (objects, _, overlay) = CreateStep().CreateProjectedSceneInputs(scene);

        // A v1 scene's annotation identity is unchanged from projected-annotation-v3, so its landmarks, and with
        // them its bytes, stay exactly as released even though they ignore the image transform.
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(Projection)!;
        Assert.AreEqual(new ProjectedAnnotationOverlay(new PixelPoint(500, 400), 200,
            landmarks.North, landmarks.East, landmarks.South, landmarks.West), overlay);
        Assert.IsTrue(objects.All(static item => item.FootprintParts is null));
    }

    private static bool Lit(ReadOnlySpan<byte> pixels, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (pixels[(y + dy) * 200 + x + dx] != 0) return true;
        return false;
    }

    private static AnnotationCaptureProcessingStep CreateStep() => new(
        new CaptureProcessingStepMetadata("annotation", "Annotation", 70),
        new AnnotationProcessingStepOptions { DrawImageCircle = true, DrawCardinalDirections = true },
        new ProjectedSceneStore(), new UnusedAnnotationSceneProvider(),
        new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()));

    private static async Task<ProjectedSceneV1> SceneAsync(ProjectedSceneImageTransformV1 transform, bool resolved)
    {
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(
            utc, new ObserverLocation(0, 0, 0), Projection, new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "fisheye-v1", solarSystemBodies: [SolarSystemBody.Sun])).ConfigureAwait(false);
        if (resolved)
        {
            visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, utc,
                new AltAzPoint(60, 0), .25, 0, 1, 0, 149600000)]);
        }
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, transform,
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", "fisheye-v1");
    }

    private sealed class UnusedAnnotationSceneProvider : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(
            CameraModuleConfig config,
            ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame,
            IReadOnlyList<string> constellationIds,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("A canonical projected scene must not regenerate annotation geometry.");
    }
}
