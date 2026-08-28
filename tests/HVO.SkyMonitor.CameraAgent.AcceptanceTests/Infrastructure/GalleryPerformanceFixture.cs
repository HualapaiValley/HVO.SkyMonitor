using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

internal sealed class GalleryPerformanceFixture : IDisposable
{
    private static readonly JsonElement EmptyOptions = JsonSerializer.SerializeToElement(new { });
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly ProcessingAlgorithmIdentity[] Algorithms = [new("preview", "issue-106-v1")];
    private static readonly ProcessingCompatibilityIdentity Compatibility = new(
        "rig-v1", "orientation-v1", "calibration-v1", "mask-v1", "sensor-v1", "setpoint-v1", "processing-v1");
    private readonly SqliteCaptureProcessingStore _processingStore;

    private GalleryPerformanceFixture(
        string root,
        IOptions<CameraAgentHostOptions> options,
        SqliteCaptureProcessingStore processingStore)
    {
        Root = root;
        Options = options;
        _processingStore = processingStore;
        Gallery = new SqliteCameraAgentGallery(options, processingStore);
    }

    internal string Root { get; }

    internal string DatabasePath => Path.Combine(Root, "journal", "raw-ingress.db");

    internal IOptions<CameraAgentHostOptions> Options { get; }

    internal SqliteCameraAgentGallery Gallery { get; }

    internal IReadOnlyList<PreviewSeed> PreviewSeeds { get; private set; } = [];

    internal static async Task<GalleryPerformanceFixture> CreateAsync(int captureCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-gallery-performance-{captureCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0,
            RawIngressSqliteBusyTimeoutSeconds = 10,
            ArtifactRead = new ArtifactReadOptions
            {
                MaximumConcurrentPreviews = 2,
                PreviewCacheBytes = 1024 * 1024
            }
        });
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 10);
        var processingStore = new SqliteCaptureProcessingStore(options);
        try
        {
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await processingStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var fixture = new GalleryPerformanceFixture(root, options, processingStore);
            fixture.PreviewSeeds = await fixture.SeedAsync(captureCount).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            processingStore.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed synthetic acceptance statements declared by the harness are explained.")]
    internal async Task<IReadOnlyList<string>> ExplainAsync(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        var details = new List<string>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            details.Add(reader.GetString(3));
        }
        return details;
    }

    internal async Task<string> ReadSqliteVersionAsync()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)
            ?? "unknown";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Synchronous prepared-command execution inside one local SQLite transaction is intentional efficient setup outside the measured workload.")]
    private async Task<IReadOnlyList<PreviewSeed>> SeedAsync(int captureCount)
    {
        var previewSeeds = new List<PreviewSeed>(3);
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = CreateSeedCommand(connection, transaction);
        for (var index = 1; index <= captureCount; index++)
        {
            var captureId = CreateGuid(index, 1);
            var rawArtifactId = CreateGuid(index, 2);
            var rawPayload = CreatePayload(index, 17);
            var rawChecksum = PayloadChecksum.ComputeSha256(rawPayload);
            var source = (index % 3) switch
            {
                0 => "VirtualSky",
                1 => "RandomImage",
                _ => "PhysicalCamera"
            };
            var origin = (index % 3) switch
            {
                0 => GalleryEvidenceOrigin.Simulated,
                1 => GalleryEvidenceOrigin.DeveloperFixture,
                _ => GalleryEvidenceOrigin.Unknown
            };
            var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index);
            var rawManifest = CreateManifest(
                captureId,
                rawArtifactId,
                index,
                started,
                source,
                FrameArtifactRole.Raw,
                "native",
                [],
                RawRecipe,
                rawPayload,
                $"raw/{rawArtifactId:N}.bin",
                origin == GalleryEvidenceOrigin.Simulated ? CreateScene(index) : null);
            var rawEvidence = CaptureContractJson.Serialize(rawManifest);

            var previewPayload = CreatePayload(index, 73);
            var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
                FrameArtifactRole.Preview,
                "gallery",
                PreviewRecipeIdentity,
                [rawArtifactId]);
            var previewArtifactId = ProcessingIdentity.CreateArtifactId(outputIdentity);
            var previewManifest = CreateManifest(
                captureId,
                previewArtifactId,
                index,
                started,
                "preview",
                FrameArtifactRole.Preview,
                "gallery",
                [rawArtifactId],
                PreviewRecipe,
                previewPayload,
                $"derived/{previewArtifactId:N}.bin",
                null);
            var previewEvidence = CaptureContractJson.Serialize(previewManifest);
            var status = (index % 11) switch
            {
                0 => DurableProcessingNodeStatus.TerminalFailure,
                1 => DurableProcessingNodeStatus.RetryableFailure,
                _ => DurableProcessingNodeStatus.Completed
            };

            Set(command, "$capture", captureId.ToString("N"));
            Set(command, "$raw_artifact", rawArtifactId.ToString("N"));
            Set(command, "$sequence", index);
            Set(command, "$descriptor_sha", CaptureContractJson.ComputeDescriptorSha256(rawManifest.Descriptor));
            Set(command, "$manifest_sha", CaptureContractJson.ComputeManifestSha256(rawEvidence));
            Set(command, "$raw_checksum", rawChecksum);
            Set(command, "$raw_path", rawManifest.RelativeArtifactPath);
            Set(command, "$raw_sidecar", $"raw/{rawArtifactId:N}.json");
            Set(command, "$raw_evidence", rawEvidence);
            Set(command, "$exposure_ms", started.ToUnixTimeMilliseconds());
            Set(command, "$durable_ms", started.AddMilliseconds(4).ToUnixTimeMilliseconds());
            Set(command, "$raw_state", index % 17 == 0 ? "quarantined" : "committed");
            Set(command, "$origin", origin.ToString());
            Set(command, "$status", status.ToString());
            Set(command, "$reason", status == DurableProcessingNodeStatus.Completed ? DBNull.Value : "processing.fixture");
            Set(command, "$output_identity", outputIdentity);
            Set(command, "$preview_artifact", previewArtifactId.ToString("N"));
            Set(command, "$preview_path", previewManifest.RelativeArtifactPath);
            Set(command, "$preview_sidecar", $"derived/{previewArtifactId:N}.json");
            Set(command, "$preview_evidence", previewEvidence);
            command.ExecuteNonQuery();

            if (previewSeeds.Count < previewSeeds.Capacity)
            {
                var payloadPath = Path.Combine(Root, previewManifest.RelativeArtifactPath);
                var sidecarPath = Path.Combine(Root, $"derived/{previewArtifactId:N}.json");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
                await File.WriteAllBytesAsync(payloadPath, previewPayload).ConfigureAwait(false);
                await File.WriteAllBytesAsync(sidecarPath, previewEvidence).ConfigureAwait(false);
                previewSeeds.Add(new PreviewSeed(previewArtifactId, previewPayload, previewManifest.Descriptor.Artifact.ChecksumSha256));
            }
        }
        transaction.Commit();
        return previewSeeds;
    }

    private static SqliteCommand CreateSeedCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
            VALUES($capture, $raw_artifact, 'issue-106-gallery', $sequence);
            INSERT INTO raw_captures(
                capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                durable_ingress_unix_ms, committed_unix_ms, state, retention_hold, evidence_origin)
            VALUES($capture, $raw_artifact, 'issue-106-gallery', $sequence, $descriptor_sha,
                $manifest_sha, $raw_checksum, 4, $raw_path, $raw_sidecar, $raw_evidence,
                $exposure_ms, $durable_ms, $durable_ms, $raw_state, 1, $origin);
            INSERT INTO processing_nodes(
                capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
            VALUES($capture, 'preview', 1, '[]', 'gallery-preview', 'Preview', 'gallery',
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                $status, $reason, 1, $durable_ms);
            INSERT INTO processing_outputs(
                output_identity_sha256, capture_id, agent_id, node_id, artifact_id, role, variant,
                payload_relative_path, sidecar_relative_path, descriptor_json, recipe_identity_sha256,
                algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                committed_unix_ms)
            VALUES($output_identity, $capture, 'issue-106-gallery', 'preview', $preview_artifact,
                'Preview', 'gallery', $preview_path, $preview_sidecar, $preview_evidence,
                $preview_recipe_identity, $algorithms, $compatibility, 0, $sequence, $durable_ms);
            """;
        foreach (var name in new[]
        {
            "$capture", "$raw_artifact", "$sequence", "$descriptor_sha", "$manifest_sha",
            "$raw_checksum", "$raw_path", "$raw_sidecar", "$raw_evidence", "$exposure_ms",
            "$durable_ms", "$raw_state", "$origin", "$status", "$reason", "$output_identity",
            "$preview_artifact", "$preview_path", "$preview_sidecar", "$preview_evidence"
        })
        {
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }
        command.Parameters.AddWithValue("$preview_recipe_identity", PreviewRecipeIdentity);
        command.Parameters.AddWithValue("$algorithms", JsonSerializer.SerializeToUtf8Bytes(Algorithms, WebJson));
        command.Parameters.AddWithValue("$compatibility", JsonSerializer.SerializeToUtf8Bytes(Compatibility, WebJson));
        command.Prepare();
        return command;
    }

    private static void Set(SqliteCommand command, string name, object value) => command.Parameters[name].Value = value;

    private static ArtifactManifestV2 CreateManifest(
        Guid captureId,
        Guid artifactId,
        long sequence,
        DateTimeOffset exposureStarted,
        string source,
        FrameArtifactRole role,
        string variant,
        IReadOnlyList<Guid> sourceArtifacts,
        RecipeIdentityDescriptor recipe,
        byte[] payload,
        string relativePath,
        SceneProvenance? scene)
    {
        var hash = new string('B', 64);
        var timing = new CaptureTimingDescriptor(
            exposureStarted.AddMilliseconds(-1),
            exposureStarted,
            exposureStarted.AddMilliseconds(1),
            exposureStarted.AddMilliseconds(2),
            exposureStarted.AddMilliseconds(4));
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor("issue-106-gallery", "rig-v1", sequence, captureId),
            timing,
            new CaptureControlDescriptor(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 1, 1, 0, 0, null, null),
            new CaptureProfileSet(
                new("rig", "1", hash),
                new("calibration", "1", hash),
                new("mask", "1", hash),
                new("sensor", "1", hash),
                new("processing", "1", hash)),
            new FrameLayoutDescriptor(
                2, 2, 2, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable,
                8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, payload.LongLength),
            new ArtifactDescriptor(
                artifactId,
                role,
                source,
                variant,
                timing.ReadoutCompletedUtc,
                sourceArtifacts,
                recipe,
                role == FrameArtifactRole.Raw ? "application/octet-stream" : "application/x-hvo-packed-image",
                PayloadChecksum.ComputeSha256(payload)));
        return new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion, descriptor, relativePath, scene);
    }

    private static SceneProvenance CreateScene(int index) => new(
        $"scene-{index}",
        "rig-v1",
        "performance-fixture",
        "1",
        new string('C', 64),
        "EquidistantFisheye",
        "projection-v1",
        "astronomy-v1",
        "sensor-v1");

    private static Guid CreateGuid(int index, byte kind)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
        bytes[4] = kind;
        bytes[15] = 106;
        return new Guid(bytes);
    }

    private static byte[] CreatePayload(int index, byte salt)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        payload[0] ^= salt;
        return payload;
    }

    private static RecipeIdentityDescriptor RawRecipe { get; } =
        RecipeIdentityDescriptor.Create("capture-raw", "1.0.0", "issue-106-v1", EmptyOptions);

    private static RecipeIdentityDescriptor PreviewRecipe { get; } =
        RecipeIdentityDescriptor.Create("gallery-preview", "1.0.0", "issue-106-v1", EmptyOptions);

    private static string PreviewRecipeIdentity { get; } =
        ProcessingIdentity.CreateRecipeIdentity(PreviewRecipe).IdentitySha256;

    public void Dispose()
    {
        _processingStore.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    internal sealed record PreviewSeed(Guid ArtifactId, byte[] Payload, string ChecksumSha256);
}
