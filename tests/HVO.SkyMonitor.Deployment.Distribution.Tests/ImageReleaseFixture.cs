using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

/// <summary>
/// Builds the inputs a CameraAgent image release is assembled from: one single-platform OCI archive per published
/// architecture, a vulnerability scan report, and the per-platform component inventory the scanner renders from
/// that same scan. Shared by the image release-tool tests and the SPDX document contract tests.
/// </summary>
internal sealed class ImageReleaseFixture : IDisposable
{
    private readonly Dictionary<string, string> manifestDigests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> imageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> componentInventories = new(StringComparer.Ordinal);
    private static readonly string[] Architectures = ["amd64", "arm64"];
    private static readonly string[] InventoryCreators = ["Organization: aquasecurity", "Tool: trivy-0.74.0"];

    private ImageReleaseFixture(string root, DistributionTrustRoot trustRoot)
    {
        Root = root;
        TrustRoot = trustRoot;
        PrivateKey = Path.Combine(root, "private.pem");
        PublicKey = Path.Combine(root, "public.pem");
    }

    public string Root { get; }
    public string PrivateKey { get; }
    public string PublicKey { get; }
    public string KeyId => TrustRoot.KeyId;
    public DistributionTrustRoot TrustRoot { get; }
    public string Amd64Archive { get; private set; } = string.Empty;
    public string Arm64Archive { get; private set; } = string.Empty;
    public string Dockerfile { get; private set; } = string.Empty;
    public string Notices { get; private set; } = string.Empty;
    public string ScanReport { get; private set; } = string.Empty;
    public string[] AllImageIds => [imageIds["amd64"], imageIds["arm64"]];

    public const string Revision = "1f5c1a3b7d9e2f4a6b8c0d1e3f5a7b9c1d3e5f70";
    public const string Tree = "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567";
    public const string MinimumRevision = "70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f";
    public const string IndexDigest = "sha256:3c0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";
    public const string Repository = "ghcr.io/roysalisbury/hvo.skymonitor/cameraagent";

    /// <summary>The component count a real inventory clears easily and the release tool requires.</summary>
    public const int InventoryComponents = 40;

    public string ComponentInventoryFor(string architecture) => componentInventories[architecture];

    public string ManifestDigestFor(string architecture) => manifestDigests[architecture];

    public string ImageIdFor(string architecture) => imageIds[architecture];

    public static ImageReleaseFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-image-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fixture = new ImageReleaseFixture(root, DistributionTrustRoot.FromPem(key.ExportSubjectPublicKeyInfoPem()));
        File.WriteAllText(fixture.PrivateKey, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(fixture.PublicKey, key.ExportSubjectPublicKeyInfoPem());
        fixture.Dockerfile = Path.Combine(root, "Dockerfile");
        File.WriteAllText(fixture.Dockerfile, "FROM scratch\n");
        fixture.Notices = Path.Combine(root, "notices.md");
        File.WriteAllText(fixture.Notices, "test notices\n");
        fixture.Amd64Archive = fixture.WriteArchive("cameraagent-amd64", "amd64", DefaultLabels(Revision));
        fixture.Arm64Archive = fixture.WriteArchive("cameraagent-arm64", "arm64", DefaultLabels(Revision));
        fixture.ScanReport = fixture.WriteScanReport("scan.json", critical: 0, fixture.AllImageIds);
        foreach (var architecture in Architectures)
        {
            fixture.componentInventories[architecture] = fixture.WriteComponentInventory(
                $"components-{architecture}.spdx.json", fixture.ImageIdFor(architecture), InventoryComponents);
        }
        return fixture;
    }

    /// <summary>
    /// Writes a component inventory shaped like the SPDX document Trivy renders from an image scan: a container
    /// package whose annotations record the image ID that was scanned, followed by one package per component.
    /// </summary>
    public string WriteComponentInventory(
        string name,
        string imageId,
        int components,
        string spdxVersion = "SPDX-2.3",
        string? additionalImageId = null,
        bool includeFileWithoutSha1 = false)
    {
        var packages = new List<object>
        {
            new
            {
                name = "/scan/cameraagent-image.tar",
                SPDXID = "SPDXRef-ContainerImage-914ce534c9171676",
                downloadLocation = "NONE",
                filesAnalyzed = false,
                primaryPackagePurpose = "CONTAINER",
                annotations = new[]
                {
                    new
                    {
                        annotator = "Tool: trivy-0.74.0",
                        annotationDate = "2026-08-24T04:29:18Z",
                        annotationType = "OTHER",
                        comment = $"ImageID: {imageId}"
                    }
                }
            }
        };
        if (additionalImageId is not null)
        {
            // A second image claim on an ordinary package: the shape that would let one architecture's inventory
            // be presented as the other's if only the expected claim were counted.
            packages.Add(new
            {
                name = "smuggled",
                SPDXID = "SPDXRef-Package-smuggled",
                versionInfo = "1.0.0",
                downloadLocation = "NONE",
                filesAnalyzed = false,
                primaryPackagePurpose = "LIBRARY",
                annotations = new[]
                {
                    new
                    {
                        annotator = "Tool: trivy-0.74.0",
                        annotationDate = "2026-08-24T04:29:18Z",
                        annotationType = "OTHER",
                        comment = $"ImageID: {additionalImageId}"
                    }
                }
            });
        }
        for (var index = 0; index < components; index++)
        {
            packages.Add(new
            {
                name = $"component-{index}",
                SPDXID = $"SPDXRef-Package-{index}",
                versionInfo = $"1.0.{index}",
                downloadLocation = "NONE",
                filesAnalyzed = false,
                licenseConcluded = "NOASSERTION",
                licenseDeclared = "NOASSERTION",
                primaryPackagePurpose = "LIBRARY",
                externalRefs = new[]
                {
                    new
                    {
                        referenceCategory = "PACKAGE-MANAGER",
                        referenceType = "purl",
                        referenceLocator = $"pkg:deb/debian/component-{index}@1.0.{index}"
                    }
                }
            });
        }
        var path = Path.Combine(Root, name);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                spdxVersion,
                dataLicense = "CC0-1.0",
                SPDXID = "SPDXRef-DOCUMENT",
                name = "/scan/cameraagent-image.tar",
                documentNamespace = "http://trivy.dev/container_image/cameraagent",
                creationInfo = new
                {
                    creators = InventoryCreators,
                    created = "2026-08-24T04:29:18Z"
                },
                packages,
                files = includeFileWithoutSha1
                    ? new[]
                    {
                        new
                        {
                            fileName = "./app/HVO.SkyMonitor.CameraAgent.dll",
                            SPDXID = "SPDXRef-File-1",
                            checksums = new[] { new { algorithm = "SHA256", checksumValue = new string('a', 64) } }
                        }
                    }
                    : null
            }),
            new UTF8Encoding(false));
        return path;
    }

    public static Dictionary<string, string> DefaultLabels(string revision) => new(StringComparer.Ordinal)
    {
        ["io.hvo.skymonitor.component"] = "CameraAgent",
        ["io.hvo.skymonitor.state-compatibility"] = "cameraagent-state-v2",
        ["io.hvo.skymonitor.minimum-compatible-revision"] = MinimumRevision,
        ["io.hvo.skymonitor.identity-migration"] = "20260827053715_InitialIdentity",
        ["io.hvo.skymonitor.raw-ingress-schema"] = "12",
        ["io.hvo.skymonitor.catalog-manifest-version"] = "2",
        ["io.hvo.skymonitor.configuration-contract"] = "cameraagent-install-v1",
        ["io.hvo.skymonitor.catalog-contract"] = "hyg-v42-production-p3-s2",
        ["io.hvo.skymonitor.replay-runner-contract"] = "local-replay-runner-v1",
        ["org.opencontainers.image.revision"] = revision
    };

    public string[] CreateArguments(string output)
        =>
        [
            "create-image", "--version", "1.2.3", "--revision", Revision, "--tree", Tree,
            "--created-utc", "2026-08-24T04:29:18Z", "--repository", Repository, "--index-digest", IndexDigest,
            "--linux-amd64", Amd64Archive, "--linux-arm64", Arm64Archive,
            "--component-sbom-amd64", componentInventories["amd64"],
            "--component-sbom-arm64", componentInventories["arm64"],
            "--dockerfile", Dockerfile,
            "--scan-report", ScanReport, "--notices", Notices, "--signing-key-id", KeyId, "--output", output
        ];

    /// <summary>
    /// The argument set for a candidate that publishes no component inventory. Only a version ending in
    /// <c>-dryrun</c> may omit one, and its manifest stays at release-manifest version 1.
    /// </summary>
    public string[] DryRunArguments(string output)
    {
        var arguments = CreateArguments(output).ToList();
        var index = arguments.IndexOf("--component-sbom-amd64");
        arguments.RemoveRange(index, 4);
        arguments[arguments.IndexOf("--version") + 1] = "1.2.3-dryrun";
        return [.. arguments];
    }

    public string WriteScanReport(string name, int critical, IReadOnlyList<string> subjects)
    {
        var path = Path.Combine(Root, name);
        var report = new
        {
            schemaVersion = 1,
            scanner = "trivy",
            scannerVersion = "0.60.0",
            scannedUtc = "2026-08-24T04:29:18Z",
            subjects = subjects.Select(static imageId => new { imageId }).ToArray(),
            summary = new { critical, high = 0, medium = 0, low = 0, unknown = 0 },
            findings = Array.Empty<object>()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Writes a single-platform OCI archive shaped like a <c>buildx --output type=docker</c> result.</summary>
    public string WriteArchive(string name, string architecture, IReadOnlyDictionary<string, string> labels)
    {
        var configuration = JsonSerializer.SerializeToUtf8Bytes(new
        {
            architecture,
            os = "linux",
            config = new { Labels = labels },
            rootfs = new { type = "layers", diff_ids = Array.Empty<string>() }
        });
        var configDigest = Digest(configuration);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.oci.image.manifest.v1+json",
            config = new
            {
                mediaType = "application/vnd.oci.image.config.v1+json",
                digest = configDigest,
                size = configuration.Length
            },
            layers = Array.Empty<object>()
        });
        var manifestDigest = Digest(manifest);
        var index = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 2,
            mediaType = "application/vnd.oci.image.index.v1+json",
            manifests = new[]
            {
                new
                {
                    mediaType = "application/vnd.oci.image.manifest.v1+json",
                    digest = manifestDigest,
                    size = manifest.Length
                }
            }
        });
        var path = Path.Combine(Root, $"{name}.tar");
        WriteTar(path, new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["oci-layout"] = "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray(),
            ["index.json"] = index,
            [$"blobs/sha256/{manifestDigest["sha256:".Length..]}"] = manifest,
            [$"blobs/sha256/{configDigest["sha256:".Length..]}"] = configuration
        });
        manifestDigests[architecture] = manifestDigest;
        imageIds[architecture] = configDigest;
        return path;
    }

    /// <summary>Writes an archive whose configuration blob no longer hashes to the name it is stored under.</summary>
    public string WriteTamperedArchive(string name)
    {
        var source = Path.Combine(Root, $"{name}.tar");
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var file = File.OpenRead(Amd64Archive))
        using (var reader = new TarReader(file))
        {
            TarEntry? entry;
            while ((entry = reader.GetNextEntry()) is not null)
            {
                using var buffer = new MemoryStream();
                entry.DataStream!.CopyTo(buffer);
                entries[entry.Name] = buffer.ToArray();
            }
        }
        var configEntry = entries.Single(pair =>
            pair.Key.StartsWith("blobs/sha256/", StringComparison.Ordinal) &&
            pair.Key["blobs/sha256/".Length..] == ImageIdFor("amd64")["sha256:".Length..]);
        entries[configEntry.Key] = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(configEntry.Value).Replace("cameraagent-state-v2", "cameraagent-state-v9", StringComparison.Ordinal));
        WriteTar(source, entries);
        return source;
    }

    private static void WriteTar(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var (name, content) in entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            using var stream = new MemoryStream(content);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = stream,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            });
        }
    }

    private static string Digest(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
