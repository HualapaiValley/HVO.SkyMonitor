using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ProjectedSceneAnnotationTests
{
    private static readonly DateTimeOffset Utc = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SceneObjectsKeepTheLabelRuleAndCarryTheirOutline()
    {
        var scene = await SceneAsync(60, new AltAzPoint(60, 0), .25,
            ("bright", "BRIGHT", new AltAzPoint(60.5, 0), 1),
            ("faint", "FAINT", new AltAzPoint(59.5, 0), 4),
            ("anonymous", "anonymous", new AltAzPoint(60, .5), 1)).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, scene.SchemaVersion);

        var objects = ProjectedSceneAnnotation.CreateObjects(scene, 2.5).ToDictionary(static item => item.Id);

        Assert.HasCount(scene.Objects.Count, objects);
        Assert.AreSame(scene.ResolvedFootprints!.Single().Parts, objects["solar-system:Sun"].FootprintParts);
        Assert.IsTrue(objects["solar-system:Sun"] is { DrawMark: true, DrawLabel: true });
        Assert.IsTrue(objects["bright"] is { DrawMark: true, DrawLabel: true, FootprintParts: null });
        Assert.IsTrue(objects["faint"] is { DrawMark: false, DrawLabel: false });
        Assert.IsTrue(objects["anonymous"] is { DrawMark: false, DrawLabel: false });
        foreach (var item in scene.Objects) Assert.AreEqual(item.Pixel, objects[item.Id].Pixel);
    }

    [TestMethod]
    public async Task DiscCentredBelowTheGeometricHorizonKeepsItsOutlineAndLabelAtItsVisibleBounds()
    {
        // The geometric centre is 0.2 degrees below the horizon, so the scene omits the Sun object and the footprint has
        // no centre pixel, but the upper 0.3 degrees of the 0.5 degree limb is above the horizon and in the frame.
        var scene = await SceneAsync(0, new AltAzPoint(-.2, 0), .5).ConfigureAwait(false);
        Assert.IsFalse(scene.Objects.Any(static item => item.Id == "solar-system:Sun"));
        var footprint = scene.ResolvedFootprints!.Single();
        Assert.IsNull(footprint.CenterPixel);
        var expected = new PixelPoint((footprint.Bounds.MinX + footprint.Bounds.MaxX) / 2,
            (footprint.Bounds.MinY + footprint.Bounds.MaxY) / 2);

        var sun = ProjectedSceneAnnotation.CreateObjects(scene, 2.5).Single(static item => item.Id == "solar-system:Sun");

        Assert.AreEqual(expected, sun.Pixel);
        Assert.IsTrue(sun.Pixel.Y is > 0 and < 300);
        Assert.IsTrue(sun is { DisplayName: "Sun", DrawMark: true, DrawLabel: true });
        Assert.AreSame(footprint.Parts, sun.FootprintParts);
    }

    [TestMethod]
    public void FootprintAnchorUsesAnInFrameCentreAndOtherwiseTheVisibleBounds()
    {
        var transform = ProjectedSceneImageTransformV1.Identity(400, 300);
        var bounds = new ResolvedFootprintBounds(10, 20, 30, 60);
        PixelPoint Anchor(PixelPoint? center) => ProjectedSceneAnnotation.FootprintAnchor(
            new ProjectedResolvedFootprint("deep-sky:test", "Test", ResolvedFootprintSourceKind.DeepSkyObject,
                ProjectedResolvedFootprint.CurrentContractVersion, ResolvedFootprintSampler.AlgorithmVersion,
                ResolvedFootprintExtent.Circle(1, "fixture"), new AltAzPoint(10, 10), new AltAzPoint(10, 10), null,
                center, Clipped: true, bounds,
                [new ResolvedFootprintPart(false, [new PixelPoint(10, 20), new PixelPoint(30, 60)])], null), transform);

        Assert.AreEqual(new PixelPoint(25, 45), Anchor(new PixelPoint(25, 45)));
        Assert.AreEqual(new PixelPoint(20, 40), Anchor(null));
        Assert.AreEqual(new PixelPoint(20, 40), Anchor(new PixelPoint(-1, 45)));
        Assert.AreEqual(new PixelPoint(20, 40), Anchor(new PixelPoint(25, 301)));
        Assert.AreEqual(new PixelPoint(20, 40), Anchor(new PixelPoint(double.NaN, 45)));
    }

    [TestMethod]
    public async Task DiscCentredOutsideTheCropAnchorsAtItsVisibleBounds()
    {
        // At f = 4000 the frame's top edge is 2.15 degrees above the boresight; a 0.5 degree disc 2.3 degrees above it
        // has its centre outside the frame and its lower limb inside it.
        var scene = await SceneAsync(60, new AltAzPoint(62.3, 0), .5).ConfigureAwait(false);
        Assert.IsFalse(scene.Objects.Any(static item => item.Id == "solar-system:Sun"));
        var footprint = scene.ResolvedFootprints!.Single();
        Assert.IsNull(footprint.CenterPixel);
        var expected = new PixelPoint((footprint.Bounds.MinX + footprint.Bounds.MaxX) / 2,
            (footprint.Bounds.MinY + footprint.Bounds.MaxY) / 2);

        var sun = ProjectedSceneAnnotation.CreateObjects(scene, 2.5).Single(static item => item.Id == "solar-system:Sun");

        Assert.AreEqual(expected, sun.Pixel);
        Assert.IsTrue(sun.Pixel.Y is >= 0 and <= 300);
        Assert.IsTrue(sun is { DrawMark: true, DrawLabel: true });
        Assert.AreSame(footprint.Parts, sun.FootprintParts);

        // Drawn, the outline and the label both land in the frame rather than vanishing with the centre.
        var drawn = AnnotationRenderer.AnnotateMono8(new byte[400 * 300], 400, 300, [sun], new PreviewTransform(1, 1));
        Assert.IsTrue(drawn.Pixels.Span.IndexOfAnyExcept((byte)0) >= 0);
        Assert.AreEqual("solar-system:Sun", drawn.Anchors.Single().ObjectId);
    }

    [TestMethod]
    public async Task SceneWithoutFootprintsMapsExactlyTheSceneObjects()
    {
        var scene = await SceneAsync(60, sun: null, 0, ("bright", "BRIGHT", new AltAzPoint(60.5, 0), 1)).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, scene.SchemaVersion);

        var objects = ProjectedSceneAnnotation.CreateObjects(scene, 2.5);

        CollectionAssert.AreEqual(scene.Objects.Select(static item => item.Id).ToArray(),
            objects.Select(static item => item.Id).ToArray());
        Assert.IsTrue(objects.All(static item => item.FootprintParts is null));
    }

    [TestMethod]
    public async Task DeepSkyGeometryLeavesTheStarSunAndMoonAnnotationUnchanged()
    {
        (string, string, AltAzPoint, double)[] stars = [("bright", "BRIGHT", new AltAzPoint(60.5, 0), 1)];
        var v2 = await SceneAsync(60, new AltAzPoint(60, 0), .25, null, stars).ConfigureAwait(false);
        var v3 = await SceneAsync(60, new AltAzPoint(60, 0), .25, DeepSkyFixture(), stars).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, v3.SchemaVersion);
        Assert.IsTrue(v3.ResolvedFootprints!.Any(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject));
        Assert.HasCount(1, v3.DeepSky!.Outlines);

        var expected = ProjectedSceneAnnotation.CreateObjects(v2, 2.5);
        var actual = ProjectedSceneAnnotation.CreateObjects(v3, 2.5);

        CollectionAssert.AreEqual(expected.Select(Describe).ToArray(), actual.Select(Describe).ToArray());
        CollectionAssert.AreEqual(v2.ResolvedFootprints!.Select(static item => item.Id).ToArray(),
            ProjectedSceneAnnotation.SolarSystemFootprints(v3).Select(static item => item.Id).ToArray());

        static string Describe(ProjectedAnnotationObject item) =>
            $"{item.Id}|{item.DisplayName}|{item.Pixel}|{item.DrawMark}|{item.DrawLabel}|" +
            string.Join(';', (item.FootprintParts ?? []).Select(static part =>
                $"{part.Closed}:{string.Join(',', part.Points)}"));
    }

    /// <summary>
    /// A deep-sky collection that places one ellipse and one sourced outline beside the Sun in the 60 degree frame of
    /// <see cref="SceneAsync(double, AltAzPoint?, double, IDeepSkyCatalog?, (string, string, AltAzPoint, double)[])"/>.
    /// </summary>
    internal static DeepSkyCatalog DeepSkyFixture()
    {
        var ellipse = J2000(new AltAzPoint(60.8, 1.5));
        var outlined = J2000(new AltAzPoint(59.2, -1.5));
        DeepSkyObject[] objects =
        [
            new("NGC0001", "NGC 1", "NGC 1", "G", ellipse.RightAscensionHours, ellipse.DeclinationDegrees, "Peg",
                30, 15, 20, null, 11, null, null, null, null, null, null),
            new("NGC0002", "NGC 2", "NGC 2", "Neb", outlined.RightAscensionHours, outlined.DeclinationDegrees, "Peg",
                36, 36, null, null, 11, null, null, null, null, null, null)
        ];
        var ra = outlined.RightAscensionHours * 15;
        var dec = outlined.DeclinationDegrees;
        var half = .3 / Math.Cos(dec * Math.PI / 180);
        DeepSkyOutlinePoint[] ring =
        [
            new(ra - half, dec - .3), new(ra + half, dec - .3), new(ra + half, dec + .3), new(ra - half, dec + .3),
            new(ra - half, dec - .3)
        ];
        return new DeepSkyCatalog(
            new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
                new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned",
                "J2000.0", "arcminute", "degrees-north-through-east-0-inclusive-to-180-exclusive",
                "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote"),
            objects, objects.Select(static item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation)),
            [], [new DeepSkyOutline("NGC0002", 1, [new DeepSkyOutlineRing(ring)])]);
    }

    private static EquatorialPoint J2000(AltAzPoint at) =>
        EquatorialPrecession.PrecessToJ2000(CoordinateTransforms.HorizontalToEquatorial(at, Utc, 0, 0), Utc);

    internal static Task<ProjectedSceneV1> SceneAsync(double boresightAltitude, AltAzPoint? sun, double radius,
        params (string Id, string Name, AltAzPoint At, double Magnitude)[] stars) =>
        SceneAsync(boresightAltitude, sun, radius, null, stars);

    internal static async Task<ProjectedSceneV1> SceneAsync(double boresightAltitude, AltAzPoint? sun, double radius,
        IDeepSkyCatalog? deepSky, params (string Id, string Name, AltAzPoint At, double Magnitude)[] stars)
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 200, 150, 4000, 4000, 400, 300,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: boresightAltitude, BoresightAzimuthDegrees: 0);
        var catalog = stars.Select(static star =>
        {
            var j2000 = J2000(star.At);
            return new CelestialCatalogObject(star.Id, star.Name, j2000.RightAscensionHours, j2000.DeclinationDegrees,
                star.Magnitude);
        }).ToArray();
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(catalog), null,
            new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(Utc,
            new ObserverLocation(0, 0, 0), projection, new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Sun])).ConfigureAwait(false);
        if (sun is { } direction)
            visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, Utc, direction, radius,
                0, 1, 0, 149600000)]);
        if (deepSky is not null) visible = visible.WithDeepSky(deepSky, ProjectedSceneDeepSkySelection.Default);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(400, 300),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);
    }
}
