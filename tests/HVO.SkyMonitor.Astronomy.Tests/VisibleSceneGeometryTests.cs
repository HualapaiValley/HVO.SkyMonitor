using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class VisibleSceneGeometryTests
{
    private static readonly DateTimeOffset Utc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Observer = new(35.347, -113.878, 0);
    private static readonly CatalogMetadata Metadata = new(
        "Test", "1", new Uri("https://example.test/catalog"), "fixture", "CC0", "1");

    [TestMethod]
    public void TryProjectGeometry_CoversProjectionDomainsAndHorizonPolicy()
    {
        var zenithBasis = CameraBasis.Create(90, 0);
        var center = new EnuVector(0, 0, 1);
        var oblique = new EnuVector(0.5, 0.5, Math.Sqrt(0.5));
        var horizon = new EnuVector(1, 0, 0);
        var nadir = new EnuVector(0, 0, -1);

        var perspective = CreateRequest(new ProjectionContext(
            ProjectionModel.Perspective, 100, 100, 100, 100, 200, 200, ProjectionAperture.Rectangular));
        Assert.IsTrue(VisibleSceneBuilder.TryProjectGeometry(perspective, zenithBasis, center, out var centerPixel));
        Assert.AreEqual(new PixelPoint(100, 100), centerPixel);
        Assert.IsFalse(VisibleSceneBuilder.TryProjectGeometry(perspective, zenithBasis, horizon, out _));

        var northBasis = CameraBasis.Create(0, 0);
        var north = CameraBasis.FromHorizontal(new AltAzPoint(0, 0));
        Assert.IsTrue(VisibleSceneBuilder.TryProjectGeometry(perspective, northBasis, north, out var northPixel));
        Assert.AreEqual(new PixelPoint(100, 100), northPixel);

        var geometric = CreateRequest(perspective.Projection, HorizonPolicy.GeometricHorizon);
        Assert.IsFalse(VisibleSceneBuilder.TryProjectGeometry(geometric, zenithBasis, nadir, out _));

        foreach (var model in new[]
                 {
                     ProjectionModel.EquidistantFisheye,
                     ProjectionModel.EquisolidFisheye,
                     ProjectionModel.OrthographicFisheye,
                     ProjectionModel.StereographicFisheye
                 })
        {
            var request = CreateRequest(Circular(model));
            Assert.IsTrue(VisibleSceneBuilder.TryProjectGeometry(request, zenithBasis, oblique, out var pixel));
            Assert.IsTrue(double.IsFinite(pixel.X));
            Assert.IsTrue(double.IsFinite(pixel.Y));
        }

        var orthographic = CreateRequest(Circular(ProjectionModel.OrthographicFisheye));
        Assert.IsFalse(VisibleSceneBuilder.TryProjectGeometry(orthographic, zenithBasis, nadir, out _));
        var equidistant = CreateRequest(Circular(ProjectionModel.EquidistantFisheye));
        Assert.IsFalse(VisibleSceneBuilder.TryProjectGeometry(equidistant, zenithBasis, nadir, out _));
        var stereographic = CreateRequest(Circular(ProjectionModel.StereographicFisheye));
        Assert.IsFalse(VisibleSceneBuilder.TryProjectGeometry(stereographic, zenithBasis, nadir, out _));
        Assert.IsFalse(VisibleSceneBuilder.TryProjectGeometry(stereographic, zenithBasis,
            new EnuVector(1e-10, 0, -1).Normalize(), out _));
    }

    [TestMethod]
    public void TryClipToProjection_CoversRectangularAndCircularBoundaries()
    {
        var rectangle = new ProjectionContext(
            ProjectionModel.Perspective, 50, 50, 50, 50, 100, 100, ProjectionAperture.Rectangular);
        Assert.IsTrue(VisibleSceneBuilder.TryClipToProjection(
            rectangle, new PixelPoint(-10, 50), new PixelPoint(110, 50), out var from, out var to));
        Assert.AreEqual(0, from.X, 1e-12);
        Assert.AreEqual(100, to.X, 1e-12);
        Assert.IsTrue(VisibleSceneBuilder.TryClipToProjection(
            rectangle, new PixelPoint(50, -10), new PixelPoint(50, 110), out from, out to));
        Assert.AreEqual(0, from.Y, 1e-12);
        Assert.AreEqual(100, to.Y, 1e-12);
        Assert.IsFalse(VisibleSceneBuilder.TryClipToProjection(
            rectangle, new PixelPoint(-10, -10), new PixelPoint(-10, 110), out _, out _));

        var circle = Circular(ProjectionModel.EquidistantFisheye, radius: 40);
        Assert.IsTrue(VisibleSceneBuilder.TryClipToProjection(
            circle, new PixelPoint(0, 50), new PixelPoint(100, 50), out from, out to));
        Assert.AreEqual(10, from.X, 1e-12);
        Assert.AreEqual(90, to.X, 1e-12);
        Assert.IsFalse(VisibleSceneBuilder.TryClipToProjection(
            circle, new PixelPoint(0, 0), new PixelPoint(100, 0), out _, out _));
    }

    [TestMethod]
    public void TryClipToProjection_PerspectiveWithoutSensorBoundsRetainsOffSensorSegment()
    {
        var projection = new ProjectionContext(
            ProjectionModel.Perspective, 50, 50, 50, 50, 100, 100,
            ProjectionAperture.Rectangular, EnforceSensorBounds: false);
        var from = new PixelPoint(-25, 50);
        var to = new PixelPoint(125, 50);

        Assert.IsTrue(VisibleSceneBuilder.TryClipToProjection(projection, from, to, out var clippedFrom, out var clippedTo));
        Assert.AreEqual(from, clippedFrom);
        Assert.AreEqual(to, clippedTo);

        var output = new List<ProjectedConstellationSegment>();
        VisibleSceneBuilder.AddClippedChord(projection, "TST", "a", "b", from, to, output);
        Assert.HasCount(1, output);
        Assert.AreEqual(from, output[0].FromPixel);
        Assert.AreEqual(to, output[0].ToPixel);
    }

    [TestMethod]
    public void PrimitiveClipping_CoversParallelDegenerateAndRejectedIntervals()
    {
        var minimum = 0d;
        var maximum = 1d;
        Assert.IsTrue(VisibleSceneBuilder.ClipBoundary(0, 1, ref minimum, ref maximum));
        Assert.IsFalse(VisibleSceneBuilder.ClipBoundary(0, -1, ref minimum, ref maximum));

        minimum = 0;
        maximum = 1;
        Assert.IsFalse(VisibleSceneBuilder.ClipBoundary(-1, -2, ref minimum, ref maximum));
        minimum = 0.75;
        maximum = 1;
        Assert.IsFalse(VisibleSceneBuilder.ClipBoundary(1, 0.5, ref minimum, ref maximum));

        var circle = Circular(ProjectionModel.EquidistantFisheye, radius: 40);
        minimum = 0;
        maximum = 1;
        Assert.IsTrue(VisibleSceneBuilder.ClipCircle(
            circle, new PixelPoint(50, 50), 0, 0, ref minimum, ref maximum));
        minimum = 0;
        maximum = 1;
        Assert.IsFalse(VisibleSceneBuilder.ClipCircle(
            circle, new PixelPoint(0, 0), 0, 0, ref minimum, ref maximum));
        minimum = 0;
        maximum = 1;
        Assert.IsFalse(VisibleSceneBuilder.ClipCircle(
            circle, new PixelPoint(0, 0), 100, 0, ref minimum, ref maximum));
    }

    [TestMethod]
    public void ChordHelpers_CoverDegenerateSubdivisionAndBothBoundaryOrientations()
    {
        Assert.AreEqual(1, VisibleSceneBuilder.DistanceFromChord(
            new PixelPoint(1, 0), new PixelPoint(0, 0), new PixelPoint(0, 0)), 1e-12);
        Assert.AreEqual(1, VisibleSceneBuilder.DistanceFromChord(
            new PixelPoint(1, 1), new PixelPoint(0, 0), new PixelPoint(2, 0)), 1e-12);
        Assert.AreEqual(new EnuVector(0, 0, 1), VisibleSceneBuilder.Slerp(
            new EnuVector(0, 0, 1), new EnuVector(0, 0, 1), 0.5));
        Assert.AreEqual(1, VisibleSceneBuilder.Slerp(
            new EnuVector(0, 0, 1), new EnuVector(1, 0, 0), 0.5).Length, 1e-12);

        var request = CreateRequest(new ProjectionContext(
            ProjectionModel.Perspective, 50, 50, 50, 50, 100, 100, ProjectionAperture.Rectangular));
        var basis = CameraBasis.Create(90, 0);
        var center = new EnuVector(0, 0, 1);
        var outside = new EnuVector(1, 0, 0);
        var output = new List<ProjectedConstellationSegment>();

        VisibleSceneBuilder.AppendClippedChord(request, basis, "TST", "a", "b", center, outside, output, 8);
        VisibleSceneBuilder.AppendClippedChord(request, basis, "TST", "a", "b", outside, center, output, 8);
        var count = output.Count;
        VisibleSceneBuilder.AppendClippedChord(request, basis, "TST", "a", "b", outside,
            new EnuVector(0, 1, 0), output, 8);
        Assert.AreEqual(count, output.Count);
        Assert.HasCount(2, output);

        var subdivided = new List<ProjectedConstellationSegment>();
        VisibleSceneBuilder.AppendClippedChord(
            request, basis, "TST", "a", "b", center, outside, subdivided);
        Assert.IsNotEmpty(subdivided);

        var curvedRequest = CreateRequest(Circular(ProjectionModel.EquidistantFisheye, radius: 200));
        var curved = new List<ProjectedConstellationSegment>();
        VisibleSceneBuilder.AppendClippedChord(curvedRequest, basis, "TST", "a", "b",
            new EnuVector(1, 0, 0), new EnuVector(0, 1, 0), curved);
        Assert.IsGreaterThan(1, curved.Count);

        VisibleSceneBuilder.AddClippedChord(request.Projection, "TST", "a", "b",
            new PixelPoint(50, 50), new PixelPoint(50, 50), output);
        Assert.HasCount(2, output);
    }

    [TestMethod]
    public void AppendProjectedSegmentChords_RejectsAntipodalAndNumbersCurvedParts()
    {
        var request = CreateRequest(Circular(ProjectionModel.EquidistantFisheye, radius: 100));
        var basis = CameraBasis.Create(90, 0);
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(Utc, Observer.LongitudeDegrees) / 15;
        var from = new CelestialCatalogObject("from", "From", rightAscension, 0, 1);
        var opposite = new CelestialCatalogObject("opposite", "Opposite", (rightAscension + 12) % 24, 0, 1);
        var near = new CelestialCatalogObject("near", "Near", rightAscension, 20, 1);
        var output = new List<ProjectedConstellationSegment>();

        VisibleSceneBuilder.AppendProjectedSegmentChords(request, basis, "TST", from, opposite, output);
        Assert.IsEmpty(output);
        VisibleSceneBuilder.AppendProjectedSegmentChords(request, basis, "TST", from, near, output);

        Assert.IsNotEmpty(output);
        CollectionAssert.AreEqual(Enumerable.Range(0, output.Count).ToArray(),
            output.Select(segment => segment.PartIndex).ToArray());
    }

    private static ProjectionContext Circular(ProjectionModel model, double radius = 100)
        => new(model, 50, 50, 100, 100, 100, 100, ProjectionAperture.Circular, radius,
            EnforceSensorBounds: false);

    private static VisibleSceneRequest CreateRequest(
        ProjectionContext projection,
        HorizonPolicy horizonPolicy = HorizonPolicy.ProjectionOnly)
        => new(
            Utc,
            Observer,
            projection,
            new CatalogQuery(6, 100),
            Metadata,
            horizonPolicy: horizonPolicy);
}
