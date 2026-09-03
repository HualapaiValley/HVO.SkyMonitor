using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.CameraAgent.Tests.Contracts;

internal static class ReconstructableCaptureContractTests
{
    internal static ArtifactManifestV2 CreateManifest(
        CameraPixelFormat format,
        int width,
        int height,
        int stride,
        byte[] payload)
        => ArtifactManifestFixture.CreateManifest(format, width, height, stride, payload);
}
