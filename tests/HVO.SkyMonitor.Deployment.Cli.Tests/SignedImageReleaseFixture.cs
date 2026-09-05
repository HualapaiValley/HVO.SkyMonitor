using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A complete signed CameraAgent image release on local media, as an air-gapped operator would receive it, together
/// with an acquirer bound to the ephemeral key that signed it. Production releases are signed by the key baked into
/// <see cref="DistributionTrustRoot.Production"/>; only that key's holder can produce a publishable release, so the
/// installation path is proved here against a release signed by an equivalent key.
/// </summary>
internal sealed class SignedImageReleaseFixture : IDisposable
{
    private readonly string mediaRoot;
    private readonly string cacheRoot;

    private SignedImageReleaseFixture(
        string mediaRoot,
        string cacheRoot,
        string manifestPath,
        DistributionTrustRoot trustRoot)
    {
        this.mediaRoot = mediaRoot;
        this.cacheRoot = cacheRoot;
        ManifestPath = manifestPath;
        TrustRoot = trustRoot;
    }

    public string ManifestPath { get; }
    public DistributionTrustRoot TrustRoot { get; }

    public DistributionAcquirer CreateAcquirer() => new(cacheRoot: cacheRoot, trustRoot: TrustRoot);

    /// <summary>The labels a fake Docker daemon reports for a candidate CameraAgent image.</summary>
    public static readonly Dictionary<string, string> ContractLabels = new(StringComparer.Ordinal)
    {
        ["org.opencontainers.image.revision"] = new string('a', 40),
        ["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v2",
        ["io.hvo.skymonitor.minimum-compatible-revision"] = new string('7', 40),
        ["io.hvo.skymonitor.identity-migration"] = "20260827053715_InitialIdentity",
        ["io.hvo.skymonitor.raw-ingress-schema"] = "12",
        ["io.hvo.skymonitor.catalog-manifest-version"] = "2",
        ["io.hvo.skymonitor.component"] = "CameraAgent",
        ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
        ["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s2",
        ["io.hvo.skymonitor.replay-runner-contract"] = "local-replay-runner-v1"
    };

    public static SignedImageReleaseFixture Create(
        string root,
        string imageId,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyList<string>? publishedArchitectures = null)
    {
        var mediaRoot = Path.Combine(root, "release-media", $"image-v1.2.3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaRoot);
        var cacheRoot = Path.Combine(root, "distribution-cache");
        Directory.CreateDirectory(cacheRoot);

        var artifacts = new List<DistributionArtifact>();
        var platforms = new List<DistributionImagePlatform>();
        foreach (var architecture in publishedArchitectures ?? ["amd64", "arm64"])
        {
            var assetName = $"cameraagent-image-v1.2.3-linux-{architecture}.tar";
            var path = Path.Combine(mediaRoot, assetName);
            var content = Encoding.UTF8.GetBytes($"cameraagent-image-archive-{architecture}\n");
            File.WriteAllBytes(path, content);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            artifacts.Add(new DistributionArtifact(
                DistributionArtifactRole.ImageArchive, assetName, "application/x-tar", content.Length,
                Convert.ToHexStringLower(SHA256.HashData(content)), "linux", architecture));
            // Both platforms carry the same immutable image ID so the fixture exercises whichever architecture the
            // host actually reports, rather than only the one the author happened to build on.
            platforms.Add(new DistributionImagePlatform(
                "linux",
                architecture,
                $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"manifest-{architecture}")))}",
                assetName,
                imageId));
        }
        foreach (var (role, assetName) in new[]
                 {
                     (DistributionArtifactRole.Sbom, "image-sbom.spdx.json"),
                     (DistributionArtifactRole.Provenance, "image-provenance.json"),
                     (DistributionArtifactRole.VulnerabilityScan, "image-vulnerability-scan.json"),
                     (DistributionArtifactRole.License, "THIRD-PARTY-NOTICES.md"),
                     (DistributionArtifactRole.Checksums, "SHA256SUMS")
                 })
        {
            var content = Encoding.UTF8.GetBytes(assetName);
            File.WriteAllBytes(Path.Combine(mediaRoot, assetName), content);
            artifacts.Add(new DistributionArtifact(
                role, assetName, "application/json", content.Length, Convert.ToHexStringLower(SHA256.HashData(content))));
        }

        var revision = labels["org.opencontainers.image.revision"];
        var image = new DistributionImageIdentity(
            "CameraAgent",
            "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent",
            $"sha256:{new string('c', 64)}",
            revision,
            new string('b', 40),
            platforms,
            "image-provenance.json",
            "image-sbom.spdx.json",
            "image-vulnerability-scan.json",
            new DistributionImageCompatibility(
                labels["io.hvo.skymonitor.state-compatibility"],
                labels["io.hvo.skymonitor.minimum-compatible-revision"],
                labels["io.hvo.skymonitor.identity-migration"],
                int.Parse(labels["io.hvo.skymonitor.raw-ingress-schema"], CultureInfo.InvariantCulture),
                int.Parse(labels["io.hvo.skymonitor.catalog-manifest-version"], CultureInfo.InvariantCulture),
                labels["io.hvo.skymonitor.configuration-contract"],
                labels["io.hvo.skymonitor.catalog-contract"],
                labels["io.hvo.skymonitor.replay-runner-contract"]));
        var manifest = new DistributionReleaseManifest(
            DistributionSchemaVersions.ReleaseManifest,
            DistributionManifestKind.ImageRelease,
            new DistributionReleaseIdentity(
                "image", "1.2.3", "image-v1.2.3", "RoySalisbury/HVO.SkyMonitor", revision, new string('b', 40),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z", CultureInfo.InvariantCulture)),
            new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, "placeholder"),
            artifacts,
            null,
            [image]);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trustRoot = DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem());
        manifest = manifest with
        {
            Signing = new DistributionSigningIdentity(DistributionTrustRoot.Algorithm, trustRoot.KeyId)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DistributionJsonContext.Default.DistributionReleaseManifest);
        var manifestPath = Path.Combine(mediaRoot, "image-manifest.json");
        File.WriteAllBytes(manifestPath, bytes);
        File.WriteAllBytes(
            manifestPath + ".sig",
            Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(
                bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) + "\n"));
        return new SignedImageReleaseFixture(mediaRoot, cacheRoot, manifestPath, trustRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(mediaRoot)) Directory.Delete(mediaRoot, recursive: true);
        if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
    }
}
