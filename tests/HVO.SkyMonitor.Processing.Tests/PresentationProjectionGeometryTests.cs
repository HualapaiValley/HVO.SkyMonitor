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

        AssertEllipse(clockwisePayload, new PixelPoint(10, 25), 5, 10);
        AssertEllipse(counterClockwisePayload, new PixelPoint(10, 25), 5, 10);
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

        AssertEllipse(mirroredPayload, new PixelPoint(10, 25), 5, 10);
        AssertCardinals(mirrored, mirroredPayload);
        CollectionAssert.AreNotEqual(plainPayload.TextBlocks.ToArray(), mirroredPayload.TextBlocks.ToArray());
        Assert.AreNotEqual(plainPayload.ContentIdentitySha256, mirroredPayload.ContentIdentitySha256);
    }

    [TestMethod]
    public async Task CropExcludingCircleBasisOrIndividualCardinalsOmitsGeometryWithoutThrowing()
    {
        var basisExcluded = await SceneAsync(new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            100, 80, 40, 20, 20, 40, 2, 4, false, false,
            ProjectedSceneQuarterRotation.Degrees0, 10, 10)).ConfigureAwait(false);
        var partialCardinals = await SceneAsync(new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            100, 80, 40, 0, 60, 80, 2, 4, false, false,
            ProjectedSceneQuarterRotation.Degrees0, 30, 20)).ConfigureAwait(false);
        var centerExcluded = await SceneAsync(new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            100, 80, 0, 0, 40, 80, 2, 4, false, false,
            ProjectedSceneQuarterRotation.Degrees0, 20, 20)).ConfigureAwait(false);

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
            .Select(item => (item.Item1, Point(scene, landmarks.Center, item.Item2, 2))).ToArray();
        Assert.HasCount(expected.Length, payload.TextBlocks);
        foreach (var (label, point) in expected)
        {
            var block = payload.TextBlocks.Single(value => value.Lines[0] == label);
            Assert.AreEqual(point.X, block.Point.X, 1e-12, label);
            Assert.AreEqual(point.Y, block.Point.Y, 1e-12, label);
            Assert.AreEqual(2, block.Scale);
        }
    }

    private static PixelPoint Point(ProjectedSceneV1 scene, PixelPoint centerSource, PixelPoint targetSource, int scale)
    {
        var center = ProjectedSceneImageTransform.Apply(scene.ImageTransform, centerSource);
        var target = ProjectedSceneImageTransform.Apply(scene.ImageTransform, targetSource);
        var dx = target.X - center.X;
        var dy = target.Y - center.Y;
        var factor = 1d;
        if (dx < 0) factor = Math.Min(factor, (3 * scale - center.X) / dx);
        else if (dx > 0) factor = Math.Min(factor, (scene.ImageTransform.OutputWidthPixels - 3 * scale - 1 - center.X) / dx);
        if (dy < 0) factor = Math.Min(factor, (4 * scale - center.Y) / dy);
        else if (dy > 0) factor = Math.Min(factor, (scene.ImageTransform.OutputHeightPixels - 4 * scale - 1 - center.Y) / dy);
        factor = Math.Clamp(factor, 0, 1);
        return new(Math.Round(center.X + dx * factor, MidpointRounding.AwayFromZero) - 2 * scale,
            Math.Round(center.Y + dy * factor, MidpointRounding.AwayFromZero) - 3 * scale);
    }

    private static bool ContainsCrop(ProjectedSceneImageTransformV1 transform, PixelPoint point) =>
        point.X >= transform.CropX && point.X <= transform.CropX + transform.CropWidth &&
        point.Y >= transform.CropY && point.Y <= transform.CropY + transform.CropHeight;

    private static ProjectedSceneImageTransformV1 Transform(ProjectedSceneQuarterRotation rotation,
        bool horizontalMirror = false, bool verticalMirror = false) => new(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            100, 80, 0, 0, 100, 80, 2, 4, horizontalMirror, verticalMirror, rotation, 20, 50);

    private static async Task<ProjectedSceneV1> SceneAsync(ProjectedSceneImageTransformV1 transform)
    {
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([])).BuildAsync(new VisibleSceneRequest(
            utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 50, 40, 20, 20, 100, 80,
                ProjectionAperture.Circular, 20, 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "fisheye-v1")).ConfigureAwait(false);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, transform,
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", "fisheye-v1");
    }
}
