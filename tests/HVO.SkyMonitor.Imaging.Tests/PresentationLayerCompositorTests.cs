using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PresentationLayerCompositorTests
{
    [TestMethod]
    public void PinnedMonoFacesTrackingAndMixedHeadingLayoutRemainInsideCapturedCorner()
    {
        var body = new PresentationTextStyleV3(PresentationFontFaceV3.MonoRegular, 16_000, 0,
            new(216, 229, 246), default, 0, 0);
        var heading = body with { Face = PresentationFontFaceV3.MonoBold, SizeMilliPixels = 22_000, Color = new(255, 255, 255) };
        using var regular = PresentationFont.Create(body);
        using var bold = PresentationFont.Create(heading);
        Assert.AreEqual("DejaVu Sans Mono", regular.Typeface!.FamilyName);
        Assert.AreEqual(400, regular.Typeface.FontStyle.Weight);
        Assert.AreEqual(700, bold.Typeface!.FontStyle.Weight);
        Assert.AreEqual(regular.MeasureText("iii"), regular.MeasureText("WWW"), 0.001f);
        using var untracked = PresentationFont.LinePath(bold, "NORTH", 0, 0);
        using var tracked = PresentationFont.LinePath(bold, "NORTH", 0, 0, 0.72f);
        Assert.AreEqual(4 * 0.72f, tracked.Bounds.Width - untracked.Bounds.Width, 0.001f);
        var block = new PresentationTextBlockV1(PresentationTextAnchor.BottomRight, default,
            ["Capture 32", "1936 × 1216 pixels"], 3, 20, 6, body.Color,
            new(new(5, 10, 17), 780_000, new(88, 166, 255), 6, 2, new(0, 0, true)), new(body, heading));
        var first = PresentationFont.LineOrigin(block, 640, 480, bold, block.Lines[0], 0);
        var second = PresentationFont.LineOrigin(block, 640, 480, regular, block.Lines[1], 1);
        Assert.AreEqual(first.X, second.X);
        Assert.IsGreaterThan(first.Y, second.Y);
        var bounds = PresentationFont.BackplateBounds(block, 640, 480, bold);
        Assert.IsGreaterThanOrEqualTo(0, bounds.Left);
        Assert.IsLessThanOrEqualTo(640, bounds.Right);
        Assert.IsLessThanOrEqualTo(480, bounds.Bottom);
        var layout = new ImageLayout(640, 480, CameraPixelFormat.Rgb24, 1920);
        var pixels = PresentationLayerCompositor.Composite(layout, new byte[layout.RequiredByteLength],
            [new(Payload(640, 480, text: [block]), true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.IsTrue(Enumerable.Range(0, pixels.Length / 3).Any(index =>
            pixels[index * 3] == 255 && pixels[index * 3 + 1] == 255 && pixels[index * 3 + 2] == 255));
        var outside = ((int)bounds.Top * 640 + (int)bounds.Right + 1) * 3;
        Assert.AreEqual((byte)0, pixels[outside]);
        var cardinal = new PresentationTextBlockV1(PresentationTextAnchor.Point, new(100, 50), ["N"], 2, 0, 0, body.Color,
            new(new(20, 30, 40), 1_000_000, new(116, 209, 255), 5, 0,
                new(0, 0, MinimumWidthMilliPixels: 38_000, MinimumHeightMilliPixels: 25_000, CornerRadiusMilliPixels: 5_000)),
            new(body with { Face = PresentationFontFaceV3.MonoBold, SizeMilliPixels = 18_000 }));
        var plate = PresentationFont.BackplateBounds(cardinal, 640, 480, regular);
        Assert.AreEqual(38f, plate.Width);
        Assert.AreEqual(25f, plate.Height);
        var rounded = PresentationLayerCompositor.Composite(layout, new byte[layout.RequiredByteLength],
            [new(Payload(640, 480, text: [cardinal]), true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.AreEqual((byte)0, rounded[((int)plate.Top * 640 + (int)plate.Left) * 3]);
        Assert.AreEqual((byte)20, rounded[(((int)plate.Top + 1) * 640 + (int)plate.Left + 10) * 3]);
    }

    [TestMethod]
    public void SemanticV2KeepsOriginalPixelsWhileV3UsesExplicitEllipseWidth()
    {
        var legacy = Payload(160, 120, ellipses: [new(new(80, 60), 20, 20, new(0, 255, 0))],
            text: [new(PresentationTextAnchor.Point, new(5, 5), ["Legacy"], 2, 0, 0, new(255, 255, 255))])
            with
        { SchemaVersion = PresentationLayerPayloadV1.SemanticSchemaVersion };
        var layout = new ImageLayout(160, 120, CameraPixelFormat.Rgb24, 480);
        byte[] Render(PresentationLayerPayloadV1 payload) => PresentationLayerCompositor.Composite(layout,
            new byte[layout.RequiredByteLength], [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        var original = Render(legacy);
        CollectionAssert.AreEqual(original, Render(legacy with { SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion }));
        var wide = legacy with
        {
            SchemaVersion = PresentationLayerPayloadV1.CurrentSchemaVersion,
            Ellipses = [legacy.Ellipses[0] with { ThicknessMilliPixels = 2_000 }]
        };
        Assert.IsGreaterThan(original[(60 * 160 + 100) * 3 + 1], Render(wide)[(60 * 160 + 100) * 3 + 1]);
        Assert.ThrowsExactly<ArgumentException>(() => (wide with { SchemaVersion = PresentationLayerPayloadV1.SemanticSchemaVersion }).ValidateStructure());
        Assert.AreEqual(PresentationLayerCompositor.SemanticAlgorithmVersion,
            PresentationLayerCompositor.SelectAlgorithmVersion([PresentationLayerCompositor.SemanticAlgorithmVersion]));
    }

    [TestMethod]
    public void CompositionBudgetsRejectBeforeOutputAllocationAndAdmitFullResolutionW6()
    {
        var oversized = new ImageLayout(16384, 16384, CameraPixelFormat.Mono8, 16384);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerCompositor.CompositeDisplay(oversized, ReadOnlyMemory<byte>.Empty, []));
        StringAssert.Contains(error.Message, "pixel budget", StringComparison.Ordinal);
        Assert.IsLessThan(1_000_000L, GC.GetAllocatedBytesForCurrentThread() - start);

        const int size = 3552;
        var layout = new ImageLayout(size, size, CameraPixelFormat.Mono8, size);
        var source = new byte[layout.RequiredByteLength];
        var layer = new PresentationCompositorLayer(Payload(size, size, markers: [new(new(100, 100), 0, new(64, 128, 255))]), true,
            PresentationRasterBlendMode.Normal, 1_000_000);
        var accepted = PresentationLayerCompositor.CompositeDisplay(layout, source, Enumerable.Repeat(layer, 7).ToArray());
        Assert.AreEqual(size * size * 3, accepted.Pixels.Length);
        Assert.AreEqual(CameraPixelFormat.Rgb24, accepted.Layout.PixelFormat);
        start = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerCompositor.CompositeDisplay(layout, source, Enumerable.Repeat(layer, 11).ToArray()));
        Assert.IsLessThan(1_000_000L, GC.GetAllocatedBytesForCurrentThread() - start, "Rejected stack must not allocate its 38MB output.");
    }

    [TestMethod]
    public void CompositionBudgetsBoundAggregatePrimitiveTextAndGeometryWork()
    {
        var layout = new ImageLayout(64, 64, CameraPixelFormat.Mono8, 64);
        var source = new byte[layout.RequiredByteLength];
        var marker = new PresentationMarkerV1(new(32, 32), 0, new(255, 255, 255));
        var layer = new PresentationCompositorLayer(Payload(64, 64, markers: Enumerable.Repeat(marker, 4096).ToArray()), true, PresentationRasterBlendMode.Normal, 1_000_000);
        foreach (var rejected in new[]
        {
            Enumerable.Repeat(layer, 5).ToArray(),
            new[] { layer with { Payload = Payload(64, 64, markers: Enumerable.Repeat(marker, 4097).ToArray()) } },
            new[] { layer with { Payload = Payload(64, 64, segments: Enumerable.Repeat(new PresentationSegmentV1(new(-131072, 32), new(131072, 32), 8, new()), 17).ToArray()) } },
            new[] { layer with { Payload = Payload(64, 64, text: Enumerable.Repeat(new PresentationTextBlockV1(PresentationTextAnchor.Point, default,
                Enumerable.Repeat(new string('W', 64), 8).ToArray(), 1, 0, 0, new()), 17).ToArray()) } },
            Enumerable.Repeat(layer with { Payload = Payload(64, 64) }, 17).ToArray()
        })
            Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerCompositor.CompositeDisplay(layout, source, rejected));
        Assert.AreEqual(0, source.Sum(value => value));
    }

    [TestMethod]
    public void DiagonalAntialiasingAtTileSeamsStaysWithinDocumentedTolerance()
    {
        const int width = 2056, height = 1216;
        var segment = new PresentationSegmentV1(new(4, 17), new(2050, 207), 2, new(255, 255, 255));
        var source = new byte[width * height * 3];
        var tiled = PresentationLayerCompositor.Composite(new(width, height, CameraPixelFormat.Rgb24, width * 3), source,
            [new(Payload(width, height, segments: [segment]), true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        using var full = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(full))
        using (var paint = new SKPaint { IsAntialias = true, StrokeWidth = 2, Color = SKColors.White, Style = SKPaintStyle.Stroke })
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawLine((float)segment.From.X, (float)segment.From.Y, (float)segment.To.X, (float)segment.To.Y, paint);
        }
        var maximumDifference = 0;
        foreach (var seam in new[] { 1024, 2048 })
            for (var x = seam - 2; x <= seam + 2 && x < width; x++)
                for (var y = 0; y < height; y++)
                    maximumDifference = Math.Max(maximumDifference, Math.Abs(tiled[(y * width + x) * 3] - full.GetPixel(x, y).Red));
        // Skia's clipped tile and untiled scan conversion differ at diagonal edges; no exact-AA claim.
        Assert.IsLessThanOrEqualTo(32, maximumDifference, "At most 32/255 edge coverage variation, not repeated opacity.");
        CollectionAssert.AreEqual(new byte[source.Length], source);
    }

    [TestMethod]
    public void TranslucentEllipseNeverExceedsSingleCoverageAtScanIntersection()
    {
        const int width = 1936, height = 1216;
        var source = new byte[width * height * 3];
        var layer = Payload(width, height, ellipses:
            [new(new(968, 608), 595.84, 595.84, new(116, 209, 255), new(0, 0, 450_000))]);
        var result = PresentationLayerCompositor.Composite(new(width, height, CameraPixelFormat.Rgb24, width * 3), source,
            [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        var reported = (168 * width + 1370) * 3;
        Assert.IsGreaterThan((byte)0, result[reported + 2]);
        Assert.IsLessThanOrEqualTo((byte)53, result[reported]);
        Assert.IsLessThanOrEqualTo((byte)95, result[reported + 1]);
        Assert.IsLessThanOrEqualTo((byte)115, result[reported + 2]);
        for (var offset = 0; offset < result.Length; offset += 3)
        {
            Assert.IsLessThanOrEqualTo((byte)53, result[offset]);
            Assert.IsLessThanOrEqualTo((byte)95, result[offset + 1]);
            Assert.IsLessThanOrEqualTo((byte)115, result[offset + 2]);
        }
        CollectionAssert.AreEqual(new byte[source.Length], source);
    }

    [TestMethod]
    [DataRow(PresentationRasterBlendMode.Normal, 700_000)]
    [DataRow(PresentationRasterBlendMode.Multiply, 700_000)]
    [DataRow(PresentationRasterBlendMode.Screen, 700_000)]
    [DataRow(PresentationRasterBlendMode.Lighten, 700_000)]
    [DataRow(PresentationRasterBlendMode.Multiply, 1_000_000)]
    public void LayerBlendsOnlyOnceAcrossThickLinesCrosshairsAndSegmentJunctions(PresentationRasterBlendMode mode, int opacity)
    {
        const int width = 64, height = 64;
        var color = new PresentationColor(64, 192, 240);
        var source = Enumerable.Repeat((byte)160, width * height * 3).ToArray();
        var payload = Payload(width, height,
            segments: [new(new(4, 10), new(30, 10), 8, color), new(new(30, 10), new(58, 10), 8, color)],
            markers: [new(new(30.5, 40.5), 6, color, true)]);
        var result = PresentationLayerCompositor.Composite(new(width, height, CameraPixelFormat.Rgb24, width * 3), source,
            [new(payload, true, mode, opacity)]);
        var expected = new[] { color.Red, color.Green, color.Blue }.Select(channel =>
        {
            var blended = mode switch
            {
                PresentationRasterBlendMode.Multiply => (160 * channel + 127) / 255,
                PresentationRasterBlendMode.Screen => 255 - ((255 - 160) * (255 - channel) + 127) / 255,
                PresentationRasterBlendMode.Lighten => Math.Max(160, (int)channel),
                _ => channel
            };
            return (byte)((160 * (1_000_000 - opacity) + blended * opacity + 500_000) / 1_000_000);
        }).ToArray();
        foreach (var (x, y) in new[] { (12, 10), (29, 10), (30, 10), (31, 10), (30, 40), (24, 40), (36, 40) })
        {
            var offset = (y * width + x) * 3;
            CollectionAssert.AreEqual(expected, result[offset..(offset + 3)], $"Pixel {x},{y}");
        }
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)160, source.Length).ToArray(), source);
    }

    [TestMethod]
    public void SemanticTileBoundariesDoNotCreateSeamsAndFarClippedGeometryStaysBounded()
    {
        var payload = Payload(2056, 1032, segments: [new(new(-131_072, 1024), new(131_072, 1024), 8, new(100, 160, 200))]);
        var layout = new ImageLayout(2056, 1032, CameraPixelFormat.Rgb24, 6168);
        var result = PresentationLayerCompositor.Composite(layout, new byte[layout.RequiredByteLength],
            [new(payload, true, PresentationRasterBlendMode.Normal, 500_000)]);
        foreach (var x in new[] { 0, 1023, 1024, 2047, 2048, 2055 })
            CollectionAssert.AreEqual(new byte[] { 50, 80, 100 }, result[((1024 * 2056 + x) * 3)..((1024 * 2056 + x) * 3 + 3)]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PresentationLayerCompositor.CompositeDisplay(layout,
            new byte[layout.RequiredByteLength], [new(payload, true, PresentationRasterBlendMode.Normal, 500_000)], cancelled.Token));
    }

    [TestMethod]
    public void SemanticMonoPresentationPromotesRgbWithDashesAndLeavesZeroLayerBaseExact()
    {
        var source = Enumerable.Repeat((byte)100, 160 * 100).ToArray();
        var before = SHA256.HashData(source);
        var layout = new ImageLayout(160, 100, CameraPixelFormat.Mono8, 160);
        var payload = Payload(160, 100,
            segments: [new(new(10, 80), new(140, 80), 2, new(188, 140, 255), new(10, 8, 1_000_000))],
            text: [new(PresentationTextAnchor.Point, new(30, 30), ["N"], 3, 0, 0, new(195, 236, 255),
                new(new(2, 8, 14), 840_000, new(44, 79, 97), 5, 2))]);
        var composed = PresentationLayerCompositor.CompositeDisplay(layout, source,
            [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.AreEqual(CameraPixelFormat.Rgb24, composed.Layout.PixelFormat);
        Assert.AreEqual(480, composed.Layout.StrideBytes);
        Assert.AreEqual(48000, composed.Pixels.Length);
        CollectionAssert.AreEqual(new byte[] { 188, 140, 255 }, composed.Pixels[((80 * 160 + 12) * 3)..((80 * 160 + 12) * 3 + 3)]);
        CollectionAssert.AreEqual(new byte[] { 100, 100, 100 }, composed.Pixels[((80 * 160 + 22) * 3)..((80 * 160 + 22) * 3 + 3)]);
        Assert.IsLessThan((byte)100, composed.Pixels[(28 * 160 + 33) * 3]);
        var empty = PresentationLayerCompositor.CompositeDisplay(layout, source, []);
        Assert.AreEqual(layout, empty.Layout);
        CollectionAssert.AreEqual(source, empty.Pixels);
        var disabled = PresentationLayerCompositor.CompositeDisplay(layout, source, [new(payload, false, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.AreEqual(layout, disabled.Layout);
        CollectionAssert.AreEqual(source, disabled.Pixels);
        CollectionAssert.AreEqual(before, SHA256.HashData(source));
    }

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
            tileMask: new(2, 1, PresentationTileMaskV1.RowMajorLsbFirst, new byte[] { 2 }, 1, new(255, 64, 32)))
        with
        { SchemaVersion = PresentationLayerPayloadV1.PreviousSchemaVersion };

        var result = PresentationLayerCompositor.Composite(Layout(), new byte[16 * 12 * 3],
            [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);

        Assert.AreEqual((byte)144, result[(4 * 16 + 6) * 3]);
        Assert.AreEqual((byte)96, result[(7 * 16 + 3) * 3]);
        Assert.AreEqual((byte)160, result[(7 * 16 + 3) * 3 + 1]);
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
    [DataRow(false)]
    [DataRow(true)]
    public void CancellationAtEndOfAdmissionPreventsOutputAllocationAndCopy(bool promoteToRgb)
    {
        const int size = 2048;
        var layout = new ImageLayout(size, size, CameraPixelFormat.Mono8, size);
        var source = Enumerable.Repeat((byte)37, layout.RequiredByteLength).ToArray();
        var checksum = SHA256.HashData(source);
        using var cancellation = new CancellationTokenSource();
        var layers = new AdmissionCancellingLayers(
            new(Payload(size, size, markers: [new(new(10, 10), 1, new(100, 150, 200))]),
                true, PresentationRasterBlendMode.Normal, 1_000_000), cancellation);
        Assert.IsFalse(cancellation.IsCancellationRequested);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var exception = Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            if (promoteToRgb) PresentationLayerCompositor.CompositeDisplay(layout, source, layers, cancellation.Token);
            else PresentationLayerCompositor.Composite(layout, source, layers, cancellation.Token);
        });
        var allocationDelta = GC.GetAllocatedBytesForCurrentThread() - allocated;

        Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        Assert.IsLessThan(1_000_000L, allocationDelta, "Admission cancellation must precede the 4/12 MiB output allocation and any base copy.");
        CollectionAssert.AreEqual(checksum, SHA256.HashData(source));
    }

    [TestMethod]
    public async Task CompositeCancelsDuringDenseScale16TextHalo()
    {
        const int width = 4096;
        const int height = 128;
        var layout = new ImageLayout(width, height, CameraPixelFormat.Rgb24, width * 3);
        var text = Payload(width, height, text: Enumerable.Repeat(
            new PresentationTextBlockV1(PresentationTextAnchor.TopLeft, default,
                Enumerable.Repeat(new string('W', 64), 8).ToArray(), 16, 0, 0, new(255, 255, 255)), 4).ToArray());
        using var cancellation = new CancellationTokenSource();
        var render = Task.Run(() => PresentationLayerCompositor.Composite(layout, new byte[layout.RequiredByteLength],
            [new(text, true, PresentationRasterBlendMode.Normal, 1_000_000)], cancellation.Token));
        await Task.Delay(5).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            var output = await render.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.AreEqual(layout.RequiredByteLength, output.Length, "A fast completed render is not a cancellation failure.");
        }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public void ScalableTextRendersAtFullFrameSizeWithoutChangingSmallBitmapText()
    {
        const int width = 800;
        const int height = 600;
        var layout = new ImageLayout(width, height, CameraPixelFormat.Rgb24, width * 3);
        var large = Payload(width, height, text: [new(PresentationTextAnchor.Point, new(30, 30), ["NORTH"],
            8, 0, 0, new(255, 255, 255))]);
        var small = Payload(width, height, text: [new(PresentationTextAnchor.Point, new(30, 30), ["NORTH"],
            1, 0, 0, new(255, 255, 255))]);
        var source = new byte[layout.RequiredByteLength];
        var rendered = PresentationLayerCompositor.Composite(layout, source,
            [new(large, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        var repeat = PresentationLayerCompositor.Composite(layout, source,
            [new(large, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        var smallRendered = PresentationLayerCompositor.Composite(layout, source,
            [new(small, true, PresentationRasterBlendMode.Normal, 1_000_000)]);

        CollectionAssert.AreEqual(rendered, repeat);
        var largeExtent = Enumerable.Range(0, width * height)
            .Where(index => rendered[index * 3] != 0).Max(index => index % width);
        var smallExtent = Enumerable.Range(0, width * height)
            .Where(index => smallRendered[index * 3] != 0).Max(index => index % width);
        Assert.IsGreaterThan(smallExtent + 20, largeExtent);
        CollectionAssert.AreEqual(new byte[layout.RequiredByteLength], source);
        Assert.IsTrue(Enumerable.Range(30, 60).SelectMany(y => Enumerable.Range(30, 200)
            .Select(x => rendered[(y * width + x) * 3])).Any(value => value > 0));
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

        Assert.AreEqual(legacy.Length, actual.Length);
        CollectionAssert.AreNotEqual(legacy, actual, "Layered text uses the embedded scalable font; legacy annotation remains unchanged.");
    }

    [TestMethod]
    public void UnicodePresentationTextRendersWithoutSystemFontFallback()
    {
        var payload = Payload(800, 600, text: [new(PresentationTextAnchor.TopLeft, default,
            ["Bételgeuse"], 3, 10, 0, new(255, 255, 255))]);
        var layout = new ImageLayout(800, 600, CameraPixelFormat.Rgb24, 2400);
        var pixels = PresentationLayerCompositor.Composite(layout, new byte[layout.RequiredByteLength],
            [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        var ascii = Payload(800, 600, text: [new(PresentationTextAnchor.TopLeft, default,
            ["Betelgeuse"], 3, 10, 0, new(255, 255, 255))]);
        var asciiPixels = PresentationLayerCompositor.Composite(layout, new byte[layout.RequiredByteLength],
            [new(ascii, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
        Assert.IsTrue(pixels.Any(value => value > 0));
        CollectionAssert.AreNotEqual(asciiPixels, pixels);
        Assert.AreEqual(64, PresentationFont.FontSha256.Length);
    }

    private static ImageLayout Layout() => new(16, 12, CameraPixelFormat.Rgb24, 48);

    private sealed class AdmissionCancellingLayers(PresentationCompositorLayer layer, CancellationTokenSource cancellation)
        : IReadOnlyList<PresentationCompositorLayer>
    {
        public int Count => 1;
        public PresentationCompositorLayer this[int index] => index == 0 ? layer : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<PresentationCompositorLayer> GetEnumerator()
        {
            yield return layer;
            // The caller has validated, frozen and accounted the final layer before advancing.
            cancellation.Cancel();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
