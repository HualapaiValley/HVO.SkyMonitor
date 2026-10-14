using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Issue #1170 S2-S4: the shipped CameraAgent graph composed exactly as the host composes it (module capture,
/// projected-scene staging, raw ingress, standard lane), one isolated process per trial. The same source runs
/// against baseline and candidate revisions; the runner in docs/validation selects the scenario and workload.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class Issue1170ComposedPipelineEvidenceTests
{
    private const int Warmup = 5;
    private const int Measured = 30;
    private const string SchemaVersion = "issue1170-composed-pipeline-evidence-v1";
    private const string CatalogSha256 = "B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly DateTimeOffset NightUtc = new(2026, 2, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayUtc = new(2026, 2, 10, 20, 0, 0, TimeSpan.Zero);
    private static readonly string[] SampleAnnotationInputs = ["constellationIds", "includeConstellationEndpointStars", "solarSystemBodies"];
    // Runtime knobs that change JIT/codegen. Production-default cells must not set any of them.
    private static readonly string[] CodegenOverrides =
        ["DOTNET_TieredCompilation", "DOTNET_TC_QuickJitForLoops", "DOTNET_TieredPGO", "DOTNET_ReadyToRun",
            "DOTNET_TC_QuickJit", "DOTNET_OSR_HitLimit", "DOTNET_EnableAVX2", "DOTNET_EnableHWIntrinsic"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ComposedShippedGraph()
    {
        if (Environment.GetEnvironmentVariable("HVO_PIPELINE_EVIDENCE") != "1")
            Assert.Inconclusive("Requires an explicitly scheduled #1170 evidence run (docs/validation/issue-1170-pipeline.sh).");
        var revision = Required("HVO_EVIDENCE_REVISION");
        Assert.IsTrue(revision is { Length: 40 } && revision.All(Uri.IsHexDigit));
        var binaryRevision = GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.IsTrue(binaryRevision.Contains(revision, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("Release", GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration);
        var scenario = Required("HVO_PIPELINE_SCENARIO");
        Assert.IsTrue(scenario is "steady" or "cold" or "saturation", scenario);
        var workload = Workload.Find(Required("HVO_PIPELINE_WORKLOAD"));
        var label = Required("HVO_PIPELINE_LABEL");
        var trial = int.Parse(Required("HVO_PIPELINE_TRIAL"), CultureInfo.InvariantCulture);
        var runtimeProfile = Required("HVO_PIPELINE_RUNTIME_PROFILE");
        var dotnetVariables = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(static key => key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.StartsWith("COMPlus_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToDictionary(static key => key, static key => Environment.GetEnvironmentVariable(key), StringComparer.Ordinal);
        if (runtimeProfile == "production-defaults")
            Assert.IsFalse(CodegenOverrides.Any(dotnetVariables.ContainsKey) || dotnetVariables.Keys.Any(static key => key.StartsWith("COMPlus_", StringComparison.Ordinal)),
                "Production-default cells must not override JIT or codegen settings.");
        else Assert.AreEqual("tc0-side-cell", runtimeProfile, "Only production-defaults or the labelled tc0-side-cell are defined.");
        if (runtimeProfile == "tc0-side-cell") Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"));
        var output = Path.GetFullPath(Required("HVO_PIPELINE_OUTPUT"));
        Assert.IsFalse(File.Exists(output), "Evidence is append-only; select a fresh output path.");
        var root = Path.GetFullPath(Required("HVO_PIPELINE_WORK_ROOT"));
        Assert.IsFalse(Directory.Exists(root), "Each trial composes a fresh durable root.");
        var catalogRoot = Required("HVO_ASTROMETRY_CATALOG_ROOT");
        // Attribution runs only: publish this testhost's PID and wait until the external EventPipe collector
        // (dotnet-trace) reports it is attached. Measurement runs leave this unset and run without collectors.
        var attachDirectory = Environment.GetEnvironmentVariable("HVO_PIPELINE_ATTACH_DIR");
        if (!string.IsNullOrWhiteSpace(attachDirectory))
            await WaitForCollectorAsync(attachDirectory).ConfigureAwait(false);

        var assemblySha256 = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(GetType().Assembly.Location).ConfigureAwait(false)));
        using var process = Process.GetCurrentProcess();
        using var meters = new MeterProbe();
        var setup = new Dictionary<string, object>(StringComparer.Ordinal);
        var samples = new List<OperationSample>();
        var failures = new List<string>();
        var captureIds = new List<Guid>();
        object? saturation = null;
        object? outputs = null;
        CameraModuleConfig? config = null;
        var start = Probe.Take(process);
        Directory.CreateDirectory(root);
        try
        {
            var phase = Probe.Take(process);
            var snapshot = CatalogSnapshotResolver.Resolve(new(catalogRoot, "hyg-v42-production"));
            Assert.AreEqual(119625L, snapshot.RowCount);
            Assert.AreEqual(CatalogSha256, snapshot.DatabaseSha256);
            setup["catalogResolve"] = Probe.Take(process).Since(phase);

            phase = Probe.Take(process);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICelestialCatalog>(snapshot.Catalog);
            services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = Path.Combine(root, "ingress"),
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:AgentId"] = "issue1170-evidence",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
            services.AddCameraModule<VirtualSkyCameraModule>("VirtualSky");
            var provider = services.BuildServiceProvider();
            await using var providerLifetime = provider.ConfigureAwait(false);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            setup["providerAndIngress"] = Probe.Take(process).Since(phase);

            phase = Probe.Take(process);
            config = await LoadConfigurationAsync(workload, root).ConfigureAwait(false);
            _ = await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
                .EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            setup["configurationAndGraph"] = Probe.Take(process).Since(phase);

            phase = Probe.Take(process);
            var module = provider.GetRequiredService<ICameraModuleFactory>().Create(config.ModuleType);
            await using var moduleLifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            setup["moduleInitialize"] = Probe.Take(process).Since(phase);

            var pipeline = new Pipeline(
                module,
                new CaptureHostContext(config, ingress, provider.GetRequiredService<ICaptureDistributor>(), null,
                    provider.GetRequiredService<IProjectedSceneStagingStore>(),
                    provider.GetRequiredService<ProjectedSceneStageLifecycleCoordinator>(),
                    provider.GetRequiredService<CaptureProjectedSceneStager>()),
                provider.GetRequiredService<ICaptureLaneStore>(),
                provider.GetServices<ICaptureLaneHandler>().Single(static handler => handler.Lane == "standard"),
                provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "standard"),
                config, workload);

            if (scenario == "cold")
            {
                var first = await pipeline.RunAsync(0, process, meters).ConfigureAwait(false);
                samples.Add(first);
                captureIds.Add(first.CaptureId);
                setup["firstOperation"] = first.Stages;
                setup["testStartToFirstCompletion"] = Probe.Take(process).Since(start);
                setup["processUptimeAtFirstCompletionMilliseconds"] = (DateTime.Now - process.StartTime).TotalMilliseconds;
            }
            else
            {
                for (var ordinal = 0; ordinal < Warmup; ordinal++)
                {
                    var warm = await pipeline.RunAsync(ordinal, process, meters).ConfigureAwait(false);
                    samples.Add(warm with { Measured = false });
                    captureIds.Add(warm.CaptureId);
                }
                if (scenario == "steady")
                {
                    for (var ordinal = Warmup; ordinal < Warmup + Measured; ordinal++)
                    {
                        var sample = await pipeline.RunAsync(ordinal, process, meters).ConfigureAwait(false);
                        samples.Add(sample);
                        captureIds.Add(sample.CaptureId);
                    }
                }
                else saturation = await RunSaturationAsync(pipeline, process, meters, samples, captureIds).ConfigureAwait(false);
            }
            outputs = await ReadOutputsAsync(provider.GetRequiredService<SqliteCaptureProcessingStore>(), config, captureIds, failures)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception.ToString());
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(attachDirectory))
                await StopCollectorAsync(attachDirectory, failures).ConfigureAwait(false);
            var finish = Probe.Take(process);
            var status = ReadStatus();
            var measured = samples.Where(static sample => sample.Measured).ToArray();
            var evidence = new
            {
                schemaVersion = SchemaVersion,
                scenario,
                workload = workload.Id,
                workloadDefinition = workload,
                label,
                trial,
                revision,
                binaryRevision,
                assemblySha256,
                runtimeProfile,
                collectorAttached = !string.IsNullOrWhiteSpace(attachDirectory),
                productionLatency = runtimeProfile == "production-defaults",
                environment = new
                {
                    dotnetVariables,
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = RuntimeInformation.FrameworkDescription,
                    processorCount = Environment.ProcessorCount,
                    serverGc = GCSettings.IsServerGC,
                    gcLatencyMode = GCSettings.LatencyMode.ToString(),
                    vector128 = Vector128.IsHardwareAccelerated,
                    vector256 = Vector256.IsHardwareAccelerated,
                    vector512 = Vector512.IsHardwareAccelerated,
                    avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported,
                    advSimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported
                },
                catalogSha256 = CatalogSha256,
                configurationSha256 = config is null ? null : CaptureContractJson.ComputeCanonicalJsonSha256(config),
                steps = config?.Pipeline.Steps.Select(static step => new { step.Id, step.Type }).ToArray(),
                protocol = new
                {
                    warmup = scenario == "cold" ? 0 : Warmup,
                    measured = scenario == "cold" ? 1 : Measured,
                    concurrency = scenario == "saturation" ? "foreground back-to-back with one concurrent standard-lane worker" : "serial",
                    foreground = "module capture + projected-scene staging + raw ingress accept (CaptureHostContext.PublishAsync)",
                    processing = "standard lane claim + handle + complete",
                    instrumentation = "symmetric per-stage process probes; meter listener on the processing-graph meter; no collectors",
                    cadenceIntervalSeconds = config?.Rig.Pipeline.CaptureInterval.TotalSeconds
                },
                setup,
                samples,
                summary = measured.Length == 0 ? null : new
                {
                    foregroundMilliseconds = Describe(measured.Select(static sample => sample.ForegroundMilliseconds)),
                    processingMilliseconds = scenario == "steady" ? Describe(measured.Select(static sample => sample.ProcessingMilliseconds)) : null,
                    serviceMilliseconds = scenario == "steady" ? Describe(measured.Select(static sample => sample.ForegroundMilliseconds + sample.ProcessingMilliseconds)) : null,
                    allocatedBytesPerOperation = Describe(measured.Select(static sample => (double)sample.AllocatedBytes)),
                    allocatedBytesPerRawByte = Describe(measured.Select(static sample => (double)sample.AllocatedBytes / sample.RawBytes))
                },
                saturation,
                totals = finish.Since(start),
                jit = new
                {
                    compiledMethods = System.Runtime.JitInfo.GetCompiledMethodCount(),
                    compiledIlBytes = System.Runtime.JitInfo.GetCompiledILBytes(),
                    compilationMilliseconds = System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds
                },
                memory = new
                {
                    status,
                    peakWorkingSetBytes = process.PeakWorkingSet64,
                    lohSizeAfterBytes = GenerationSize(3),
                    pohSizeAfterBytes = GenerationSize(4),
                    gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds
                },
                retainedFilesystemBytes = Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(static path => new FileInfo(path).Length) : 0,
                outputs,
                failures
            };
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(evidence, JsonOptions)).ConfigureAwait(false);
            TestContext.AddResultFile(output);
            SqliteConnection.ClearAllPools();
            if (Environment.GetEnvironmentVariable("HVO_PIPELINE_KEEP_ROOT") != "1" && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    // S4: the foreground runs back-to-back with no cadence wait while one standard-lane worker drains
    // concurrently, as the host's lane worker does. Backlog is accepted-but-not-completed captures.
    private static async Task<object> RunSaturationAsync(Pipeline pipeline, Process process, MeterProbe meters,
        List<OperationSample> samples, List<Guid> captureIds)
    {
        var timeline = new ConcurrentQueue<BacklogEvent>();
        var completedIds = new ConcurrentQueue<Guid>();
        var processingMilliseconds = new ConcurrentQueue<double>();
        var accepted = 0;
        var completed = 0;
        var foregroundDone = 0;
        var clock = Stopwatch.StartNew();
        var drain = Task.Run(async () =>
        {
            var idleSince = Stopwatch.GetTimestamp();
            while (true)
            {
                var begin = Stopwatch.GetTimestamp();
                var lease = await pipeline.LaneStore.ClaimAsync(pipeline.Standard, "issue1170-saturation", pipeline.Configuration,
                    CancellationToken.None).ConfigureAwait(false);
                if (lease is null)
                {
                    if (Volatile.Read(ref foregroundDone) == 1 && Volatile.Read(ref completed) == Volatile.Read(ref accepted)) return;
                    if (Stopwatch.GetElapsedTime(idleSince) > TimeSpan.FromMinutes(10))
                        throw new TimeoutException("The standard lane stalled with an undrained backlog.");
                    await Task.Delay(2).ConfigureAwait(false);
                    continue;
                }
                var result = await pipeline.Handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await pipeline.LaneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                processingMilliseconds.Enqueue(Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
                completedIds.Enqueue(lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId);
                var done = Interlocked.Increment(ref completed);
                timeline.Enqueue(new(clock.Elapsed.TotalMilliseconds, "completed", Volatile.Read(ref accepted), done));
                idleSince = Stopwatch.GetTimestamp();
            }
        });
        double lastAcceptMilliseconds = 0;
        for (var ordinal = Warmup; ordinal < Warmup + Measured; ordinal++)
        {
            var sample = await pipeline.ForegroundAsync(ordinal, process, meters: null).ConfigureAwait(false);
            samples.Add(sample);
            var now = Interlocked.Increment(ref accepted);
            lastAcceptMilliseconds = clock.Elapsed.TotalMilliseconds;
            timeline.Enqueue(new(lastAcceptMilliseconds, "accepted", now, Volatile.Read(ref completed)));
        }
        Volatile.Write(ref foregroundDone, 1);
        await drain.ConfigureAwait(false);
        var drainedMilliseconds = clock.Elapsed.TotalMilliseconds;
        captureIds.AddRange(completedIds);
        var events = timeline.OrderBy(static item => item.Milliseconds).ToArray();
        return new
        {
            foregroundOperations = Measured,
            completedOperations = completed,
            foregroundElapsedMilliseconds = lastAcceptMilliseconds,
            drainAfterLastAcceptMilliseconds = drainedMilliseconds - lastAcceptMilliseconds,
            totalElapsedMilliseconds = drainedMilliseconds,
            completionRatePerSecond = completed / (drainedMilliseconds / 1000),
            backlogPeak = events.Length == 0 ? 0 : events.Max(static item => item.Accepted - item.Completed),
            meters = meters.Take(),
            processingMilliseconds = Describe(processingMilliseconds),
            previewConsumer = "not composed: FramesController preview encode is host-owned and measured in the S2 attribution only",
            timeline = events
        };
    }

    private static async Task<object> ReadOutputsAsync(SqliteCaptureProcessingStore store, CameraModuleConfig config,
        IReadOnlyList<Guid> captureIds, List<string> failures)
    {
        var nodes = new List<object>();
        foreach (var (captureId, ordinal) in captureIds.Select(static (id, index) => (id, index)))
        {
            foreach (var step in config.Pipeline.Steps)
            {
                var nodeId = step.Id ?? throw new AssertFailedException("The shipped graph names every node explicitly.");
                var node = await store.ReadNodeAsync(captureId, nodeId, CancellationToken.None).ConfigureAwait(false);
                if (node is null || node.Status is not (DurableProcessingNodeStatus.Completed or DurableProcessingNodeStatus.Skipped))
                {
                    failures.Add($"Capture {ordinal} node {step.Id} ended {node?.Status.ToString() ?? "missing"}: {node?.Reason}");
                    continue;
                }
                if (step.Type == "ProjectedScene" && (node.Status != DurableProcessingNodeStatus.Completed || node.Outputs.Count == 0))
                    failures.Add($"Capture {ordinal} produced no projected scene; staging was unavailable.");
                nodes.Add(new
                {
                    ordinal,
                    node = step.Id,
                    status = node.Status.ToString(),
                    outcome = node.Outcome?.ToString(),
                    durationMilliseconds = node.Duration?.TotalMilliseconds,
                    outputs = node.Outputs.Select(static item => new
                    {
                        role = item.Descriptor?.Artifact.Role.ToString(),
                        variant = item.Descriptor?.Artifact.Variant,
                        payloadSha256 = item.Descriptor?.Artifact.ChecksumSha256,
                        payloadBytes = item.Descriptor?.Layout.ByteLength,
                        item.RecipeIdentitySha256,
                        item.OutputIdentitySha256
                    }).ToArray()
                });
            }
        }
        return nodes;
    }

    private static async Task<CameraModuleConfig> LoadConfigurationAsync(Workload workload, string root)
    {
        // The shipped graph is the sample's complete pipeline; canonical W1/W2/W6 contribute rig and module
        // options only. Storage roots move under the trial root; sample annotation inputs are added when absent.
        var sample = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json"))
            .ConfigureAwait(false))!.AsObject();
        var source = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, workload.ConfigFile))
            .ConfigureAwait(false))!.AsObject();
        source["agentId"] = "issue1170-evidence";
        var pipeline = sample["pipeline"]!.DeepClone().AsObject();
        foreach (var step in pipeline["steps"]!.AsArray().Select(static step => step!.AsObject()))
        {
            if (step["type"]!.GetValue<string>() != "Storage") continue;
            step["options"]!["storageRoot"] = Path.Combine(root, step["id"]!.GetValue<string>() == "LocalStorage" ? "agent" : "archive");
        }
        source["pipeline"] = pipeline;
        var options = source["module"]!["options"]!.AsObject();
        foreach (var key in SampleAnnotationInputs)
            if (!options.ContainsKey(key)) options[key] = sample["module"]!["options"]![key]!.DeepClone();
        if (workload.MaximumMagnitude is { } magnitude) options["maximumMagnitude"] = magnitude;
        var path = Path.Combine(root, "cameraagent.composed.json");
        await File.WriteAllTextAsync(path, source.ToJsonString()).ConfigureAwait(false);
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = path,
            AgentId = "issue1170-evidence",
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix")
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record Pipeline(ICameraModule Module, CaptureHostContext Host, ICaptureLaneStore LaneStore,
        ICaptureLaneHandler Handler, CaptureLaneDefinition Standard, CameraModuleConfig Configuration, Workload Workload)
    {
        public CaptureRequest Request(int ordinal)
        {
            var exposure = Workload.ExposureSeconds ?? (Workload.Day ? Configuration.Rig.Pipeline.DayExposure : Configuration.Rig.Pipeline.NightExposure).TotalSeconds;
            var gain = Workload.Day ? Configuration.Rig.Pipeline.DayGain : Configuration.Rig.Pipeline.NightGain;
            var interval = Configuration.Rig.Pipeline.CaptureInterval;
            return new CaptureRequest((Workload.Day ? DayUtc : NightUtc) + (interval * ordinal), interval, CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(exposure), gain, null, null));
        }

        // Saturation passes no meter probe: the concurrent drain owns graph meters, which are reported as totals.
        public async Task<OperationSample> ForegroundAsync(int ordinal, Process process, MeterProbe? meters)
        {
            meters?.Take();
            var request = Request(ordinal);
            var p0 = Probe.Take(process);
            var result = await Module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var p1 = Probe.Take(process);
            var frame = result.Frame ?? throw new AssertFailedException("The module returned no frame.");
            await Host.PublishAsync(new CaptureLoopSubmission(request, result, request.RequestedStartUtc, request.TargetInterval,
                p1.Elapsed(p0)), CancellationToken.None).ConfigureAwait(false);
            var p2 = Probe.Take(process);
            return new OperationSample(ordinal, true, Guid.Empty, frame.PixelData.Length,
                PayloadChecksumHex(frame.PixelData.Span),
                p2.Elapsed(p0).TotalMilliseconds, 0, p2.Allocated - p0.Allocated,
                new Dictionary<string, object> { ["capture"] = p1.Since(p0), ["publish"] = p2.Since(p1) },
                meters?.Take() ?? [], ReadStatus());
        }

        public async Task<OperationSample> RunAsync(int ordinal, Process process, MeterProbe meters)
        {
            var foreground = await ForegroundAsync(ordinal, process, meters).ConfigureAwait(false);
            var p2 = Probe.Take(process);
            var lease = await LaneStore.ClaimAsync(Standard, "issue1170-evidence", Configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var p3 = Probe.Take(process);
            var result = await Handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            var p4 = Probe.Take(process);
            await LaneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            var p5 = Probe.Take(process);
            var stages = new Dictionary<string, object>(foreground.Stages, StringComparer.Ordinal)
            {
                ["claim"] = p3.Since(p2),
                ["handle"] = p4.Since(p3),
                ["complete"] = p5.Since(p4)
            };
            var processingMeters = meters.Take();
            foreach (var (key, value) in foreground.Meters) processingMeters[key] = processingMeters.GetValueOrDefault(key) + value;
            return foreground with
            {
                CaptureId = lease.Context.RawCapture.Manifest.Descriptor.Capture.CaptureId,
                ProcessingMilliseconds = p5.Elapsed(p2).TotalMilliseconds,
                AllocatedBytes = foreground.AllocatedBytes + (p5.Allocated - p2.Allocated),
                Stages = stages,
                Meters = processingMeters,
                Status = ReadStatus()
            };
        }
    }

    private sealed record BacklogEvent(double Milliseconds, string Event, int Accepted, int Completed);

    private sealed record OperationSample(int Ordinal, bool Measured, Guid CaptureId, long RawBytes, string RawSha256,
        double ForegroundMilliseconds, double ProcessingMilliseconds, long AllocatedBytes,
        Dictionary<string, object> Stages, Dictionary<string, double> Meters, Dictionary<string, long> Status);

    private sealed record Workload(string Id, string ConfigFile, bool Day, double? ExposureSeconds, double? MaximumMagnitude)
    {
        public static Workload Find(string id) => id switch
        {
            "sample-night" => new(id, "cameraagent.sample.json", false, null, null),
            "w1-night-long" => new(id, "virtual-asi174.full.json", false, null, null),
            "w1-night-short" => new(id, "virtual-asi174.full.json", false, .1, null),
            "w1-day" => new(id, "virtual-asi174.full.json", true, null, null),
            "w1-dense" => new(id, "virtual-asi174.full.json", false, null, 8.5),
            "w2-night" => new(id, "virtual-asi178mc.full.json", false, null, null),
            // performance-validation.md: W6 is a five-second logical light exposure.
            "w6-night" => new(id, "virtual-asi676mc.full.json", false, 5, null),
            _ => throw new AssertFailedException($"Unknown #1170 workload '{id}'.")
        };
    }

    private readonly record struct Probe(long Timestamp, TimeSpan Cpu, long Allocated, int Gen0, int Gen1, int Gen2,
        IReadOnlyDictionary<string, long> Io)
    {
        public static Probe Take(Process process)
        {
            process.Refresh();
            return new(Stopwatch.GetTimestamp(), process.TotalProcessorTime, GC.GetTotalAllocatedBytes(precise: true),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), ReadIo());
        }

        public TimeSpan Elapsed(Probe before) => Stopwatch.GetElapsedTime(before.Timestamp, Timestamp);

        public object Since(Probe before) => new
        {
            milliseconds = Elapsed(before).TotalMilliseconds,
            cpuMilliseconds = (Cpu - before.Cpu).TotalMilliseconds,
            allocatedBytes = Allocated - before.Allocated,
            gen0 = Gen0 - before.Gen0,
            gen1 = Gen1 - before.Gen1,
            gen2 = Gen2 - before.Gen2,
            io = Io.ToDictionary(static item => item.Key, item => item.Value - before.Io.GetValueOrDefault(item.Key), StringComparer.Ordinal)
        };
    }

    // Aggregates the processing-graph meter (recipe/persistence/graph durations, output bytes, staging outcomes)
    // by instrument and step so per-node attribution needs no collector.
    private sealed class MeterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, double> _values = new(StringComparer.Ordinal);

        public MeterProbe()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CaptureProcessingTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public Dictionary<string, double> Take()
        {
            var taken = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var key in _values.Keys.Order(StringComparer.Ordinal))
                if (_values.TryRemove(key, out var value)) taken[key] = value;
            return taken;
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var key = instrument.Name;
            foreach (var tag in tags)
                if (tag.Key is "step" or "outcome") key += $"|{tag.Key}={tag.Value}";
            _values.AddOrUpdate(key, value, (_, current) => current + value);
        }
    }

    private static async Task WaitForCollectorAsync(string directory)
    {
        var staged = Path.Combine(directory, "pid.tmp");
        await File.WriteAllTextAsync(staged, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        File.Move(staged, Path.Combine(directory, "pid"));
        var waited = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "attached")))
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(2)) Assert.Fail("The attribution collector did not attach.");
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    // The runner's dotnet-trace session stops on this event, so the trace is finalized with rundown while the
    // process is still alive instead of being cut off by process exit.
    private static async Task StopCollectorAsync(string directory, List<string> failures)
    {
        HarnessEventSource.Log.MeasurementComplete();
        var waited = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "detached")))
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(2))
            {
                failures.Add("The attribution collector did not detach after the stopping event.");
                return;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    [EventSource(Name = HarnessEventSource.ProviderName)]
    private sealed class HarnessEventSource : EventSource
    {
        public const string ProviderName = "HVO-Issue1170-Harness";

        public static readonly HarnessEventSource Log = new();

        [Event(1)]
        public void MeasurementComplete() => WriteEvent(1);
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) Assert.Fail($"{name} is required for #1170 evidence.");
        return value;
    }

    private static string PayloadChecksumHex(ReadOnlySpan<byte> payload) => Convert.ToHexString(SHA256.HashData(payload));

    private static long GenerationSize(int generation)
    {
        var info = GC.GetGCMemoryInfo().GenerationInfo;
        return info.Length > generation ? info[generation].SizeAfterBytes : 0;
    }

    private static object? Describe(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return null;
        return new
        {
            count = ordered.Length,
            minimum = ordered[0],
            median = ordered.Length % 2 == 1 ? ordered[ordered.Length / 2] : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2,
            // Nearest-rank p95 within this trial; pooled p95 is computed by the runner across trials.
            p95 = ordered[(int)Math.Ceiling(.95 * ordered.Length) - 1],
            maximum = ordered[^1],
            mean = ordered.Average()
        };
    }

    private static Dictionary<string, long> ReadIo()
    {
        if (!OperatingSystem.IsLinux()) return [];
        return File.ReadAllLines("/proc/self/io").Select(static line => line.Split(':', 2))
            .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }

    private static Dictionary<string, long> ReadStatus()
    {
        if (!OperatingSystem.IsLinux()) return [];
        return File.ReadAllLines("/proc/self/status").Select(static line => line.Split(':', 2))
            .Where(static parts => parts[0] is "VmRSS" or "VmHWM" or "RssAnon" or "RssFile" or "Threads")
            .ToDictionary(static parts => parts[0],
                // Memory lines are reported in kB; Threads is a count.
                static parts => long.Parse(parts[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture) * (parts[0] == "Threads" ? 1 : 1024),
                StringComparer.Ordinal);
    }
}
