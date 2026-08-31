using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PresentationLayerCompositorTests
{
    [TestMethod]
    public void CompositeIsDeterministicOrderedAndDoesNotMutateInputs()
    {
        var source = Enumerable.Repeat((byte)20, 16 * 12 * 3).ToArray();
        var first = Payload(markers: [new(new PixelPoint(5, 5), 0, new(200, 100, 50))]);
        var second = Payload(markers: [new(new PixelPoint(5, 5), 0, new(10, 220, 100))]);
        var layers = new[]
        {
            new PresentationCompositorLayer(first, true, PresentationRasterBlendMode.Normal, 500_000),
            new PresentationCompositorLayer(second, true, PresentationRasterBlendMode.Screen, 750_000)
        };

        var result = PresentationLayerCompositor.Composite(Layout(), source, layers);
        var repeat = PresentationLayerCompositor.Composite(Layout(), source, layers);
        var reordered = PresentationLayerCompositor.Composite(Layout(), source, layers.Reverse().ToArray());
        var disabled = PresentationLayerCompositor.Composite(Layout(), source,
            [layers[0], layers[1] with { Enabled = false }]);

        CollectionAssert.AreEqual(Enumerable.Repeat((byte)20, source.Length).ToArray(), source);
        CollectionAssert.AreEqual(result, repeat);
        CollectionAssert.AreNotEqual(result, reordered);
        CollectionAssert.AreNotEqual(result, disabled);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(result)),
            Convert.ToHexString(SHA256.HashData(repeat)));
    }

    [TestMethod]
    public void CompositeReproducesRepresentativeAnnotationMetadataAndCloudSemantics()
    {
        var payload = Payload(
            markers: [new(new PixelPoint(4, 4), 2, new(144, 144, 144))],
            segments: [new(new PixelPoint(-10, 7), new PixelPoint(30, 7), 1, new(96, 160, 255))],
            ellipses: [new(new PixelPoint(8, 6), 6, 4, new(80, 80, 80))],
            text: [new(PresentationTextAnchor.TopLeft, default, ["A"], 1, 0, 0, new(255, 255, 255))],
            tileMask: new(2, 1, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 2 }, 1, new(255, 64, 32)));

        var result = PresentationLayerCompositor.Composite(Layout(), new byte[16 * 12 * 3],
            [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);

        Assert.AreEqual((byte)144, result[(4 * 16 + 6) * 3]);
        Assert.AreEqual((byte)96, result[(7 * 16 + 3) * 3]);
        Assert.AreEqual((byte)160, result[(7 * 16 + 3) * 3 + 1]);
        Assert.AreEqual((byte)255, result[(0 * 16 + 1) * 3]);
        Assert.AreEqual((byte)255, result[(1 * 16 + 8) * 3]);
        Assert.AreEqual((byte)64, result[(1 * 16 + 8) * 3 + 1]);
    }

    [TestMethod]
    public void CompositeHonorsCancellationBeforeAllocatingOutput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PresentationLayerCompositor.Composite(
            Layout(), new byte[16 * 12 * 3], [], cancellation.Token));
    }

    [TestMethod]
    public void LightenIsChannelMaximumAndCancellationIsObservedWithinPrimitiveLoops()
    {
        var basePixels = Enumerable.Repeat((byte)100, 16 * 12 * 3).ToArray();
        var payload = Payload(markers: [new(new PixelPoint(4, 4), 0, new(50, 150, 90))]);
        var result = PresentationLayerCompositor.Composite(Layout(), basePixels,
            [new(payload, true, PresentationRasterBlendMode.Lighten, 1_000_000)]);
        var offset = (4 * 16 + 4) * 3;
        CollectionAssert.AreEqual(new byte[] { 100, 150, 100 }, result[offset..(offset + 3)]);

        using var cancellation = new CancellationTokenSource();
        var manyMarkers = Payload(markers: Enumerable.Range(0, 10_000)
            .Select(index => new PresentationMarkerV1(new(index % 16, index % 12), 1, new())).ToArray());
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PresentationLayerCompositor.Composite(
            Layout(), basePixels, [new(manyMarkers, true, PresentationRasterBlendMode.Normal, 1_000_000)], cancellation.Token));
    }

    [TestMethod]
    public void OrderedTypedLayersExactlyMatchRepresentativeLegacyAnnotationAndWeatherChain()
    {
        const int width = 40;
        const int height = 30;
        var layout = new ImageLayout(width, height, CameraPixelFormat.Rgb24, width * 3);
        var source = Enumerable.Repeat((byte)20, layout.RequiredByteLength).ToArray();
        var objects = new[] { new ProjectedAnnotationObject("star", "STAR", new PixelPoint(12, 12)) };
        var segments = new[] { new ProjectedAnnotationSegment("ORI", new PixelPoint(-2, 20), new PixelPoint(35, 20)) };
        var projectionOverlay = new ProjectedAnnotationOverlay(
            new PixelPoint(20, 15), 10,
            new PixelPoint(20, 10), new PixelPoint(30, 15),
            new PixelPoint(20, 22), new PixelPoint(10, 15));
        var annotationOptions = new AnnotationOptions
        {
            MarkRadius = 3,
            MarkerValue = 144,
            DrawLabels = true,
            LabelScale = 1,
            ConstellationLineRed = 96,
            ConstellationLineGreen = 160,
            ConstellationLineBlue = 255,
            ConstellationLineOpacity = 0.8,
            DrawImageCircle = true,
            ImageCircleValue = 96,
            DrawCardinalDirections = true,
            CardinalValue = 255,
            CardinalScale = 2
        };
        var annotated = AnnotationRenderer.AnnotateRgb24WithSegments(source, width, height, objects, segments,
            new PreviewTransform(1, 1), annotationOptions, projectionOverlay);
        var legacy = WeatherCloudOverlayRenderer.Render(layout, annotated.Pixels, 2, 1, new byte[] { 2 },
            ["Cloud 12.3%", "Quality Good", "Precipitation Fresh"],
            new WeatherCloudOverlayRenderOptions()).Pixels.ToArray();

        var constellation = Payload(width, height, segments: [new(new(-2, 20), new(35, 20), 1, new(96, 160, 255))]);
        var imageCircle = Payload(width, height,
            ellipses: [new(new(20, 15), 10, 10, new(96, 96, 96))]);
        var starAnnotations = Payload(width, height,
            markers: [new(new(12, 12), 3, new(144, 144, 144))],
            text: [new(PresentationTextAnchor.Point, new PixelPoint(17, 9), ["STAR"], 1, 0, 0, new(255, 255, 255))]);
        var cardinalDirections = Payload(width, height,
            text:
            [
                new(PresentationTextAnchor.Point, new PixelPoint(16, 4), ["N"], 2, 0, 0, new(255, 255, 255)),
                new(PresentationTextAnchor.Point, new PixelPoint(26, 9), ["E"], 2, 0, 0, new(255, 255, 255)),
                new(PresentationTextAnchor.Point, new PixelPoint(16, 15), ["S"], 2, 0, 0, new(255, 255, 255)),
                new(PresentationTextAnchor.Point, new PixelPoint(6, 9), ["W"], 2, 0, 0, new(255, 255, 255))
            ]);
        var cloudMask = Payload(width, height, tileMask: new(2, 1, PresentationTileMaskV1.RowMajorLsbFirst,
            new byte[] { 2 }, 1, new(255, 64, 32)));
        var cloudLabels = Payload(width, height, text: [new(PresentationTextAnchor.Point, new PixelPoint(3, -2),
            ["Cloud 12.3%", "Quality Good", "Precipitation Fresh"], 1, 0, 2, new(255, 255, 255))]);
        var actual = PresentationLayerCompositor.Composite(layout, source,
        [
            new(constellation, true, PresentationRasterBlendMode.Normal, 800_000),
            new(imageCircle, true, PresentationRasterBlendMode.Normal, 1_000_000),
            new(starAnnotations, true, PresentationRasterBlendMode.Normal, 1_000_000),
            new(cardinalDirections, true, PresentationRasterBlendMode.Normal, 1_000_000),
            new(cloudMask, true, PresentationRasterBlendMode.Normal, 1_000_000),
            new(cloudLabels, true, PresentationRasterBlendMode.Lighten, 1_000_000)
        ]);

        CollectionAssert.AreEqual(legacy, actual);
    }

    private static ImageLayout Layout() => new(16, 12, CameraPixelFormat.Rgb24, 48);

    private static PresentationLayerPayloadV1 Payload(
        IReadOnlyList<PresentationMarkerV1>? markers = null,
        IReadOnlyList<PresentationSegmentV1>? segments = null,
        IReadOnlyList<PresentationEllipseV1>? ellipses = null,
        IReadOnlyList<PresentationTextBlockV1>? text = null,
        PresentationTileMaskV1? tileMask = null) => Payload(16, 12, markers, segments, ellipses, text, tileMask);

    private static PresentationLayerPayloadV1 Payload(
        int width,
        int height,
        IReadOnlyList<PresentationMarkerV1>? markers = null,
        IReadOnlyList<PresentationSegmentV1>? segments = null,
        IReadOnlyList<PresentationEllipseV1>? ellipses = null,
        IReadOnlyList<PresentationTextBlockV1>? text = null,
        PresentationTileMaskV1? tileMask = null) => new(
            PresentationLayerPayloadV1.CurrentSchemaVersion, new string('A', 64), new string('B', 64), width, height,
            markers ?? [], segments ?? [], ellipses ?? [], text ?? [], tileMask);
}
