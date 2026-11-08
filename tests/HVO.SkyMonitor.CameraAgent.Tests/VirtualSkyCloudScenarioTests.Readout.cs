using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    [TestMethod]
    [DataRow("crop")]
    [DataRow("bin")]
    [DataRow("sample-depth")]
    [DataRow("big-endian")]
    [DataRow("stored-transform")]
    public async Task CloudReadoutStillRejectsGeometryAndCodeConversions(string conversion)
    {
        var config = Config(CameraPixelFormat.Mono16, Definition(), physicalMono: true);
        var readout = new SensorReadoutProfile(new SensorCrop(0, 0, 64, 48), 1, 1,
            FrameBinningAlgorithm.IdentityV1, CameraPixelFormat.Mono16, 12, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample, 64, 4095, 128);
        readout = conversion switch
        {
            "crop" => readout with { Roi = new SensorCrop(2, 0, 60, 48), StrideBytes = 120 },
            "bin" => readout with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1, StrideBytes = 64 },
            "sample-depth" => readout with { SampleDepthBits = 10, WhiteLevel = 1023 },
            "big-endian" => readout with { ByteOrder = SampleByteOrder.BigEndian },
            "stored-transform" => readout with { StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1 },
            _ => throw new ArgumentOutOfRangeException(nameof(conversion))
        };
        var camera = Module();
        await using var lifetime = camera.ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => camera.InitializeAsync(
            config with { Rig = config.Rig with { Readout = readout } }, CancellationToken.None)).ConfigureAwait(false);
    }
}
