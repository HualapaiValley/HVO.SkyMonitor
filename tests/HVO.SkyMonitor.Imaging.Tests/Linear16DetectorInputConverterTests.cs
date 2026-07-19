using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16DetectorInputConverterTests
{
    [TestMethod]
    public void Convert_PaddedMono16BorrowsExactMemoryAndPreservesLayout()
    {
        var pixels = new byte[]
        {
            1, 0, 2, 0, 0xaa, 0xbb,
            3, 0, 4, 0, 0xcc, 0xdd
        };
        var layout = new ImageLayout(2, 2, CameraPixelFormat.Mono16, 6);

        var result = Linear16DetectorInputConverter.Convert(
            layout,
            FrameByteOrder.LittleEndian,
            pixels);

        Assert.AreEqual(layout, result.Layout);
        Assert.AreEqual(Linear16DetectorInputOwnership.Borrowed, result.Ownership);
        Assert.AreEqual(Linear16DetectorInputConverter.Mono16AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(0, result.BytesScanned);
        Assert.AreEqual(0, result.BytesCopied);
        Assert.AreEqual(
            new Linear16SourceToOutputTransform(
                Linear16DetectorInputConverter.TransformVersion, 1, 1, 0, 0),
            result.SourceToOutputTransform);
        Assert.IsTrue(MemoryMarshal.TryGetArray(result.PixelData, out var segment));
        Assert.AreSame(pixels, segment.Array);
        Assert.AreEqual(0, segment.Offset);
        Assert.AreEqual(pixels.Length, segment.Count);
    }

    [TestMethod]
    public void Convert_PaddedRggb16ProducesExactRoundedPackedLuminance()
    {
        const int width = 4;
        const int height = 4;
        const int stride = 12;
        var pixels = Enumerable.Repeat((byte)0xee, stride * height).ToArray();
        WriteCell(pixels, stride, 0, 0, 1, 2, 3, 4);
        WriteCell(pixels, stride, 2, 0, 100, 200, 300, 400);
        WriteCell(pixels, stride, 0, 2, 0, 0, 0, 1);
        WriteCell(pixels, stride, 2, 2, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);
        var sourceChecksum = Sha256(pixels);

        var result = Linear16DetectorInputConverter.Convert(
            new ImageLayout(width, height, CameraPixelFormat.BayerRggb16, stride),
            FrameByteOrder.LittleEndian,
            pixels);

        Assert.AreEqual(new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4), result.Layout);
        Assert.AreEqual(Linear16DetectorInputOwnership.Owned, result.Ownership);
        Assert.AreEqual(Linear16DetectorInputConverter.Rggb16AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(32, result.BytesScanned);
        Assert.AreEqual(8, result.BytesCopied);
        Assert.AreEqual(
            new Linear16SourceToOutputTransform(
                Linear16DetectorInputConverter.TransformVersion, 0.5, 0.5, 0, 0),
            result.SourceToOutputTransform);
        CollectionAssert.AreEqual(
            new byte[]
            {
                3, 0,
                250, 0,
                0, 0,
                0xff, 0xff
            },
            result.PixelData.ToArray());
        Assert.AreEqual(sourceChecksum, Sha256(pixels));
        Assert.IsTrue(MemoryMarshal.TryGetArray(result.PixelData, out var segment));
        Assert.AreNotSame(pixels, segment.Array);
    }

    [TestMethod]
    public void Convert_Rggb16TransformMapsPixelEdgesAndRoundTrips()
    {
        const int sourceWidth = 6;
        const int sourceHeight = 4;
        var result = Linear16DetectorInputConverter.Convert(
            new ImageLayout(sourceWidth, sourceHeight, CameraPixelFormat.BayerRggb16, sourceWidth * 2),
            FrameByteOrder.LittleEndian,
            new byte[sourceWidth * sourceHeight * 2]);
        var transform = result.SourceToOutputTransform;

        Assert.AreEqual(0d, MapX(transform, 0), 1e-12);
        Assert.AreEqual(0d, MapY(transform, 0), 1e-12);
        Assert.AreEqual(result.Layout.Width, MapX(transform, sourceWidth), 1e-12);
        Assert.AreEqual(result.Layout.Height, MapY(transform, sourceHeight), 1e-12);
        Assert.AreEqual(0d, InverseX(transform, 0), 1e-12);
        Assert.AreEqual(0d, InverseY(transform, 0), 1e-12);
        Assert.AreEqual(sourceWidth, InverseX(transform, result.Layout.Width), 1e-12);
        Assert.AreEqual(sourceHeight, InverseY(transform, result.Layout.Height), 1e-12);
        Assert.AreEqual(3.25, InverseX(transform, MapX(transform, 3.25)), 1e-12);
        Assert.AreEqual(1.75, InverseY(transform, MapY(transform, 1.75)), 1e-12);
    }

    [TestMethod]
    public void Convert_Rggb16RepeatedConversionIsDeterministicAndDoesNotMutateSource()
    {
        var pixels = new byte[]
        {
            10, 0, 20, 0,
            30, 0, 40, 0
        };
        var original = pixels.ToArray();
        var layout = new ImageLayout(2, 2, CameraPixelFormat.BayerRggb16, 4);

        var first = Linear16DetectorInputConverter.Convert(layout, FrameByteOrder.LittleEndian, pixels);
        var second = Linear16DetectorInputConverter.Convert(layout, FrameByteOrder.LittleEndian, pixels);

        CollectionAssert.AreEqual(original, pixels);
        CollectionAssert.AreEqual(first.PixelData.ToArray(), second.PixelData.ToArray());
        Assert.AreEqual(Sha256(first.PixelData.Span), Sha256(second.PixelData.Span));
    }

    [TestMethod]
    public void Convert_RejectsInvalidLayoutAndExactLengthMismatch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 3),
            FrameByteOrder.LittleEndian,
            new byte[8]));
        Assert.Throws<ArgumentException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4),
            FrameByteOrder.LittleEndian,
            new byte[7]));
        Assert.Throws<ArgumentException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4),
            FrameByteOrder.LittleEndian,
            new byte[9]));
    }

    [TestMethod]
    public void Convert_RejectsUnsupportedFormatByteOrderAndOddRggbDimensions()
    {
        Assert.Throws<ArgumentException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.Mono8, 2),
            FrameByteOrder.LittleEndian,
            new byte[4]));
        Assert.Throws<ArgumentException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4),
            FrameByteOrder.BigEndian,
            new byte[8]));
        Assert.Throws<ArgumentException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(3, 2, CameraPixelFormat.BayerRggb16, 6),
            FrameByteOrder.LittleEndian,
            new byte[12]));
        Assert.Throws<ArgumentException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 3, CameraPixelFormat.BayerRggb16, 4),
            FrameByteOrder.LittleEndian,
            new byte[12]));
    }

    [TestMethod]
    public void Convert_HonorsCancellationBeforeBorrowOrAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4),
            FrameByteOrder.LittleEndian,
            new byte[8],
            cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(2, 2, CameraPixelFormat.BayerRggb16, 4),
            FrameByteOrder.LittleEndian,
            new byte[8],
            cancellation.Token));
    }

    [TestMethod]
    public async Task Convert_Rggb16HonorsCancellationAfterConversionBegins()
    {
        const int width = 16_384;
        const int height = 2;
        using var pixels = new GatedMemoryManager(new byte[width * height * 2]);
        using var cancellation = new CancellationTokenSource();
        var conversion = Task.Run(() => Linear16DetectorInputConverter.Convert(
            new ImageLayout(width, height, CameraPixelFormat.BayerRggb16, width * 2),
            FrameByteOrder.LittleEndian,
            pixels.CreateReadOnlyMemory(),
            cancellation.Token));

        try
        {
            Assert.IsTrue(pixels.WaitForSpanRequest(TimeSpan.FromSeconds(10)));
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            pixels.ReleaseSpan();
        }

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await conversion.ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static void WriteCell(
        byte[] pixels,
        int stride,
        int x,
        int y,
        ushort red,
        ushort greenOnRedRow,
        ushort greenOnBlueRow,
        ushort blue)
    {
        Write(pixels, stride, x, y, red);
        Write(pixels, stride, x + 1, y, greenOnRedRow);
        Write(pixels, stride, x, y + 1, greenOnBlueRow);
        Write(pixels, stride, x + 1, y + 1, blue);
    }

    private static void Write(byte[] pixels, int stride, int x, int y, ushort value)
    {
        var offset = y * stride + x * 2;
        pixels[offset] = (byte)value;
        pixels[offset + 1] = (byte)(value >> 8);
    }

    private static string Sha256(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value));

    private static double MapX(Linear16SourceToOutputTransform transform, double sourceX)
        => sourceX * transform.ScaleX + transform.OffsetX;

    private static double MapY(Linear16SourceToOutputTransform transform, double sourceY)
        => sourceY * transform.ScaleY + transform.OffsetY;

    private static double InverseX(Linear16SourceToOutputTransform transform, double outputX)
        => (outputX - transform.OffsetX) / transform.ScaleX;

    private static double InverseY(Linear16SourceToOutputTransform transform, double outputY)
        => (outputY - transform.OffsetY) / transform.ScaleY;

    private sealed class GatedMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] pixels;
        private readonly ManualResetEventSlim spanRequested = new();
        private readonly ManualResetEventSlim releaseSpan = new();

        public GatedMemoryManager(byte[] pixels)
        {
            this.pixels = pixels;
        }

        public ReadOnlyMemory<byte> CreateReadOnlyMemory() => CreateMemory(pixels.Length);

        public bool WaitForSpanRequest(TimeSpan timeout) => spanRequested.Wait(timeout);

        public void ReleaseSpan() => releaseSpan.Set();

        public override Span<byte> GetSpan()
        {
            spanRequested.Set();
            releaseSpan.Wait();
            return pixels;
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                spanRequested.Dispose();
                releaseSpan.Dispose();
            }
        }
    }
}
