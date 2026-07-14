using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AnnotationRendererTests
{
    [TestMethod]
    public async Task AnnotateMono8_ScalesProjectedAnchorWithoutChangingSource()
    {
        var source = new byte[100];
        var scene = await SceneTestFactory.CreateCenteredAsync(11, 11).ConfigureAwait(false);
        var item = scene.Objects[0];

        var result = AnnotationRenderer.AnnotateMono8(source, 10, 10, scene.Objects,
            new PreviewTransform(0.5, 0.25, 1, 2), new AnnotationOptions { MarkRadius = 0, DrawLabels = false });

        var expected = new PixelPoint(item.Pixel.X * 0.5 + 1, item.Pixel.Y * 0.25 + 2);
        Assert.AreEqual(expected, result.Anchors[0].PreviewPixel);
        Assert.AreEqual(0, source[(int)Math.Round(expected.Y) * 10 + (int)Math.Round(expected.X)]);
        Assert.AreEqual((byte)144, result.Pixels.Span[(int)Math.Round(expected.Y) * 10 + (int)Math.Round(expected.X)]);
    }

    [TestMethod]
    public async Task AnnotationAnchorAgreesWithRenderedCentroidAfterPreviewScaling()
    {
        var scene = await SceneTestFactory.CreateCenteredAsync(21, 21).ConfigureAwait(false);
        var render = Mono16SceneRenderer.Render(scene,
            new ImageLayout(21, 21, CameraPixelFormat.Mono16, 42),
            new Mono16SceneRenderOptions { MagnitudeZeroElectronsPerSecond = 20_000 });
        var annotation = AnnotationRenderer.AnnotateMono8(new byte[11 * 11], 11, 11, scene.Objects,
            new PreviewTransform(11d / 21, 11d / 21), new AnnotationOptions { DrawLabels = false });

        var expectedX = render.Objects[0].DepositedCentroid.X * 11d / 21;
        var expectedY = render.Objects[0].DepositedCentroid.Y * 11d / 21;
        Assert.AreEqual(expectedX, annotation.Anchors[0].PreviewPixel.X, 1e-9);
        Assert.AreEqual(expectedY, annotation.Anchors[0].PreviewPixel.Y, 1e-9);
    }

    [TestMethod]
    public void AnnotateMono8WithSegments_DrawsBoundedDeterministicLineWithoutChangingSource()
    {
        var source = new byte[8 * 8];
        var result = AnnotationRenderer.AnnotateMono8WithSegments(
            source, 8, 8, Array.Empty<ProjectedAnnotationObject>(),
            [new ProjectedAnnotationSegment("TST", new PixelPoint(-2, -2), new PixelPoint(6, 6))],
            new PreviewTransform(1, 1), new AnnotationOptions
            {
                DrawLabels = false,
                ConstellationLineValue = byte.MaxValue,
                ConstellationLineOpacity = 1
            });

        Assert.AreEqual(0, source[3 * 8 + 3]);
        Assert.AreEqual(byte.MaxValue, result.Pixels.Span[3 * 8 + 3]);
        CollectionAssert.AreEqual(result.Pixels.ToArray(),
            AnnotationRenderer.AnnotateMono8WithSegments(
                 source, 8, 8, Array.Empty<ProjectedAnnotationObject>(),
                 [new ProjectedAnnotationSegment("TST", new PixelPoint(-2, -2), new PixelPoint(6, 6))],
                  new PreviewTransform(1, 1), new AnnotationOptions
                  {
                      DrawLabels = false,
                      ConstellationLineValue = byte.MaxValue,
                      ConstellationLineOpacity = 1
                  }).Pixels.ToArray());
    }

    [TestMethod]
    public void AnnotateRgb24WithSegments_UsesConfiguredColorThicknessAndOpacity()
    {
        var source = new byte[9 * 9 * 3];
        var result = AnnotationRenderer.AnnotateRgb24WithSegments(
            source, 9, 9, [],
            [new ProjectedAnnotationSegment("TST", new PixelPoint(-1_000_000, 4), new PixelPoint(1_000_000, 4))],
            new PreviewTransform(1, 1), new AnnotationOptions
            {
                DrawLabels = false,
                ConstellationLineRed = 100,
                ConstellationLineGreen = 150,
                ConstellationLineBlue = 200,
                ConstellationLineThickness = 3,
                ConstellationLineOpacity = 0.5
            });

        Assert.AreEqual((byte)50, result.Pixels.Span[(3 * 9 + 4) * 3]);
        Assert.AreEqual((byte)75, result.Pixels.Span[(4 * 9 + 4) * 3 + 1]);
        Assert.AreEqual((byte)100, result.Pixels.Span[(5 * 9 + 4) * 3 + 2]);
        Assert.AreEqual(0, result.Pixels.Span[(2 * 9 + 4) * 3]);
    }

    [TestMethod]
    public void AnnotateMono8_SeparatesMarksFromReadableScaledLabels()
    {
        var source = new byte[80 * 30];
        var hidden = new ProjectedAnnotationObject("catalog:1", "catalog:1", new PixelPoint(5, 15), false, false);
        var named = new ProjectedAnnotationObject("catalog:2", "A", new PixelPoint(20, 15), true, true);

        var result = AnnotationRenderer.AnnotateMono8(
            source, 80, 30, [hidden, named], new PreviewTransform(1, 1),
            new AnnotationOptions { MarkRadius = 6, LabelScale = 1 });

        Assert.AreEqual(0, result.Pixels.Span[15 * 80 + 5]);
        Assert.AreEqual(0, result.Pixels.Span[15 * 80 + 20]);
        Assert.AreEqual((byte)144, result.Pixels.Span[15 * 80 + 26]);
        Assert.AreEqual(byte.MaxValue, result.Pixels.Span[12 * 80 + 29]);
    }

    [TestMethod]
    public void AnnotateRgb24_ComposesNeutralOverlayWithoutChangingSourceOrObjectCenter()
    {
        var source = Enumerable.Repeat((byte)20, 20 * 20 * 3).ToArray();
        var result = AnnotationRenderer.AnnotateRgb24WithSegments(
            source, 20, 20,
            [new ProjectedAnnotationObject("star", "Star", new PixelPoint(10, 10), true, false)],
            [], new PreviewTransform(1, 1), new AnnotationOptions { MarkRadius = 4, MarkerValue = 100 });

        Assert.AreEqual(20, source[(10 * 20 + 14) * 3]);
        Assert.AreEqual(20, result.Pixels.Span[(10 * 20 + 10) * 3]);
        Assert.AreEqual(100, result.Pixels.Span[(10 * 20 + 14) * 3]);
        Assert.AreEqual(100, result.Pixels.Span[(10 * 20 + 14) * 3 + 1]);
        Assert.AreEqual(100, result.Pixels.Span[(10 * 20 + 14) * 3 + 2]);
    }

    [TestMethod]
    public void AnnotateMono8WithSegments_DrawsClippedImageCircleAndMirroredCardinals()
    {
        var source = new byte[100 * 80];
        var overlay = new ProjectedAnnotationOverlay(
            new PixelPoint(50, 40), 45,
            new PixelPoint(50, -5),
            new PixelPoint(5, 40),
            new PixelPoint(50, 85),
            new PixelPoint(95, 40));
        var options = new AnnotationOptions
        {
            DrawLabels = false,
            DrawImageCircle = true,
            DrawCardinalDirections = true,
            ImageCircleValue = 80,
            CardinalScale = 1
        };

        var result = AnnotationRenderer.AnnotateMono8WithSegments(
            source, 100, 80, [], [], new PreviewTransform(1, 1), options, overlay);

        Assert.AreEqual((byte)80, result.Pixels.Span[72 * 100 + 82]);
        Assert.IsTrue(HasMarkedPixel(result.Pixels.Span, 100, 40, 0, 60, 12));
        Assert.IsTrue(HasMarkedPixel(result.Pixels.Span, 100, 0, 30, 12, 50));
        Assert.IsTrue(HasMarkedPixel(result.Pixels.Span, 100, 40, 68, 60, 80));
        Assert.IsTrue(HasMarkedPixel(result.Pixels.Span, 100, 88, 30, 100, 50));
        Assert.AreEqual(0, source[40 * 100 + 5]);
    }

    [TestMethod]
    public void AnnotateMono8WithProjectionOverlay_WhenCardinalsAreDisabled_DrawsOnlyImageCircle()
    {
        var source = new byte[20 * 20];
        var overlay = new ProjectedAnnotationOverlay(
            new PixelPoint(10, 10), 8,
            new PixelPoint(10, 2),
            new PixelPoint(2, 10),
            new PixelPoint(10, 18),
            new PixelPoint(18, 10));
        var options = new AnnotationOptions
        {
            DrawLabels = false,
            DrawImageCircle = true,
            DrawCardinalDirections = false,
            ImageCircleValue = 80
        };
        var shiftedCardinals = overlay with
        {
            North = new PixelPoint(1, 1),
            East = new PixelPoint(1, 18),
            South = new PixelPoint(18, 18),
            West = new PixelPoint(18, 1)
        };

        var result = AnnotationRenderer.AnnotateMono8WithSegments(
            source, 20, 20, [], [], new PreviewTransform(1, 1), options, overlay);
        var shiftedResult = AnnotationRenderer.AnnotateMono8WithSegments(
            source, 20, 20, [], [], new PreviewTransform(1, 1), options, shiftedCardinals);

        Assert.IsTrue(result.Pixels.Span.Contains((byte)80));
        Assert.IsFalse(result.Pixels.Span.Contains(byte.MaxValue));
        CollectionAssert.AreEqual(result.Pixels.ToArray(), shiftedResult.Pixels.ToArray());
        Assert.IsTrue(source.AsSpan().IndexOfAnyExcept((byte)0) < 0);

        var enormousOverlay = overlay with { ImageCircleRadius = double.MaxValue / 2 };
        var enormousResult = AnnotationRenderer.AnnotateMono8WithSegments(
            source, 20, 20, [], [], new PreviewTransform(1, 1), options, enormousOverlay);
        Assert.HasCount(source.Length, enormousResult.Pixels.ToArray());

        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => AnnotationRenderer.AnnotateMono8WithSegments(
            source, 20, 20, CancelAfterEnumeration(cancellation), [], new PreviewTransform(1, 1), options, overlay,
            cancellation.Token));
    }

    private static IEnumerable<ProjectedAnnotationObject> CancelAfterEnumeration(CancellationTokenSource cancellation)
    {
        yield return new ProjectedAnnotationObject("star", "Star", new PixelPoint(10, 10));
        cancellation.Cancel();
    }

    private static bool HasMarkedPixel(
        ReadOnlySpan<byte> pixels, int width, int minX, int minY, int maxX, int maxY)
    {
        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                if (pixels[y * width + x] > 0)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
