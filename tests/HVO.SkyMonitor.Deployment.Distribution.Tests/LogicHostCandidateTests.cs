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
        using var fixture = new CandidateFixture();
        fixture.DamageFiles(fault);
        var before = fixture.Snapshot();
        Assert.AreEqual(1, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]), fault);
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
        Assert.AreEqual(1, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]), fault);
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
        using var fixture = new CandidateFixture(fault);
        Assert.AreEqual(1, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]), fault);
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("case")]
    [DataRow("large")]
    [DataRow("deep")]
    public async Task Verify_MalformedManifest_Refuses(string fault)
    {
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
        Assert.AreEqual(1, await ReleaseProgram.Main(["verify-logichost-candidate", "--from", fixture.Root]));
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

    private sealed class CandidateFixture : IDisposable
    {
        private const string Repository = "ghcr.io/hualapaivalley/skymonitor-logichost";
        private const string ArchiveName = "skymonitor-logichost-1.2.0-dev.3-linux-amd64.oci.tar";
        public string Root { get; } = Directory.CreateTempSubdirectory("hvo-lh-candidate-").FullName;
        public string ManifestPath => Path.Combine(Root, "release.json");
        public JsonObject Document { get; }
        public string ImageId { get; }

        public CandidateFixture(string? fault = null)
        {
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
            input.Build(platforms: ["linux/amd64"], singleManifest: true, labels: (_, labels) =>
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
            });
            var entries = ReadEntries(input.ImageArchive);
            var (manifest, config, _) = input.Platforms["linux/amd64"];
            ImageId = config;
            var layout = JsonNode.Parse(entries["index.json"])!;
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
                ["Layers"] = new JsonArray("blobs/sha256/" + input.Platforms["linux/amd64"].Layer[7..])
            }));
            if (fault == "platform")
                entries["blobs/sha256/" + config[7..]] = Encoding.UTF8.GetBytes("{\"os\":\"linux\",\"architecture\":\"arm64\"}");
            if (fault == "blob") entries["blobs/sha256/" + input.Platforms["linux/amd64"].Layer[7..]] = "altered"u8.ToArray();
            using (var file = File.Create(Path.Combine(Root, ArchiveName)))
            using (var tar = new TarWriter(file, TarEntryFormat.Ustar))
            {
                foreach (var (name, bytes) in entries)
                    Write(tar, name, bytes);
                if (fault == "link") tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "unexpected") { LinkName = "/etc/passwd" });
                if (fault == "traversal") Write(tar, "../escape", "x"u8.ToArray());
                if (fault == "duplicate") Write(tar, "index.json", entries["index.json"]);
                if (fault == "entries")
                {
                    for (var index = 0; index < 4096; index++)
                        Write(tar, "blobs/sha256/" + index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), []);
                }
            }
            File.WriteAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"), "Fixture notices; no published trust.");
            var assets = new JsonArray(Asset(ArchiveName, "image-archive", "linux-x64"), Asset("THIRD-PARTY-NOTICES.md", "notices", null));
            var archive = assets[0]!.DeepClone().AsObject();
            archive.Remove("kind");
            archive["platform"] = "linux/amd64";
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
                        ["digest"] = manifest,
                        ["reference"] = Repository + ":" + OciReleaseFixture.Version + "@" + manifest,
                        ["platforms"] = new JsonArray("linux/amd64"),
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
