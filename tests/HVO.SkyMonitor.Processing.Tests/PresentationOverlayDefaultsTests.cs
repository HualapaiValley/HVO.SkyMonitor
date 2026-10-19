using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// #1383: the overlay defaults are the ones #1035 replaced (as at 08b79fb7^), with solid constellation lines and the
/// image circle at full opacity and their original widths. The cardinal badge, the corner-text panel and the crosshair
/// marker from #1035 stay.
/// LogicHost draws these payloads with both the grouped SVG renderer and the raster compositor, so both are checked.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationOverlayDefaultsTests
{
    private const int Width = 1936;
    private const int Height = 1216;
    private static readonly PresentationColor Marker = new(144, 144, 144);
    private static readonly PresentationColor White = new(255, 255, 255);
    private static readonly PresentationColor Constellation = new(96, 160, 255);
    private static readonly PresentationColor ImageCircle = new(96, 96, 96);
    private static readonly PresentationColor CloudMask = new(255, 64, 32);
    private static readonly PresentationBackplateV2 CardinalBadge = new(new(2, 8, 14), 840_000, new(116, 209, 255), 5, 0,
        new(1_700, 380_000, MinimumWidthMilliPixels: 38_000, MinimumHeightMilliPixels: 25_000, CornerRadiusMilliPixels: 5_000));
    private static readonly string[] WhiteFill = ["#FFFFFF"];

    [TestMethod]
    public async Task SceneLayersUseTheOriginalColoursWithSolidLinesBadgesAndCrosshairs()
    {
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(await SceneAsync().ConfigureAwait(false));

        Assert.IsNotEmpty(groups.StarAnnotations.Markers);
        foreach (var marker in groups.StarAnnotations.Markers)
        {
            Assert.AreEqual(Marker, marker.Color);
            Assert.IsTrue(marker.Crosshair);
        }
        Assert.IsNotEmpty(groups.StarAnnotations.TextBlocks);
        foreach (var label in groups.StarAnnotations.TextBlocks)
        {
            Assert.AreEqual(White, label.Color);
            Assert.AreEqual(White, label.Appearance!.Body.Color);
        }
        Assert.IsNotEmpty(groups.Constellations.Segments);
        foreach (var segment in groups.Constellations.Segments)
        {
            Assert.AreEqual(Constellation, segment.Color);
            Assert.IsNull(segment.Stroke, "Constellation lines are solid at full stroke opacity.");
            Assert.AreEqual(1, segment.Thickness);
        }
        var circle = groups.ImageCircle.Ellipses.Single();
        Assert.AreEqual(ImageCircle, circle.Color);
        Assert.IsNull(circle.Stroke, "The image circle is drawn at full opacity.");
        Assert.IsNull(circle.ThicknessMilliPixels, "The image circle keeps the renderers' 1 px default.");
        Assert.HasCount(4, groups.CardinalDirections.TextBlocks);
        foreach (var cardinal in groups.CardinalDirections.TextBlocks)
        {
            Assert.AreEqual(White, cardinal.Color);
            Assert.AreEqual(White, cardinal.Appearance!.Body.Color);
            Assert.AreEqual(CardinalBadge, cardinal.Backplate);
        }
    }

    [TestMethod]
    public void CornerTextIsWhiteOnTheKeptDarkPanel()
    {
        var payload = PresentationLayerProducers.FromMetadataFacts(Facts(), Width, Height);

        Assert.HasCount(4, payload.TextBlocks);
        foreach (var block in payload.TextBlocks)
        {
            Assert.AreEqual(White, block.Color);
            Assert.AreEqual(White, block.Appearance!.Body.Color);
            Assert.AreEqual(White, block.Appearance.Heading!.Color);
            var panel = block.Backplate!;
            Assert.AreEqual(new PresentationColor(5, 10, 17), panel.Fill);
            Assert.AreEqual(780_000, panel.OpacityMillionths);
            Assert.AreEqual(new PresentationColor(88, 166, 255), panel.Border);
            Assert.AreEqual(2, panel.AccentPixels);
            Assert.IsTrue(panel.Style!.HeadingRule);
        }
    }

    [TestMethod]
    public void CloudMaskIsOrangeRedAgain()
    {
        var groups = PresentationLayerProducers.FromCloudAssessmentGroups(CloudAssessment(), Width, Height);

        Assert.AreEqual(CloudMask, groups.Mask.TileMask!.Color);
        Assert.AreEqual(White, groups.Labels.TextBlocks.Single().Color);
    }

    [TestMethod]
    public async Task SvgAndRasterRenderersDrawTheSameRestoredColours()
    {
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(await SceneAsync().ConfigureAwait(false));
        var payloads = new (string Kind, PresentationLayerPayloadV1 Payload, PresentationColor Color)[]
        {
            ("scene-image-circle", groups.ImageCircle, ImageCircle),
            ("scene-constellations", groups.Constellations, Constellation),
            ("scene-annotation", groups.StarAnnotations with { TextBlocks = [] }, Marker),
            ("scene-cardinals", groups.CardinalDirections, White),
            ("environment", PresentationLayerProducers.FromMetadataFacts(Facts(), Width, Height), White),
            ("cloud-mask", PresentationLayerProducers.FromCloudAssessmentGroups(CloudAssessment(), Width, Height, lineThickness: 2).Mask, CloudMask)
        };
        var compatibility = new PresentationCompatibilityDescriptor(Width, Height, new string('D', 64), new string('E', 64));
        var source = new PresentationProductReference(Guid.NewGuid(), new string('A', 64), "image/png", compatibility);
        var layers = payloads.Select((item, index) => LayeredPresentationJson.CreateLayer(item.Kind,
            source with { ArtifactId = Guid.NewGuid() }, null,
            PresentationCoordinateSpace.ScenePixels, PresentationLayerCompositor.AlgorithmVersion,
            PresentationLayerProducers.SceneProducerVersion, index, PresentationBlendMode.Normal, 1_000_000, true,
            JsonSerializer.SerializeToElement(new { item.Kind }))).ToArray();
        var svg = XDocument.Parse(System.Text.Encoding.UTF8.GetString(GroupedSvgPresentationRenderer.Render(
            LayeredPresentationJson.CreateManifest(source, null, layers),
            payloads.Select(static item => item.Payload).ToArray(), new string('F', 64)).Svg.Span));

        var ellipse = Elements(svg, "scene-image-circle", "ellipse").Single();
        Assert.AreEqual("#606060", (string?)ellipse.Attribute("stroke"));
        Assert.IsNull(ellipse.Attribute("stroke-opacity"));
        Assert.IsNull(ellipse.Attribute("stroke-width"));
        var lines = Elements(svg, "scene-constellations", "line");
        Assert.IsNotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.AreEqual("#60A0FF", (string?)line.Attribute("stroke"));
            Assert.AreEqual("1", (string?)line.Attribute("stroke-width"));
            Assert.IsNull(line.Attribute("stroke-dasharray"));
            Assert.IsNull(line.Attribute("stroke-opacity"));
        }
        var markers = Elements(svg, "scene-annotation", "circle").Concat(Elements(svg, "scene-annotation", "path")).ToArray();
        Assert.IsNotEmpty(markers);
        Assert.IsTrue(markers.All(static marker => (string?)marker.Attribute("stroke") == "#909090"));
        foreach (var (kind, plate) in new[] { ("scene-cardinals", "#02080E"), ("environment", "#050A11") })
        {
            CollectionAssert.AreEquivalent(WhiteFill,
                Elements(svg, kind, "path").Select(static path => (string?)path.Attribute("fill")).Distinct().ToArray(), kind);
            Assert.Contains(plate, Elements(svg, kind, "rect").Select(static rect => (string?)rect.Attribute("fill")).ToArray());
        }
        Assert.AreEqual("#FF4020", (string?)Elements(svg, "cloud-mask", "path").Single().Attribute("stroke"));

        // Each layer alone over black: its brightest pixel is its own colour, scaled only by antialiased coverage.
        var layout = new ImageLayout(Width, Height, CameraPixelFormat.Rgb24, Width * 3);
        foreach (var (kind, payload, color) in payloads)
        {
            var brightest = Brightest(PresentationLayerCompositor.Composite(layout, new byte[Width * Height * 3],
                [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]));
            var coverage = (double)Math.Max(brightest.Red, Math.Max(brightest.Green, brightest.Blue)) /
                Math.Max(color.Red, Math.Max(color.Green, color.Blue));
            Assert.IsGreaterThan(0.5, coverage, kind);
            Assert.IsLessThanOrEqualTo(2, Math.Abs(brightest.Red - color.Red * coverage), kind);
            Assert.IsLessThanOrEqualTo(2, Math.Abs(brightest.Green - color.Green * coverage), kind);
            Assert.IsLessThanOrEqualTo(2, Math.Abs(brightest.Blue - color.Blue * coverage), kind);
        }
    }

    private static XElement[] Elements(XDocument svg, string kind, string name) => svg.Descendants()
        .Single(group => group.Name.LocalName == "g" && (string?)group.Attribute("data-layer-kind") == kind)
        .Descendants().Where(element => element.Name.LocalName == name).ToArray();

    private static PresentationColor Brightest(byte[] pixels)
    {
        var best = default(PresentationColor);
        for (var offset = 0; offset < pixels.Length; offset += 3)
            if (pixels[offset] + pixels[offset + 1] + pixels[offset + 2] > best.Red + best.Green + best.Blue)
                best = new(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
        return best;
    }

    private static PresentationMetadataFactsV1 Facts() => new(new string('B', 64),
        ["CAMERA", "2026-08-25 00:00:00 UTC"], ["EXPOSURE", "20 s | Gain 0"], ["ROLLING STACK", "3 frames"], ["SITE", "Clear"]);

    private static async Task<ProjectedSceneV1> SceneAsync()
    {
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new("center", "CENTER", siderealHours, 0, 1, HipparcosId: "1"),
            new("near", "NEAR", (siderealHours + 1) % 24, 10, 1.5, HipparcosId: "2")
        ]);
        var topology = new InMemoryConstellationTopology([new ConstellationSegment("TST", "1", "2")],
            new ConstellationTopologyMetadata("fixture-topology", "1", new Uri("https://example.test/topology"),
                new string('D', 64), "test", "fixture-v1"));
        var visible = await new VisibleSceneBuilder(catalog, topology).BuildAsync(new VisibleSceneRequest(utc,
            new ObserverLocation(0, 0, 0), new ProjectionContext(ProjectionModel.EquidistantFisheye,
                Width / 2, Height / 2, 568, 568, Width, Height, ProjectionAperture.Circular,
                ImageCircleRadiusPixels: 595.84, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "fisheye-v1", constellationIds: ["TST"])).ConfigureAwait(false);
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            new ProjectedSceneImageTransformV1(ProjectedSceneImageTransformV1.CurrentSchemaVersion,
                Width, Height, 0, 0, Width, Height, 1, 1, false, false, ProjectedSceneQuarterRotation.Degrees0, Width, Height),
            new ProjectedSceneSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), new string('A', 64)),
            "calibration-v1", "fisheye-v1");
    }

    private static CloudAssessmentV1 CloudAssessment() => new(
        CloudAssessmentV1.CurrentSchemaVersion, CloudAssessmentStatus.Quantified, CloudAssessmentQuality.Degraded,
        [CloudAssessmentReasonCodes.EnvironmentMissing], 500_000, 750_000, new CloudAssessmentGridV1(2, 1, 750_000, 2, 1, 8, 4),
        [new(0, 0, 0, 0, 2, 2, 4, 4, 0, 1_000_000, false), new(1, 0, 2, 0, 2, 2, 4, 4, 0, 500_000, true)],
        new CloudAssessmentMaskV1(CloudAssessmentMaskV1.RowMajorLsbFirst, 2, 1, new byte[] { 0b0000_0010 }),
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"), FrameArtifactRole.Calibrated, "linear-v1", new string('A', 64)),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"), FrameArtifactRole.Calibrated, "clear-v1", new string('B', 64)),
        new CloudAssessmentCalibrationV1(0, ushort.MaxValue, ushort.MaxValue, "calibration", "mask", "sensor", "processing"),
        new CloudAssessmentEnvironmentV1(CloudAssessmentEnvironmentV1.CurrentSchemaVersion, CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Missing, null, null, false),
        new string('C', 64), [new ProcessingAlgorithmIdentity("cloud-transmission", "v1")]);
}
