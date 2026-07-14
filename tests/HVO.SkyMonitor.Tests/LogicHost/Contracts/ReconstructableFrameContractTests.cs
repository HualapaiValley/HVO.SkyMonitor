using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Contracts;

[TestClass]
[TestCategory("Unit")]
public sealed class ReconstructableFrameContractTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian, ColorFilterArrayPattern.None, 2)]
    [DataRow(CameraPixelFormat.Rgb24, FrameByteOrder.NotApplicable, ColorFilterArrayPattern.None, 3)]
    [DataRow(CameraPixelFormat.BayerRggb16, FrameByteOrder.LittleEndian, ColorFilterArrayPattern.Rggb, 2)]
    public void CentralConsumer_ReconstructsExactBytesFromManifestV2(
        CameraPixelFormat format,
        FrameByteOrder byteOrder,
        ColorFilterArrayPattern cfa,
        int bytesPerPixel)
    {
        var payload = Enumerable.Range(0, bytesPerPixel * 4).Select(static value => (byte)(value * 3)).ToArray();
        var manifest = CreateManifest(format, byteOrder, cfa, bytesPerPixel, payload);

        var parsed = CaptureContractJson.ParseManifest(CaptureContractJson.Serialize(manifest));
        Assert.IsTrue(parsed.IsValid);
        var descriptor = parsed.Document!.Manifest!.Descriptor;
        var result = FrameReconstructor.TryReconstruct(descriptor, payload, out var frame);

        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(payload, frame!.PixelData.ToArray());
        Assert.AreEqual(payload.Length, frame.StrideBytes);
    }

    private static ArtifactManifestV2 CreateManifest(
        CameraPixelFormat format,
        FrameByteOrder byteOrder,
        ColorFilterArrayPattern cfa,
        int bytesPerPixel,
        byte[] payload)
    {
        using var options = JsonDocument.Parse("{}");
        var sha256 = new string('A', 64);
        var instant = DateTimeOffset.UnixEpoch;
        var sampleDepth = format == CameraPixelFormat.Rgb24 ? 8 : 16;
        return new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            new ReconstructionDescriptor(
                new("central-agent", "central-rig", 1, Guid.Parse("10000000-0000-0000-0000-000000000001")),
                new(instant, instant, instant, instant, instant),
                new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null, null),
                new(
                    new("rig", "1", sha256),
                    new("calibration", "1", sha256),
                    new("mask", "1", sha256),
                    new("sensor", "1", sha256),
                    new("processing", "1", sha256)),
                new(4, 1, payload.Length, format, byteOrder, sampleDepth, sampleDepth,
                    FrameSamplePacking.ByteAligned, cfa, 0, sampleDepth == 8 ? 255 : 65535, payload.Length),
                new(
                    Guid.Parse("20000000-0000-0000-0000-000000000001"),
                    FrameArtifactRole.Raw,
                    "central-source",
                    "native",
                    instant,
                    [],
                    RecipeIdentityDescriptor.Create("raw", "1.0.0", "central-1", options.RootElement),
                    "application/octet-stream",
                    PayloadChecksum.ComputeSha256(payload))),
            "frames/raw.bin");
    }
}
