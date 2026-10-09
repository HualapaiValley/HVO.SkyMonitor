using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationResolvedFootprintTests
{
    private static readonly DateTimeOffset Utc = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ResolvedDiscIsOutlinedPaddedAndKeepsOtherLabelsOffIt()
    {
        var scene = await SceneAsync(4000).ConfigureAwait(false);
        Assert.AreEqual(ProjectedSceneV1.ResolvedFootprintSchemaVersion, scene.SchemaVersion);
        var footprint = scene.ResolvedFootprints!.Single();
        var sun = scene.Objects.Single(static item => item.Id == "solar-system:Sun");
        var center = footprint.CenterPixel!.Value;

        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);

        // The disc is wider than the 6 px point marker, so its outline replaces the crosshair; the star keeps one.
        Assert.IsFalse(groups.StarAnnotations.Markers.Any(marker => marker.Center == sun.Pixel));
        Assert.HasCount(1, groups.StarAnnotations.Markers);
        var segments = groups.StarAnnotations.Segments;
        Assert.HasCount(footprint.Parts.Sum(static part => part.Points.Count), segments);
        var limb = footprint.Parts.SelectMany(static part => part.Points).Select(point => Distance(point, center)).ToArray();
        foreach (var segment in segments)
        {
            Assert.AreEqual(2, segment.Thickness);
            Assert.IsTrue(Distance(segment.From, center) >= limb.Min() + PresentationLayerProducers.ResolvedFootprintPaddingPixels - 1e-9);
            Assert.IsTrue(Distance(segment.From, center) <= limb.Max() + PresentationLayerProducers.ResolvedFootprintPaddingPixels + 1e-9);
        }

        // The Sun's label clears the padded outline; the star inside the disc loses its label to the disc.
        var sunLabel = groups.StarAnnotations.TextBlocks.Single(static block => block.Lines[0] == "Sun");
        Assert.IsTrue(sunLabel.Point.X >= footprint.Bounds.MaxX + PresentationLayerProducers.ResolvedFootprintPaddingPixels);
        Assert.IsFalse(groups.StarAnnotations.TextBlocks.Any(static block => block.Lines[0] == "INSIDE"));

        // The combined and legacy grouped payloads carry the same outline.
        CollectionAssert.IsSubsetOf(segments.ToArray(), PresentationLayerProducers.FromProjectedScene(scene).Segments.ToArray());
        CollectionAssert.AreEqual(segments.ToArray(),
            PresentationLayerProducers.FromProjectedSceneGroups(scene).AnnotationAndGeometry.Segments.ToArray());
    }

    [TestMethod]
    public async Task DiscWithinThePointMarkerKeepsTheCrosshairAndNoOutline()
    {
        // At f = 400 the 0.25 degree limb is 1.75 px, so the padded half-extent of 5.75 px fits the 6 px marker.
        var scene = await SceneAsync(400).ConfigureAwait(false);
        var sun = scene.Objects.Single(static item => item.Id == "solar-system:Sun");

        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);

        Assert.IsTrue(groups.StarAnnotations.Markers.Any(marker => marker.Center == sun.Pixel));
        Assert.IsEmpty(groups.StarAnnotations.Segments);
    }

    [TestMethod]
    public async Task DiscCentredBelowTheGeometricHorizonIsOutlinedAndNamedAtTheRasterAnchor()
    {
        var scene = await ProjectedSceneAnnotationTests.SceneAsync(0, new AltAzPoint(-.2, 0), .5).ConfigureAwait(false);
        Assert.IsFalse(scene.Objects.Any(static item => item.Id == "solar-system:Sun"));
        var footprint = scene.ResolvedFootprints!.Single();
        var anchor = ProjectedSceneAnnotation.FootprintAnchor(footprint, scene.ImageTransform);

        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);

        // Regression for #518 r0: the outline was drawn but the body went unnamed, unlike the raster annotation.
        // The horizon clips the limb into an open part, which has one segment fewer than it has points.
        Assert.IsTrue(footprint.Clipped);
        Assert.HasCount(footprint.Parts.Sum(static part => part.Points.Count - (part.Closed ? 0 : 1)),
            groups.StarAnnotations.Segments);
        Assert.IsEmpty(groups.StarAnnotations.Markers);
        var label = groups.StarAnnotations.TextBlocks.Single(static block => block.Lines[0] == "Sun");
        Assert.IsTrue(label.Point.X >= footprint.Bounds.MaxX + PresentationLayerProducers.ResolvedFootprintPaddingPixels);
        Assert.AreEqual(Math.Round(anchor.Y - 3 * label.Scale, MidpointRounding.AwayFromZero), label.Point.Y);
    }

    [TestMethod]
    public async Task ClippedDiscAtTheMarkerThresholdTakesTheSameMarkerAsTheRaster()
    {
        // Regression for #518 r1: an open limb 4.2 px wide, anchored at its visible bounds (100, 100). Unpadded half
        // extent plus padding is 6.1 px, over the 6 px marker, but the outline actually padded from the anchor is only
        // 5.99 px, so the raster draws the marker; presentation must decide on the same padded outline.
        var horizon = await ProjectedSceneAnnotationTests.SceneAsync(0, new AltAzPoint(-.2, 0), .5).ConfigureAwait(false);
        var footprint = horizon.ResolvedFootprints!.Single() with
        {
            CenterPixel = null,
            Clipped = true,
            Bounds = new ResolvedFootprintBounds(97.9, 99.5, 102.1, 100.5),
            Parts = [new ResolvedFootprintPart(false, [new PixelPoint(97.9, 100.5), new PixelPoint(100, 99.5),
                new PixelPoint(102.1, 100.5)])]
        };
        var edited = horizon with { ResolvedFootprints = [footprint] };
        var scene = edited with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(edited) };
        ProjectedSceneJson.Validate(scene);
        var sun = ProjectedSceneAnnotation.CreateObjects(scene, 2.5).Single(static item => item.Id == "solar-system:Sun");
        Assert.AreEqual(new PixelPoint(100, 100), sun.Pixel);

        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);

        Assert.AreEqual(new PixelPoint(100, 100), groups.StarAnnotations.Markers.Single().Center);
        Assert.IsEmpty(groups.StarAnnotations.Segments);
        var raster = AnnotationRenderer.AnnotateMono8(new byte[400 * 300], 400, 300, [sun], new PreviewTransform(1, 1));
        var marker = AnnotationRenderer.AnnotateMono8(new byte[400 * 300], 400, 300, [sun with { FootprintParts = null }],
            new PreviewTransform(1, 1));
        Assert.IsTrue(raster.Pixels.Span.SequenceEqual(marker.Pixels.Span), "The raster draws the point marker here.");
    }

    [TestMethod]
    public async Task MeasuredAssociationsGateStarLabelsAndLeaveTheDiscAndItsLabelUntouched()
    {
        // #518 x #526: the resolved Sun disc, an associated star inside it, an associated star below it and an
        // unassociated star below that. The disc's outline and label are independent of the measured product.
        var scene = await SceneAsync(4000, ("measured", "MEASURED", 59, 1.5), ("unmeasured", "UNMEASURED", 58.6, 1.8))
            .ConfigureAwait(false);
        var product = MeasuredStellarAssociationFixtures.AllEligible(scene);
        var unmeasured = product.Associations.Single(static item => item.CatalogId == "unmeasured");
        MeasuredStellarAssociationV1[] kept = [.. product.Associations.Where(static item => item.CatalogId != "unmeasured")
            .Select(static (item, index) => item with { DetectionIndex = index })];
        var associations = product with
        {
            Measurement = product.Measurement with { CandidateCount = kept.Length, DetectionCount = kept.Length },
            Associations = kept,
            UnmatchedPredictions = [new(unmeasured.CatalogId, unmeasured.Magnitude, unmeasured.ExpectedX, unmeasured.ExpectedY,
                MeasuredStellarAssociationReasonCodes.NoMeasuredSource)]
        };
        Assert.IsTrue(MeasuredStellarAssociationJson.Validate(associations).IsValid);
        string[] associated = ["inside", "measured"];
        CollectionAssert.AreEquivalent(associated, kept.Select(static item => item.CatalogId).ToArray());

        var measured = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false,
            associations: associations);
        var withoutProduct = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);

        string[] labelled = ["Sun", "MEASURED"];
        CollectionAssert.AreEquivalent(labelled,
            measured.StarAnnotations.TextBlocks.Select(static block => block.Lines[0]).ToArray(),
            "the disc keeps INSIDE unlabeled even though it is associated, and UNMEASURED fails closed");
        Assert.AreEqual("Sun", withoutProduct.StarAnnotations.TextBlocks.Single().Lines[0]);
        // The disc's outline, its label placement and every star marker are identical with and without the product.
        CollectionAssert.AreEqual(withoutProduct.StarAnnotations.Segments.ToArray(), measured.StarAnnotations.Segments.ToArray());
        Assert.IsNotEmpty(measured.StarAnnotations.Segments);
        CollectionAssert.AreEqual(withoutProduct.StarAnnotations.Markers.ToArray(), measured.StarAnnotations.Markers.ToArray());
        Assert.HasCount(3, measured.StarAnnotations.Markers);
        var sunLabel = measured.StarAnnotations.TextBlocks.Single(static block => block.Lines[0] == "Sun");
        Assert.AreEqual(withoutProduct.StarAnnotations.TextBlocks.Single().Point, sunLabel.Point);
        Assert.AreEqual(withoutProduct.StarAnnotations.TextBlocks.Single().Appearance, sunLabel.Appearance);
    }

    [TestMethod]
    public async Task MarkersOffLeaveNoOutline()
    {
        var scene = await SceneAsync(4000).ConfigureAwait(false);
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeMarkers: false,
            includeConstellations: false);
        Assert.IsEmpty(groups.StarAnnotations.Markers);
        Assert.IsEmpty(groups.StarAnnotations.Segments);
    }

    private static async Task<ProjectedSceneV1> SceneAsync(double focalLengthPixels,
        params (string Id, string Name, double AltitudeDegrees, double Magnitude)[] extraStars)
    {
        // A first-magnitude star 0.24 degrees below the Sun's centre, inside its 0.25 degree limb.
        var inside = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(59.76, 0), Utc, 0, 0), Utc);
        var extras = extraStars.Select(static star => (star, Position: EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(star.AltitudeDegrees, 0), Utc, 0, 0), Utc)))
            .Select(static item => new CelestialCatalogObject(item.star.Id, item.star.Name,
                item.Position.RightAscensionHours, item.Position.DeclinationDegrees, item.star.Magnitude));
        var projection = new ProjectionContext(ProjectionModel.Perspective, 200, 150, focalLengthPixels,
            focalLengthPixels, 400, 300, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 0);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("inside", "INSIDE", inside.RightAscensionHours, inside.DeclinationDegrees, 1),
            .. extras
        ]), null, new AstronomyEnginePlanetEphemeris()).BuildAsync(new VisibleSceneRequest(Utc,
            new ObserverLocation(0, 0, 0), projection, new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Sun])).ConfigureAwait(false);
        visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, Utc,
            new AltAzPoint(60, 0), .25, 0, 1, 0, 149600000)]);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(400, 300),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);
    }

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
