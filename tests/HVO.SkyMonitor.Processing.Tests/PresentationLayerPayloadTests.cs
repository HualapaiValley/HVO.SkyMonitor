using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PresentationLayerPayloadTests
{
    [TestMethod]
    public void PayloadRoundTripsCanonicallyWithStableContentIdentity()
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 640, 480,
            markers: [new(new(20.5, 30.25), 6, new(144, 144, 144))],
            segments: [new(new(0, 0), new(639, 479), 1, new(96, 160, 255))],
            textBlocks: [new(PresentationTextAnchor.TopRight, default, ["Cloud 12.3%"], 1, 4, 2, new(255, 255, 255))],
            tileMask: new(2, 2, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 5 }, 1, new(255, 64, 32)));
        var bytes = PresentationLayerPayloadJson.Serialize(payload);
        var parsed = PresentationLayerPayloadJson.Parse(bytes);

        Assert.IsTrue(parsed.IsValid, parsed.ErrorPath);
        Assert.AreEqual(payload.ContentIdentitySha256, parsed.Payload!.ContentIdentitySha256);
        CollectionAssert.AreEqual(bytes, PresentationLayerPayloadJson.Serialize(parsed.Payload));
        Assert.IsFalse(parsed.Payload.Markers is PresentationMarkerV1[]);
    }

    [TestMethod]
    public void ParserRejectsMalformedNonCanonicalIdentityAndBounds()
    {
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 10, 10);
        var json = Encoding.UTF8.GetString(PresentationLayerPayloadJson.Serialize(payload));
        var malformed = new[]
        {
            "null",
            json.Replace("\"widthPixels\":10", "\"widthPixels\":1e1", StringComparison.Ordinal),
            json.Replace("\"markers\":[]", "\"markers\":[],\"MARKERS\":[]", StringComparison.Ordinal),
            json.Replace(payload.ContentIdentitySha256, new string('F', 64), StringComparison.Ordinal),
            "{" + json[1..^1] + ",\"unknown\":true}"
        };
        foreach (var candidate in malformed)
            Assert.IsFalse(PresentationLayerPayloadJson.Parse(Encoding.UTF8.GetBytes(candidate)).IsValid, candidate);
        Assert.IsFalse(PresentationLayerPayloadJson.Parse(new byte[PresentationLayerPayloadJson.MaximumPayloadBytes + 1]).IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Create(new string('A', 64), 10, 10,
            markers: Enumerable.Repeat(new PresentationMarkerV1(default, 1, new()), PresentationLayerPayloadV1.MaximumMarkers + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Create(new string('A', 64), 10, 10,
            tileMask: new(2, 2, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 0xF5 }, 1, new())));
    }

    [TestMethod]
    public void MetadataProducerConsumesFactsWithoutAnyBasePixelParameter()
    {
        var facts = new PresentationMetadataFactsV1(new string('B', 64), ["Capture"], [], [], ["Clear"]);
        var payload = PresentationLayerProducers.FromMetadataFacts(facts, 320, 240);

        Assert.AreEqual(new string('B', 64), payload.SourceIdentitySha256);
        Assert.HasCount(4, payload.TextBlocks);
        Assert.IsFalse(typeof(PresentationLayerProducers).GetMethods().Any(method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(ReadOnlyMemory<byte>) || parameter.ParameterType == typeof(byte[]))));
    }

    [TestMethod]
    public void CornerTextScalesWithFrameWhilePreservingUnicode()
    {
        var facts = new PresentationMetadataFactsV1(new string('B', 64), ["Café"], [], [], ["Étoile"]);
        var small = PresentationLayerProducers.FromMetadataFacts(facts, 320, 240);
        var full = PresentationLayerProducers.FromMetadataFacts(facts, 4000, 3000);

        Assert.AreEqual("Café", full.TextBlocks[0].Lines[0]);
        Assert.AreEqual("Étoile", full.TextBlocks[3].Lines[0]);
        Assert.AreEqual(1, small.TextBlocks[0].Scale);
        Assert.IsGreaterThan(small.TextBlocks[0].Scale, full.TextBlocks[0].Scale);
        Assert.AreEqual(PresentationFont.FrameScale(4000, 3000), full.TextBlocks[0].Scale);
    }

    [TestMethod]
    public void CornerTextFitsItsOwnFrameQuadrantOrRejectsUnrenderableFacts()
    {
        var facts = new PresentationMetadataFactsV1(new string('B', 64),
            [new string('W', 24)], ["Right"], ["Bottom"], ["End"]);
        var payload = PresentationLayerProducers.FromMetadataFacts(facts, 800, 600);
        Assert.IsLessThan(PresentationFont.FrameScale(800, 600), payload.TextBlocks[0].Scale);
        using var font = PresentationFont.Create(payload.TextBlocks[0].Scale);
        var block = payload.TextBlocks[0];
        var (x, y) = PresentationFont.LineOrigin(block, 800, 600, font, block.Lines[0], 0);
        Assert.IsLessThanOrEqualTo(800d / 3, PresentationFont.LineBounds(font, block.Lines[0], x, y).Right);

        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerProducers.FromMetadataFacts(facts, 40, 30));
    }

    [TestMethod]
    public async Task FullFrameLabelsPreferBrightestAndKeepMarkersWhenLabelsCollide()
    {
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([
            new CelestialCatalogObject("z-bright", "BRIGHT", siderealHours, 0, 0),
            new CelestialCatalogObject("a-dim", "DIM", siderealHours, 0, 1)
        ])).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 400, 300, 400, 400, 800, 600,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(800, 600),
            new ProjectedSceneSource(Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);
        var first = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false);
        var repeat = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false);

        Assert.HasCount(2, first.StarAnnotations.Markers);
        Assert.HasCount(1, first.StarAnnotations.TextBlocks);
        Assert.AreEqual("BRIGHT", first.StarAnnotations.TextBlocks[0].Lines[0]);
        Assert.AreEqual(first.StarAnnotations.ContentIdentitySha256, repeat.StarAnnotations.ContentIdentitySha256);
        Assert.IsTrue(PresentationLayerPayloadJson.Parse(PresentationLayerPayloadJson.Serialize(first.StarAnnotations)).IsValid);
    }

    [TestMethod]
    public async Task FullFrameStarLabelsAvoidAllMetadataCornersAndCardinals()
    {
        const int width = 1936, height = 1216;
        var utc = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var siderealHours = AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("tl", "TOP LEFT", (siderealHours - 40d / 15 + 24) % 24, 30, 0),
            new CelestialCatalogObject("tr", "TOP RIGHT", (siderealHours + 40d / 15) % 24, 30, 0.1),
            new CelestialCatalogObject("bl", "BOTTOM LEFT", (siderealHours - 40d / 15 + 24) % 24, -30, 0.2),
            new CelestialCatalogObject("br", "BOTTOM RIGHT", (siderealHours + 40d / 15) % 24, -30, 0.3),
            new CelestialCatalogObject("center", "CENTER", siderealHours, 0, 1)
        ]);
        var visible = await new VisibleSceneBuilder(catalog).BuildAsync(new VisibleSceneRequest(utc,
            new ObserverLocation(0, 0, 0), new ProjectionContext(ProjectionModel.EquidistantFisheye,
                 width / 2, height / 2, 568, 568, width, height, ProjectionAperture.Circular,
                 ImageCircleRadiusPixels: 595.84, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1")).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(width, height),
            new ProjectedSceneSource(Guid.NewGuid(), Guid.NewGuid(), new string('A', 64)),
            "calibration-v1", visible.Request.ProjectionVersion);
        var groups = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);
        var repeat = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeConstellations: false);

        Assert.HasCount(5, groups.StarAnnotations.Markers);
        CollectionAssert.AreEquivalent(new[] { "N", "E", "S", "W" },
            groups.CardinalDirections.TextBlocks.Select(static block => block.Lines[0]).ToArray());
        Assert.HasCount(1, groups.StarAnnotations.TextBlocks);
        Assert.AreEqual("CENTER", groups.StarAnnotations.TextBlocks[0].Lines[0]);
        Assert.AreEqual(groups.StarAnnotations.ContentIdentitySha256, repeat.StarAnnotations.ContentIdentitySha256);
    }

    [TestMethod]
    public void GroupedSvgUsesEmbeddedFontOutlinesForUnicodeText()
    {
        var compatibility = new PresentationCompatibilityDescriptor(800, 600, new string('D', 64), new string('E', 64));
        var source = new PresentationProductReference(Guid.NewGuid(), new string('A', 64), "image/png", compatibility);
        using var options = JsonDocument.Parse("{}");
        var layer = LayeredPresentationJson.CreateLayer("labels", source, new string('C', 64),
            PresentationCoordinateSpace.ScenePixels, GroupedSvgPresentationRenderer.RendererVersion, "style-v1",
            0, PresentationBlendMode.Normal, 1_000_000, true, options.RootElement);
        var manifest = LayeredPresentationJson.CreateManifest(source, new string('C', 64), [layer]);
        var payload = PresentationLayerPayloadJson.Create(new string('C', 64), 800, 600,
            textBlocks: [new(PresentationTextAnchor.Point, new(30, 30), ["Étoile"], 8, 0, 0, new(255, 255, 255))]);

        var svg = Encoding.UTF8.GetString(GroupedSvgPresentationRenderer.Render(manifest, [payload], new string('F', 64)).Svg.Span);

        StringAssert.Contains(svg, "<path d=", StringComparison.Ordinal);
        StringAssert.Contains(svg, "stroke-width=\"4\"", StringComparison.Ordinal);
        Assert.IsFalse(svg.Contains("font-family", StringComparison.Ordinal));
        Assert.IsFalse(svg.Contains("<text", StringComparison.Ordinal));
        Assert.IsFalse(svg.Contains("Étoile", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GroupedSvgRejectsAggregateDenseGlyphPathsBeforeUnboundedOutput()
    {
        var compatibility = new PresentationCompatibilityDescriptor(800, 600, new string('D', 64), new string('E', 64));
        var source = new PresentationProductReference(Guid.NewGuid(), new string('A', 64), "image/png", compatibility);
        using var options = JsonDocument.Parse("{}");
        var layer = LayeredPresentationJson.CreateLayer("labels", source, new string('C', 64),
            PresentationCoordinateSpace.ScenePixels, GroupedSvgPresentationRenderer.RendererVersion, "style-v1",
            0, PresentationBlendMode.Normal, 1_000_000, true, options.RootElement);
        var manifest = LayeredPresentationJson.CreateManifest(source, new string('C', 64), [layer]);
        var block = new PresentationTextBlockV1(PresentationTextAnchor.TopLeft, default,
            Enumerable.Repeat(new string('W', 64), 8).ToArray(), 16, 0, 0, new(255, 255, 255));
        var payload = PresentationLayerPayloadJson.Create(new string('C', 64), 800, 600,
            textBlocks: Enumerable.Repeat(block, PresentationLayerPayloadV1.MaximumTextBlocks));

        Assert.ThrowsExactly<InvalidDataException>(() =>
            GroupedSvgPresentationRenderer.Render(manifest, [payload], new string('F', 64)));
    }

    [TestMethod]
    public void CreateFreezesCallerCollectionsAndRejectsOversizedCanonicalPayload()
    {
        var markers = new List<PresentationMarkerV1> { new(new(1, 1), 1, new()) };
        var lines = new List<string> { "FIRST" };
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 100, 100, markers,
            textBlocks: [new(PresentationTextAnchor.TopLeft, default, lines, 1, 0, 0, new())]);
        markers[0] = new(new(99, 99), 1, new());
        lines[0] = "CHANGED";

        Assert.AreEqual(new HVO.SkyMonitor.Astronomy.PixelPoint(1, 1), payload.Markers[0].Center);
        Assert.AreEqual("FIRST", payload.TextBlocks[0].Lines[0]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PresentationMarkerV1>)payload.Markers).Clear());

        var segments = Enumerable.Range(0, PresentationLayerPayloadV1.MaximumSegments)
            .Select(index => new PresentationSegmentV1(new(index, index), new(index + 1, index + 1), 1, new()))
            .ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerPayloadJson.Create(
            new string('A', 64), 65_536, 4_096, segments: segments));
    }

    [TestMethod]
    public async Task FrozenPayloadSerializationIsStableWhileCallerCollectionsMutateConcurrently()
    {
        var callerMarkers = Enumerable.Range(0, 1_000)
            .Select(index => new PresentationMarkerV1(new(index % 100, index / 100), 1, new())).ToList();
        var payload = PresentationLayerPayloadJson.Create(new string('A', 64), 100, 100, callerMarkers);
        var expected = PresentationLayerPayloadJson.Serialize(payload);

        var mutation = Task.Run(() =>
        {
            for (var index = 0; index < callerMarkers.Count; index++)
                callerMarkers[index] = callerMarkers[index] with { Center = new(99, 99) };
            callerMarkers.Clear();
        });
        var serializations = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            PresentationLayerPayloadJson.Serialize(payload))).ToArray();
        await Task.WhenAll(serializations.Append(mutation)).ConfigureAwait(false);

        foreach (var serialization in serializations)
            CollectionAssert.AreEqual(expected, await serialization.ConfigureAwait(false));
    }
}
