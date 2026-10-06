using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>The exact same source runs against baseline and candidate; no compact-contract APIs are used.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Issue1055SceneCompactionEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly DateTimeOffset SceneUtc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] Constellations = ["ORI", "UMA", "CAS"];
    private const int Warmup = 5;
    private const int Measured = 30;

    [TestMethod]
    public async Task RealCapturePublicationAndFreshProcessRecovery()
    {
        if (Environment.GetEnvironmentVariable("HVO_ISSUE1055_EVIDENCE") != "1")
            Assert.Inconclusive("Requires an explicitly scheduled #1055 evidence run.");
        var mode = Required("HVO_ISSUE1055_MODE");
        Assert.IsTrue(mode is "capture" or "cold" or "retention");
        var revision = Required("HVO_EVIDENCE_REVISION");
        Assert.AreEqual(40, revision.Length);
        Assert.IsTrue(GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Contains(revision, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("Release", GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration);
        var root = Path.GetFullPath(Required("HVO_ISSUE1055_ROOT"));
        var output = Path.GetFullPath(Required("HVO_ISSUE1055_OUTPUT"));
        Assert.IsFalse(File.Exists(output), "Evidence is append-only; select a fresh output path.");
        var repository = RepositoryRoot();
        Assert.AreEqual(revision, Git(repository, "rev-parse", "HEAD").Trim());
        Assert.AreEqual(string.Empty, Git(repository, "status", "--porcelain").Trim(), "Commit the harness and implementation before claiming evidence.");
        var harnessPath = Path.Combine(repository, "tests/HVO.SkyMonitor.CameraAgent.Tests/Issue1055SceneCompactionEvidenceTests.cs");
        var harnessSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(harnessPath).ConfigureAwait(false)));
        var assemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(GetType().Assembly.Location).ConfigureAwait(false)));
        if (mode == "capture")
        {
            Assert.IsFalse(Directory.Exists(root));
            Directory.CreateDirectory(root);
        }
        else Assert.IsTrue(Directory.Exists(Path.Combine(root, "canonical", "journal")));
        var canonical = Path.Combine(root, "canonical");
        var archive = Path.Combine(root, "archive");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = canonical,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:AgentId"] = "issue1055-production",
            ["CameraAgent:CentralIntegration:Mode"] = "Enabled"
        }).Build();
        var services = new ServiceCollection();
        using var stageProbe = new StageProbe(Environment.GetEnvironmentVariable("HVO_ISSUE1055_STAGE_PROBE") == "1");
        services.AddLogging(builder =>
        {
            if (stageProbe.Enabled) builder.SetMinimumLevel(LogLevel.Debug).AddProvider(stageProbe);
        });
        CatalogSnapshotResult? snapshot = null;
        if (mode == "capture")
        {
            snapshot = CatalogSnapshotResolver.Resolve(new(Required("HVO_ISSUE1055_CATALOG_ROOT"), "hyg-v42-production"));
            Assert.AreEqual(119625L, snapshot.RowCount);
            Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);
            services.AddSingleton<ICelestialCatalog>(snapshot.Catalog);
        }
        else services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(configuration);
        using var process = Process.GetCurrentProcess();
        var startCpu = process.TotalProcessorTime;
        var startAllocated = GC.GetTotalAllocatedBytes(precise: true);
        var startIo = ReadIo();
        var timer = Stopwatch.StartNew();
        using var provider = services.BuildServiceProvider();
        var ingress = provider.GetRequiredService<RawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var coldMilliseconds = timer.Elapsed.TotalMilliseconds;
        var samples = new List<object>();
        if (mode == "capture")
        {
            var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = Path.Combine(repository, "src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json"),
                AgentId = "issue1055-production",
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Enabled },
                Observatory = new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix")
            }), NullLogger<FileCameraAgentConfigurationLoader>.Instance,
                captureAgentIdentityProvider: new EvidenceIdentityProvider());
            var loaded = await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var moduleOptions = new VirtualSkyCameraModuleOptions
            {
                Seed = 2025,
                MaximumMagnitude = 6.5,
                MaximumResults = 32768,
                MagnitudeZeroElectronsPerSecond = 300,
                BortleClass = 3,
                Asi174Sensor = new() { Enabled = true, BlackLevelAdu = 64 },
                ConstellationIds = Constellations,
                IncludeConstellationEndpointStars = true
            };
            var config = loaded with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(moduleOptions)),
                // Baseline explicit-v2 rejects metadata upload. Both revisions use the same policy:
                // image outbox enabled; canonical structured upload has separate focused identity tests.
                Pipeline = new CapturePipelineConfig([
                    new("ProjectedScene", "scene", DependsOn: ["$raw"]),
                    new("Preview", "preview", DependsOn: ["$raw"]),
                    new("Annotation", "annotation", Options: JsonSerializer.SerializeToElement(new
                    { requireProjectedSceneDependency = true, drawConstellationLines = true, constellationIds = Constellations }),
                        DependsOn: ["preview", "scene"]),
                    new("Storage", "canonical-upload", Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = canonical, QueueForUpload = true, UpdateLatestFrame = false,
                        Policies = [new() { Role = FrameArtifactRole.Metadata, QueueForUpload = false }] }),
                        DependsOn: ["$raw", "scene"]),
                    new("Storage", "archive", Options: JsonSerializer.SerializeToElement(new FileStorageCaptureProcessingStepOptions
                    { StorageRoot = archive, QueueForUpload = true, UpdateLatestFrame = false,
                        Policies = [new() { Role = FrameArtifactRole.Metadata, QueueForUpload = false }] }),
                        DependsOn: ["$raw", "scene", "preview", "annotation"])
                ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot!.Catalog,
                provider.GetRequiredService<IProjectedSceneStore>(), StandardConstellationTopology.CreateD3Celestial(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static item => item.Name == "standard");
            var handler = provider.GetRequiredService<StandardCaptureLaneHandler>();
            for (var ordinal = 0; ordinal < Warmup + Measured; ordinal++)
            {
                stageProbe.Clear();
                var beforeCpu = process.TotalProcessorTime;
                var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
                var beforeIo = ReadIo();
                var sampleTimer = Stopwatch.StartNew();
                var utc = SceneUtc.AddSeconds(ordinal * 25);
                var request = new CaptureRequest(utc, TimeSpan.FromSeconds(25), CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null));
                var stageStart = stageProbe.Start();
                var captured = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
                stageProbe.Record("capture", stageStart);
                Assert.IsNotNull(captured.Frame);
                stageStart = stageProbe.Start();
                var submission = new CaptureLoopSubmission(request, captured, utc, request.TargetInterval, TimeSpan.Zero);
                await ingress.EnsureCanAcceptAsync(captured.Frame.PixelData.Length, CancellationToken.None).ConfigureAwait(false);
                var receipt = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
                stageProbe.Record("ingress", stageStart);
                Assert.IsNotNull(receipt);
                stageStart = stageProbe.Start();
                var lease = await ingress.ClaimAsync(lane, "issue1055-evidence", config, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(lease);
                var outcome = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome, outcome.Reason);
                await ingress.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                stageProbe.Record("lane-inclusive", stageStart);
                if (stageProbe.Enabled) Assert.HasCount(8, stageProbe.Take(), "Three inclusive phases and five processing nodes are required.");
                process.Refresh();
                samples.Add(new
                {
                    ordinal,
                    measured = ordinal >= Warmup,
                    milliseconds = sampleTimer.Elapsed.TotalMilliseconds,
                    cpuMilliseconds = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated,
                    workingSetBytes = process.WorkingSet64,
                    peakWorkingSetBytes = process.PeakWorkingSet64,
                    gc = ReadGc(),
                    io = IoDelta(beforeIo, ReadIo()),
                    stages = stageProbe.Take(),
                    rawSha256 = PayloadChecksum.ComputeSha256(captured.Frame.PixelData.Span),
                    receipt.Manifest.Descriptor.Capture.CaptureSequence,
                    manifestBytes = CaptureContractJson.Serialize(receipt.Manifest).Length
                });
            }
        }
        if (mode == "retention")
        {
            // W3M adds 10,000 metadata records sharing one fixture payload, copied once outside timing.
            // Raw ingress remains canonical; archive Storage intentionally publishes only derivatives.
            var templatePath = Directory.EnumerateFiles(Path.Combine(canonical, "frames"), "*.json", SearchOption.AllDirectories)
                .First(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Raw");
            var template = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(templatePath).ConfigureAwait(false)).Document!.Manifest;
            var fixturePayloadPath = Path.Combine(archive, template.RelativeArtifactPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePayloadPath)!);
            File.Copy(Path.Combine(canonical, template.RelativeArtifactPath), fixturePayloadPath);
            var sidecarDirectory = Path.GetDirectoryName(fixturePayloadPath)!;
            for (var ordinal = 0; ordinal < 10000; ordinal++)
            {
                var foreign = template with
                {
                    Scene = null,
                    Descriptor = template.Descriptor with
                    {
                        Capture = template.Descriptor.Capture with
                        { CaptureId = Guid.Parse($"91000000-0000-0000-0000-{ordinal:D12}"), CaptureSequence = 100000 + ordinal },
                        Artifact = template.Descriptor.Artifact with
                        { ArtifactId = Guid.Parse($"92000000-0000-0000-0000-{ordinal:D12}") }
                    }
                };
                Assert.IsTrue(foreign.Validate().IsValid);
                var path = Path.Combine(sidecarDirectory, $"w3m-{ordinal:D5}.json");
                Assert.IsFalse(File.Exists(path), "Use a fresh producer fixture for each retention trial.");
                await File.WriteAllBytesAsync(path, CaptureContractJson.Serialize(foreign)).ConfigureAwait(false);
            }
            // Prepare the completed-capture state where the raw/canonical pixel policy has expired while
            // archive policy still retains the same capture. This deliberate fixture mutation is outside timing.
            foreach (var path in Directory.EnumerateFiles(Path.Combine(canonical, "frames"), "*.json", SearchOption.AllDirectories))
            {
                var manifest = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).Document?.Manifest;
                if (manifest?.Descriptor.Artifact.Role != FrameArtifactRole.Raw) continue;
                File.Delete(Path.Combine(canonical, manifest.RelativeArtifactPath));
                File.Delete(path);
            }
            var scenePaths = Directory.EnumerateFiles(Path.Combine(canonical, "derived"), "*.json", SearchOption.AllDirectories)
                .Where(path =>
                {
                    using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                    return json.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.GetString() == ProjectedSceneV1.CurrentSchemaVersion;
                }).ToHashSet(StringComparer.Ordinal);
            Assert.HasCount(Warmup + Measured, scenePaths);
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var gate = HVO.SkyMonitor.CameraAgent.Common.Storage.StorageLifecycleLock.ForRoot(canonical);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
                await persistence.ExpireOutputsAsync(canonical, cutoff, scenePaths, CancellationToken.None).ConfigureAwait(false);
                for (var ordinal = 0; ordinal < Warmup + Measured; ordinal++)
                {
                    var beforeCpu = process.TotalProcessorTime;
                    var beforeAllocated = GC.GetTotalAllocatedBytes(precise: true);
                    var beforeIo = ReadIo();
                    var sampleTimer = Stopwatch.StartNew();
                    var deleted = await persistence.ExpireOutputsAsync(canonical, cutoff,
                        new HashSet<string>(StringComparer.Ordinal), CancellationToken.None).ConfigureAwait(false);
                    process.Refresh();
                    samples.Add(new
                    {
                        ordinal,
                        measured = ordinal >= Warmup,
                        milliseconds = sampleTimer.Elapsed.TotalMilliseconds,
                        cpuMilliseconds = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds,
                        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - beforeAllocated,
                        workingSetBytes = process.WorkingSet64,
                        peakWorkingSetBytes = process.PeakWorkingSet64,
                        gc = ReadGc(),
                        io = IoDelta(beforeIo, ReadIo()),
                        deleted,
                        retainedCanonicalScenes = scenePaths.Count(File.Exists)
                    });
                }
            }
            finally { gate.Release(); }
        }
        timer.Stop();
        process.Refresh();
        var evidence = new
        {
            schemaVersion = "issue1055-real-capture-evidence-v1",
            mode,
            revision,
            harnessSha256,
            assemblySha256,
            runtime = Environment.Version.ToString(),
            processorCount = Environment.ProcessorCount,
            baselineProductionRevision = "fead5666bae05f3a51affee94847de93141c56cd",
            catalogSha256 = snapshot?.DatabaseSha256,
            warmup = mode == "cold" ? 0 : Warmup,
            measured = mode == "cold" ? 1 : Measured,
            metadataStressRecords = mode == "retention" ? 10000 : 0,
            aggregateIncludesPreparation = true,
            stageProbeEnabled = stageProbe.Enabled,
            timedOperation = mode switch { "capture" => "capture-to-archive", "cold" => "raw-ingress-initialize", _ => "canonical-derived-expiration" },
            coldMilliseconds,
            elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            cpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - startAllocated,
            workingSetBytes = process.WorkingSet64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            gc = ReadGc(),
            io = IoDelta(startIo, ReadIo()),
            samples,
            files = ReadFiles(root),
            journal = ReadJournal(canonical),
            outputs = ReadOutputs(root),
            outbox = ReadOutbox(root)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions)).ConfigureAwait(false);
    }

    // Symmetric, opt-in test instrumentation. Node spans include persistence; lane spans include nodes.
    // Process-wide counters measure this isolated harness, not exclusive CPU/allocations of an async call.
    private sealed class StageProbe : ILoggerProvider, ILogger
    {
        private const string SnapshotKey = "issue1055.stage-snapshot";
        private const string NameKey = "issue1055.stage-name";
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly List<object> _samples = [];
        private readonly ActivityListener? _listener;
        private bool _disposed;

        public StageProbe(bool enabled)
        {
            Enabled = enabled;
            if (!enabled) return;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "HVO.SkyMonitor.CameraAgent.ProcessingGraph",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.OperationName == "processing-step.execute")
                        activity.SetCustomProperty(SnapshotKey, Start());
                },
                ActivityStopped = activity =>
                {
                    if (activity.GetCustomProperty(SnapshotKey) is StageSnapshot snapshot)
                        Record("node:" + (activity.GetCustomProperty(NameKey) as string ?? "unknown"), snapshot);
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public bool Enabled { get; }
        public StageSnapshot? Start()
        {
            if (!Enabled) return null;
            _process.Refresh();
            return new(Stopwatch.GetTimestamp(), _process.TotalProcessorTime,
                GC.GetTotalAllocatedBytes(precise: true), ReadIo());
        }

        public void Record(string name, StageSnapshot? before)
        {
            if (before is null) return;
            Assert.AreNotEqual("node:unknown", name, "Processing node labels must come from the existing start event.");
            var elapsed = Stopwatch.GetElapsedTime(before.Timestamp).TotalMilliseconds;
            _process.Refresh();
            var sample = new
            {
                name,
                milliseconds = elapsed,
                cpuMilliseconds = (_process.TotalProcessorTime - before.Cpu).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - before.Allocated,
                io = IoDelta(before.Io, ReadIo())
            };
            lock (_samples) _samples.Add(sample);
        }

        public void Clear() { lock (_samples) _samples.Clear(); }
        public object[] Take() { lock (_samples) return [.. _samples]; }
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => Enabled && logLevel == LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 2065 || Activity.Current?.OperationName != "processing-step.execute" ||
                state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            Activity.Current.SetCustomProperty(NameKey, values.FirstOrDefault(item => item.Key == "Step").Value as string);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _listener?.Dispose();
            _process.Dispose();
        }
    }

    private sealed record StageSnapshot(long Timestamp, TimeSpan Cpu, long Allocated, Dictionary<string, long> Io);

    // This isolated fixture publishes only local outbox records; it never registers or uploads to a host.
    private sealed class EvidenceIdentityProvider : ICaptureAgentIdentityProvider
    {
        public ValueTask<string?> GetAgentIdAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>("issue1055-production");
        }
    }

    private static object ReadGc()
    {
        var memory = GC.GetGCMemoryInfo();
        return new
        {
            generation0 = GC.CollectionCount(0),
            generation1 = GC.CollectionCount(1),
            generation2 = GC.CollectionCount(2),
            liveBytes = GC.GetTotalMemory(forceFullCollection: false),
            heapBytes = memory.HeapSizeBytes,
            committedBytes = memory.TotalCommittedBytes,
            fragmentedBytes = memory.FragmentedBytes,
            pauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds
        };
    }

    private static object[] ReadFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(static path => !path.EndsWith("-shm", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => (object)new
        {
            path = Path.GetRelativePath(root, path),
            bytes = new FileInfo(path).Length,
            sha256 = path.EndsWith(".bin", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null
        }).ToArray();

    private static object ReadOutputs(string root)
    {
        var frames = new List<object>();
        var scenes = new List<object>();
        // Journal manifests remain immutable after raw payload expiry and bind retained scene source identity.
        var raw = new Dictionary<Guid, ReconstructionDescriptor>();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "canonical", "journal", "raw-ingress.db")};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT manifest_json FROM raw_captures;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var parsed = CaptureContractJson.ParseManifest(reader.GetFieldValue<byte[]>(0));
                Assert.IsTrue(parsed.IsValid);
                var descriptor = parsed.Document!.Manifest.Descriptor;
                raw.Add(descriptor.Capture.CaptureId, descriptor);
            }
        }
        var jsonFiles = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(static path => !Path.GetFileName(path).StartsWith("w3m-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var path in jsonFiles)
        {
            var bytes = File.ReadAllBytes(path);
            using var json = JsonDocument.Parse(bytes);
            if (!json.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.GetString() != ArtifactManifestV2.CurrentSchemaVersion) continue;
            var parsed = CaptureContractJson.ParseManifest(bytes);
            Assert.IsTrue(parsed.IsValid, path);
            var manifest = parsed.Document!.Manifest;
            var descriptor = manifest.Descriptor;
            var storageRoot = Path.Combine(root, Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0]);
            var payload = File.ReadAllBytes(Path.Combine(storageRoot, manifest.RelativeArtifactPath));
            Assert.AreEqual(descriptor.Artifact.ChecksumSha256, PayloadChecksum.ComputeSha256(payload));
            Assert.AreEqual(descriptor.Layout.ByteLength, payload.LongLength);
            if (descriptor.Artifact.Role == FrameArtifactRole.Raw) raw[descriptor.Capture.CaptureId] = descriptor;
            frames.Add(new
            {
                descriptor.Capture.CaptureSequence,
                role = descriptor.Artifact.Role.ToString(),
                path = Path.GetRelativePath(root, path),
                sha256 = descriptor.Artifact.ChecksumSha256,
                manifestBytes = bytes.Length,
                inlineObjects = manifest.Scene?.Objects?.Count ?? 0,
                inlineSegments = manifest.Scene?.Segments?.Count ?? 0,
                descriptorSha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor)
            });
        }
        foreach (var path in jsonFiles)
        {
            var bytes = File.ReadAllBytes(path);
            using var json = JsonDocument.Parse(bytes);
            if (!json.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.GetString() != ProjectedSceneV1.CurrentSchemaVersion) continue;
            var parsed = ProjectedSceneJson.Parse(bytes);
            Assert.IsTrue(parsed.IsValid, path);
            var scene = parsed.Scene!;
            Assert.IsNotNull(scene.Source);
            var descriptor = raw[scene.Source.CaptureId];
            Assert.AreEqual(descriptor.Artifact.ArtifactId, scene.Source.ArtifactId);
            Assert.AreEqual(CaptureContractJson.ComputeDescriptorSha256(descriptor), scene.Source.ArtifactIdentitySha256);
            // Render the real presentation producer/compositor outside timed capture work. The source-bound
            // scene identity varies with capture GUIDs, but rendered layer pixels must remain identical.
            var layer = PresentationLayerProducers.FromProjectedScene(scene,
                new PresentationAnnotationStyleV1(ConstellationIds: Constellations));
            var layerLayout = new ImageLayout(scene.ImageTransform.OutputWidthPixels,
                scene.ImageTransform.OutputHeightPixels, CameraPixelFormat.Mono8, scene.ImageTransform.OutputWidthPixels);
            var layerPixels = PresentationLayerCompositor.Composite(layerLayout, new byte[layerLayout.RequiredByteLength],
                [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
            scenes.Add(new
            {
                descriptor.Capture.CaptureSequence,
                layerRasterSha256 = PayloadChecksum.ComputeSha256(layerPixels),
                path = Path.GetRelativePath(root, path),
                bytes = bytes.Length,
                objects = scene.Objects.Count,
                segments = scene.Segments.Count,
                geometrySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new { scene.Objects, scene.Segments }),
                scene.SceneIdentitySha256,
                sourceDescriptorSha256 = scene.Source.ArtifactIdentitySha256
            });
        }
        Assert.HasCount(Warmup + Measured, raw);
        return new { frames, scenes };
    }

    private static object[] ReadOutbox(string root)
    {
        var result = new List<object>();
        foreach (var database in Directory.EnumerateFiles(root, "artifact-outbox.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT artifact_id, relative_artifact_path, manifest_bytes FROM artifact_outbox_records ORDER BY artifact_id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var bytes = reader.GetFieldValue<byte[]>(2);
                using var document = JsonDocument.Parse(bytes);
                result.Add(new
                {
                    database = Path.GetRelativePath(root, database),
                    artifactId = reader.GetString(0),
                    path = reader.GetString(1),
                    manifestBytes = bytes.Length,
                    manifestSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    inlineGeometryItems = CountGeometry(document.RootElement)
                });
            }
        }
        return result.ToArray();
    }

    private static int CountGeometry(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) return element.EnumerateArray().Sum(CountGeometry);
        if (element.ValueKind != JsonValueKind.Object) return 0;
        return element.EnumerateObject().Sum(property =>
            property.Name is "objects" or "segments" && property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.GetArrayLength() : CountGeometry(property.Value));
    }

    private static object ReadJournal(string canonical)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(canonical, "journal", "raw-ingress.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM raw_captures),
                   (SELECT COALESCE(SUM(length(manifest_json)), 0) FROM raw_captures),
                   (SELECT COUNT(*) FROM processing_outputs),
                   (SELECT COALESCE(SUM(length(descriptor_json)), 0) FROM processing_outputs);
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        return new
        {
            captures = reader.GetInt64(0),
            rawManifestBytes = reader.GetInt64(1),
            products = reader.GetInt64(2),
            productManifestBytes = reader.GetInt64(3)
        };
    }

    private static Dictionary<string, long> ReadIo() => File.ReadLines("/proc/self/io")
        .Select(static line => line.Split(':', 2))
        .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1], CultureInfo.InvariantCulture), StringComparer.Ordinal);

    private static Dictionary<string, long> IoDelta(Dictionary<string, long> before, Dictionary<string, long> after)
        => after.ToDictionary(static item => item.Key, item => item.Value - before[item.Key], StringComparer.Ordinal);

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required.");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root is unavailable.");
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
        return output;
    }
}
