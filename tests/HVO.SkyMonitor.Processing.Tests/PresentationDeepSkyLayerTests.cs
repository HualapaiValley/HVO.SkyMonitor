using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationDeepSkyLayerTests
{
    private const int Frame = 4_000;
    private static readonly DateTimeOffset Utc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 0);
    private static readonly PresentationStrokeV2 GeometryDash = new(6, 4, 900_000);
    private static readonly PresentationStrokeV2 UnknownExtentDash = new(2, 3, 900_000);
    private static readonly string[] FeaturedLabels = ["M101", "NGC 2"];
    private static readonly string[] CatalogLabels = ["M101", "NGC 1", "NGC 2"];

    [TestMethod]
    public async Task AtItsPrimitiveCapTheLayerDegradesInPriorityOrderAndRepeatsByteForByte()
    {
        var andromeda = At(60, 30);
        var filler = Enumerable.Range(0, 297).Select(index => Galaxy(
            $"NGC{1000 + index:D4}", At(20 + 5 * (index / 23), index % 23 * 360d / 23), 10, null, null)).ToArray();
        var scene = await SceneAsync(
            [
                Galaxy("NGC0224", andromeda, 190, 60, 35, messier: 31, common: "Andromeda Galaxy"),
                Galaxy("NGC5457", At(70, 120), 40, 38, 28, messier: 101),
                .. filler
            ],
            [Circle("NGC0224", andromeda, 1.5, 4_200)]).ConfigureAwait(false);
        Assert.AreEqual(299, scene.DeepSky!.Objects.Count);
        Assert.AreEqual(DeepSkyRepresentation.Outline,
            scene.DeepSky.Objects.Single(static item => item.CatalogObjectId == "NGC0224").Representation);

        var layer = PresentationDeepSkyLayerProducer.Create(scene);
        var again = PresentationDeepSkyLayerProducer.Create(scene);

        // The sourced outline needs more segments than the whole layer budget, so it falls back to its class glyph;
        // the ellipse still fits; the lowest-priority glyphs are omitted rather than the budget being exceeded.
        var byId = layer.Objects.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        Assert.AreEqual(PresentationDeepSkyDrawing.Glyph, byId["deep-sky:NGC0224"].Drawing);
        Assert.AreEqual(DeepSkyRepresentation.Outline, byId["deep-sky:NGC0224"].Representation);
        Assert.AreEqual(PresentationDeepSkyDrawing.Footprint, byId["deep-sky:NGC5457"].Drawing);
        var omitted = layer.Objects.Count(static item => item.Drawing == PresentationDeepSkyDrawing.Omitted);
        Assert.IsGreaterThan(0, omitted);
        Assert.IsTrue(layer.Objects.SkipWhile(static item => item.Drawing != PresentationDeepSkyDrawing.Omitted)
            .All(static item => item.Drawing == PresentationDeepSkyDrawing.Omitted), "Omission follows priority order.");
        var ordered = scene.DeepSky.Objects.ToList();
        ordered.Sort(ProjectedDeepSkyObject.ComparePriority);
        CollectionAssert.AreEqual(ordered.Select(static item => item.Id).ToArray(),
            layer.Objects.Select(static item => item.Id).ToArray());

        var payload = layer.Payload;
        var style = new PresentationDeepSkyStyleV1();
        Assert.IsLessThanOrEqualTo(PresentationDeepSkyLayerProducer.MaximumPrimitives - style.MaximumLabels, payload.Segments.Count);
        Assert.IsGreaterThan(PresentationDeepSkyLayerProducer.MaximumPrimitives - style.MaximumLabels - 16, payload.Segments.Count,
            "Only the remainder too small for one more glyph is left unspent.");
        Assert.IsLessThanOrEqualTo(PresentationDeepSkyLayerProducer.MaximumPrimitives, payload.Segments.Count + payload.TextBlocks.Count);
        Assert.IsEmpty(payload.Markers);
        Assert.IsEmpty(payload.Ellipses);
        CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(payload), PresentationLayerPayloadJson.Serialize(again.Payload));
        Assert.AreEqual(scene.SceneIdentitySha256, payload.SourceIdentitySha256);
    }

    [TestMethod]
    public async Task FeaturedObjectsAreLabelledByDefaultAndCatalogDesignationsOnlyOnRequest()
    {
        var scene = await SceneAsync(
            [
                Galaxy("NGC5457", At(60, 90), 40, 38, 28, messier: 101),
                Galaxy("NGC0001", At(50, 200), 10, null, null),
                Galaxy("NGC0002", At(50, 300), 10, null, null, common: "Fixture Cluster", type: "OCl")
            ],
            disputed: [new DeepSkyAlias("M102", "NGC5457", DeepSkyAliasKinds.Disputed)]).ConfigureAwait(false);

        var featured = PresentationDeepSkyLayerProducer.Create(scene);
        var catalog = PresentationDeepSkyLayerProducer.Create(scene, new PresentationDeepSkyStyleV1(LabelCatalogObjects: true));
        var none = PresentationDeepSkyLayerProducer.Create(scene, new PresentationDeepSkyStyleV1(MaximumLabels: 0));

        CollectionAssert.AreEquivalent(FeaturedLabels, Labels(featured));
        CollectionAssert.AreEquivalent(CatalogLabels, Labels(catalog));
        Assert.IsEmpty(none.Payload.TextBlocks);
        Assert.AreEqual("M101", featured.Objects.Single(static item => item.Id == "deep-sky:NGC5457").Label);
        Assert.IsNull(featured.Objects.Single(static item => item.Id == "deep-sky:NGC0001").Label);
        // Labels never change geometry.
        CollectionAssert.AreEqual(featured.Payload.Segments.ToArray(), catalog.Payload.Segments.ToArray());
        CollectionAssert.AreEqual(featured.Payload.Segments.ToArray(), none.Payload.Segments.ToArray());

        // The disputed Messier 102 is an alias of M101 only: never a display name, label or drawn object.
        var sceneJson = Encoding.UTF8.GetString(ProjectedSceneJson.Serialize(scene));
        Assert.DoesNotContain("M102", sceneJson, StringComparison.Ordinal);
        foreach (var layer in new[] { featured, catalog })
        {
            Assert.IsFalse(layer.Payload.TextBlocks.SelectMany(static block => block.Lines)
                .Any(static line => line.Contains("M102", StringComparison.Ordinal)));
            Assert.HasCount(3, layer.Objects);
        }
    }

    [TestMethod]
    public async Task CatalogGeometryAndLabelsStayApartFromTheStarAndMeasuredStyles()
    {
        var scene = await SceneAsync(
            [
                Galaxy("NGC0224", At(60, 30), 190, 60, 35, messier: 31, common: "Andromeda Galaxy"),
                Galaxy("NGC0001", At(50, 200), null, null, null)
            ]).ConfigureAwait(false);

        var layer = PresentationDeepSkyLayerProducer.Create(scene).Payload;

        // Every catalog primitive is dashed in the one layer colour; the label uses the regular face in that colour,
        // never the bold face or the colours of the star names, markers, constellations, image circle, cardinal
        // directions or tile mask.
        var color = new PresentationColor(255, 196, 120);
        Assert.IsNotEmpty(layer.Segments);
        Assert.IsTrue(layer.Segments.All(segment => segment.Color == color && segment.Stroke is { DashPixels: > 0, GapPixels: > 0 }));
        Assert.IsEmpty(layer.Markers);
        Assert.IsEmpty(layer.Ellipses);
        var label = layer.TextBlocks.Single();
        Assert.AreEqual("M31", label.Lines.Single());
        Assert.IsNull(label.Backplate);
        Assert.AreEqual(PresentationFontFaceV3.MonoRegular, label.Appearance!.Body.Face);
        Assert.AreEqual(color, label.Appearance.Body.Color);
        Assert.AreEqual(color, label.Color);
        PresentationColor[] otherColors =
            [new(210, 184, 244), new(188, 140, 255), new(116, 209, 255), new(195, 236, 255), new(57, 197, 207)];
        Assert.DoesNotContain(color, otherColors);
    }

    [TestMethod]
    public async Task EveryObjectClassHasItsGlyphAndUnknownExtentsKeepTheirOwnDash()
    {
        var objects = DeepSkyObjectTypes.All.Select((type, index) =>
        {
            var at = At(35 + 4 * (index % 8), index * 360d / DeepSkyObjectTypes.All.Count);
            return type switch
            {
                DeepSkyObjectTypes.DoubleStar => new DeepSkyObject("M040", "M40", "M40", type, at.Ra, at.Dec, "UMa",
                    null, null, null, null, 8, null, null, 40, null, null, null),
                DeepSkyObjectTypes.Star => new DeepSkyObject("M073", "M73", "M73", type, at.Ra, at.Dec, "Aqr",
                    null, null, null, null, 9, null, null, 73, null, null, null),
                _ => Galaxy($"NGC{100 + index:D4}", at, null, null, null, type: type)
            };
        }).ToArray();
        var scene = await SceneAsync(objects).ConfigureAwait(false);
        Assert.HasCount(DeepSkyObjectTypes.All.Count, scene.DeepSky!.Objects);

        var layer = PresentationDeepSkyLayerProducer.Create(scene);

        // Segments are spent in decision order, so each object's glyph is the next run of segments.
        var offset = 0;
        var byId = scene.DeepSky.Objects.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        foreach (var decision in layer.Objects)
        {
            var item = byId[decision.Id];
            Assert.AreEqual(PresentationDeepSkyDrawing.Glyph, decision.Drawing, item.ObjectType);
            var stellar = DeepSkyObjectTypes.IsStellar(item.ObjectType);
            Assert.AreEqual(stellar ? DeepSkyRepresentation.StellarGlyph : DeepSkyRepresentation.UnknownExtentGlyph,
                decision.Representation, item.ObjectType);
            var count = GlyphSegments(item.ObjectType);
            var run = layer.Payload.Segments.Skip(offset).Take(count).ToArray();
            Assert.HasCount(count, run, item.ObjectType);
            var dash = stellar ? GeometryDash : UnknownExtentDash;
            Assert.IsTrue(run.All(segment => segment.Stroke! with { DashOffsetPixels = 0 } == dash), item.ObjectType);
            Assert.AreEqual(0d, run[0].Stroke!.DashOffsetPixels, item.ObjectType);
            offset += count;
        }
        Assert.AreEqual(layer.Payload.Segments.Count, offset);
        // M40 is drawn as a double star, two diamonds either side of its catalog position.
        var doubleStar = layer.Objects.TakeWhile(static item => item.Id != "deep-sky:M040")
            .Sum(item => GlyphSegments(byId[item.Id].ObjectType));
        var diamonds = layer.Payload.Segments.Skip(doubleStar).Take(8).ToArray();
        var center = byId["deep-sky:M040"].Pixel!.Value;
        Assert.IsTrue(diamonds.Take(4).All(segment => segment.From.X <= center.X + 0.01 && segment.To.X <= center.X + 0.01));
        Assert.IsTrue(diamonds.Skip(4).All(segment => segment.From.X >= center.X - 0.01 && segment.To.X >= center.X - 0.01));
    }

    [TestMethod]
    public async Task ALayerAtItsCapStaysWithinItsShareOfTheGroupedPresentationBounds()
    {
        // The widest placement the producer allows: every primitive a dashed line across a 4000 pixel frame.
        var filler = Enumerable.Range(0, 297).Select(index => Galaxy(
            $"NGC{1000 + index:D4}", At(20 + 5 * (index / 23), index % 23 * 360d / 23), 10, null, null)).ToArray();
        var scene = await SceneAsync(filler).ConfigureAwait(false);
        var layer = PresentationDeepSkyLayerProducer.Create(scene, new PresentationDeepSkyStyleV1(LabelCatalogObjects: true)).Payload;
        Assert.IsGreaterThan(PresentationDeepSkyLayerProducer.MaximumPrimitives - 64, layer.Segments.Count + layer.TextBlocks.Count);

        var compatibility = new PresentationCompatibilityDescriptor(Frame, Frame, new string('D', 64), new string('E', 64));
        var source = new PresentationProductReference(Guid.NewGuid(), layer.ContentIdentitySha256,
            PresentationLayerPayloadJson.MediaType, compatibility);
        var manifestLayer = LayeredPresentationJson.CreateLayer("deep-sky", source, scene.SceneIdentitySha256,
            PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
            PresentationDeepSkyLayerProducer.ProducerVersion, 12, PresentationBlendMode.Normal, 1_000_000, false,
            JsonSerializer.SerializeToElement(new { }));
        var rendered = GroupedSvgPresentationRenderer.Render(
            LayeredPresentationJson.CreateManifest(source, scene.SceneIdentitySha256, [manifestLayer]), [layer], new string('F', 64));

        // At its cap the layer takes at most three eighths of the grouped presentation's bytes and only its own
        // primitive budget of the element bound, one element per segment or plate-free label line, leaving the rest
        // to the layers that already shared them.
        Assert.IsLessThanOrEqualTo(GroupedSvgPresentationRenderer.MaximumSvgBytes * 3 / 8, rendered.Svg.Length);
        Assert.IsTrue(layer.TextBlocks.All(static block => block.Backplate is null && block.Lines.Count == 1));
        var document = XDocument.Parse(Encoding.UTF8.GetString(rendered.Svg.Span));
        var group = document.Root!.Elements().Single();
        Assert.AreEqual("none", group.Attribute("display")?.Value, "The layer is off until selected.");
        Assert.AreEqual(layer.Segments.Count, group.Elements(group.Name.Namespace + "line").Count());
        Assert.IsFalse(rendered.Layers.Single().EnabledByDefault);
    }

    [TestMethod]
    public async Task ASceneWithoutDeepSkyGivesAnEmptyLayerAndInvalidStylesAreRejected()
    {
        var scene = await SceneAsync([Galaxy("NGC0001", At(50, 200), 10, null, null)]).ConfigureAwait(false);
        var plain = scene with { DeepSky = null };

        var empty = PresentationDeepSkyLayerProducer.Create(await SceneAsync(null).ConfigureAwait(false));

        Assert.IsEmpty(empty.Objects);
        Assert.IsEmpty(empty.Payload.Segments);
        Assert.IsEmpty(empty.Payload.TextBlocks);
        Assert.ThrowsExactly<ArgumentException>(() => PresentationDeepSkyLayerProducer.Create(plain));
        foreach (var style in new PresentationDeepSkyStyleV1[]
        {
            new(MaximumLabels: -1), new(MaximumLabelCharacters: -1), new(LabelScale: 0), new(LabelScale: 9),
            new(MinimumGlyphRadius: 1), new(MinimumGlyphRadius: 33)
        })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PresentationDeepSkyLayerProducer.Create(scene, style));
    }

    private static string[] Labels(PresentationDeepSkyLayerV1 layer) =>
        layer.Payload.TextBlocks.Select(static block => block.Lines.Single()).ToArray();

    private static int GlyphSegments(string objectType) => objectType switch
    {
        "G" or "GPair" or "GTrpl" or "GGroup" or "OCl" or "*Ass" or "PN" => 16,
        "GCl" => 18,
        "Cl+N" or "DrkN" or "EmN" or "HII" or "Neb" or "RfN" or "SNR" or DeepSkyObjectTypes.Star => 4,
        DeepSkyObjectTypes.DoubleStar => 8,
        _ => 2
    };

    private static async Task<ProjectedSceneV1> SceneAsync(
        IReadOnlyList<DeepSkyObject>? objects,
        IReadOnlyList<DeepSkyOutline>? outlines = null,
        IReadOnlyList<DeepSkyAlias>? disputed = null)
    {
        var projection = new ProjectionContext(ProjectionModel.EquidistantFisheye, Frame / 2, Frame / 2, 1_200, 1_200,
            Frame, Frame, ProjectionAperture.Circular, 1_950, BoresightAltitudeDegrees: 90);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, new AstronomyEnginePlanetEphemeris())
            .BuildAsync(new VisibleSceneRequest(Utc, Site, projection, new CatalogQuery(6, 10),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('B', 64), "test", "v1"),
                default, projectionVersion: "fisheye-v1", solarSystemBodies: [])).ConfigureAwait(false);
        if (objects is not null)
        {
            var catalog = new DeepSkyCatalog(
                new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
                    new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned",
                    "J2000.0", "arcminute", "degrees-north-through-east-0-inclusive-to-180-exclusive",
                    "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote"),
                objects,
                objects.Select(static item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation))
                    .Concat(disputed ?? []),
                [], outlines ?? []);
            visible = visible.WithDeepSky(catalog, ProjectedSceneDeepSkySelection.Default);
        }
        return ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, visible,
            ProjectedSceneImageTransformV1.Identity(Frame, Frame),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);
    }

    private static DeepSkyObject Galaxy(
        string id,
        (double Ra, double Dec) at,
        double? major,
        double? minor,
        double? positionAngle,
        int? messier = null,
        string? common = null,
        string type = "G")
    {
        var designation = "NGC " + int.Parse(id[3..], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var displayName = messier is { } number ? "M" + number.ToString(CultureInfo.InvariantCulture) : designation;
        return new DeepSkyObject(id, designation, displayName, type, at.Ra, at.Dec, "UMa", major, minor, positionAngle,
            null, 10, null, null, messier, null, null, common);
    }

    /// <summary>A closed ring of the given number of vertices at a fixed angular radius about a J2000 centre.</summary>
    private static DeepSkyOutline Circle(string id, (double Ra, double Dec) center, double radiusDegrees, int vertices)
    {
        var start = new EquatorialPoint(center.Ra, center.Dec);
        var points = Enumerable.Range(0, vertices).Select(index =>
        {
            var point = Destination(start, 360d * index / vertices, radiusDegrees);
            return new DeepSkyOutlinePoint(point.RightAscensionHours * 15, point.DeclinationDegrees);
        }).ToList();
        points.Add(points[0]);
        return new DeepSkyOutline(id, DeepSkyOutline.WidestLevel, [new DeepSkyOutlineRing(points)]);
    }

    private static EquatorialPoint Destination(EquatorialPoint start, double positionAngleDegrees, double distanceDegrees)
    {
        var declination = start.DeclinationDegrees * Math.PI / 180;
        var angle = positionAngleDegrees * Math.PI / 180;
        var distance = distanceDegrees * Math.PI / 180;
        var end = Math.Asin(Math.Sin(declination) * Math.Cos(distance) +
            Math.Cos(declination) * Math.Sin(distance) * Math.Cos(angle));
        var hours = start.RightAscensionHours + Math.Atan2(Math.Sin(angle) * Math.Sin(distance) * Math.Cos(declination),
            Math.Cos(distance) - Math.Sin(declination) * Math.Sin(end)) * 12 / Math.PI;
        return new EquatorialPoint((hours % 24 + 24) % 24, end * 180 / Math.PI);
    }

    /// <summary>Returns the J2000 position whose geometric direction at the fixture instant is the given one.</summary>
    private static (double Ra, double Dec) At(double altitude, double azimuth)
    {
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(altitude, azimuth), Utc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, Utc);
        return (j2000.RightAscensionHours, j2000.DeclinationDegrees);
    }
}
