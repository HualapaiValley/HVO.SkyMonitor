using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
public sealed class ManualFocusFrameSamplerTests
{
    private static readonly bool[] ShallowClipping = [false, true, false, false, false, false, false, false];

    [TestMethod]
    public void ExtractWindow_KeepsExactSourceCoordinatesAndClampsAtTheFrameEdge()
    {
        var frame = Mono16(40, 30, static (x, y) => (ushort)(x + 100 * y));

        var corner = ManualFocusFrameSampler.ExtractWindow(frame, -5, -3, 20, 10, CancellationToken.None);
        var far = ManualFocusFrameSampler.ExtractWindow(frame, 30, 20, 20, 20, CancellationToken.None);

        Assert.AreEqual((0, 0, 15, 7), (corner.OriginX, corner.OriginY, corner.Width, corner.Height));
        Assert.AreEqual(2 + 300, corner.Pixels[3 * corner.Width + 2]);
        Assert.IsTrue(corner.ValidMask.All(static valid => valid));
        Assert.AreEqual((30, 20, 10, 10), (far.OriginX, far.OriginY, far.Width, far.Height));
        Assert.AreEqual(35 + 100 * 24, far.Pixels[4 * far.Width + 5]);
    }

    [TestMethod]
    public void ExtractWindow_FlagsClippedSamples()
    {
        var mono16 = Mono16(8, 8, static (x, y) => x == 3 && y == 4 ? ushort.MaxValue : (ushort)1000);
        var mono8 = new CameraFrame(DateTimeOffset.UnixEpoch, 4, 2, CameraPixelFormat.Mono8,
            new byte[] { 10, 255, 10, 10, 10, 10, 254, 10 }, Metadata());

        var deep = ManualFocusFrameSampler.ExtractWindow(mono16, 0, 0, 8, 8, CancellationToken.None);
        var shallow = ManualFocusFrameSampler.ExtractWindow(mono8, 0, 0, 4, 2, CancellationToken.None);

        Assert.AreEqual(1, deep.SaturatedMask.Count(static value => value));
        Assert.IsTrue(deep.SaturatedMask[4 * 8 + 3]);
        CollectionAssert.AreEqual(ShallowClipping, shallow.SaturatedMask);
    }

    [TestMethod]
    public void ExtractWindow_MasksSamplesOutsideTheImageCircleWithTheMeteringConvention()
    {
        var frame = Mono16(100, 100, static (_, _) => 1000);

        var window = ManualFocusFrameSampler.ExtractWindow(frame, 10, 10, 80, 80, CancellationToken.None,
            new MeteringImageCircle(50, 50, 20));

        bool Valid(int x, int y) => window.ValidMask[(y - window.OriginY) * window.Width + x - window.OriginX];
        Assert.IsTrue(Valid(50, 50));
        Assert.IsTrue(Valid(70, 50), "A sample index exactly on the radius is inside, as in metering.");
        Assert.IsFalse(Valid(71, 50));
        Assert.IsFalse(Valid(65, 65));
        Assert.IsFalse(Valid(10, 10));
    }

    [TestMethod]
    public void ExtractWindow_ReadsRgbLuminanceAndReconstructsBayerAtExactCoordinates()
    {
        var rgb = new byte[4 * 3 * 3];
        for (var index = 0; index < rgb.Length; index += 3)
        {
            (rgb[index], rgb[index + 1], rgb[index + 2]) = ((byte)10, (byte)20, (byte)30);
        }
        var rgbFrame = new CameraFrame(DateTimeOffset.UnixEpoch, 4, 3, CameraPixelFormat.Rgb24, rgb, Metadata());
        var bayer = new byte[32 * 24 * 2];
        for (var index = 0; index < bayer.Length; index += 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bayer.AsSpan(index), 1000);
        }
        var bayerFrame = new CameraFrame(DateTimeOffset.UnixEpoch, 32, 24, CameraPixelFormat.BayerRggb16, bayer, Metadata());

        var luminance = ManualFocusFrameSampler.ExtractWindow(rgbFrame, 0, 0, 4, 3, CancellationToken.None);
        var mosaic = ManualFocusFrameSampler.ExtractWindow(bayerFrame, 3, 5, 9, 7, CancellationToken.None);

        Assert.AreEqual(0.2126 * 10 + 0.7152 * 20 + 0.0722 * 30, luminance.Pixels[5], 1e-12);
        Assert.AreEqual((3, 5, 9, 7), (mosaic.OriginX, mosaic.OriginY, mosaic.Width, mosaic.Height));
        foreach (var value in mosaic.Pixels)
        {
            Assert.AreEqual(1000, value, 1e-9, "A flat mosaic reconstructs flat at odd origins.");
        }
    }

    [TestMethod]
    public void MalformedOrUnsupportedFrames_AreRejected()
    {
        var shortFrame = new CameraFrame(DateTimeOffset.UnixEpoch, 10, 10, CameraPixelFormat.Mono16, new byte[150], Metadata());
        var unknown = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 2, (CameraPixelFormat)99, new byte[16], Metadata());

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ManualFocusFrameSampler.ExtractWindow(shortFrame, 0, 0, 4, 4, CancellationToken.None));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ManualFocusFrameSampler.ExtractWindow(unknown, 0, 0, 2, 2, CancellationToken.None));
    }

    [TestMethod]
    public void CreateOverview_BinsToTheDisplayBoundAndKeepsWholeBayerCells()
    {
        var mono = Mono16(1500, 1000, static (_, _) => 500);
        var bayerBytes = new byte[1500 * 1000 * 2];
        var bayer = new CameraFrame(DateTimeOffset.UnixEpoch, 1500, 1000, CameraPixelFormat.BayerRggb16, bayerBytes, Metadata());

        var monoOverview = ManualFocusFrameSampler.CreateOverview(mono, 640, CancellationToken.None);
        var bayerOverview = ManualFocusFrameSampler.CreateOverview(bayer, 640, CancellationToken.None);

        Assert.AreEqual((3, 500, 334), (monoOverview.BinFactor, monoOverview.Width, monoOverview.Height));
        Assert.IsTrue(monoOverview.Pixels.All(static value => value == 500));
        Assert.AreEqual(4, bayerOverview.BinFactor);
        Assert.IsTrue(monoOverview.SkyMask.All(static sky => sky));
    }

    [TestMethod]
    public void CreateOverview_MarksOnlyCellsCentredInsideTheImageCircleAsSky()
    {
        var frame = Mono16(40, 20, static (_, _) => 500);

        var overview = ManualFocusFrameSampler.CreateOverview(frame, 20, CancellationToken.None, new MeteringImageCircle(19.5, 9.5, 8));

        // Bin 2: cell (9,4) is centred on source 18.5,8.5 (inside); cell (0,0) on 0.5,0.5 and cell (14,4) on 28.5,8.5 (outside).
        Assert.AreEqual((2, 20, 10), (overview.BinFactor, overview.Width, overview.Height));
        Assert.IsTrue(overview.SkyMask[4 * 20 + 9]);
        Assert.IsFalse(overview.SkyMask[0]);
        Assert.IsFalse(overview.SkyMask[4 * 20 + 14]);
    }

    [TestMethod]
    public void Measure_StretchesTheOverviewFromTheImageCircleSkyNotTheDarkCorners()
    {
        // A fisheye frame: black outside a 60-pixel circle, a 1000-1100 sky gradient inside, no star.
        var circle = new MeteringImageCircle(99.5, 79.5, 60);
        var frame = Mono16(200, 160, (x, y) =>
            (x - circle.CenterX) * (x - circle.CenterX) + (y - circle.CenterY) * (y - circle.CenterY) > circle.Radius * circle.Radius
                ? (ushort)0
                : (ushort)(1000 + x / 2));

        var images = ManualFocusPreviewMeasurement.Measure(frame, 1, null, null, CancellationToken.None, circle).Images;

        var decoded = JpegImageCodec.DecodeJpeg(images.OverviewJpeg).PixelData.Span;
        var inside = new List<byte>();
        for (var y = 50; y < 110; y++)
        {
            for (var x = 70; x < 130; x++)
            {
                inside.Add(decoded[y * images.OverviewWidth + x]);
            }
        }
        // Stretched from the corners the whole sky would sit at 243-255; stretched from the sky it spans the range.
        Assert.IsLessThan(160, inside.Min());
        Assert.IsGreaterThan(200, inside.Max());
    }

    [TestMethod]
    public void Measure_ReportsAClippedSkyAsSaturatedRatherThanStarless()
    {
        var daylight = Mono16(200, 160, static (_, _) => ushort.MaxValue);
        var night = Mono16(200, 160, static (x, y) => (ushort)(1000 + (x * 7 + y * 13) % 5));

        var clipped = ManualFocusPreviewMeasurement.Measure(daylight, 1, null, null, CancellationToken.None).Measurement;
        var starless = ManualFocusPreviewMeasurement.Measure(night, 2, null, null, CancellationToken.None).Measurement;

        Assert.AreEqual((FocusStarStatus.Saturated, FocusStarReasonCodes.Saturated), (clipped.Status, clipped.ReasonCode));
        Assert.IsNull(clipped.Centroid);
        Assert.AreEqual((FocusStarStatus.NoStar, FocusStarReasonCodes.NoCandidate), (starless.Status, starless.ReasonCode));
    }

    private static CameraFrame Mono16(int width, int height, Func<int, int, ushort> value)
    {
        var bytes = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((y * width + x) * 2), value(x, y));
            }
        }
        return new CameraFrame(DateTimeOffset.UnixEpoch, width, height, CameraPixelFormat.Mono16, bytes, Metadata());
    }

    private static FrameMetadata Metadata() => new(TimeSpan.FromSeconds(1), 0, 20);
}
