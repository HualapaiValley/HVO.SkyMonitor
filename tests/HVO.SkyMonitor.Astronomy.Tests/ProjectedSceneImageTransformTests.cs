using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProjectedSceneImageTransformTests
{
    private static readonly string[] ExpectedRotationIdentities =
    [
        "B4A465EAA475BD4AAA01EEC4DDBB0E5E7D9DD7DFE397606F293011FE05B465D3",
        "E8E92011EADA3C29277E10F34B0E5C7D1ED526FFC593DF16ACED63D05A66C39B",
        "5FCDBAB95917BEBD3A3EC632C844F0EC96FD8B223C5A4D5E6C895A679E8C535A",
        "1C4FBBC78DEF7D552D5908E27A57AE0072C00AC7D3C14EC2BDA183CAE0865145"
    ];
    private static readonly int[] ExpectedPartIndices = [0, 1];
    [TestMethod]
    [DataRow(ProjectedSceneQuarterRotation.Degrees0, 40, 20, 10, 5, 0, 0, 40, 20)]
    [DataRow(ProjectedSceneQuarterRotation.Degrees90, 20, 40, 15, 10, 20, 0, 0, 40)]
    [DataRow(ProjectedSceneQuarterRotation.Degrees180, 40, 20, 30, 15, 40, 20, 0, 0)]
    [DataRow(ProjectedSceneQuarterRotation.Degrees270, 20, 40, 5, 30, 0, 40, 20, 0)]
    public void Apply_CropBinAndEveryQuarterRotationTransformObjectAndSegmentCoordinates(
        ProjectedSceneQuarterRotation rotation,
        int outputWidth,
        int outputHeight,
        double expectedObjectX,
        double expectedObjectY,
        double expectedFromX,
        double expectedFromY,
        double expectedToX,
        double expectedToY)
    {
        var transform = Transform(rotation, outputWidth, outputHeight);
        var scene = CreateGeometryScene(new PixelPoint(30, 35), new PixelPoint(10, 20), new PixelPoint(90, 80));

        var transformed = ProjectedSceneImageTransform.CreateGeometrySnapshot(scene, transform);

        Assert.AreEqual(new PixelPoint(expectedObjectX, expectedObjectY), transformed.Objects[0].Pixel);
        Assert.AreEqual(new PixelPoint(expectedFromX, expectedFromY), transformed.Segments[0].FromPixel);
        Assert.AreEqual(new PixelPoint(expectedToX, expectedToY), transformed.Segments[0].ToPixel);
        Assert.AreEqual(scene.Objects[0].Pixel,
            ProjectedSceneImageTransform.Inverse(transform, transformed.Objects[0].Pixel));
    }

    [TestMethod]
    [DataRow(false, false, 10, 5)]
    [DataRow(true, false, 30, 5)]
    [DataRow(false, true, 10, 15)]
    [DataRow(true, true, 30, 15)]
    public void Apply_PostReadoutMirrorsFollowCanonicalOrder(
        bool horizontalMirror,
        bool verticalMirror,
        double expectedX,
        double expectedY)
    {
        var transform = Transform(ProjectedSceneQuarterRotation.Degrees0, 40, 20) with
        {
            HorizontalMirror = horizontalMirror,
            VerticalMirror = verticalMirror
        };

        Assert.AreEqual(new PixelPoint(expectedX, expectedY),
            ProjectedSceneImageTransform.Apply(transform, new PixelPoint(30, 35)));
    }

    [TestMethod]
    public void Validate_RejectsInvalidDimensionsCropBinsAndRotation()
    {
        var valid = Transform(ProjectedSceneQuarterRotation.Degrees0, 40, 20);
        ProjectedSceneImageTransform.Validate(valid, 100, 100);

        var invalid = new[]
        {
            valid with { SourceWidthPixels = 101 },
            valid with { CropX = -1 },
            valid with { CropWidth = 81 },
            valid with { BinX = 3 },
            valid with { Rotation = (ProjectedSceneQuarterRotation)99 },
            valid with { OutputWidthPixels = 41 }
        };
        foreach (var transform in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => ProjectedSceneImageTransform.Validate(transform, 100, 100));
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProjectedSceneImageTransform.Apply(valid, new PixelPoint(9, 20)));
    }

    [TestMethod]
    public void ProjectedSceneCreate_TransformsGeometryOnceAndPinsTransformIdentity()
    {
        var source = CreateGeometryScene(new PixelPoint(30, 35), new PixelPoint(10, 20), new PixelPoint(90, 80));
        var identities = new List<string>();
        foreach (var rotation in Enum.GetValues<ProjectedSceneQuarterRotation>())
        {
            var swapsAxes = rotation is ProjectedSceneQuarterRotation.Degrees90 or ProjectedSceneQuarterRotation.Degrees270;
            var transform = Transform(rotation, swapsAxes ? 20 : 40, swapsAxes ? 40 : 20);
            var scene = ProjectedSceneJson.Create(
                ProjectedSceneKind.Predicted, source, transform,
                new ProjectedSceneSource(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
                "calibration-v1", "perspective-v1");
            var parsed = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(scene));

            Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
            Assert.AreEqual(ProjectedSceneImageTransform.Apply(transform, source.Objects[0].Pixel), scene.Objects[0].Pixel);
            Assert.AreEqual(ProjectedSceneImageTransform.Apply(transform, source.Segments[0].FromPixel), scene.Segments[0].FromPixel);
            identities.Add(scene.SceneIdentitySha256);
        }

        CollectionAssert.AreEqual(ExpectedRotationIdentities, identities);
    }

    [TestMethod]
    public void CreateGeometrySnapshot_FiltersObjectsAndClipsOrDropsSegmentsBeforeTransform()
    {
        var source = CreateGeometryScene(
            [new PixelPoint(30, 35), new PixelPoint(5, 5)],
            [
                new ProjectedConstellationSegment("TST", "cross", "edge", new PixelPoint(0, 50), new PixelPoint(100, 50), 4),
                new ProjectedConstellationSegment("TST", "outside", "outside", new PixelPoint(0, 0), new PixelPoint(5, 5), 8),
                new ProjectedConstellationSegment("TST", "cross", "edge", new PixelPoint(30, 30), new PixelPoint(100, 30), 9)
            ]);
        var transform = Transform(ProjectedSceneQuarterRotation.Degrees90, 20, 40) with
        {
            HorizontalMirror = true,
            VerticalMirror = true
        };

        var snapshot = ProjectedSceneImageTransform.CreateGeometrySnapshot(source, transform);

        Assert.HasCount(1, snapshot.Objects);
        Assert.AreEqual("object-0", snapshot.Objects[0].Id);
        Assert.HasCount(2, snapshot.Segments);
        CollectionAssert.AreEqual(ExpectedPartIndices, snapshot.Segments.Select(static segment => segment.PartIndex).ToArray());
        Assert.AreEqual(new PixelPoint(10, 40), snapshot.Segments[0].FromPixel);
        Assert.AreEqual(new PixelPoint(10, 0), snapshot.Segments[0].ToPixel);
        Assert.AreEqual(new PixelPoint(3.333333333333332, 30), snapshot.Segments[1].FromPixel);
        Assert.AreEqual(new PixelPoint(3.333333333333332, 0), snapshot.Segments[1].ToPixel);
    }

    [TestMethod]
    public void ProjectedSceneCreate_AcceptsNormalOffCropGeometryAndPersistsOnlyClippedSnapshot()
    {
        var source = CreateGeometryScene(
            [new PixelPoint(30, 35), new PixelPoint(5, 5)],
            [
                new ProjectedConstellationSegment("TST", "cross", "edge", new PixelPoint(0, 50), new PixelPoint(100, 50)),
                new ProjectedConstellationSegment("TST", "outside", "outside", new PixelPoint(0, 0), new PixelPoint(5, 5))
            ]);
        var transform = Transform(ProjectedSceneQuarterRotation.Degrees180, 40, 20);

        var scene = ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, source, transform,
            new ProjectedSceneSource(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", "perspective-v1");
        var parsed = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(scene));

        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.HasCount(1, scene.Objects);
        Assert.HasCount(1, scene.Segments);
        Assert.AreEqual(0, scene.Segments[0].PartIndex);
        Assert.AreEqual(new PixelPoint(40, 10), scene.Segments[0].FromPixel);
        Assert.AreEqual(new PixelPoint(0, 10), scene.Segments[0].ToPixel);
    }

    private static ProjectedSceneImageTransformV1 Transform(
        ProjectedSceneQuarterRotation rotation,
        int outputWidth,
        int outputHeight) => new(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            100, 100, 10, 20, 80, 60, 2, 3, false, false, rotation, outputWidth, outputHeight);

    private static VisibleScene CreateGeometryScene(PixelPoint objectPixel, PixelPoint from, PixelPoint to)
        => CreateGeometryScene(
            [objectPixel],
            [new ProjectedConstellationSegment("TST", "from", "to", from, to)]);

    private static VisibleScene CreateGeometryScene(
        IReadOnlyList<PixelPoint> objectPixels,
        IReadOnlyList<ProjectedConstellationSegment> segments)
    {
        var utc = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 50, 50, 50, 50, 100, 100,
            ProjectionAperture.Rectangular, EnforceSensorBounds: false);
        var basis = CameraBasis.Create(90, 0);
        var request = new VisibleSceneRequest(
            utc, new ObserverLocation(0, 0, 0), projection, new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
            projectionVersion: "perspective-v1", constellationIds: ["TST"]);
        var projector = ProjectorFactory.Create(projection);
        var objects = objectPixels.Select((pixel, index) =>
        {
            var apparent = projector.Unproject(pixel)!.Value;
            return new ProjectedCelestialObject(
                $"object-{index}", $"Object {index}", CelestialObjectKind.Star,
                new EquatorialPoint(0, 0), new EquatorialPoint(0, 0), apparent, apparent,
                basis.ToCamera(CameraBasis.FromHorizontal(apparent)), pixel, index + 1, null,
                "1", "perspective-v1", request.AlgorithmVersion);
        }).ToArray();
        var topology = new ConstellationTopologyMetadata(
            "fixture-topology", "1", new Uri("https://example.test/topology"),
            new string('C', 64), "test", "fixture-v1");
        return new VisibleScene(
            request,
            objects,
            segments,
            new VisibleSceneComputationProvenance("unspecified", topology, null, null));
    }
}
