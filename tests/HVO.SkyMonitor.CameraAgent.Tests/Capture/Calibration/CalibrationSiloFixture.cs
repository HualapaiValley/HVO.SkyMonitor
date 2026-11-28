using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Extensions.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

internal static class CalibrationSiloFixture
{
    internal static CalibrationArtifactPublisher Publisher(IOptions<CameraAgentHostOptions> options,
        ICalibrationPublicationFaultInjector? faults = null)
        => new(faults ?? NullCalibrationPublicationFaultInjector.Instance,
            SiloInfrastructureFixture.ProductStorage(options.Value.RawIngressRoot));

    internal static SiloArtifactStorage Storage(string root)
        => SiloInfrastructureFixture.ProductStorage(root);

    internal static SiloTestStore Objects(string root)
        => SiloInfrastructureFixture.ProductStore(root).GetRequiredService<SiloTestStore>();

    internal static async Task<ArtifactManifestV2> ReadManifestAsync(string root, Guid artifactId)
    {
        var bytes = await ReadMemberAsync(root, artifactId, CalibrationArtifactPublisher.SiloEvidenceRevision).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(bytes);
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        return parsed.Document!.Manifest;
    }

    internal static async Task<byte[]> ReadMemberAsync(string root, Guid id, string revision)
    {
        var locator = await FindMemberAsync(root, id, revision).ConfigureAwait(false);
        return Objects(root).Read(locator.Key, locator.VersionId);
    }

    internal static ValueTask<SiloExactLocator> FindMemberAsync(string root, Guid id, string revision)
        => SiloInfrastructureFixture.ReadLocalLocatorAsync(SiloInfrastructureFixture.ProductStore(root),
            id, revision, CancellationToken.None);

    internal static Dictionary<string, (Guid ArtifactId, string Revision)> SyntheticMembers(SyntheticCalibrationBundle bundle)
    {
        var bundleId = CalibrationArtifactPublisher.BundleArtifactId(bundle.LibraryBundle);
        var members = new Dictionary<string, (Guid, string)>(StringComparer.Ordinal)
        {
            [CalibrationLibraryEvidenceNames.BundleEnvelope] = (bundleId, CalibrationArtifactPublisher.SiloBundleRevision),
            [CalibrationLibraryEvidenceNames.ProfileMarker] = (bundleId, CalibrationArtifactPublisher.SiloProfileRevision)
        };
        foreach (var (kind, manifest) in bundle.ReferenceManifests)
        {
            var descriptor = manifest.Descriptor;
            members.Add($"{kind}.bin", (descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor)));
            members.Add($"{kind}.json", (descriptor.Artifact.ArtifactId, CalibrationArtifactPublisher.SiloEvidenceRevision));
        }
        return members;
    }

    internal static async Task OverrideMemberAsync(string root, Guid id, string revision, byte[] bytes)
    {
        var locator = await FindMemberAsync(root, id, revision).ConfigureAwait(false);
        var objects = Objects(root);
        var previous = objects.ReadContentOverride;
        objects.ReadContentOverride = requested => requested == locator ? new MemoryStream(bytes, writable: false) : previous?.Invoke(requested);
    }

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
