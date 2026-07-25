using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class MonoDigitalReadoutRendererTests
{
    [TestMethod]
    public void Apply_DigitalAverageReducesDepthBeforeBinningAndPreservesPadding()
    {
        var nativeLayout = new ImageLayout(4, 2, CameraPixelFormat.Mono16, 8);
        var native = Result(U16(0, 16, 32, 48, 64, 80, 96, 112));
        var outputLayout = new ImageLayout(2, 1, CameraPixelFormat.Mono8, 4);
        var readout = Profile(FrameBinningAlgorithm.DigitalAverageV1, 2, 2);

        var result = MonoDigitalReadoutRenderer.Apply(native, nativeLayout, outputLayout, readout, 12);

        CollectionAssert.AreEqual(new byte[] { 3, 5, 0, 0 }, result.Pixels.ToArray());
        StringAssert.Contains(result.AlgorithmVersion, "digital-average-v1", StringComparison.Ordinal);
        Assert.AreEqual(3d, result.Statistics.Minimum);
        Assert.AreEqual(5d, result.Statistics.Maximum);

        Assert.ThrowsExactly<NotSupportedException>(() => MonoDigitalReadoutRenderer.Apply(
            Result(U16(0, 16, 32, 48, 64, 80)),
            new ImageLayout(3, 2, CameraPixelFormat.Mono16, 6),
            new ImageLayout(1, 1, CameraPixelFormat.Mono8, 1),
            readout,
            12));
    }

    [TestMethod]
    public void Apply_DigitalSumClampsAndIdentityCopiesMeaningfulCodes()
    {
        var sum = MonoDigitalReadoutRenderer.Apply(
            Result(U16(3000, 3000, 3000, 3000)),
            new ImageLayout(2, 2, CameraPixelFormat.Mono16, 4),
            new ImageLayout(1, 1, CameraPixelFormat.Mono16, 2),
            Profile(FrameBinningAlgorithm.DigitalSumV1, 2, 2, CameraPixelFormat.Mono16, 12, 16),
            12);
        Assert.AreEqual(4095, sum.Pixels.Span[0] | sum.Pixels.Span[1] << 8);
        Assert.AreEqual(1, sum.Statistics.ClippedHigh);

        var identity = MonoDigitalReadoutRenderer.Apply(
            Result(U16(7, 11)),
            new ImageLayout(2, 1, CameraPixelFormat.Mono16, 4),
            new ImageLayout(2, 1, CameraPixelFormat.Mono16, 4),
            Profile(FrameBinningAlgorithm.IdentityV1, 1, 1, CameraPixelFormat.Mono16, 12, 16),
            12);
        CollectionAssert.AreEqual(new byte[] { 7, 0, 11, 0 }, identity.Pixels.ToArray());
    }

    [TestMethod]
    public void Apply_HonorsCancellationBeforeAllocatingOutput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => MonoDigitalReadoutRenderer.Apply(
            Result(U16(7, 11)),
            new ImageLayout(2, 1, CameraPixelFormat.Mono16, 4),
            new ImageLayout(2, 1, CameraPixelFormat.Mono16, 4),
            Profile(FrameBinningAlgorithm.IdentityV1, 1, 1, CameraPixelFormat.Mono16, 12, 16),
            12,
            cancellationToken: cancellation.Token));
    }

    private static SensorReadoutProfile Profile(
        FrameBinningAlgorithm algorithm,
        int binX,
        int binY,
        CameraPixelFormat format = CameraPixelFormat.Mono8,
        int sampleDepth = 8,
        int containerDepth = 8)
        => new(
            new SensorCrop(0, 0, checked(2 * binX), checked(binY)),
            binX,
            binY,
            algorithm,
            format,
            sampleDepth,
            containerDepth,
            FrameSamplePacking.ByteAligned,
            sampleDepth == containerDepth
                ? FrameStoredCodeTransform.IdentityV1
                : FrameStoredCodeTransform.RightAlignedV1,
            FrameLevelCodeSpace.StoredContainer,
            0,
            Math.Pow(2, sampleDepth) - 1,
            StrideBytes: format == CameraPixelFormat.Mono8 ? 4 : null);

    private static SceneRenderResult Result(byte[] pixels)
        => new(
            pixels,
            "native-test-v1",
            "test",
            new RenderStatistics(pixels.Length / 2, 0, 0, 0, 0, 0),
            []);

    private static byte[] U16(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            bytes[index * 2] = (byte)values[index];
            bytes[index * 2 + 1] = (byte)(values[index] >> 8);
        }
        return bytes;
    }
}
