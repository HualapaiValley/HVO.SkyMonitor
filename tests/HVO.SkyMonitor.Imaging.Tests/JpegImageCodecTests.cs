using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class JpegImageCodecTests
{
    [TestMethod]
    public void EncodeMono8ToJpeg_IsDeterministicAndDecodesToPackedMono8()
    {
        var pixels = Enumerable.Range(0, 64).Select(value => (byte)(value * 4)).ToArray();

        var first = JpegImageCodec.EncodeMono8ToJpeg(8, 8, pixels);
        var second = JpegImageCodec.EncodeMono8ToJpeg(8, 8, pixels);
        var info = JpegImageCodec.InspectJpeg(first);
        var validated = JpegImageCodec.ValidateJpeg(first);
        var decoded = JpegImageCodec.DecodeJpeg(first);

        CollectionAssert.AreEqual(first, second);
        Assert.AreEqual(0xff, first[0]);
        Assert.AreEqual(0xd8, first[1]);
        Assert.AreEqual(JpegImageCodec.MediaType, decoded.MediaType);
        Assert.AreEqual(JpegImageCodec.AlgorithmVersion, decoded.AlgorithmVersion);
        Assert.AreEqual(8, info.Width);
        Assert.AreEqual(8, info.Height);
        Assert.AreEqual(CameraPixelFormat.Mono8, info.PixelFormat);
        Assert.AreEqual(JpegImageCodec.MediaType, info.MediaType);
        Assert.AreEqual(info, validated);
        Assert.AreEqual(CameraPixelFormat.Mono8, decoded.PixelFormat);
        Assert.AreEqual(8, decoded.Width);
        Assert.AreEqual(8, decoded.Height);
        Assert.AreEqual(8, decoded.StrideBytes);
        Assert.HasCount(64, decoded.PixelData.ToArray());
    }

    [TestMethod]
    public void EncodeRgb24ToJpeg_IgnoresRowPaddingAndDecodesToPackedRgb24()
    {
        const int width = 8;
        const int height = 8;
        const int packedStride = width * 3;
        const int paddedStride = packedStride + 5;
        var packed = new byte[packedStride * height];
        var padded = Enumerable.Repeat((byte)0xcc, paddedStride * height).ToArray();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var packedOffset = y * packedStride + x * 3;
                var paddedOffset = y * paddedStride + x * 3;
                packed[packedOffset] = 220;
                packed[packedOffset + 1] = 30;
                packed[packedOffset + 2] = 10;
                padded[paddedOffset] = packed[packedOffset];
                padded[paddedOffset + 1] = packed[packedOffset + 1];
                padded[paddedOffset + 2] = packed[packedOffset + 2];
            }
        }

        var packedJpeg = JpegImageCodec.EncodeRgb24ToJpeg(width, height, packed);
        var paddedJpeg = JpegImageCodec.EncodeRgb24ToJpeg(
            width, height, padded, paddedStride);
        var info = JpegImageCodec.InspectJpeg(paddedJpeg);
        var validated = JpegImageCodec.ValidateJpeg(paddedJpeg);
        var decoded = JpegImageCodec.DecodeJpeg(paddedJpeg);

        CollectionAssert.AreEqual(packedJpeg, paddedJpeg);
        Assert.AreEqual(CameraPixelFormat.Rgb24, info.PixelFormat);
        Assert.AreEqual(info, validated);
        Assert.AreEqual(CameraPixelFormat.Rgb24, decoded.PixelFormat);
        Assert.AreEqual(packedStride, decoded.StrideBytes);
        Assert.HasCount(packedStride * height, decoded.PixelData.ToArray());
        Assert.IsGreaterThan(decoded.PixelData.Span[1], decoded.PixelData.Span[0]);
        Assert.IsGreaterThan(decoded.PixelData.Span[2], decoded.PixelData.Span[1]);
    }

    [TestMethod]
    public void EncodeToJpeg_ValidatesDimensionsStrideBufferFormatAndQuality()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JpegImageCodec.EncodeMono8ToJpeg(0, 1, new byte[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JpegImageCodec.EncodeRgb24ToJpeg(2, 1, new byte[6], 5));
        Assert.Throws<ArgumentException>(() =>
            JpegImageCodec.EncodeRgb24ToJpeg(2, 1, new byte[5]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JpegImageCodec.EncodeMono8ToJpeg(1, 1, new byte[1], quality: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JpegImageCodec.EncodeMono8ToJpeg(1, 1, new byte[1], quality: 101));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JpegImageCodec.EncodeRgb24ToJpeg(int.MaxValue, 1, new byte[1]));
        Assert.Throws<ArgumentException>(() => JpegImageCodec.EncodeToJpeg(
            new ImageLayout(1, 1, CameraPixelFormat.Mono16, 2), new byte[2]));
        Assert.Throws<ArgumentException>(() => JpegImageCodec.DecodeJpeg(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => JpegImageCodec.InspectJpeg(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => JpegImageCodec.DecodeJpeg(new byte[] { 1, 2, 3 }));
        Assert.Throws<ArgumentException>(() => JpegImageCodec.InspectJpeg(new byte[] { 1, 2, 3 }));
        Assert.Throws<ArgumentException>(() => JpegImageCodec.DecodeJpeg(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z1XkAAAAASUVORK5CYII=")));

        var mono = JpegImageCodec.EncodeToJpeg(
            new ImageLayout(1, 1, CameraPixelFormat.Mono8, 1), new byte[1]);
        var rgb = JpegImageCodec.EncodeToJpeg(
            new ImageLayout(1, 1, CameraPixelFormat.Rgb24, 3), new byte[3]);
        Assert.IsNotEmpty(mono);
        Assert.IsNotEmpty(rgb);
    }

    [TestMethod]
    public void ValidateJpeg_RejectsTruncatedOrProgressiveDataAcceptedByInspection()
    {
        var encoded = JpegImageCodec.EncodeMono8ToJpeg(
            8,
            8,
            Enumerable.Range(0, 64).Select(static value => (byte)(value * 4)).ToArray());
        var truncated = encoded[..^2];

        var info = JpegImageCodec.InspectJpeg(truncated);

        Assert.AreEqual(8, info.Width);
        Assert.AreEqual(8, info.Height);
        Assert.Throws<ArgumentException>(() => JpegImageCodec.ValidateJpeg(truncated));
        Assert.Throws<InvalidOperationException>(() => JpegImageCodec.DecodeJpeg(truncated));

        var scanMarker = Enumerable.Range(1, encoded.Length - 1)
            .First(index => encoded[index - 1] == 0xff && encoded[index] == 0xda);
        var scanHeaderLength = (encoded[scanMarker + 1] << 8) | encoded[scanMarker + 2];
        var entropyStart = scanMarker + 1 + scanHeaderLength;
        var noEntropy = encoded[..entropyStart].Concat(encoded[^2..]).ToArray();
        Assert.Throws<ArgumentException>(() => JpegImageCodec.ValidateJpeg(noEntropy));

        var progressiveFrame = encoded.ToArray();
        var frameMarker = Enumerable.Range(1, progressiveFrame.Length - 1)
            .First(index => progressiveFrame[index - 1] == 0xff && progressiveFrame[index] == 0xc0);
        progressiveFrame[frameMarker] = 0xc2;
        Assert.Throws<ArgumentException>(() => JpegImageCodec.ValidateJpeg(progressiveFrame));

        var rgb = JpegImageCodec.EncodeRgb24ToJpeg(8, 8, new byte[8 * 8 * 3]);
        scanMarker = Enumerable.Range(1, rgb.Length - 1)
            .First(index => rgb[index - 1] == 0xff && rgb[index] == 0xda);
        var splitScan = rgb[..(scanMarker + 6)].Concat(rgb[(scanMarker + 10)..]).ToArray();
        splitScan[scanMarker + 1] = 0;
        splitScan[scanMarker + 2] = 8;
        splitScan[scanMarker + 3] = 1;
        Assert.Throws<ArgumentException>(() => JpegImageCodec.ValidateJpeg(splitScan));
    }

    [TestMethod]
    public void EncodeAndDecode_PreCanceledTokenThrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => JpegImageCodec.EncodeMono8ToJpeg(
            1, 1, new byte[1], cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => JpegImageCodec.EncodeRgb24ToJpeg(
            1, 1, new byte[3], cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            JpegImageCodec.DecodeJpeg(new byte[] { 1 }, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            JpegImageCodec.ValidateJpeg(new byte[] { 1 }, cancellation.Token));
    }
}
