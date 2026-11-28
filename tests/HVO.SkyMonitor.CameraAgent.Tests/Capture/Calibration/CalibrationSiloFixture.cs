using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

internal static class CalibrationSiloFixture
{
    internal static async Task PublishAsync(IServiceProvider services, string fixtureRoot, CalibrationLibraryBundleV1 bundle)
    {
        var publisher = services.GetRequiredService<CalibrationArtifactPublisher>();
        var token = CancellationToken.None;
        foreach (var artifact in bundle.Artifacts)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, artifact.ManifestRelativePath), token).ConfigureAwait(false);
            var parsed = CaptureContractJson.ParseManifest(bytes);
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            var manifest = parsed.Document!.Manifest;
            var payload = await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, manifest.RelativeArtifactPath), token).ConfigureAwait(false);
            await publisher.PublishPairAsync(manifest.RelativeArtifactPath, payload, artifact.ManifestRelativePath, bytes, token)
                .ConfigureAwait(false);
        }
        var profile = await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, bundle.ProfileRelativePath), token).ConfigureAwait(false);
        await publisher.PublishSiloBundleAsync(bundle, profile, token).ConfigureAwait(false);
    }
}
