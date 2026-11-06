using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CaptureSatelliteResourceTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ProducerPersistenceRendererAndExportRetainMeasuredAndUnavailableOutcomes()
    {
        var observations = new List<object>();
        var fixture = new CaptureSatelliteFixture();
        using var process = Process.GetCurrentProcess();
        foreach (var scenario in new[] { "disabled-baseline", "enabled", "time-unavailable", "deadline" })
        {
            var root = Path.Combine(TestContext.TestRunDirectory!, "satellite-resources-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var f = fixture;
                var clock = new CaptureSatelliteIntegrationTests.Clock(f.Descriptor.Timing.DurableIngressUtc);
                using var provider = CaptureSatelliteIntegrationTests.CreateProvider(root, f, clock,
                    unavailable: scenario == "time-unavailable" ? "time" : null);
                var bounds = f.Input.Plan.Bounds with { MaximumElapsedMilliseconds = scenario == "deadline" ? .000001 : 200 };
                var config = CaptureSatelliteIntegrationTests.Configuration(root, f, bounds);
                if (scenario == "disabled-baseline")
                    config = config with
                    {
                        Pipeline = config.Pipeline with
                        {
                            Steps = config.Pipeline.Steps.Where(static s => s.Id is not ("satellite" or "satellite-layer")).Select(step => step with
                            { DependsOn = step.DependsOn?.Where(static d => d is not ("satellite" or "satellite-layer")).ToArray() }).ToArray()
                        }
                    };
                var gallery = provider.GetRequiredService<ICameraAgentLayeredPresentationService>();
                var materializer = provider.GetRequiredService<ICameraAgentPresentationMaterializer>();
                var rows = new List<object>();
                var producerTimes = new List<double>();
                var renderTimes = new List<double>();
                for (var index = 0; index < 6; index++)
                {
                    clock.Now = f.Descriptor.Timing.DurableIngressUtc.AddSeconds(index);
                    var beforeAllocations = GC.GetTotalAllocatedBytes(true);
                    process.Refresh();
                    var beforeWorking = process.WorkingSet64;
                    var beforeCpu = process.TotalProcessorTime;
                    var io = ReadIo();
                    var total = Stopwatch.GetTimestamp();
                    var raw = await CaptureSatelliteIntegrationTests.CaptureAsync(provider, config, f, index).ConfigureAwait(false);
                    var admission = Stopwatch.GetElapsedTime(total).TotalMilliseconds;
                    var started = Stopwatch.GetTimestamp();
                    var handled = await CaptureSatelliteIntegrationTests.ProcessAsync(provider, config).ConfigureAwait(false);
                    var producer = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, handled.Outcome, handled.Reason);
                    started = Stopwatch.GetTimestamp();
                    var view = await gallery.GetCurrentAsync(raw.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                    var render = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, view.Status, view.Reason);
                    var selected = view.Presentation!.Layers.Where(static l => l.Satellite?.Renderable == true).Select(static l => l.IdentitySha256).ToArray();
                    started = Stopwatch.GetTimestamp();
                    var exported = await materializer.SaveFrozenAsync(view.Presentation.CaptureId,
                        view.Presentation.ManifestIdentitySha256, selected, "resource-evidence", CancellationToken.None).ConfigureAwait(false);
                    var export = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Saved, exported.Status, exported.Reason);
                    var elapsed = Stopwatch.GetElapsedTime(total).TotalMilliseconds;
                    var allocated = GC.GetTotalAllocatedBytes(true) - beforeAllocations;
                    process.Refresh();
                    var cpu = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
                    var workingDelta = process.WorkingSet64 - beforeWorking;
                    var afterIo = ReadIo();
                    var status = view.Presentation.Layers.SingleOrDefault()?.Satellite;
                    var backlog = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
                        .ReadOperationalStateAsync(CancellationToken.None).ConfigureAwait(false);
                    if (index == 0) continue; // One cold complete operation is excluded from the declared warm distribution.
                    producerTimes.Add(producer); renderTimes.Add(render);
                    rows.Add(new
                    {
                        index,
                        admissionMilliseconds = admission,
                        producerAndPersistenceMilliseconds = producer,
                        renderAndReadMilliseconds = render,
                        exportMilliseconds = export,
                        totalMilliseconds = elapsed,
                        cpuMilliseconds = cpu,
                        allocatedBytes = allocated,
                        workingSetDeltaBytes = workingDelta,
                        processPeakWorkingSetBytes = process.PeakWorkingSet64,
                        logicalReadCharacters = afterIo.Read - io.Read,
                        logicalWriteCharacters = afterIo.Written - io.Written,
                        storageReadBytes = afterIo.StorageRead - io.StorageRead,
                        storageWriteBytes = afterIo.StorageWritten - io.StorageWritten,
                        readOperations = afterIo.ReadOperations - io.ReadOperations,
                        writeOperations = afterIo.WriteOperations - io.WriteOperations,
                        durableRootBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(static p => new FileInfo(p).Length),
                        svgBytes = view.Presentation.Svg.Length,
                        exportBytes = exported.Receipt!.ByteLength,
                        exportChecksumSha256 = exported.Receipt.ChecksumSha256,
                        status = status?.Status ?? "Disabled",
                        reason = status?.Reason ?? "OptInAbsent",
                        tracks = status?.TrackCount ?? 0,
                        renderable = status?.Renderable ?? false,
                        processingPending = backlog.PendingCount,
                        processingLeased = backlog.LeasedCount,
                        processingRetry = backlog.RetryCount,
                        processingOldestUtc = backlog.OldestPendingUtc,
                        budgetMet = producer <= 3000 && render <= 1000 && allocated <= 128L * 1024 * 1024
                    });
                }
                producerTimes.Sort(); renderTimes.Sort();
                observations.Add(new
                {
                    scenario,
                    warmup = 1,
                    operations = 5,
                    producerMedianMilliseconds = producerTimes[2],
                    producerWorstMilliseconds = producerTimes[^1],
                    renderMedianMilliseconds = renderTimes[2],
                    renderWorstMilliseconds = renderTimes[^1],
                    rows
                });
            }
            finally { Directory.Delete(root, true); }
        }
        var evidence = new
        {
            schema = "capture-satellite-resources-1641-v1",
            measuredUtc = DateTimeOffset.UtcNow,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount,
                serverGc = System.Runtime.GCSettings.IsServerGC,
                latencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
                configuration = "Release",
                native = true,
                centralServices = false
            },
            workload = new
            {
                width = 640,
                height = 640,
                format = "BayerRggb16",
                rawBytes = 819200,
                selectedObjects = 1,
                sourceRecords = 6,
                exposureSeconds = 5,
                capturesPerScenario = 6,
                concurrency = 1,
                snapshotBytes = fixture.Snapshot.Manifest.NormalizedPayloadLengthBytes,
                earthOrientationBytes = fixture.Time.ExportEarthOrientation().Length,
                leapBytes = fixture.Time.ExportLeapSeconds().Length,
                engineDeadlineMilliseconds = 200,
                maximumSamples = 8192,
                maximumEvaluations = 100000,
                producerAndPersistenceBudgetMilliseconds = 3000,
                renderBudgetMilliseconds = 1000,
                allocatedByteBudgetPerCapture = 128L * 1024 * 1024
            },
            backlog = new { maximumClaimedCaptures = 1, observedPerOperation = true, addedSchedulers = 0 },
            interpretation = "Warmed local software composition only. Actual unavailable/deadline outcomes remain in rows. No full-resolution five-second or physical accuracy qualification.",
            observations
        };
        var directory = Environment.GetEnvironmentVariable("HVO_CAPTURE_SATELLITE_EVIDENCE_DIR") ?? TestContext.TestRunDirectory!;
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "capture-satellite-1641-resources.json");
        await File.WriteAllBytesAsync(output, JsonSerializer.SerializeToUtf8Bytes(evidence, Json)).ConfigureAwait(false);
        TestContext.AddResultFile(output);
    }

    private static (long Read, long Written, long ReadOperations, long WriteOperations, long StorageRead, long StorageWritten) ReadIo()
    {
        if (!File.Exists("/proc/self/io")) return (0, 0, 0, 0, 0, 0);
        var rows = File.ReadLines("/proc/self/io").Select(static line => line.Split(':', 2))
            .ToDictionary(static row => row[0], static row => long.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return (rows["rchar"], rows["wchar"], rows["syscr"], rows["syscw"], rows["read_bytes"], rows["write_bytes"]);
    }
}
