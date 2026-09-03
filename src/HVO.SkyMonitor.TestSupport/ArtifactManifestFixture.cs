using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.TestSupport;

public static class ArtifactManifestFixture
{
    public static ArtifactManifestV2 CreateManifest(
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        byte[] payload)
    {
        using var options = JsonDocument.Parse("{\"stretch\":{\"white\":65535,\"black\":0},\"enabled\":true}");
        var hash = new string('A', 64);
        var sampleDepth = format is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 ? 8 : 16;
        var requestedStart = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor("agent-a", "rig-a", 42, Guid.Parse("00000000-0000-0000-0000-000000000001")),
            new CaptureTimingDescriptor(
                requestedStart,
                requestedStart.AddSeconds(1),
                requestedStart.AddSeconds(2),
                requestedStart.AddSeconds(3),
                requestedStart.AddSeconds(4)),
            new CaptureControlDescriptor(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 100, 99.5,
                10, 10, -10, -9.5),
            new CaptureProfileSet(
                new("rig-a", "1.2.3", hash),
                new("dark-library", "2.0.0", hash),
                new("sensor-mask", "1.0.0", hash),
                new("asi-sensor", "3.0.0", hash),
                new("capture-profile", "4.0.0", hash)),
            new FrameLayoutDescriptor(
                width,
                height,
                stride,
                format,
                sampleDepth == 16 ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable,
                sampleDepth,
                sampleDepth,
                FrameSamplePacking.ByteAligned,
                format == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                0,
                sampleDepth == 16 ? 65535 : 255,
                payload.LongLength),
            new ArtifactDescriptor(
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                FrameArtifactRole.Raw,
                "VirtualSky",
                "native",
                requestedStart.AddSeconds(4),
                [],
                RecipeIdentityDescriptor.Create("capture-raw", "1.0.0", "build-1", options.RootElement),
                "application/octet-stream",
                PayloadChecksum.ComputeSha256(payload)));
        return new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, "frames/raw.bin");
    }
}
