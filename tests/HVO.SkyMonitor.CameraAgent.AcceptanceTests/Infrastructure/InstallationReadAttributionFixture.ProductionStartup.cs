using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed partial class InstallationReadAttributionFixture
{
    private static readonly string[] ProductionSqliteSuffixes = ["", "-journal", "-wal", "-shm"];
    private static readonly string[] ProductionLedgerFiles = ["retained-integrity.v1.db", "retained-integrity.v1.protected"];
    private static readonly string[] ProductionParameterNames = ["$capture", "$artifact", "$agent", "$sequence", "$descriptor", "$manifest",
        "$payload", "$path", "$json", "$exposure", "$committed"];
    internal sealed record ProductionCorpus(int Captures, long JsonFiles, int SidecarsPerCapture,
        long JsonBytes, string ConstructedJsonSha256, int RecentCaptures);
    internal ProductionCorpus? LastProductionCorpus { get; private set; }

    // Metadata-only production cardinality/layout: four image sidecars plus eight
    // metadata payload/sidecar pairs. Payload shapes/sizes are synthetic; no claim
    // is made that this executes acquisition or all processing recipes.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Synthetic corpus construction deliberately uses synchronous tiny-file writes outside all startup timers.")]
    internal async Task<ProductionCorpus> SeedProductionStartupAsync(int captures, DateTimeOffset now, Action? beforeHostStart = null)
    {
        Assert.IsTrue(Path.GetFileName(Host.Root).StartsWith("iv-", StringComparison.Ordinal));
        var config = await Loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var location = Locations.Active!;
        var state = Path.Combine(IngressRoot, ".location");
        var historyPath = Path.Combine(state, "deployment-location.v1.protected");
        var protector = Host.Services.GetRequiredService<IDeploymentLocationProtector>();
        var history = JsonNode.Parse(protector.Unprotect(await File.ReadAllBytesAsync(historyPath).ConfigureAwait(false)))!.AsObject();
#if !HVO_BASELINE
        Assert.IsTrue(history.Remove("retainedIntegrityNonce"));
#endif
        var legacy = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(history));
        ProductionCorpus? corpus = null;
        await Host.RunWithHostStoppedAsync(async stoppedRoot =>
        {
            // This is construction of a new, marked fixture, not a recovery/reset
            // procedure. No real retained evidence or external root is touched.
            Assert.IsFalse(Directory.Exists(Path.Combine(IngressRoot, "frames")) &&
                Directory.EnumerateFiles(Path.Combine(IngressRoot, "frames"), "*.json", SearchOption.AllDirectories).Any());
            await File.WriteAllBytesAsync(historyPath, legacy).ConfigureAwait(false);
            foreach (var name in ProductionLedgerFiles)
            {
                var file = Path.Combine(state, name);
                foreach (var suffix in ProductionSqliteSuffixes) File.Delete(file + suffix);
            }
            var journal = new SqliteRawCaptureJournal(JournalPath, 10);
            await journal.InitializeAsync([], CancellationToken.None).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={JournalPath};Pooling=False;Foreign Keys=True");
            await connection.OpenAsync().ConfigureAwait(false);
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO raw_capture_assignments(capture_id,raw_artifact_id,agent_id,capture_sequence)
                VALUES($capture,$artifact,$agent,$sequence);
                INSERT INTO raw_captures(capture_id,raw_artifact_id,agent_id,capture_sequence,
                    descriptor_sha256,manifest_sha256,payload_sha256,payload_length,
                    payload_relative_path,sidecar_relative_path,manifest_json,
                    exposure_started_unix_ms,durable_ingress_unix_ms,committed_unix_ms,state,evidence_origin)
                VALUES($capture,$artifact,$agent,$sequence,$descriptor,$manifest,$payload,8,
                    $path||'.bin',$path||'.json',$json,$exposure,$committed,$committed,'committed','DeveloperFixture');
                """;
            foreach (var name in ProductionParameterNames)
                command.Parameters.Add(new SqliteParameter(name, null));
            var payload = new byte[8];
            var original = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, payload);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            var metadataPadding = new string('x', 1024);
            var manifestPadding = new string('y', 2048);
            void Write(string relative, byte[] json)
            {
                var path = Path.Combine(IngressRoot, relative);
                if (directories.Add(Path.GetDirectoryName(path)!)) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, json);
                hash.AppendData(json);
                bytes += json.Length;
            }
            for (var capture = 1; capture <= captures; capture++)
            {
                var scheduled = now.AddSeconds(-20L * (captures - capture + 1));
                var captureId = new Guid(capture, 1041, 5, new byte[8]);
                var rawId = new Guid(capture * 16, 1041, 6, new byte[8]);
                for (var product = 0; product < 4; product++)
                {
                    var artifactId = new Guid(capture * 16 + product, 1041, 6, new byte[8]);
                    var role = product == 0 ? FrameArtifactRole.Raw : product == 2 ? FrameArtifactRole.AnnotatedPreview : FrameArtifactRole.Preview;
                    var exposure = scheduled.AddSeconds(1);
                    var relative = string.Create(CultureInfo.InvariantCulture,
                        $"frames/{exposure:yyyy/MM/dd}/{role}/{exposure:yyyy-MM-dd_HH-mm-ss.fff'Z'}-{artifactId:N}");
                    var item = original with
                    {
                        Descriptor = original.Descriptor with
                        {
                            Capture = original.Descriptor.Capture with { AgentId = config.AgentId!, CaptureSequence = capture, CaptureId = captureId },
                            Timing = new CaptureTimingDescriptor(scheduled, exposure, scheduled.AddSeconds(2), scheduled.AddSeconds(3), scheduled.AddSeconds(4)),
                            Artifact = original.Descriptor.Artifact with
                            { ArtifactId = artifactId, Role = role, CreatedUtc = scheduled.AddSeconds(4), SourceArtifactIds = product == 0 ? [] : [rawId] },
                            Location = location.ToProvenance()
                        },
                        RelativeArtifactPath = relative + ".bin"
                    };
                    var json = CaptureContractJson.Serialize(item);
                    if (capture == 1) Assert.IsTrue(CaptureContractJson.ParseManifest(json).IsValid);
                    Write(relative + ".json", json);
                    File.WriteAllBytes(Path.Combine(IngressRoot, relative + ".bin"), payload);
                    if (product != 0) continue;
                    command.Parameters["$capture"].Value = captureId.ToString("N");
                    command.Parameters["$artifact"].Value = artifactId.ToString("N");
                    command.Parameters["$agent"].Value = config.AgentId!;
                    command.Parameters["$sequence"].Value = capture;
                    command.Parameters["$descriptor"].Value = CaptureContractJson.ComputeDescriptorSha256(item.Descriptor);
                    command.Parameters["$manifest"].Value = CaptureContractJson.ComputeManifestSha256(json);
                    command.Parameters["$payload"].Value = item.Descriptor.Artifact.ChecksumSha256;
                    command.Parameters["$path"].Value = relative;
                    command.Parameters["$json"].Value = json;
                    command.Parameters["$exposure"].Value = exposure.ToUnixTimeMilliseconds();
                    command.Parameters["$committed"].Value = scheduled.AddSeconds(4).ToUnixTimeMilliseconds();
                    _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                for (var product = 0; product < 8; product++)
                {
                    var artifactId = new Guid(capture * 16 + 4 + product, 1041, 6, new byte[8]);
                    var timestamp = scheduled.AddSeconds(4);
                    var relative = string.Create(CultureInfo.InvariantCulture,
                        $"derived/{timestamp:yyyy/MM/dd}/Metadata/{timestamp:yyyy-MM-dd_HH-mm-ss.fff'Z'}-{artifactId:N}");
                    var data = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = "synthetic-metadata-v1", CaptureId = captureId, Product = product, Data = metadataPadding }, EvidenceJson);
                    var sidecar = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = "synthetic-processing-product-v1", CaptureId = captureId, ArtifactId = artifactId,
                        RelativeArtifactPath = relative + ".json", PayloadSha256 = Convert.ToHexString(SHA256.HashData(data)), Data = manifestPadding }, EvidenceJson);
                    Write(relative + ".json", data);
                    Write(relative + ".manifest.json", sidecar);
                }
            }
            await transaction.CommitAsync().ConfigureAwait(false);
            corpus = new ProductionCorpus(captures, 20L * captures, 12, bytes,
                Convert.ToHexString(hash.GetHashAndReset()), Math.Min(captures, 8640));
            LastProductionCorpus = corpus;
            beforeHostStart?.Invoke();
        }).ConfigureAwait(false);
        return corpus!;
    }

    internal async Task<string> CaptureInputCommitmentAsync()
    {
        var root = new byte[64];
        long count = 0;
        foreach (var area in new[] { "frames", "derived" })
        {
            var directory = Path.Combine(IngressRoot, area);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                var digest = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
                var leaf = SHA512.HashData([.. Encoding.UTF8.GetBytes(Path.GetRelativePath(IngressRoot, file)), .. digest]);
                var carry = 0;
                for (var index = 0; index < root.Length; index++)
                {
                    var sum = root[index] + leaf[index] + carry;
                    root[index] = unchecked((byte)sum);
                    carry = sum >> 8;
                }
                count++;
            }
        }
        return string.Create(CultureInfo.InvariantCulture, $"{count}:{Convert.ToHexString(root)}");
    }
}
