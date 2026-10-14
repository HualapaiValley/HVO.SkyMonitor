using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class ProjectedSceneAnnotationTests
{
    [TestMethod]
    [DataRow(32, 0)]
    [DataRow(31, 128)]
    public async Task FullCircleCounterexamplesMaterializeWithinTheFinalLayerPrimitiveBudget(int circles, int stars)
    {
        const int Size = 1024;
        var ra = AstronomyTime.LocalMeanSiderealDegrees(CuratedUtc, 0) / 15;
        var starRows = Enumerable.Range(0, stars).Select(index => new CelestialCatalogObject(
            $"S{index:D3}", $"Named{index:D3}", ra, 30, 1)).ToArray();
        var request = new VisibleSceneRequest(CuratedUtc, new(30, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 512, 512, 20_000, 20_000, Size, Size,
                ProjectionAperture.Rectangular), new CatalogQuery(6, 128),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64)));
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(starRows)).BuildAsync(request).ConfigureAwait(false);
        var objects = Enumerable.Range(1, circles).Select(index => new DeepSkyObject(
            $"NGC{index:D4}", $"NGC {index}", $"M{index}", "OCl", ra, 30, "And",
            120, 120, null, null, null, null, null, index, null, null, null)).ToArray();
        var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
            new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
            "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
            "b-mag-per-square-arcsecond-within-25-mag-isophote");
        var aliases = objects.SelectMany(static item => new[]
        {
            new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation),
            new DeepSkyAlias(item.DisplayName, item.Id, DeepSkyAliasKinds.Messier)
        });
        visible = visible.WithCuratedDeepSky(new DeepSkyCatalog(semantics, objects, aliases, [], []), new(128, 32, 0, 1, .1));
        Assert.AreEqual(circles, visible.ResolvedFootprints.Count);
        Assert.IsTrue(visible.ResolvedFootprints.All(static footprint => footprint.Parts.Single().Closed &&
            footprint.Parts.Single().Points.Count == 128), "Exercise the reviewer's exact 128-segment circles.");
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Size, Size), new(CaptureId, Guid.NewGuid(), new string('D', 64)), "rig-v1", "cal-v1");
        var standalone = PresentationDeepSkyLayerProducer.Create(scene, new(MaximumLabels: 0));
        Assert.IsTrue(standalone.Objects.Any(static item => item.Drawing != PresentationDeepSkyDrawing.Footprint) || stars > 0,
            "The standalone 32-circle case must fall back or omit geometry to reserve its caption.");
        var grouped = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene, includeLabels: false,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false).StarAnnotations;
        Assert.AreEqual(stars, grouped.Markers.Count);
        Assert.AreEqual(2, grouped.TextBlocks.Sum(static block => block.Lines.Count));
        var primitiveCount = grouped.Markers.Count + grouped.Segments.Count + grouped.Ellipses.Count +
            grouped.TextBlocks.Sum(static block => block.Lines.Count);
        Assert.IsTrue(primitiveCount <= PresentationLayerCompositor.MaximumLayerPrimitives);
        Assert.IsTrue(grouped.Segments.Count < circles * 128, "Caption and stellar primitives share the final layer's budget.");
        var rebuilt = PresentationLayerProducers.FromProjectedSceneGroupsV2(
            ProjectedSceneJson.Parse(ProjectedSceneJson.Serialize(scene)).Scene!, includeLabels: false,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false).StarAnnotations;
        CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(grouped), PresentationLayerPayloadJson.Serialize(rebuilt));
        var layout = new ImageLayout(Size, Size, CameraPixelFormat.Mono8, Size);
        var raw = new byte[Size * Size];
        var standaloneOutput = PresentationLayerCompositor.Composite(layout, raw,
            [new(standalone.Payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.IsTrue(standaloneOutput.Any(static pixel => pixel != 0));
        var output = PresentationLayerCompositor.Composite(layout, raw,
            [new(grouped, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.AreEqual(raw.Length, output.Length);
        Assert.IsTrue(output.Any(static pixel => pixel != 0), "The final payload is actually rasterized, including its caption.");
        Assert.IsTrue(raw.All(static pixel => pixel == 0));
    }
}
