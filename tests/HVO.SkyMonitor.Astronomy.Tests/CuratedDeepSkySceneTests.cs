using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed partial class CuratedDeepSkySceneTests
{
    private static readonly DateTimeOffset Utc = new(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DeepSkySemantics Semantics = new(
        "OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b", new Uri("https://github.com/mattiaverga/OpenNGC"),
        "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote");

    [TestMethod]
    public void PresetDeduplicatesCanonicalAliasesAndSkipsDisputedIdentity()
    {
        var item = Object("NGC0224", 31, 120, 60, 90);
        var catalog = Catalog([item], [new("M31", item.Id, DeepSkyAliasKinds.Messier),
            new("NGC7000", item.Id, DeepSkyAliasKinds.CrossReference), new("M102", item.Id, DeepSkyAliasKinds.Disputed)]);
        var plan = CuratedDeepSkySelectionPlan.For(catalog);
        Assert.HasCount(1, plan.Objects);
        Assert.AreEqual(item.Id, plan.Objects[0].Id);
        CollectionAssert.Contains(plan.UnresolvedAliases.ToArray(), "M102");
        Assert.AreSame(plan, CuratedDeepSkySelectionPlan.For(catalog));
    }

    [TestMethod]
    public void WarmProjectionDoesNotRequeryCatalogOrVisitContours()
    {
        var item = Object("NGC0224", 31, 120, 60, 90);
        var catalog = new CountingCatalog(Catalog([item], [new("M31", item.Id, DeepSkyAliasKinds.Messier)]));
        var plan = CuratedDeepSkySelectionPlan.For(catalog);
        var lookups = catalog.AliasLookups;
        var first = Scene().WithCuratedDeepSky(catalog, Selection());
        var second = Scene().WithCuratedDeepSky(catalog, Selection());
        Assert.HasCount(1, first.DeepSky!.Objects);
        Assert.HasCount(1, second.DeepSky!.Objects);
        Assert.AreEqual(lookups, catalog.AliasLookups);
        Assert.AreSame(plan, CuratedDeepSkySelectionPlan.For(catalog));
    }

    [TestMethod]
    public void MissingOrientationRemainsNullableAndUsesSymbolicSize()
    {
        var item = Object("NGC1976", 42, 90, 60, null);
        var projected = Scene().WithCuratedDeepSky(Catalog([item], [new("M42", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var actual = projected.DeepSky!.Objects.Single();
        Assert.IsNull(actual.PositionAngleDegrees);
        Assert.IsNull(actual.VisualMagnitude);
        Assert.AreEqual(DeepSkyRepresentation.SizedGlyph, actual.Representation);
        Assert.IsEmpty(projected.ResolvedFootprints);
    }

    [TestMethod]
    public void CircleAgreesWithIndependentPerspectiveRadiusAndEllipseKeepsAxes()
    {
        var round = Object("M045", 45, 150, 150, null);
        var ellipse = Object("NGC0224", 31, 120, 60, 90);
        var catalog = Catalog([round, ellipse], [new("M45", round.Id, DeepSkyAliasKinds.Messier),
            new("M31", ellipse.Id, DeepSkyAliasKinds.Messier)]);
        var scene = Scene().WithCuratedDeepSky(catalog, Selection());
        var circle = scene.ResolvedFootprints.Single(item => item.Id == "deep-sky:M045");
        var expectedDiameter = 2 * 100 * Math.Tan(1.25 * Math.PI / 180);
        Assert.AreEqual(expectedDiameter, circle.Bounds.MaxX - circle.Bounds.MinX, 0.02);
        Assert.AreEqual(expectedDiameter, circle.Bounds.MaxY - circle.Bounds.MinY, 0.02);
        var shape = scene.ResolvedFootprints.Single(item => item.Id == "deep-sky:NGC0224");
        Assert.AreEqual(ResolvedFootprintShape.Ellipse, shape.Extent.Shape);
        Assert.AreEqual(1, shape.Extent.SemiMajorAxisDegrees, 1e-12);
        Assert.AreEqual(0.5, shape.Extent.SemiMinorAxisDegrees, 1e-12);
    }

    [TestMethod]
    public void CapIsAppliedToCachedPresetBeforeProjectionAndContoursAreRejected()
    {
        var first = Object("NGC0224", 31, null, null, null);
        var second = Object("NGC1976", 42, null, null, null);
        var scene = Scene().WithCuratedDeepSky(Catalog([first, second],
            [new("M31", first.Id, DeepSkyAliasKinds.Messier), new("M42", second.Id, DeepSkyAliasKinds.Messier)]),
            Selection() with { MaximumObjects = 1 });
        Assert.HasCount(1, scene.DeepSky!.Objects);
        Assert.AreEqual(1, scene.DeepSky.OmittedCandidateCount);
        Assert.AreEqual(DeepSkyRepresentation.UnknownExtentGlyph, scene.DeepSky.Objects[0].Representation);
        Assert.Throws<ArgumentException>(() => (Selection() with { MaximumOutlines = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Selection() with { PresetVersion = "unknown" }).Validate());
    }

    [TestMethod]
    public void TypedSceneRoundTripsAndOldReaderRejectsNewSchema()
    {
        var item = Object("NGC0224", 31, 120, 60, 90);
        var visible = Scene().WithCuratedDeepSky(Catalog([item], [new("M31", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(484, 304),
            new ProjectedSceneSource(Guid.NewGuid(), Guid.NewGuid(), new string('B', 64)), new string('C', 64), "cal-v1");
        var bytes = ProjectedSceneJson.Serialize(scene);
        var parsed = ProjectedSceneJson.Parse(bytes).Scene;
        Assert.IsNotNull(parsed);
        Assert.AreEqual(scene.SceneIdentitySha256, parsed.SceneIdentitySha256);
        Assert.AreEqual(0, parsed.Selection.DeepSky!.MaximumOutlines);
        Assert.IsNull(ProjectedSceneJson.Parse(bytes, [ProjectedSceneV1.CurrentSchemaVersion,
            ProjectedSceneV1.ResolvedFootprintSchemaVersion]).Scene);
    }

    [TestMethod]
    public void OffImageCenterRetainsVisibleCircleLimbAgainstIndependentTangent()
    {
        var item = Object("NGC0224", 31, 60, 60, null) with { DeclinationDegrees = 32.3 };
        var scene = Scene(focalPixels: 4000).WithCuratedDeepSky(
            Catalog([item], [new("M31", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var footprint = scene.ResolvedFootprints.Single();
        Assert.IsNull(scene.DeepSky!.Objects.Single().Pixel);
        Assert.IsTrue(footprint.Clipped);
        // Along the meridian the near limb is 1.8 degrees from the zenith boresight.
        Assert.AreEqual(152 - 4000 * Math.Tan(1.8 * Math.PI / 180), footprint.Bounds.MaxY, 0.05);
        Assert.IsTrue(footprint.Parts.SelectMany(static part => part.Points).All(static point =>
            point.X >= -1e-8 && point.X <= 484 + 1e-8 && point.Y >= -1e-8 && point.Y <= 304 + 1e-8));
    }

    [TestMethod]
    public void CuratedCenterAndFootprintFollowCropBinMirrorAndRotationOnce()
    {
        var item = Object("NGC0224", 31, 120, 60, 90);
        var scene = Scene().WithCuratedDeepSky(
            Catalog([item], [new("M31", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            484, 304, 100, 60, 280, 180, 2, 3, true, false, ProjectedSceneQuarterRotation.Degrees90, 60, 140);
        var geometry = ProjectedSceneImageTransform.CreateGeometrySnapshot(scene, transform);
        var center = geometry.DeepSky!.Objects.Single().Pixel!.Value;
        Assert.AreEqual(60 - (152 - 60) / 3d, center.X, 1e-8);
        Assert.AreEqual(140 - (242 - 100) / 2d, center.Y, 1e-8);
        var source = scene.ResolvedFootprints.Single().Parts.Single().Points;
        var target = geometry.ResolvedFootprints!.Single().Parts.Single().Points;
        Assert.AreEqual(source.Count, target.Count);
        for (var index = 0; index < source.Count; index++)
        {
            Assert.AreEqual(60 - (source[index].Y - 60) / 3d, target[index].X, 1e-8);
            Assert.AreEqual(140 - (source[index].X - 100) / 2d, target[index].Y, 1e-8);
        }
        Assert.AreEqual(scene.DeepSky!.Objects.Single().J2000Equatorial,
            geometry.DeepSky.Objects.Single().J2000Equatorial);
    }

    private static ProjectedSceneDeepSkySelection Selection() => new(128, 32, 0, 1, 0.1);

    [TestMethod]
    public void RetainedM45EquidistantAntipodeBreaksInsteadOfDrawingAnInteriorChord()
    {
        var item = Object("M045", 45, 150, 150, null);
        var center = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(new(item.RightAscensionHours, item.DeclinationDegrees), Utc), Utc, 30, 0);
        Assert.IsTrue(center.AltitudeDegrees > 0);
        var frame = ResolvedSourceFrame.Create(center, 30);
        var radius = 1.25 * Math.PI / 180;
        var q = frame.DirectionFromCelestial(radius * Math.Cos(1), radius * Math.Sin(1));
        var boresight = CameraBasis.ToHorizontal(q * -1);
        var focal = 500 / (179.5 * Math.PI / 180);
        var visible = new VisibleScene(new VisibleSceneRequest(Utc, new(30, 0, 0),
            new ProjectionContext(ProjectionModel.EquidistantFisheye, 512, 512, focal, focal, 1024, 1024,
                ProjectionAperture.Circular, 500, boresight.AltitudeDegrees, boresight.AzimuthDegrees),
            new CatalogQuery(6, 100), Scene().Request.CatalogMetadata), []).WithCuratedDeepSky(
                Catalog([item], [new("M45", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var retained = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible, ProjectedSceneImageTransformV1.Identity(1024, 1024),
            new(Guid.NewGuid(), Guid.NewGuid(), new string('B', 64)), new string('C', 64), visible.Request.ProjectionVersion))).Scene;
        Assert.IsNotNull(retained);
        Assert.HasCount(1, retained.ResolvedFootprints!);
        var footprint = retained.ResolvedFootprints!.Single();
        Assert.IsTrue(footprint.Clipped);
        Assert.IsTrue(footprint.Parts.All(static part => !part.Closed));
        foreach (var part in footprint.Parts)
        {
            for (var index = 1; index < part.Points.Count; index++)
            {
                var from = part.Points[index - 1];
                var to = part.Points[index];
                var dx = to.X - from.X;
                var dy = to.Y - from.Y;
                var t = Math.Clamp(-((from.X - 512) * dx + (from.Y - 512) * dy) / (dx * dx + dy * dy), 0, 1);
                var x = from.X + t * dx - 512;
                var y = from.Y + t * dy - 512;
                Assert.IsTrue(Math.Sqrt(x * x + y * y) > 490, "The true M45 limb stays near radii 494–501, not the interior.");
            }
        }
    }

    [TestMethod]
    [DataRow(ProjectionModel.Perspective)]
    [DataRow(ProjectionModel.EquidistantFisheye)]
    public void OrdinaryCenteredCircleRemainsClosedAndMatchesItsProjectionRadius(ProjectionModel model)
    {
        var item = Object("M045", 45, 150, 150, null);
        var request = Scene().Request;
        var projection = new ProjectionContext(model, 242, 152, 100, 100, 484, 304,
            model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
            model == ProjectionModel.Perspective ? null : 140);
        var visible = new VisibleScene(new VisibleSceneRequest(Utc, request.Observer, projection,
            request.CatalogQuery, request.CatalogMetadata), []).WithCuratedDeepSky(
                Catalog([item], [new("M45", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var footprint = visible.ResolvedFootprints.Single();
        Assert.IsFalse(footprint.Clipped);
        Assert.IsTrue(footprint.Parts.Single().Closed);
        var angularRadius = 1.25 * Math.PI / 180;
        var expected = 100 * (model == ProjectionModel.Perspective ? Math.Tan(angularRadius) : angularRadius);
        Assert.AreEqual(2 * expected, footprint.Bounds.MaxX - footprint.Bounds.MinX, 0.02);
        Assert.AreEqual(2 * expected, footprint.Bounds.MaxY - footprint.Bounds.MinY, 0.02);
    }

    [TestMethod]
    public void CropRemovingAllCuratedObjectsKeepsAnHonestEmptyV3Selection()
    {
        var item = Object("NGC0224", 31, 120, 60, 90);
        var visible = Scene().WithCuratedDeepSky(
            Catalog([item], [new("M31", item.Id, DeepSkyAliasKinds.Messier)]), Selection());
        var transform = new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            484, 304, 0, 0, 100, 60, 1, 1, false, false, ProjectedSceneQuarterRotation.Degrees0, 100, 60);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, transform,
            new ProjectedSceneSource(Guid.NewGuid(), Guid.NewGuid(), new string('B', 64)), new string('C', 64), "cal-v1");
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, scene.SchemaVersion);
        Assert.IsEmpty(scene.DeepSky!.Objects);
        Assert.IsNull(scene.ResolvedFootprints);
        var parsed = ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(scene));
        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.IsEmpty(parsed.Scene!.DeepSky!.Objects);
    }

    private static VisibleScene Scene(double focalPixels = 100) => new(new VisibleSceneRequest(Utc, new ObserverLocation(30, 0, 0),
        new ProjectionContext(ProjectionModel.Perspective, 242, 152, focalPixels, focalPixels, 484, 304,
            ProjectionAperture.Rectangular), new CatalogQuery(6, 100),
        new CatalogMetadata("HYG+OpenNGC", "test", new Uri("https://example.test/catalog"), new string('A', 64), "CC BY-SA 4.0", "4"),
        projectionVersion: "cal-v1"), []);

    private static DeepSkyObject Object(string id, int messier, double? major, double? minor, double? angle) =>
        new(id, id, "M" + messier.ToString(System.Globalization.CultureInfo.InvariantCulture), "G", AstronomyTime.LocalMeanSiderealDegrees(Utc, 0) / 15, 30,
            "And", major, minor, angle, null, null, null, null, messier, null, null, null);

    private static DeepSkyCatalog Catalog(IEnumerable<DeepSkyObject> objects, IEnumerable<DeepSkyAlias> aliases)
    {
        var rows = objects.ToArray();
        return new(Semantics, rows, aliases.Concat(rows.Select(static item =>
            new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation))), [], []);
    }

    private sealed class CountingCatalog(IDeepSkyCatalog inner) : IDeepSkyCatalog
    {
        public int AliasLookups { get; private set; }
        public DeepSkySemantics Semantics => inner.Semantics;
        public IReadOnlyList<DeepSkyObject> Objects => throw new InvalidOperationException("No full-catalog frame scan.");
        public int AliasCount => inner.AliasCount;
        public IReadOnlyList<DeepSkyTombstone> Tombstones => inner.Tombstones;
        public bool TryGetObject(string id, out DeepSkyObject value) => inner.TryGetObject(id, out value);
        public IReadOnlyList<DeepSkyOutline> GetOutlines(string id) => throw new InvalidOperationException("No contour traversal.");
        public IReadOnlyList<DeepSkyAlias> FindByAlias(string designation)
        {
            AliasLookups++;
            return inner.FindByAlias(designation);
        }
        public bool TryGetTombstone(string id, out DeepSkyTombstone value) => inner.TryGetTombstone(id, out value);
        public DeepSkyQueryResult Query(DeepSkyQuery query) => throw new InvalidOperationException("No per-frame catalog query.");
    }
}
