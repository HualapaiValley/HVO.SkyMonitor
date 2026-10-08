using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Cross-version evidence that a capture retained by one build and processed by another emits the same durable
/// bytes. The same source runs against the baseline and the candidate and uses no candidate-only API: the baseline
/// captures once, and each build processes its own copy of that retained root in place, because the frozen plan
/// names the absolute archive root. Equal dumps mean a retry after an upgrade reproduces every output and outbox
/// manifest under the same key with the same bytes. The <c>interrupted</c> mode handles the standard lane without
/// completing its lease, as a process stopped after committing its outputs, so a later <c>process</c> run with
/// <c>HVO_ISSUE518_RETRY_CLOCK_SECONDS</c> beyond the lease retries that work on top of the committed state.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Issue518UpgradeRetryEvidenceTests
{
    private static readonly DateTimeOffset SceneUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    private static readonly string[] SelectedBodies = ["Sun", "Moon"];

    [TestMethod]
    public async Task RetainedCaptureProcessesToTheSameBytesAcrossBuilds()
    {
        if (Environment.GetEnvironmentVariable("HVO_ISSUE518_RETRY_EVIDENCE") != "1")
            Assert.Inconclusive("Requires an explicitly scheduled #518 upgrade-retry evidence run.");
        var mode = Environment.GetEnvironmentVariable("HVO_ISSUE518_RETRY_MODE");
        Assert.IsTrue(mode is "capture" or "process" or "interrupted");
        var clockSeconds = int.Parse(Environment.GetEnvironmentVariable("HVO_ISSUE518_RETRY_CLOCK_SECONDS") ?? "300",
            System.Globalization.CultureInfo.InvariantCulture);
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("HVO_ISSUE518_RETRY_ROOT")!);
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("HVO_ISSUE518_RETRY_OUTPUT")!);
        Assert.IsFalse(File.Exists(output), "Evidence is append-only; select a fresh output path.");
        if (mode == "capture")
        {
            Assert.IsFalse(Directory.Exists(root));
        }
        else
        {
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "journal")));
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(SceneUtc.AddSeconds(clockSeconds)));
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:AgentId"] = "issue518-retry",
            ["CameraAgent:CentralIntegration:Mode"] = "Enabled",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "true"
        }).Build());
        using var provider = services.BuildServiceProvider();
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var config = CreateConfiguration(root);
        object result;
        if (mode == "capture")
        {
            var module = new VirtualSkyCameraModule(TimeProvider.System,
                provider.GetRequiredService<ICelestialCatalog>(), provider.GetRequiredService<IProjectedSceneStore>(),
                planetEphemeris: new AstronomyEnginePlanetEphemeris(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var request = new CaptureRequest(SceneUtc, TimeSpan.FromSeconds(5), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var captured = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(captured);
            var receipt = await ingress.AcceptAsync(config, new CaptureLoopSubmission(request, captured, SceneUtc,
                request.TargetInterval, TimeSpan.Zero), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            result = new
            {
                mode,
                receipt.Manifest.Descriptor.Capture.CaptureId,
                sceneSchemaVersion = captured.Frame?.Metadata?.Scene?.ProjectedSceneSchemaVersion,
                sceneStageKey = captured.Frame?.Metadata?.Scene?.ProjectedSceneStageKey
            };
        }
        else
        {
            var lanes = provider.GetRequiredService<CaptureLanePolicy>().Definitions;
            var outcomes = new List<object>();
            var standard = lanes.Single(static lane => lane.Name == "standard");
            var upload = lanes.Single(static lane => lane.Name == "upload");
            foreach (var (lane, handler) in new (CaptureLaneDefinition, ICaptureLaneHandler)[]
            {
                (standard, provider.GetRequiredService<StandardCaptureLaneHandler>()),
                (upload, provider.GetRequiredService<UploadCaptureLaneHandler>())
            }.Take(mode == "interrupted" ? 1 : 2))
            {
                var lease = await ingress.ClaimAsync(lane, "issue518-retry", config, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease, lane.Name);
                var handled = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                outcomes.Add(new { lane = lane.Name, outcome = handled.Outcome.ToString(), handled.Reason });
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, handled.Outcome, handled.Reason);
                if (mode == "process")
                    await ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
            result = new { mode, outcomes, files = ReadFiles(root), tables = ReadTables(root) };
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, EvidenceJson))
            .ConfigureAwait(false);
    }

    private static CameraModuleConfig CreateConfiguration(string root)
        => new(
            new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 518,
                maximumResults = 1,
                shotNoiseEnabled = false,
                renderSolarSystemDisks = true,
                solarSystemBodies = SelectedBodies
            })),
            new CameraRigConfig(
                new SensorProfile("Issue518RetryFixture", 96, 96, 5.86, SensorColorMode.Mono,
                    CameraPixelFormat.Mono16, SensorResponseMode.Monochrome, SensorRecipeVersion: "issue518-retry-v1"),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 48, 48, 47,
                    CalibrationVersion: "issue518-retry-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig([
                new("ProjectedScene", "scene", DependsOn: ["$raw"]),
                new("Preview", "preview", DependsOn: ["$raw"]),
                new("Annotation", "annotation", Options: JsonSerializer.SerializeToElement(new
                    { requireProjectedSceneDependency = true }), DependsOn: ["preview", "scene"]),
                new("Storage", "archive", Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = Path.Combine(root, "archive"), QueueForUpload = true, UpdateLatestFrame = false,
                        Policies = [new() { Role = FrameArtifactRole.Metadata, QueueForUpload = false }] }),
                    DependsOn: ["$raw", "scene", "preview", "annotation"])
            ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent),
            AgentId: "issue518-retry");

    private static object[] ReadFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(static path => !path.EndsWith(".db", StringComparison.Ordinal) &&
            !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal) &&
            !path.EndsWith(".lock", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .Select(path => (object)new
        {
            path = Path.GetRelativePath(root, path),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        }).ToArray();

    // Every durable row that names an output or an outbox manifest, with blobs reduced to their SHA-256.
    private static Dictionary<string, object[]> ReadTables(string root)
    {
        var tables = new Dictionary<string, object[]>(StringComparer.Ordinal);
        foreach (var database in Directory.EnumerateFiles(root, "*.db", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            connection.Open();
            foreach (var table in new[] { "processing_outputs", "processing_execution_outputs", "products",
                "artifact_outbox_records", "artifact_outbox_conflicts" })
            {
                using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
                exists.Parameters.AddWithValue("$name", table);
                if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0) continue;
                using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Table names come from the fixed list above.
                command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
#pragma warning restore CA2100
                using var reader = command.ExecuteReader();
                var rows = new List<object>();
                while (reader.Read())
                {
                    var row = new SortedDictionary<string, string?>(StringComparer.Ordinal);
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index) switch
                        {
                            byte[] bytes => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)),
                            var value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                        };
                    }
                    rows.Add(row);
                }
                tables[$"{Path.GetRelativePath(root, database)}:{table}"] = rows.ToArray();
            }
        }
        return tables;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
