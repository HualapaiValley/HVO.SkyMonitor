using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The offline route for the stock Silo image (#1966, G1): an operator-staged <c>docker save --platform linux/ARCH</c>
/// archive is verified before anything loads it. The fixtures have the shape a containerd-store <c>docker save</c> of the
/// pinned reference writes: the layout directories, content-addressed blobs, an index naming the platform manifest and
/// its provenance attestation, <c>manifest.json</c> for the classic store, and <c>oci-layout</c>. Each test pins the
/// fixture's own digests, as the installer pins upstream's, and every inconsistency is refused before mutation.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloImageArchiveTests
{
    private const string OciManifest = "application/vnd.oci.image.manifest.v1+json";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Verify_TheShapeDockerSaveWrites_IsAccepted()
    {
        var archive = Build();

        await VerifyAsync(archive);
    }

    [TestMethod]
    public async Task Verify_AnArchiveSavedByThePinnedReleaseTag_IsAccepted()
    {
        var archive = Build(new Options
        {
            PinnedAnnotations = new JsonObject
            {
                ["containerd.io/distribution.source.docker.io"] = "pgsty/silo",
                ["io.containerd.image.name"] = $"docker.io/pgsty/silo:{SiloImagePin.Release}",
                ["org.opencontainers.image.ref.name"] = SiloImagePin.Release
            },
            RepoTags = [$"pgsty/silo:{SiloImagePin.Release}"]
        });

        await VerifyAsync(archive);
    }

    [TestMethod]
    public async Task Verify_AnArchiveWithoutAttestation_IsAccepted()
        => await VerifyAsync(Build(new Options { Attestation = false }));

    [TestMethod]
    public async Task Verify_TheRealPinAgainstAnotherImage_RefusesBeforeLoading()
    {
        var archive = Build();
        var path = Write(archive.Entries);

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => ReleaseImageArchive.VerifySiloArchiveAsync(
            path, SiloImagePin.ForArchitecture("amd64")!, TestContext.CancellationToken));

        AssertRefused(refusal, "does not hold the pinned linux/amd64 manifest");
    }

    [TestMethod]
    [DataRow("tampered-layer", "does not hash to its name")]
    [DataRow("tampered-config", "does not hash to its name")]
    [DataRow("missing-layer", "a layer of the platform manifest is missing")]
    [DataRow("missing-config", "does not hold the pinned config")]
    [DataRow("unused-blob", "which the pinned image does not use")]
    [DataRow("no-manifest-json", "it has no manifest.json")]
    [DataRow("no-oci-layout", "it has no oci-layout")]
    [DataRow("no-index", "it has no index.json")]
    [DataRow("foreign-entry", "which is not part of an OCI image layout")]
    [DataRow("duplicate-entry", "twice")]
    [DataRow("not-a-tar", "is not a readable tar archive")]
    public async Task Verify_AnArchiveThatIsNotThePinnedImageBlobForBlob_IsRefused(string change, string reason)
    {
        var archive = Build();
        var entries = archive.Entries;
        var layer = archive.LayerBlobs[0];
        switch (change)
        {
            case "tampered-layer": Replace(entries, layer, "tampered"u8.ToArray()); break;
            case "tampered-config": Replace(entries, archive.ConfigBlob, Encoding.UTF8.GetBytes("{\"os\":\"linux\"}")); break;
            case "missing-layer": entries.RemoveAll(entry => entry.Name == layer); break;
            case "missing-config": entries.RemoveAll(entry => entry.Name == archive.ConfigBlob); break;
            case "unused-blob": AddBlob(entries, "an extra image layer"u8.ToArray()); break;
            case "no-manifest-json": entries.RemoveAll(static entry => entry.Name == "manifest.json"); break;
            case "no-oci-layout": entries.RemoveAll(static entry => entry.Name == "oci-layout"); break;
            case "no-index": entries.RemoveAll(static entry => entry.Name == "index.json"); break;
            case "foreign-entry": entries.Add(("repositories", "{}"u8.ToArray())); break;
            case "duplicate-entry": entries.Add(("index.json", entries.Single(static entry => entry.Name == "index.json").Data)); break;
        }
        var path = change == "not-a-tar" ? WriteRaw(new byte[2048].Select(static (_, index) => (byte)(index % 251 + 1)).ToArray()) : Write(entries);

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            ReleaseImageArchive.VerifySiloArchiveAsync(path, archive.Platform, TestContext.CancellationToken));

        Assert.IsTrue(refusal.IsRefusal, refusal.Message);
        StringAssert.Contains(refusal.Message, reason, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("another-image", "names an image other than the pinned linux/amd64 manifest")]
    [DataRow("pinned-twice", "exactly once")]
    [DataRow("pinned-absent", "exactly once")]
    [DataRow("other-platform", "for a platform other than linux/amd64")]
    [DataRow("foreign-name", "something other than the pinned release")]
    [DataRow("foreign-ref-name", "something other than the pinned release")]
    [DataRow("unknown-annotation", "something other than the pinned release")]
    [DataRow("size-mismatch", "does not describe the pinned linux/amd64 manifest as stored")]
    [DataRow("attestation-of-another", "names an image other than the pinned linux/amd64 manifest")]
    [DataRow("attestation-named", "names an image other than the pinned linux/amd64 manifest")]
    [DataRow("index-field", "has a field 'annotations'")]
    public async Task Verify_AnIndexThatLoadsOrNamesAnythingElse_IsRefused(string change, string reason)
    {
        var archive = Build(new Options
        {
            ChangeIndex = (index, pinned, attestation) =>
            {
                var manifests = index["manifests"]!.AsArray();
                switch (change)
                {
                    case "another-image":
                        manifests.Add(new JsonObject { ["mediaType"] = OciManifest, ["digest"] = "sha256:" + new string('a', 64), ["size"] = 10 });
                        break;
                    case "pinned-twice": manifests.Add(pinned.DeepClone()); break;
                    case "pinned-absent": manifests.Remove(pinned); break;
                    case "other-platform": pinned["platform"]!["architecture"] = "arm64"; break;
                    case "foreign-name": pinned["annotations"]!["io.containerd.image.name"] = "docker.io/hvo/skymonitor-cameraagent:1.2.0"; break;
                    case "foreign-ref-name": pinned["annotations"]!["org.opencontainers.image.ref.name"] = "latest"; break;
                    case "unknown-annotation": pinned["annotations"]!["io.example.load-hook"] = "x"; break;
                    case "size-mismatch": pinned["size"] = pinned["size"]!.GetValue<int>() + 1; break;
                    case "attestation-of-another": attestation!["annotations"]!["io.containerd.manifest.subject"] = "sha256:" + new string('b', 64); break;
                    case "attestation-named": attestation!["annotations"]!["io.containerd.image.name"] = "docker.io/hvo/other:1"; break;
                    case "index-field": index["annotations"] = new JsonObject { ["io.containerd.image.name"] = "docker.io/hvo/other:1" }; break;
                }
            }
        });

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => VerifyAsync(archive));

        AssertRefused(refusal, reason);
    }

    [TestMethod]
    [DataRow("other-config")]
    [DataRow("other-layers")]
    [DataRow("foreign-tag")]
    [DataRow("two-images")]
    [DataRow("extra-field")]
    public async Task Verify_AManifestJsonTheClassicStoreWouldLoadDifferently_IsRefused(string change)
    {
        var archive = Build(new Options
        {
            ChangeDockerManifest = (manifest, configBlob, layers) =>
            {
                var only = manifest[0]!.AsObject();
                switch (change)
                {
                    case "other-config": only["Config"] = layers[0]; break;
                    case "other-layers": only["Layers"] = new JsonArray(JsonValue.Create(layers[0])); break;
                    case "foreign-tag": only["RepoTags"] = new JsonArray(JsonValue.Create("hvo/skymonitor-cameraagent:1.2.0")); break;
                    case "two-images": manifest.Add(only.DeepClone()); break;
                    case "extra-field": only["Parent"] = configBlob; break;
                }
            }
        });

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => VerifyAsync(archive));

        Assert.IsTrue(refusal.IsRefusal);
        StringAssert.Contains(refusal.Message, "manifest.json", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("architecture", "is not the pinned Silo release for linux/amd64")]
    [DataRow("os", "is not the pinned Silo release for linux/amd64")]
    [DataRow("release", "is not the pinned Silo release for linux/amd64")]
    [DataRow("revision", "is not the pinned Silo release for linux/amd64")]
    [DataRow("license", "is not the pinned Silo release for linux/amd64")]
    [DataRow("attestation-subject", "is not an attestation of the pinned manifest")]
    public async Task Verify_ConsistentContentThatIsNotThePinnedRelease_IsRefused(string change, string reason)
    {
        var archive = Build(new Options
        {
            ChangeConfig = config =>
            {
                var labels = config["config"]!["Labels"]!;
                switch (change)
                {
                    case "architecture": config["architecture"] = "arm64"; break;
                    case "os": config["os"] = "windows"; break;
                    case "release": labels["org.opencontainers.image.version"] = "RELEASE.2026-01-01T00-00-00Z"; break;
                    case "revision": labels["org.opencontainers.image.revision"] = new string('9', 40); break;
                    case "license": labels["org.opencontainers.image.licenses"] = "Apache-2.0"; break;
                }
            },
            AttestationSubject = change == "attestation-subject" ? "sha256:" + new string('c', 64) : null
        });

        var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => VerifyAsync(archive));

        AssertRefused(refusal, reason);
    }

    private static void AssertRefused(InstallerException refusal, string reason)
    {
        Assert.IsTrue(refusal.IsRefusal, refusal.Message);
        StringAssert.Contains(refusal.Message, reason, StringComparison.Ordinal);
        StringAssert.EndsWith(refusal.Message, "Nothing was loaded.", StringComparison.Ordinal);
    }

    private Task VerifyAsync(Archive archive)
        => ReleaseImageArchive.VerifySiloArchiveAsync(Write(archive.Entries), archive.Platform, TestContext.CancellationToken);

    internal sealed record Options
    {
        public bool Attestation { get; init; } = true;
        public JsonObject? PinnedAnnotations { get; init; }
        public string[]? RepoTags { get; init; }
        public string? AttestationSubject { get; init; }
        public Action<JsonObject>? ChangeConfig { get; init; }
        public Action<JsonObject, JsonObject, JsonObject?>? ChangeIndex { get; init; }
        public Action<JsonArray, string, string[]>? ChangeDockerManifest { get; init; }
    }

    internal sealed record Archive(List<(string Name, byte[] Data)> Entries, SiloImagePin.PlatformImage Platform, string ConfigBlob, string[] LayerBlobs);

    /// <summary>A linux/amd64 image of the pinned release in the layout docker save writes, and the pin of its digests.</summary>
    internal static Archive Build(Options? options = null)
    {
        options ??= new Options();
        var entries = new List<(string Name, byte[] Data)>();
        var layers = new[] { "first layer of the image"u8.ToArray(), "second layer of the image"u8.ToArray() };
        var layerDescriptors = new JsonArray();
        var layerBlobs = new List<string>();
        foreach (var layer in layers)
        {
            var digest = AddBlob(entries, layer);
            layerBlobs.Add(Blob(digest));
            layerDescriptors.Add(new JsonObject { ["mediaType"] = "application/vnd.oci.image.layer.v1.tar+gzip", ["digest"] = digest, ["size"] = layer.Length });
        }
        var config = new JsonObject
        {
            ["architecture"] = "amd64",
            ["os"] = "linux",
            ["config"] = new JsonObject
            {
                ["Labels"] = new JsonObject
                {
                    ["org.opencontainers.image.version"] = SiloImagePin.Release,
                    ["org.opencontainers.image.revision"] = SiloImagePin.SourceRevision,
                    ["org.opencontainers.image.licenses"] = SiloImagePin.License
                }
            },
            ["rootfs"] = new JsonObject { ["type"] = "layers", ["diff_ids"] = new JsonArray() }
        };
        options.ChangeConfig?.Invoke(config);
        var configBytes = Encoding.UTF8.GetBytes(config.ToJsonString());
        var configDigest = AddBlob(entries, configBytes);
        var manifestBytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = OciManifest,
            ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.image.config.v1+json", ["digest"] = configDigest, ["size"] = configBytes.Length },
            ["layers"] = layerDescriptors
        }.ToJsonString());
        var manifestDigest = AddBlob(entries, manifestBytes);

        var pinned = new JsonObject
        {
            ["mediaType"] = OciManifest,
            ["digest"] = manifestDigest,
            ["size"] = manifestBytes.Length,
            ["annotations"] = options.PinnedAnnotations ?? new JsonObject { ["containerd.io/distribution.source.docker.io"] = "pgsty/silo" },
            ["platform"] = new JsonObject { ["architecture"] = "amd64", ["os"] = "linux" }
        };
        var index = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.index.v1+json",
            ["manifests"] = new JsonArray(pinned)
        };
        JsonObject? attestationDescriptor = null;
        if (options.Attestation)
        {
            var emptyDigest = AddBlob(entries, "{}"u8.ToArray());
            var statement = "{\"_type\":\"https://in-toto.io/Statement/v0.1\"}"u8.ToArray();
            var statementDigest = AddBlob(entries, statement);
            var attestationBytes = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["schemaVersion"] = 2,
                ["mediaType"] = OciManifest,
                ["artifactType"] = ReleaseImageArchive.AttestationArtifactType,
                ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.empty.v1+json", ["digest"] = emptyDigest, ["size"] = 2 },
                ["layers"] = new JsonArray(new JsonObject { ["mediaType"] = "application/vnd.in-toto+json", ["digest"] = statementDigest, ["size"] = statement.Length }),
                ["subject"] = new JsonObject { ["mediaType"] = OciManifest, ["digest"] = options.AttestationSubject ?? manifestDigest, ["size"] = manifestBytes.Length }
            }.ToJsonString());
            var attestationDigest = AddBlob(entries, attestationBytes);
            attestationDescriptor = new JsonObject
            {
                ["mediaType"] = OciManifest,
                ["digest"] = attestationDigest,
                ["size"] = attestationBytes.Length,
                ["annotations"] = new JsonObject { ["io.containerd.manifest.subject"] = manifestDigest },
                ["artifactType"] = ReleaseImageArchive.AttestationArtifactType
            };
            index["manifests"]!.AsArray().Add(attestationDescriptor);
        }
        options.ChangeIndex?.Invoke(index, pinned, attestationDescriptor);

        var dockerManifest = new JsonArray(new JsonObject
        {
            ["Config"] = Blob(configDigest),
            ["RepoTags"] = options.RepoTags is null ? null : new JsonArray([.. options.RepoTags.Select(static tag => (JsonNode)JsonValue.Create(tag))]),
            ["Layers"] = new JsonArray([.. layerBlobs.Select(static blob => (JsonNode)JsonValue.Create(blob))])
        });
        options.ChangeDockerManifest?.Invoke(dockerManifest, Blob(configDigest), [.. layerBlobs]);

        entries.Add(("index.json", Encoding.UTF8.GetBytes(index.ToJsonString())));
        entries.Add(("manifest.json", Encoding.UTF8.GetBytes(dockerManifest.ToJsonString())));
        entries.Add(("oci-layout", "{\"imageLayoutVersion\":\"1.0.0\"}"u8.ToArray()));
        return new Archive(entries, new SiloImagePin.PlatformImage("linux/amd64", "amd64", manifestDigest, configDigest), Blob(configDigest), [.. layerBlobs]);
    }

    private static string AddBlob(List<(string Name, byte[] Data)> entries, byte[] data)
    {
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));
        if (!entries.Any(entry => entry.Name == Blob(digest)))
            entries.Add((Blob(digest), data));
        return digest;
    }

    private static string Blob(string digest) => "blobs/sha256/" + digest["sha256:".Length..];

    private static void Replace(List<(string Name, byte[] Data)> entries, string name, byte[] data)
    {
        var index = entries.FindIndex(entry => entry.Name == name);
        entries[index] = (name, data);
    }

    private string Write(List<(string Name, byte[] Data)> entries)
    {
        var path = Path.Combine(TestContext.TestRunDirectory ?? Path.GetTempPath(), $"silo-{Guid.NewGuid():N}.tar");
        WriteTar(path, entries);
        return path;
    }

    /// <summary>Writes <paramref name="entries"/> as docker save lays them out: the blob directories, then each entry.</summary>
    internal static void WriteTar(string path, List<(string Name, byte[] Data)> entries)
    {
        using var file = File.Create(path);
        using var tar = new TarWriter(file, TarEntryFormat.Pax, leaveOpen: false);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "blobs/"));
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "blobs/sha256/"));
        foreach (var (name, data) in entries)
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
    }

    private string WriteRaw(byte[] data)
    {
        var path = Path.Combine(TestContext.TestRunDirectory ?? Path.GetTempPath(), $"silo-{Guid.NewGuid():N}.tar");
        File.WriteAllBytes(path, data);
        return path;
    }
}
