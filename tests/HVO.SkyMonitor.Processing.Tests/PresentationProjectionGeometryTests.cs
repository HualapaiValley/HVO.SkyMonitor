using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationProjectionGeometryTests
{
    [TestMethod]
    public async Task UnequalBinsAndQuarterRotationsProduceExactEllipseAndCardinalPositions()
    {
        var clockwise = await SceneAsync(Transform(ProjectedSceneQuarterRotation.Degrees90)).ConfigureAwait(false);
        var counterClockwise = await SceneAsync(Transform(ProjectedSceneQuarterRotation.Degrees270)).ConfigureAwait(false);

        var clockwisePayload = PresentationLayerProducers.FromProjectedScene(clockwise);
        var counterClockwisePayload = PresentationLayerProducers.FromProjectedScene(counterClockwise);

        AssertEllipse(clockwisePayload, new PixelPoint(100, 250), 50, 100);
        AssertEllipse(counterClockwisePayload, new PixelPoint(100, 250), 50, 100);
        AssertCardinals(clockwise, clockwisePayload);
        AssertCardinals(counterClockwise, counterClockwisePayload);
        Assert.AreNotEqual(clockwisePayload.ContentIdentitySha256, counterClockwisePayload.ContentIdentitySha256);
    }

    [TestMethod]
    public async Task MirrorsTransformCardinalsAndChangeIdentityWithoutChangingEllipseRadii()
    {
        var plain = await SceneAsync(Transform(ProjectedSceneQuarterRotation.Degrees90)).ConfigureAwait(false);
        var mirrored = await SceneAsync(Transform(ProjectedSceneQuarterRotation.Degrees90, horizontalMirror: true,
            verticalMirror: true)).ConfigureAwait(false);

        var plainPayload = PresentationLayerProducers.FromProjectedScene(plain);
        var mirroredPayload = PresentationLayerProducers.FromProjectedScene(mirrored);

        AssertEllipse(mirroredPayload, new PixelPoint(100, 250), 50, 100);
        AssertCardinals(mirrored, mirroredPayload);
        CollectionAssert.AreNotEqual(plainPayload.TextBlocks.ToArray(), mirroredPayload.TextBlocks.ToArray());
        Assert.AreNotEqual(plainPayload.ContentIdentitySha256, mirroredPayload.ContentIdentitySha256);
    }

    [TestMethod]
    public async Task CropExcludingCircleBasisOrIndividualCardinalsOmitsGeometryWithoutThrowing()
    {
        var basisExcluded = await SceneAsync(new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            1000, 800, 400, 200, 200, 400, 2, 4, false, false,
            ProjectedSceneQuarterRotation.Degrees0, 100, 100)).ConfigureAwait(false);
        var partialCardinals = await SceneAsync(new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            1000, 800, 400, 0, 600, 800, 2, 4, false, false,
            ProjectedSceneQuarterRotation.Degrees0, 300, 200)).ConfigureAwait(false);
        var centerExcluded = await SceneAsync(new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            1000, 800, 0, 0, 400, 800, 2, 4, false, false,
            ProjectedSceneQuarterRotation.Degrees0, 200, 200)).ConfigureAwait(false);

        var basisPayload = PresentationLayerProducers.FromProjectedScene(basisExcluded);
        var partialPayload = PresentationLayerProducers.FromProjectedScene(partialCardinals);
        var centerPayload = PresentationLayerProducers.FromProjectedScene(centerExcluded);

        Assert.IsEmpty(basisPayload.Ellipses);
        Assert.IsEmpty(centerPayload.Ellipses);
        Assert.IsEmpty(centerPayload.TextBlocks);
        AssertCardinals(partialCardinals, partialPayload);
        Assert.IsLessThan(4, partialPayload.TextBlocks.Count);
        Assert.AreNotEqual(basisPayload.ContentIdentitySha256, partialPayload.ContentIdentitySha256);
    }

    [TestMethod]
    public async Task SceneGroupsKeepStarsCardinalsCircleAndConstellationsIndependent()
    {
        var scene = await SceneAsync(Transform(ProjectedSceneQuarterRotation.Degrees90)).ConfigureAwait(false);

        var circleOnly = PresentationLayerProducers.FromProjectedSceneGroupsV2(
            scene, includeMarkers: false, includeLabels: false, includeConstellations: false,
            includeImageCircle: true, includeCardinalDirections: false);
        var cardinalsOnly = PresentationLayerProducers.FromProjectedSceneGroupsV2(
            scene, includeMarkers: false, includeLabels: false, includeConstellations: false,
            includeImageCircle: false, includeCardinalDirections: true);
        var neither = PresentationLayerProducers.FromProjectedSceneGroupsV2(
            scene, includeMarkers: false, includeLabels: false, includeConstellations: false,
            includeImageCircle: false, includeCardinalDirections: false);

        Assert.HasCount(1, circleOnly.ImageCircle.Ellipses);
        Assert.IsEmpty(circleOnly.CardinalDirections.TextBlocks);
        Assert.IsEmpty(cardinalsOnly.ImageCircle.Ellipses);
        AssertCardinals(scene, cardinalsOnly.CardinalDirections);
        Assert.IsEmpty(neither.ImageCircle.Ellipses);
        Assert.IsEmpty(neither.CardinalDirections.TextBlocks);
        Assert.IsEmpty(circleOnly.StarAnnotations.Markers);
        Assert.IsEmpty(circleOnly.StarAnnotations.TextBlocks);
        Assert.IsEmpty(circleOnly.Constellations.Segments);
    }

    private static void AssertEllipse(PresentationLayerPayloadV1 payload, PixelPoint center, double radiusX, double radiusY)
    {
        Assert.HasCount(1, payload.Ellipses);
        Assert.AreEqual(center, payload.Ellipses[0].Center);
        Assert.AreEqual(radiusX, payload.Ellipses[0].RadiusX, 1e-12);
        Assert.AreEqual(radiusY, payload.Ellipses[0].RadiusY, 1e-12);
    }

    private static void AssertCardinals(ProjectedSceneV1 scene, PresentationLayerPayloadV1 payload)
    {
        var projection = new ProjectionContext(scene.Projection.Model, scene.Projection.PrincipalPointX,
            scene.Projection.PrincipalPointY, scene.Projection.FocalLengthXPixels, scene.Projection.FocalLengthYPixels,
            scene.Projection.WidthPixels, scene.Projection.HeightPixels, scene.Projection.Aperture,
            scene.Projection.ImageCircleRadiusPixels, scene.Projection.BoresightAltitudeDegrees,
            scene.Projection.BoresightAzimuthDegrees, scene.Projection.RollDegrees,
            scene.Projection.HorizontalFlip, scene.Projection.EnforceSensorBounds);
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(projection)!;
        var expected = new[] { ("N", landmarks.North), ("E", landmarks.East), ("S", landmarks.South), ("W", landmarks.West) }
            .Where(item => ContainsCrop(scene.ImageTransform, item.Item2))
            .Select(item => (item.Item1, ProjectedSceneImageTransform.Apply(scene.ImageTransform, item.Item2))).ToArray();
        Assert.HasCount(expected.Length, payload.TextBlocks);
        foreach (var (label, point) in expected)
        {
            var block = payload.TextBlocks.Single(value => value.Lines[0] == label);
            // v3 centers the pinned glyph on the transformed sky landmark. Use
            // its actual ink bounds, rather than the retired bitmap top-left offset.
            using var font = PresentationFont.Create(block, 0);
            var (x, y) = PresentationFont.LineOrigin(block, payload.WidthPixels, payload.HeightPixels,
                font, label, 0);
            var bounds = PresentationFont.LineBounds(font, label, x, y);
            Assert.AreEqual(point.X, bounds.MidX, 0.5, label);
            Assert.AreEqual(point.Y, bounds.MidY, 0.5, label);
            Assert.AreEqual(PresentationFont.FrameScale(payload.WidthPixels, payload.HeightPixels, 2), block.Scale);
        }
    }

    private static bool ContainsCrop(ProjectedSceneImageTransformV1 transform, PixelPoint point) =>
        point.X >= transform.CropX && point.X <= transform.CropX + transform.CropWidth &&
        point.Y >= transform.CropY && point.Y <= transform.CropY + transform.CropHeight;

    private static ProjectedSceneImageTransformV1 Transform(ProjectedSceneQuarterRotation rotation,
        bool horizontalMirror = false, bool verticalMirror = false) => new(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            1000, 800, 0, 0, 1000, 800, 2, 4, horizontalMirror, verticalMirror, rotation, 200, 500);

    private static async Task<ProjectedSceneV1> SceneAsync(ProjectedSceneImageTransformV1 transform)
    {
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(new VisibleSceneRequest(
            utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 500, 400, 200, 200, 1000, 800,
                ProjectionAperture.Circular, 200, 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "fisheye-v1")).ConfigureAwait(false);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, transform,
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", "fisheye-v1");
    }
}
