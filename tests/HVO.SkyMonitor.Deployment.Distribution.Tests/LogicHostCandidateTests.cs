using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.ReleaseTool;
using ReleaseProgram = HVO.SkyMonitor.Deployment.ReleaseTool.Program;

namespace HVO.SkyMonitor.Deployment.Distribution.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class LogicHostCandidateTests
{
    [TestMethod]
    public async Task Verify_CompleteLocalCandidate_DerivesIdentityWithoutChangingInputs()
    {
        using var fixture = new CandidateFixture();
        var before = fixture.Snapshot();
        Assert.AreEqual(0, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]));
        CollectionAssert.AreEqual(before, fixture.Snapshot());
        var release = ReleaseManifest.ParseLogicHost(await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.IsNull(release.CameraAgent);
        Assert.AreEqual(fixture.ImageId, release.LogicHost!.OfflineArchives.Single().ConfigDigest);
    }

    [TestMethod]
    [DataRow("manifest-missing")]
    [DataRow("checksums-missing")]
    [DataRow("manifest-altered")]
    [DataRow("asset-altered")]
    [DataRow("asset-missing")]
    [DataRow("asset-length")]
    [DataRow("checksum-disagreement")]
    [DataRow("checksum-extra")]
    [DataRow("checksum-missing")]
    [DataRow("unlisted-file")]
    [DataRow("symlink")]
    [DataRow("hardlink")]
    public async Task Verify_IncompleteOrAlteredFiles_RefusesReadOnly(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture();
        fixture.DamageFiles(fault);
        var before = fixture.Snapshot();
        await AssertRefusedAsync(fixture, fault switch
        {
            "manifest-missing" or "checksums-missing" => "retained file is missing",
            "manifest-altered" or "checksum-missing" or "checksum-extra" => "SHA256SUMS must cover exactly",
            "asset-altered" or "asset-length" or "checksum-disagreement" => "an asset's length or SHA-256 disagrees",
            "hardlink" => "not a regular singly linked file",
            _ => "contain exactly its manifest"
        });
        CollectionAssert.AreEqual(before, fixture.Snapshot());
    }

    [TestMethod]
    [DataRow("product-type")]
    [DataRow("schema")]
    [DataRow("reader")]
    [DataRow("metadata-schema")]
    [DataRow("metadata-unknown")]
    [DataRow("metadata-missing")]
    [DataRow("upgrade-policy")]
    [DataRow("migration")]
    [DataRow("sql-hash")]
    [DataRow("seed-hash")]
    [DataRow("catalog")]
    [DataRow("catalog-package")]
    [DataRow("archive-missing")]
    [DataRow("archive-hash")]
    [DataRow("image-id")]
    [DataRow("manifest-digest")]
    [DataRow("platform")]
    [DataRow("repository")]
    [DataRow("version")]
    [DataRow("commit")]
    [DataRow("created")]
    [DataRow("metadata-disagreement")]
    [DataRow("configuration-format")]
    [DataRow("object-format")]
    [DataRow("checkpoint-format")]
    [DataRow("backup-format")]
    [DataRow("metadata-type")]
    public async Task Verify_ConsistentlyRehashedButIncompatibleMetadata_Refuses(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture();
        var root = fixture.Document;
        var image = root["images"]!["logicHost"]!;
        var archive = image["offlineArchives"]![0]!;
        var metadata = root["componentMetadata"]!["logicHost"]!;
        switch (fault)
        {
            case "product-type": root["product"] = 1; break;
            case "schema": root["schemaVersion"] = 2; break;
            case "reader": metadata["minimumVerifierVersion"] = 2; break;
            case "metadata-schema": metadata["schemaVersion"] = 2; break;
            case "metadata-unknown": metadata["unknown"] = 1; break;
            case "metadata-missing": metadata.AsObject().Remove("configurationVersion"); break;
            case "upgrade-policy": metadata["installationMode"] = "upgrade-anything"; break;
            case "migration": metadata["migrationId"] = "../migration"; break;
            case "sql-hash": metadata["generatedSqlSha256"] = "unknown"; break;
            case "seed-hash": metadata["seedManifestSha256"] = "unknown"; break;
            case "catalog": metadata["requiredCatalogId"] = "fixture"; break;
            case "catalog-package": metadata["requiredPackageVersion"] = "fixture-v1"; break;
            case "archive-missing": image["offlineArchives"] = new JsonArray(); break;
            case "archive-hash": archive["sha256"] = new string('e', 64); break;
            case "image-id": archive["configDigest"] = "sha256:" + new string('e', 64); break;
            case "manifest-digest": archive["manifestDigest"] = "sha256:" + new string('e', 64); break;
            case "platform": image["platforms"]![0] = "linux/arm64"; archive["platform"] = "linux/arm64"; break;
            case "repository": image["repository"] = "ghcr.io/hualapaivalley/skymonitor-cameraagent"; break;
            case "version": root["version"] = "1.2.1"; break;
            case "commit": root["commit"] = new string('a', 40); break;
            case "created": root["created"] = "2026-10-18T18:00:00Z"; break;
            case "metadata-disagreement": metadata["initializationTarget"] = "other-target"; break;
            case "configuration-format": metadata["configurationVersion"] = 2; break;
            case "object-format": metadata["objectLayoutVersion"] = 2; break;
            case "checkpoint-format": metadata["checkpointFormatVersion"] = 2; break;
            case "backup-format": metadata["backupFormatVersion"] = 2; break;
            case "metadata-type": metadata["migrationId"] = 1; break;
        }
        fixture.Save();
        await AssertRefusedAsync(fixture, fault switch
        {
            "product-type" => "only the LogicHost component",
            "schema" => "reads schema 1",
            "reader" or "metadata-schema" or "upgrade-policy" => "schema, verifier version or installation policy is unsupported",
            "metadata-unknown" or "metadata-missing" => "exactly the supported bounded fields",
            "migration" or "sql-hash" or "seed-hash" or "metadata-type" => "schema, initialization, SQL or seed identity",
            "catalog" or "catalog-package" => "not an approved production catalog",
            "archive-missing" => "requires exactly one linux/amd64 offline archive",
            "archive-hash" => "same size and SHA-256",
            "image-id" => "configuration",
            "manifest-digest" => "does not name an image index",
            "platform" => "not named",
            "repository" => "repository is not a",
            "version" => "tag is not v1.2.1",
            "commit" or "created" => "not labelled as LogicHost",
            "metadata-disagreement" => "different compatibility identities",
            _ => "unsupported configuration or object/recovery format"
        });
    }

    [TestMethod]
    [DataRow("component")]
    [DataRow("metadata-duplicate")]
    [DataRow("metadata-absent")]
    [DataRow("metadata")]
    [DataRow("platform")]
    [DataRow("blob")]
    [DataRow("link")]
    [DataRow("traversal")]
    [DataRow("duplicate")]
    public async Task Verify_RehashedArchiveWithWrongContent_Refuses(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture(fault);
        await AssertRefusedAsync(fixture, fault switch
        {
            "component" => "not labelled as LogicHost",
            "metadata" => "exactly the supported bounded fields",
            "metadata-duplicate" => "same property twice",
            "metadata-absent" => "omits its bounded LogicHost",
            "platform" => "configuration is not for linux/amd64",
            "blob" => "not the blob",
            "link" => "something other than a file",
            "traversal" => "not the canonical spelling",
            _ => "holds index.json twice"
        });
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("case")]
    [DataRow("large")]
    [DataRow("deep")]
    public async Task Verify_MalformedManifest_Refuses(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture();
        var json = await File.ReadAllTextAsync(fixture.ManifestPath);
        json = fault switch
        {
            "duplicate" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            "case" => json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal),
            "large" => new string(' ', ReleaseManifest.MaximumLength + 1),
            _ => new string('[', 40) + new string(']', 40)
        };
        await File.WriteAllTextAsync(fixture.ManifestPath, json);
        fixture.Checksums();
        await AssertRefusedAsync(fixture, fault switch
        {
            "duplicate" => "same property twice",
            "case" => "one property in two spellings",
            "large" => "exceeds its size bound",
            _ => "not valid JSON"
        });
    }

    [TestMethod]
    [DataRow("index-extra-platform")]
    [DataRow("index-unknown-platform")]
    [DataRow("index-duplicate-platform")]
    public async Task Verify_IndexPlatformsMustMatchTheReleaseDeclarations(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture(fault);
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => LogicHostCandidate.VerifyAsync(fixture.Root, CancellationToken.None));
        StringAssert.Contains(failure.Message, "platforms must match the release declarations", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("layout-schema")]
    [DataRow("layout-schema-missing")]
    [DataRow("layout-schema-type")]
    [DataRow("manifest-schema")]
    [DataRow("manifest-schema-missing")]
    [DataRow("manifest-schema-type")]
    [DataRow("image-index-schema")]
    [DataRow("image-index-schema-missing")]
    [DataRow("image-index-schema-type")]
    public async Task Verify_UnsupportedOciSchemas_RefusesConsistentlyRehashedCandidates(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture(fault);
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => LogicHostCandidate.VerifyAsync(fixture.Root, CancellationToken.None));
        StringAssert.Contains(error.Message, "schemaVersion 2", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Verify_ExcessiveArchiveEntries_RefusesBeforeKeepingUnboundedMetadata()
    {
        using var fixture = new CandidateFixture("entries");
        var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => LogicHostCandidate.VerifyAsync(fixture.Root, CancellationToken.None));
        StringAssert.Contains(error.Message, "4096-entry bound", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Verify_CancelledRequest_StopsWithoutChangingInputs()
    {
        using var fixture = new CandidateFixture();
        var before = fixture.Snapshot();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => LogicHostCandidate.VerifyAsync(fixture.Root, cancellation.Token));
        var retried = await LogicHostCandidate.VerifyAsync(fixture.Root, CancellationToken.None);
        Assert.AreEqual(OciReleaseFixture.Version, retried.Version);
        CollectionAssert.AreEqual(before, fixture.Snapshot());
    }

    [TestMethod]
    public void CameraAgentReader_DoesNotSelectLogicHost()
    {
        using var fixture = new CandidateFixture();
        Assert.ThrowsExactly<InstallerException>(() => ReleaseManifest.Parse(File.ReadAllBytes(fixture.ManifestPath)));
    }

    [TestMethod]
    [DataRow("linux/arm64")]
    [DataRow("two-platform")]
    public async Task Verify_UnsupportedPlatformDeclarations_ReachTheCentralPolicyGate(string platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        using var fixture = new CandidateFixture(platform: platform == "two-platform" ? "linux/amd64" : platform);
        if (platform == "two-platform")
        {
            fixture.Document["images"]!["logicHost"]!["platforms"] = new JsonArray("linux/amd64", "linux/arm64");
            fixture.Save();
        }
        await AssertRefusedAsync(fixture, "requires exactly one linux/amd64 offline archive");
    }

    [TestMethod]
    [DataRow("subdirectory")]
    [DataRow("checksums-bound")]
    [DataRow("metadata-bound")]
    [DataRow("label-bound")]
    public async Task Verify_DirectoryAndMetadataBounds_RefuseForTheirDeclaredReason(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using var fixture = new CandidateFixture(fault == "label-bound" ? fault : null);
        switch (fault)
        {
            case "subdirectory": Directory.CreateDirectory(Path.Combine(fixture.Root, "unexpected-directory")); break;
            case "checksums-bound": await File.WriteAllTextAsync(Path.Combine(fixture.Root, "SHA256SUMS"), new string(' ', ReleaseChecksums.MaximumLength + 1)); break;
            case "metadata-bound": fixture.Document["componentMetadata"]!["logicHost"]!["initializationTarget"] = new string('a', 16 * 1024); fixture.Save(); break;
        }
        await AssertRefusedAsync(fixture, fault switch
        {
            "subdirectory" => "contain exactly its manifest",
            "checksums-bound" => "exceeds its size bound",
            "metadata-bound" => "exactly the supported bounded fields",
            _ => "omits its bounded LogicHost"
        });
    }

    [TestMethod]
    public async Task Verify_SymbolicLinkAncestor_RefusesBeforeReadingCandidate()
    {
        using var fixture = new CandidateFixture();
        var alias = fixture.Root + "-alias";
        Directory.CreateSymbolicLink(alias, fixture.Root);
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<ReleaseToolException>(() => LogicHostCandidate.VerifyAsync(alias, CancellationToken.None));
            StringAssert.Contains(failure.Message, "directory traverses a symbolic link", StringComparison.Ordinal);
        }
        finally { Directory.Delete(alias); }
    }

    [TestMethod]
    [DataRow("entries-at-limit")]
    [DataRow("image-index-valid")]
    public async Task Verify_SupportedArchiveShapes_Accepts(string shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        using var fixture = new CandidateFixture(shape);
        Assert.AreEqual(0, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]));
    }

    private static async Task AssertRefusedAsync(CandidateFixture fixture, string reason)
    {
        var failure = await Assert.ThrowsAsync<Exception>(() => LogicHostCandidate.VerifyAsync(fixture.Root, CancellationToken.None));
        StringAssert.Contains(failure.Message, reason, StringComparison.Ordinal);
        Assert.AreEqual(1, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]));
    }

    private sealed class CandidateFixture : IDisposable
    {
        private const string Repository = "ghcr.io/hualapaivalley/skymonitor-logichost";
        private readonly string archiveName;
        public string Root { get; } = Directory.CreateTempSubdirectory("hvo-lh-candidate-").FullName;
        public string ManifestPath => Path.Combine(Root, "release.json");
        public JsonObject Document { get; }
        public string ImageId { get; }

        public CandidateFixture(string? fault = null, string platform = "linux/amd64")
        {
            archiveName = $"skymonitor-logichost-{OciReleaseFixture.Version}-{platform.Replace('/', '-')}.oci.tar";
            var metadata = JsonNode.Parse("""
                {"schemaVersion":1,"minimumVerifierVersion":1,"installationMode":"fresh-database-only",
                 "migrationId":"20260828153943_InitialBaseline","initializationTarget":"fixture-target",
                 "generatedSqlSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                 "seedManifestSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                 "configurationVersion":1,"objectLayoutVersion":1,"checkpointFormatVersion":1,"backupFormatVersion":1,
                 "requiredCatalogId":"hyg-v42-production","requiredPackageVersion":"hyg-v4.2-p3-s2-r1"}
                """)!;
            // Reuse the existing build fixture; assemble the distribution archive independently of its writer.
            using var input = OciReleaseFixture.Create();
            input.Build(platforms: [platform], singleManifest: fault is not ("image-index-schema" or "image-index-schema-missing" or "image-index-schema-type" or "image-index-valid"), labels: (_, labels) =>
            {
                labels.Remove("io.hvo.skymonitor.state-contracts");
                labels["io.hvo.skymonitor.component"] = fault == "component" ? "CameraAgent" : "LogicHost";
                labels["io.hvo.skymonitor.logichost-release"] = fault switch
                {
                    "metadata" => "{}",
                    "metadata-duplicate" => metadata.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
                    _ => metadata.ToJsonString()
                };
                if (fault == "metadata-absent") labels.Remove("io.hvo.skymonitor.logichost-release");
                if (fault == "label-bound") labels["io.hvo.skymonitor.logichost-release"] = metadata.ToJsonString().PadRight(16 * 1024 + 1);
            });
            var entries = ReadEntries(input.ImageArchive);
            var (manifest, config, _) = input.Platforms[platform];
            var imageDigest = string.IsNullOrEmpty(input.IndexDigest) ? manifest : input.IndexDigest;
            var layout = JsonNode.Parse(entries["index.json"])!;
            if (fault == "platform")
            {
                var configuration = JsonNode.Parse(entries["blobs/sha256/" + config[7..]])!;
                configuration["architecture"] = "arm64";
                var configurationBytes = Bytes(configuration);
                entries.Remove("blobs/sha256/" + config[7..]);
                config = "sha256:" + Hash(configurationBytes);
                entries["blobs/sha256/" + config[7..]] = configurationBytes;
                var platformManifest = JsonNode.Parse(entries["blobs/sha256/" + manifest[7..]])!;
                platformManifest["config"]!["digest"] = config;
                platformManifest["config"]!["size"] = configurationBytes.Length;
                var manifestBytes = Bytes(platformManifest);
                entries.Remove("blobs/sha256/" + manifest[7..]);
                manifest = imageDigest = "sha256:" + Hash(manifestBytes);
                entries["blobs/sha256/" + manifest[7..]] = manifestBytes;
                layout["manifests"]![0]!["digest"] = manifest;
                layout["manifests"]![0]!["size"] = manifestBytes.Length;
            }
            if (fault is "index-extra-platform" or "index-unknown-platform" or "index-duplicate-platform")
            {
                var descriptor = layout["manifests"]![0]!.DeepClone();
                descriptor["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" };
                var extra = descriptor.DeepClone();
                extra["platform"]!["architecture"] = fault == "index-extra-platform" ? "arm64" : "amd64";
                if (fault == "index-unknown-platform") extra.AsObject().Remove("platform");
                var expandedIndex = new JsonObject
                {
                    ["schemaVersion"] = 2,
                    ["mediaType"] = OciReleaseFixture.IndexMediaType,
                    ["manifests"] = new JsonArray(descriptor, extra)
                };
                var bytes = Bytes(expandedIndex);
                imageDigest = "sha256:" + Hash(bytes);
                entries["blobs/sha256/" + imageDigest[7..]] = bytes;
                layout["manifests"]![0]!["digest"] = imageDigest;
                layout["manifests"]![0]!["size"] = bytes.Length;
                layout["manifests"]![0]!["mediaType"] = OciReleaseFixture.IndexMediaType;
            }
            ImageId = config;
            if (fault == "entries-at-limit")
            {
                var expanded = JsonNode.Parse(entries["blobs/sha256/" + manifest[7..]])!;
                // Six existing file entries, plus 4090 distinct additional layer blobs: exactly 4096 entries.
                for (var index = 0; index < 4090; index++)
                {
                    var bytes = Encoding.ASCII.GetBytes("extra-layer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var digest = "sha256:" + Hash(bytes);
                    entries["blobs/sha256/" + digest[7..]] = bytes;
                    expanded["layers"]!.AsArray().Add(new JsonObject
                    {
                        ["mediaType"] = "application/vnd.oci.image.layer.v1.tar+gzip",
                        ["digest"] = digest,
                        ["size"] = bytes.Length
                    });
                }
                var manifestBytes = Bytes(expanded);
                entries.Remove("blobs/sha256/" + manifest[7..]);
                manifest = imageDigest = "sha256:" + Hash(manifestBytes);
                entries["blobs/sha256/" + manifest[7..]] = manifestBytes;
                layout["manifests"]![0]!["digest"] = manifest;
                layout["manifests"]![0]!["size"] = manifestBytes.Length;
            }
            if (fault is "layout-schema" or "layout-schema-missing" or "layout-schema-type")
            {
                if (fault == "layout-schema-missing") layout.AsObject().Remove("schemaVersion");
                else layout["schemaVersion"] = fault == "layout-schema-type" ? JsonValue.Create("2") : JsonValue.Create(999);
            }
            if (fault is "manifest-schema" or "manifest-schema-missing" or "manifest-schema-type" or "image-index-schema" or "image-index-schema-missing" or "image-index-schema-type")
            {
                var subject = fault.StartsWith("manifest-", StringComparison.Ordinal) ? manifest : imageDigest;
                var invalid = JsonNode.Parse(entries["blobs/sha256/" + subject[7..]])!;
                if (fault.EndsWith("-missing", StringComparison.Ordinal)) invalid.AsObject().Remove("schemaVersion");
                else invalid["schemaVersion"] = fault.EndsWith("-type", StringComparison.Ordinal) ? JsonValue.Create("2") : JsonValue.Create(999);
                var bytes = Bytes(invalid);
                var changed = "sha256:" + Hash(bytes);
                entries.Remove("blobs/sha256/" + subject[7..]);
                entries["blobs/sha256/" + changed[7..]] = bytes;
                imageDigest = changed;
                if (fault.StartsWith("manifest-", StringComparison.Ordinal)) manifest = changed;
                layout["manifests"]![0]!["digest"] = changed;
                layout["manifests"]![0]!["size"] = bytes.Length;
            }
            layout["manifests"]![0]!["annotations"] = new JsonObject
            {
                ["io.containerd.image.name"] = Repository + ":" + OciReleaseFixture.Version,
                ["org.opencontainers.image.ref.name"] = OciReleaseFixture.Version
            };
            entries["index.json"] = Bytes(layout);
            entries["manifest.json"] = Bytes(new JsonArray(new JsonObject
            {
                ["Config"] = "blobs/sha256/" + config[7..],
                ["RepoTags"] = new JsonArray(Repository + ":" + OciReleaseFixture.Version),
                ["Layers"] = new JsonArray([.. JsonNode.Parse(entries["blobs/sha256/" + manifest[7..]])!["layers"]!.AsArray()
                    .Select(static layer => (JsonNode)("blobs/sha256/" + ((string)layer!["digest"]!)[7..]))])
            }));
            if (fault == "blob") Array.Fill(entries["blobs/sha256/" + input.Platforms[platform].Layer[7..]], (byte)'x');
            using (var file = File.Create(Path.Combine(Root, archiveName)))
            using (var tar = new TarWriter(file, TarEntryFormat.Ustar))
            {
                foreach (var (name, bytes) in entries)
                    Write(tar, name, bytes);
                if (fault == "link") tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "oci-layout") { LinkName = "/etc/passwd" });
                if (fault == "traversal") Write(tar, "../escape", "x"u8.ToArray());
                if (fault == "duplicate") Write(tar, "index.json", entries["index.json"]);
                if (fault == "entries")
                {
                    for (var index = 0; index < 4096; index++)
                        Write(tar, "blobs/sha256/" + index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), []);
                }
            }
            File.WriteAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"), "Fixture notices; no published trust.");
            var assets = new JsonArray(Asset(archiveName, "image-archive", platform == "linux/amd64" ? "linux-x64" : "linux-arm64"), Asset("THIRD-PARTY-NOTICES.md", "notices", null));
            var archive = assets[0]!.DeepClone().AsObject();
            archive.Remove("kind");
            archive["platform"] = platform;
            archive["manifestDigest"] = manifest;
            archive["configDigest"] = config;
            Document = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["product"] = "HVO SkyMonitor",
                ["version"] = OciReleaseFixture.Version,
                ["tag"] = "v" + OciReleaseFixture.Version,
                ["prerelease"] = true,
                ["commit"] = OciReleaseFixture.Commit,
                ["created"] = OciReleaseFixture.Created,
                ["repository"] = ReleaseManifest.RepositoryUrl,
                ["images"] = new JsonObject
                {
                    ["logicHost"] = new JsonObject
                    {
                        ["repository"] = Repository,
                        ["tag"] = OciReleaseFixture.Version,
                        ["digest"] = imageDigest,
                        ["reference"] = Repository + ":" + OciReleaseFixture.Version + "@" + imageDigest,
                        ["platforms"] = new JsonArray(platform),
                        ["offlineArchives"] = new JsonArray(archive)
                    }
                },
                ["componentMetadata"] = new JsonObject { ["logicHost"] = metadata },
                ["assets"] = assets
            };
            Save();
        }

        public void Save()
        {
            File.WriteAllBytes(ManifestPath, Bytes(Document));
            Checksums();
        }

        public void Checksums() => File.WriteAllText(Path.Combine(Root, "SHA256SUMS"), string.Concat(
            Directory.GetFiles(Root).Where(p => Path.GetFileName(p) != "SHA256SUMS").Order(StringComparer.Ordinal)
                .Select(p => $"{Hash(File.ReadAllBytes(p))}  {Path.GetFileName(p)}\n")));

        public string[] Snapshot() => Directory.GetFiles(Root).Order(StringComparer.Ordinal).Select(p =>
            Path.GetFileName(p) + ":" + Hash(File.ReadAllBytes(p))).ToArray();

        public void DamageFiles(string fault)
        {
            var notices = Path.Combine(Root, "THIRD-PARTY-NOTICES.md");
            var sums = Path.Combine(Root, "SHA256SUMS");
            switch (fault)
            {
                case "manifest-missing": File.Delete(ManifestPath); break;
                case "checksums-missing": File.Delete(sums); break;
                case "manifest-altered": File.AppendAllText(ManifestPath, " "); break;
                case "asset-altered": File.WriteAllText(notices, "other"); break;
                case "asset-missing": File.Delete(notices); break;
                case "asset-length": Document["assets"]![1]!["size"] = 1; Save(); break;
                case "checksum-disagreement": File.WriteAllText(sums, File.ReadAllText(sums).Replace(Hash(File.ReadAllBytes(notices)), new string('e', 64), StringComparison.Ordinal)); break;
                case "checksum-extra": File.AppendAllText(sums, new string('e', 64) + "  extra\n"); break;
                case "checksum-missing": File.WriteAllText(sums, string.Concat(File.ReadAllLines(sums).Where(l => !l.EndsWith("release.json", StringComparison.Ordinal)).Select(l => l + "\n"))); break;
                case "unlisted-file": File.WriteAllText(Path.Combine(Root, "unexpected"), "extra"); break;
                case "symlink": File.Delete(notices); File.CreateSymbolicLink(notices, ManifestPath); break;
                case "hardlink": File.Delete(notices); using (var process = System.Diagnostics.Process.Start("ln", new[] { ManifestPath, notices })) { process!.WaitForExit(); Assert.AreEqual(0, process.ExitCode); } break;
            }
        }

        private JsonObject Asset(string name, string kind, string? platform) => new()
        {
            ["name"] = name,
            ["kind"] = kind,
            ["platform"] = platform,
            ["size"] = new FileInfo(Path.Combine(Root, name)).Length,
            ["sha256"] = Hash(File.ReadAllBytes(Path.Combine(Root, name)))
        };

        private static Dictionary<string, byte[]> ReadEntries(string path)
        {
            using var file = File.OpenRead(path);
            using var tar = new TarReader(file);
            var entries = new Dictionary<string, byte[]>();
            while (tar.GetNextEntry() is { } entry)
            {
                using var bytes = new MemoryStream();
                entry.DataStream!.CopyTo(bytes);
                entries.Add(entry.Name, bytes.ToArray());
            }
            return entries;
        }

        private static void Write(TarWriter writer, string name, byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = stream });
        }
        private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
