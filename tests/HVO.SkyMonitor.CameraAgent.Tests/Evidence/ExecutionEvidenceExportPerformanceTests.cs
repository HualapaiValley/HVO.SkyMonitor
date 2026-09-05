using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Evidence;

/// <summary>
/// Tier M `W6` evidence for the durable graph-execution export lane, recorded as baseline versus after.
/// </summary>
/// <remarks>
/// The baseline is the delivered standalone capture path with the export lane absent; the after case is the same
/// path with the lane running at saturation against a sink that refuses every submission. Both halves run the real
/// ASI676MC 3552x3552 Bayer12-in-16 `W6` graph through raw ingress and the standard lane, so the comparison covers
/// acquisition, raw acceptance, live execution, and local publication rather than a synthetic stand-in. The drain
/// stage then measures the export lane by itself: enlistment throughput, submission latency over at least thirty
/// measured operations, drain rate, backlog age, and durable byte growth. Manual because a full-resolution
/// production-catalog capture is far too expensive for the Unit or Integration gate.
/// </remarks>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class ExecutionEvidenceExportPerformanceTests
{
    private const string ConfigurationFileName = "cameraagent.standalone-w6.json";
    private const int WarmupCaptures = 1;
    private const int MeasuredCaptures = 5;
    private const int DrainUnits = 300;
    private const int MeasuredSubmissions = 30;

    private static readonly ObservatoryLocation Location = new(35.5599378, -113.9119818, 520, "America/Phoenix");
    private static readonly DateTimeOffset FixtureUtc = new(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task W6ExportLaneDoesNotDelayCaptureAndDrainsABoundedBacklog()
    {
        var baseline = await MeasureCaptureStageAsync(exportEnabled: false).ConfigureAwait(false);
        var after = await MeasureCaptureStageAsync(exportEnabled: true).ConfigureAwait(false);
        var drain = await MeasureDrainStageAsync().ConfigureAwait(false);

        // A saturated export lane must not delay acquisition, raw acceptance, live execution, or publication. The
        // budget is deliberately generous because the comparison is a non-regression check, not a latency claim.
        Assert.IsLessThan(
            baseline.MedianCaptureMilliseconds * 1.25 + 250,
            after.MedianCaptureMilliseconds,
            $"baseline {baseline.MedianCaptureMilliseconds} ms, after {after.MedianCaptureMilliseconds} ms");
        Assert.AreEqual(
            baseline.CompletedExecutions,
            after.CompletedExecutions,
            "The export lane must not change how many executions complete locally.");
        Assert.IsGreaterThan(0, after.ExportPendingUnits, "The after case must actually be saturated.");
        Assert.AreEqual(0, drain.RemainingUnits, "A finite backlog must drain completely.");
        Assert.AreEqual(0, drain.GapCount, "The drained sequence must be contiguous.");
        Assert.IsLessThanOrEqualTo(1, drain.MaximumConcurrentRequests);
        Assert.IsGreaterThanOrEqualTo(
            MeasuredSubmissions,
            drain.MeasuredSubmissions,
            "The recorded p95 must come from at least the declared number of measured operations.");
        Assert.IsGreaterThan(0, after.SubmissionAttempts, "The after case must actually attempt submissions.");

        await WriteEvidenceAsync(baseline, after, drain).ConfigureAwait(false);
    }

    private static async Task<CaptureStage> MeasureCaptureStageAsync(bool exportEnabled)
    {
        using var root = new TemporaryRoot();
        var configuration = await LoadAsync().ConfigureAwait(false);
        using var provider = CreateProvider(root.Path);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        // The export lane waits for a validated configuration before it opens anything; publish the same one the
        // capture stage uses so both halves observe identical state.
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);

        var clock = new MutableTimeProvider(FixtureUtc);
        using var outbox = new SqliteExecutionEvidenceOutbox(clock);
        var state = new ExecutionEvidenceExportState();
        using var telemetry = new ExecutionEvidenceExportTelemetry(state, clock);
        // The sink negotiates successfully and then refuses every submission, so the after case exercises the whole
        // sweep, enlist, negotiate, submit, and settle path rather than stopping at a refused negotiation.
        var sink = new ExecutionEvidenceConformanceSink(clock) { Mode = ConformanceSinkMode.Reject };
        var exporter = CreateExporter(root.Path, provider, outbox, sink, state, telemetry, clock, exportEnabled);

        var module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
            static handler => handler.Lane == "standard");
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");

        var durations = new List<double>(MeasuredCaptures);
        long rawPayloadBytes = 0;
        var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = default;
        long allocatedBefore = 0;
        for (var index = 0; index < WarmupCaptures + MeasuredCaptures; index++)
        {
            if (index == WarmupCaptures)
            {
                process.Refresh();
                cpuBefore = process.TotalProcessorTime;
                allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            }
            // The export cycle runs concurrently with the capture it must not delay, which is the only arrangement
            // in which the measured capture window can observe contention at all.
            var exportCycle = exporter is null
                ? Task.CompletedTask
                : Task.Run(async () => await exporter.RunCycleOnceAsync(CancellationToken.None).ConfigureAwait(false));
            var started = Stopwatch.GetTimestamp();
            rawPayloadBytes = await RunCaptureAsync(
                module, configuration, ingress, laneStore, laneHandler, standard, operations,
                FixtureUtc.AddMinutes(index)).ConfigureAwait(false);
            if (index >= WarmupCaptures)
            {
                durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            await exportCycle.ConfigureAwait(false);
            if (exporter is not null)
            {
                // A second cycle after the capture so the execution this capture just produced is swept before the
                // next measured window opens.
                await exporter.RunCycleOnceAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        var executions = await operations.ReadExecutionsAsync(
            ProcessingGraphExecutionClass.Live, 256, CancellationToken.None).ConfigureAwait(false);
        var snapshot = state.Snapshot;
        return new(
            exportEnabled,
            durations.Count,
            Median(durations),
            durations.Min(),
            durations.Max(),
            cpu.TotalMilliseconds,
            process.WorkingSet64,
            allocated,
            rawPayloadBytes,
            executions.Count(static execution =>
                execution.Status == ProcessingGraphExecutionStatus.Completed),
            DirectoryBytes(Path.Combine(root.Path, "journal")),
            snapshot.Backlog.PendingCount + snapshot.Backlog.RetryCount,
            snapshot.Backlog.DatabaseBytes,
            snapshot.ReasonCode,
            sink.SubmissionCount);
    }

    private static async Task<DrainStage> MeasureDrainStageAsync()
    {
        using var root = new TemporaryRoot();
        var clock = new MutableTimeProvider(FixtureUtc);
        using var outbox = new SqliteExecutionEvidenceOutbox(clock);
        var origin = ExecutionEvidenceTestFactory.CreateOrigin();
        await outbox.EnsureOriginAsync(root.Path, origin, CancellationToken.None).ConfigureAwait(false);

        var enlistStarted = Stopwatch.GetTimestamp();
        long enlistedBytes = 0;
        for (var ordinal = 1; ordinal <= DrainUnits; ordinal++)
        {
            await outbox.EnlistAsync(
                root.Path,
                origin.IdentitySha256,
                ExecutionEvidenceTestFactory.ExecutionId(ordinal),
                [ExecutionEvidenceTestFactory.ExecutionUnit(origin, ordinal)],
                new(ordinal, ExecutionEvidenceTestFactory.ExecutionId(ordinal).ToString("N"), 0, 0),
                ExecutionEvidenceTestFactory.UnboundedLimits,
                CancellationToken.None).ConfigureAwait(false);
        }
        var enlistElapsed = Stopwatch.GetElapsedTime(enlistStarted);
        var enlisted = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);

        var sink = new ExecutionEvidenceConformanceSink(clock);
        var latencies = new List<double>(MeasuredSubmissions);
        var drainStarted = Stopwatch.GetTimestamp();
        var drained = new List<long>(DrainUnits);
        while (true)
        {
            var units = await outbox.ReadPendingAsync(
                root.Path, origin.IdentitySha256, 8, 8L * 1024 * 1024, clock.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            if (units.Count == 0)
            {
                break;
            }
            var payloads = units.Select(static unit => unit.Payload.ToArray()).ToArray();
            var submitStarted = Stopwatch.GetTimestamp();
            var result = await sink.SubmitAsync(origin.IdentitySha256, payloads, CancellationToken.None)
                .ConfigureAwait(false);
            latencies.Add(Stopwatch.GetElapsedTime(submitStarted).TotalMilliseconds);
            foreach (var fact in result.Feedback!.Facts.Where(
                static fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged))
            {
                await outbox.AcknowledgeAsync(
                    root.Path, origin.IdentitySha256, fact.OriginSequence, fact.PayloadSha256, clock.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
                drained.Add(fact.OriginSequence);
            }
            enlistedBytes += payloads.Sum(static payload => (long)payload.Length);
        }
        var drainElapsed = Stopwatch.GetElapsedTime(drainStarted);
        var remaining = await outbox.ReadBacklogAsync(root.Path, CancellationToken.None).ConfigureAwait(false);
        var ordered = drained.Order().ToArray();
        var gaps = ordered.Where((sequence, index) => sequence != index + 1).Count();

        return new(
            DrainUnits,
            enlistElapsed.TotalMilliseconds,
            DrainUnits / Math.Max(enlistElapsed.TotalSeconds, 0.000_001),
            enlisted.PendingBytes,
            enlisted.DatabaseBytes,
            latencies.Count,
            Median(latencies),
            Percentile(latencies, 0.95),
            latencies.Max(),
            drainElapsed.TotalMilliseconds,
            drained.Count / Math.Max(drainElapsed.TotalSeconds, 0.000_001),
            remaining.PendingCount + remaining.RetryCount,
            gaps,
            sink.MaximumConcurrentRequests,
            remaining.DatabaseBytes,
            enlistedBytes);
    }

    private static ExecutionEvidenceExportService? CreateExporter(
        string root,
        IServiceProvider provider,
        IExecutionEvidenceOutbox outbox,
        IExecutionEvidenceTransport transport,
        ExecutionEvidenceExportState state,
        ExecutionEvidenceExportTelemetry telemetry,
        TimeProvider timeProvider,
        bool enabled)
        => enabled
            ? new ExecutionEvidenceExportService(
                outbox,
                transport,
                new PerformanceOriginProvider(),
                provider.GetRequiredService<ProcessingGraphOperationsCoordinator>(),
                provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
                state,
                telemetry,
                new ExecutionEvidenceExportWakeup(),
                provider.GetRequiredService<StoragePressureState>(),
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root,
                    ExecutionEvidenceExport = new ExecutionEvidenceExportOptions
                    {
                        MaximumDiscoveryBatchesPerCycle = 4,
                        MaximumRequestUnits = 16
                    }
                }),
                timeProvider,
                NullLogger<ExecutionEvidenceExportService>.Instance)
            : null;

    private static async Task<long> RunCaptureAsync(
        VirtualSkyCameraModule module,
        CameraModuleConfig configuration,
        IRawCaptureIngress ingress,
        ICaptureLaneStore laneStore,
        ICaptureLaneHandler laneHandler,
        CaptureLaneDefinition standard,
        ProcessingGraphOperationsCoordinator operations,
        DateTimeOffset capturedUtc)
    {
        var setpoint = new CaptureSetpoint(
            configuration.Rig.Pipeline.NightExposure, configuration.Rig.Pipeline.NightGain, null, null);
        var request = new CaptureRequest(
            capturedUtc, configuration.Rig.Pipeline.CaptureInterval, CaptureMode.Still, setpoint);
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var submission = new CaptureLoopSubmission(
            request,
            result,
            capturedUtc,
            configuration.Rig.Pipeline.NightExposure,
            configuration.Rig.Pipeline.CaptureInterval);
        var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var lease = await laneStore.ClaimAsync(standard, "issue-537", configuration, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handled = await laneHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(CaptureLaneHandlerOutcome.RetryableFailure, handled.Outcome, handled.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        operations.NotifyLiveWorkChanged();
        return new FileInfo(receipt.StoredFrame.AbsolutePath).Length;
    }

    private static async Task<VirtualSkyCameraModule> CreateModuleAsync(
        IServiceProvider provider,
        CameraModuleConfig configuration)
    {
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            new InMemoryCelestialCatalog([
                new CelestialCatalogObject("HIP 32349", "Sirius", 101.28715533 / 15, -16.71611586, -1.46, 0.009, "32349"),
                new CelestialCatalogObject("HIP 24608", "Capella", 79.17232794 / 15, 45.99799147, 0.08, 0.795, "24608")
            ]),
            provider.GetRequiredService<IProjectedSceneStore>(),
            provider.GetRequiredService<IConstellationTopology>(),
            provider.GetRequiredService<IPlanetEphemeris>(),
            provider.GetRequiredService<IProjectedSceneStagingStore>());
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        return module;
    }

    private static async Task<CameraModuleConfig> LoadAsync()
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = Location
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static long DirectoryBytes(string path)
        => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
            : 0;

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }

    private async Task WriteEvidenceAsync(CaptureStage baseline, CaptureStage after, DrainStage drain)
    {
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE537_EVIDENCE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-537");
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, "w6-execution-evidence-export.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = "issue-537-execution-evidence-export-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Workload = new
            {
                Id = "W6",
                Configuration = ConfigurationFileName,
                WarmupCaptures,
                MeasuredCaptures,
                DrainUnits,
                MeasuredSubmissions
            },
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessorCount = System.Environment.ProcessorCount
            },
            Baseline = baseline,
            After = after,
            Drain = drain
        }, EvidenceOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Evidence written to {path}");
        TestContext.WriteLine(
            $"capture median baseline {baseline.MedianCaptureMilliseconds:F1} ms, after " +
            $"{after.MedianCaptureMilliseconds:F1} ms; export backlog {after.ExportPendingUnits} unit(s) " +
            $"({after.ExportDatabaseBytes} B); drain {drain.DrainedUnitsPerSecond:F1} unit/s, submit p95 " +
            $"{drain.SubmitP95Milliseconds:F2} ms, remaining {drain.RemainingUnits}");
    }

    private sealed record CaptureStage(
        bool ExportEnabled,
        int MeasuredCaptures,
        double MedianCaptureMilliseconds,
        double MinimumCaptureMilliseconds,
        double MaximumCaptureMilliseconds,
        double ProcessCpuMilliseconds,
        long WorkingSetBytes,
        long AllocatedBytes,
        long RawPayloadBytes,
        int CompletedExecutions,
        long RawIngressBytes,
        long ExportPendingUnits,
        long ExportDatabaseBytes,
        string ExportReasonCode,
        int SubmissionAttempts);

    private sealed record DrainStage(
        int EnlistedUnits,
        double EnlistMilliseconds,
        double EnlistedUnitsPerSecond,
        long PendingBytes,
        long PendingDatabaseBytes,
        int MeasuredSubmissions,
        double SubmitMedianMilliseconds,
        double SubmitP95Milliseconds,
        double SubmitMaximumMilliseconds,
        double DrainMilliseconds,
        double DrainedUnitsPerSecond,
        long RemainingUnits,
        int GapCount,
        int MaximumConcurrentRequests,
        long DrainedDatabaseBytes,
        long TransferredBytes);

    private sealed class PerformanceOriginProvider : IExecutionEvidenceOriginProvider
    {
        public ValueTask<ExecutionEvidenceOriginDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new ExecutionEvidenceOriginDescriptor(
                new("11111111-1111-4111-8111-111111111111"),
                new("22222222-2222-4222-8222-222222222222"),
                "1.0.0-issue-537"));
    }
}
