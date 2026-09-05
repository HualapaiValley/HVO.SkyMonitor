using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// Records W6 capture timing for the rolling-combination path, split into the raw-ingress acceptance
/// critical section and the processing lane, so moving derived-window resolution out of acceptance and
/// into the consuming node can be compared on equivalent workloads. Run it once with the branch source
/// and once with the baseline source on the same binary harness.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class W6RollingWindowCaptureTimingTests
{
    private const int WindowSize = 5;
    private const int WarmupCaptures = WindowSize;
    private const int MeasuredCaptures = 6;
    private const int Width = 3552;
    private const int Height = 3552;
    private const int PayloadBytes = 25_233_408;
    private static readonly DateTimeOffset FixtureUtc = new(2026, 2, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task W6RollingWindowCaptureTiming()
    {
        var label = Environment.GetEnvironmentVariable("HVO_ISSUE608_LABEL");
        if (string.IsNullOrWhiteSpace(label))
        {
            Assert.Inconclusive("Set HVO_ISSUE608_LABEL to the measured source, for example baseline or after.");
        }

        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ISSUE608_EVIDENCE_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "issue-608-timing");
        Directory.CreateDirectory(evidenceRoot);
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-w6-timing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration();
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
                .ConfigureAwait(false);
            var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
            var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
                static handler => handler.Lane == "standard");
            var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
                static lane => lane.Name == "standard");
            var payload = CreatePayload();

            for (var index = 0; index < WarmupCaptures; index++)
            {
                _ = await RunCaptureAsync(
                    ingress, laneStore, laneHandler, standard, configuration, payload, index).ConfigureAwait(false);
            }

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var workingSetBefore = process.WorkingSet64;
            var peakWorkingSet = workingSetBefore;
            var accept = new double[MeasuredCaptures];
            var lane = new double[MeasuredCaptures];
            RawCaptureReceipt? receipt = null;
            for (var index = 0; index < MeasuredCaptures; index++)
            {
                var sample = await RunCaptureAsync(
                    ingress, laneStore, laneHandler, standard, configuration, payload,
                    WarmupCaptures + index).ConfigureAwait(false);
                accept[index] = sample.AcceptMilliseconds;
                lane[index] = sample.LaneMilliseconds;
                receipt = sample.Receipt;
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            }
            var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            Assert.IsNotNull(receipt);
            using var store = new SqliteCaptureProcessingStore(Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0
            }));
            var rolling = await store.ReadNodeAsync(
                receipt.Manifest.Descriptor.Capture.CaptureId, "rolling", CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(rolling);
            Assert.HasCount(1, rolling.Outputs);
            var stackCount = rolling.Outputs[0].Descriptor!.Artifact.SourceArtifactIds.Count;

            var evidence = new
            {
                SchemaVersion = "issue-608-w6-rolling-window-capture-timing-v1",
                Label = label,
                RecordedUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    Configuration = "Release",
                    ProcessorCount = System.Environment.ProcessorCount,
                    ServerGc = System.Runtime.GCSettings.IsServerGC
                },
                Workload = new
                {
                    Width,
                    Height,
                    PixelFormat = nameof(CameraPixelFormat.BayerRggb16),
                    PayloadBytes,
                    WindowSize,
                    WarmupCaptures,
                    MeasuredCaptures,
                    Calibration = "pass-through, so both sources resolve the same window",
                    Isolation = "strictly serial accept then lane; process counters require a filtered run",
                    Graph = "Calibration -> RollingCombination, cameraagent-capture-pipeline-v2, live durable executions"
                },
                Result = new
                {
                    StackCount = stackCount,
                    AcceptMilliseconds = Describe(accept),
                    LaneMilliseconds = Describe(lane),
                    TotalMilliseconds = Describe(accept.Zip(lane, static (a, b) => a + b).ToArray()),
                    CpuMilliseconds = cpuMilliseconds,
                    AllocatedBytes = allocatedBytes,
                    WorkingSetBeforeBytes = workingSetBefore,
                    PeakWorkingSetBytes = peakWorkingSet,
                    RetainedFilesystemBytes = Directory
                        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Sum(static path => new FileInfo(path).Length)
                }
            };
            var outputPath = Path.Combine(evidenceRoot, $"w6-rolling-window-capture-timing-{label}.json");
            await File.WriteAllTextAsync(
                outputPath, JsonSerializer.Serialize(evidence, EvidenceOptions)).ConfigureAwait(false);
            TestContext?.WriteLine($"Wrote {outputPath}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public TestContext? TestContext { get; set; }

    private static async Task<(double AcceptMilliseconds, double LaneMilliseconds, RawCaptureReceipt Receipt)>
        RunCaptureAsync(
            IRawCaptureIngress ingress,
            ICaptureLaneStore laneStore,
            ICaptureLaneHandler laneHandler,
            CaptureLaneDefinition standard,
            CameraModuleConfig configuration,
            byte[] payload,
            int index)
    {
        var acceptStart = Stopwatch.GetTimestamp();
        var receipt = await ingress.AcceptAsync(
            configuration, CreateSubmission(payload, index), CancellationToken.None).ConfigureAwait(false);
        var acceptElapsed = Stopwatch.GetElapsedTime(acceptStart).TotalMilliseconds;
        Assert.IsNotNull(receipt);
        var lease = await laneStore.ClaimAsync(
            standard, "w6-timing", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var laneStart = Stopwatch.GetTimestamp();
        var result = await laneHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        var laneElapsed = Stopwatch.GetElapsedTime(laneStart).TotalMilliseconds;
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        return (acceptElapsed, laneElapsed, receipt);
    }

    private static object Describe(double[] samples)
    {
        var ordered = samples.Order().ToArray();
        return new
        {
            Minimum = ordered[0],
            Median = ordered.Length % 2 == 1
                ? ordered[ordered.Length / 2]
                : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2,
            Maximum = ordered[^1],
            Mean = ordered.Average()
        };
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[PayloadBytes];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index * 31 + 17);
        }
        return payload;
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

    private static CameraModuleConfig CreateConfiguration()
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("w6-timing", Width, Height, 1, SensorColorMode.Color, CameraPixelFormat.BayerRggb16),
                new OpticsProfile("w6-timing", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1, 1)),
            new CapturePipelineConfig(
                [
                    new CaptureProcessingStepConfig(
                        "Calibration",
                        "calibration",
                        Options: JsonSerializer.SerializeToElement(new { }),
                        DependsOn: ["$raw"]),
                    new CaptureProcessingStepConfig(
                        "RollingCombination",
                        "rolling",
                        Options: JsonSerializer.SerializeToElement(new { windowSize = WindowSize }),
                        DependsOn: ["calibration"])
                ],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "w6-timing-agent");

    private static CaptureLoopSubmission CreateSubmission(byte[] payload, int index)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 5L);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(5), 1, null, null);
        var frame = new CameraFrame(
            startedUtc,
            Width,
            Height,
            CameraPixelFormat.BayerRggb16,
            payload,
            new FrameMetadata(TimeSpan.FromSeconds(5), 1, 10, "w6-timing"));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(6), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(5), startedUtc.AddSeconds(5.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6));
    }
}
