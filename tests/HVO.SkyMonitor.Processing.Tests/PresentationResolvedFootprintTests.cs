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
    public async Task MarkersOffLeaveNoOutline()
    {
        var scene = await SceneAsync(4000).ConfigureAwait(false);
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeMarkers: false,
            includeConstellations: false);
        Assert.IsEmpty(groups.StarAnnotations.Markers);
        Assert.IsEmpty(groups.StarAnnotations.Segments);
    }

    private static async Task<ProjectedSceneV1> SceneAsync(double focalLengthPixels)
    {
        // A first-magnitude star 0.24 degrees below the Sun's centre, inside its 0.25 degree limb.
        var inside = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(new AltAzPoint(59.76, 0), Utc, 0, 0), Utc);
        var projection = new ProjectionContext(ProjectionModel.Perspective, 200, 150, focalLengthPixels,
            focalLengthPixels, 400, 300, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: 60, BoresightAzimuthDegrees: 0);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("inside", "INSIDE", inside.RightAscensionHours, inside.DeclinationDegrees, 1)
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
