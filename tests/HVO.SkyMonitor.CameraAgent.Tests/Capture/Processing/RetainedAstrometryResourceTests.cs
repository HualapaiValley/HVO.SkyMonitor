using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private static readonly JsonSerializerOptions AstrometryEvidenceJson = new() { WriteIndented = true };

    [TestMethod]
    [TestCategory("Manual")]
    public async Task RetainedAstrometry_ResourceEvidenceMeasuresBaselineAnalyzerPersistenceAndRead()
    {
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1643_EVIDENCE_ROOT")
            ?? throw new InvalidOperationException("Set HVO_ISSUE1643_EVIDENCE_ROOT to an hvo scratch directory.");
        Directory.CreateDirectory(evidenceRoot);
        var rows = new List<object>();
        var warmEnabled = new List<(double ProducerMs, long ProducerBytes, double ReadMs, long ReadBytes)>();
        using var process = Process.GetCurrentProcess();
        foreach (var enabled in new[] { false, true })
        {
            for (var index = 0; index < 6; index++)
            {
                var root = Path.Combine(evidenceRoot, $"{(enabled ? "enabled" : "baseline")}-{index}");
                Directory.CreateDirectory(root);
                var f = await CreateAstrometryDurabilityFixtureAsync(root).ConfigureAwait(false);
                using var telemetry = new CaptureProcessingTelemetry();
                using var store = new SqliteCaptureProcessingStore(f.Fixture.Options);
                using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
                using var gate = new CaptureAstrometryExecutionGate();
                var persistence = CreatePersistence(f.Fixture.Options, store, storage, telemetry);
                var before = Sample();
                var started = Stopwatch.GetTimestamp();
                var result = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item,
                    AstrometryGraph(f, new(new ProcessingRecipeExecutor()), gate, enabled, persistence), persistence,
                    telemetry, 1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                var producerElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                var produced = Sample();
                using var artifactReader = new CameraAgentArtifactService(f.Fixture.Options, store, new CameraAgentPreviewEncoder());
                started = Stopwatch.GetTimestamp();
                using var astrometryReader = new CameraAgentRetainedAstrometryService(store, artifactReader);
                var view = await astrometryReader.GetAsync(f.Fixture.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                var readElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var read = Sample();
                Assert.AreEqual(enabled ? "Retained" : "NotRetained", view.Availability, view.Reason);
                if (enabled)
                {
                    Assert.AreEqual(AstrometricAssessmentStatus.Accepted, view.Solution!.Assessment.Status, view.Reason);
                    Assert.AreEqual("959067FF97697681F5118ED6543F39C40EEACEC4CA43999D083A75316B6B80A9", view.SolutionIdentitySha256,
                        "The budget must preserve the original accepted solution identity.");
                }
                if (enabled && index > 0) warmEnabled.Add((producerElapsed, produced.Allocated - before.Allocated, readElapsed, read.Allocated - produced.Allocated));
                rows.Add(new
                {
                    enabled,
                    index,
                    cold = index == 0,
                    rawDescriptorIdentity = CaptureContractJson.ComputeDescriptorSha256(f.Fixture.Manifest.Descriptor),
                    rawPayloadIdentity = f.Fixture.Manifest.Descriptor.Artifact.ChecksumSha256,
                    recipeOptions = f.Options.Recipe,
                    producerAndPersistence = Delta(before, produced, producerElapsed),
                    read = Delta(produced, read, readElapsed),
                    solutionIdentity = view.SolutionIdentitySha256,
                    solutionBytes = view.Solution is null ? 0 : MeasuredAstrometryJson.Serialize(view.Solution).Length,
                    rootBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length)
                });
            }
        }
        var path = Path.Combine(evidenceRoot, "retained-astrometry-resources.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "issue-1643-resources-v1",
            environment = new
            {
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount
            },
            bounds = new
            {
                rawBytes = CameraAgentRetainedAstrometryService.MaximumRawBytes,
                readBytes = CameraAgentRetainedAstrometryService.MaximumReadBytes,
                contextBytes = MeasuredAstrometryJson.MaximumContextBytes,
                solutionBytes = MeasuredAstrometryJson.MaximumSolutionBytes,
                fitMilliseconds = 20000,
                captureStride = 60
            },
            comparisonCeilings = new
            {
                producerMilliseconds = 5000,
                producerAllocationBytes = 200L * 1024 * 1024,
                readMilliseconds = 500,
                readAllocationBytes = 100L * 1024 * 1024
            },
            limitation = "512-square generated raw, local filesystem and SQLite; process counters include runtime work. No physical/full-resolution/capture-interference qualification.",
            rows
        }, AstrometryEvidenceJson)).ConfigureAwait(false);
        Console.WriteLine(path);
        Assert.IsTrue(Median(warmEnabled.Select(r => (double)r.ProducerBytes)) <= 200L * 1024 * 1024,
            $"Warm enabled producer median {Median(warmEnabled.Select(r => (double)r.ProducerBytes))} exceeds 209715200 bytes.");
        Assert.IsTrue(Median(warmEnabled.Select(r => r.ProducerMs)) <= 5000, "Producer latency exceeds 5000 ms.");
        Assert.IsTrue(Median(warmEnabled.Select(r => (double)r.ReadBytes)) <= 100L * 1024 * 1024, "Read allocation exceeds 104857600 bytes.");
        Assert.IsTrue(Median(warmEnabled.Select(r => r.ReadMs)) <= 500, "Read latency exceeds 500 ms.");

        static double Median(IEnumerable<double> samples) => samples.Order().ElementAt(2);

        ResourceSample Sample()
        {
            process.Refresh();
            var io = OperatingSystem.IsLinux() ? File.ReadAllLines("/proc/self/io").Select(l => l.Split(':', 2))
                .ToDictionary(p => p[0], p => long.Parse(p[1], CultureInfo.InvariantCulture)) : new Dictionary<string, long>();
            return new(GC.GetTotalAllocatedBytes(true), process.TotalProcessorTime.TotalMilliseconds,
                process.WorkingSet64, process.PeakWorkingSet64, io);
        }
        static object Delta(ResourceSample before, ResourceSample after, double elapsed) => new
        {
            elapsedMilliseconds = elapsed,
            cpuMilliseconds = after.Cpu - before.Cpu,
            allocatedBytes = after.Allocated - before.Allocated,
            workingSetBytes = after.Working,
            workingSetDeltaBytes = after.Working - before.Working,
            peakWorkingSetBytes = after.Peak,
            io = after.Io.ToDictionary(p => p.Key, p => p.Value - before.Io.GetValueOrDefault(p.Key))
        };
    }

    private sealed record ResourceSample(long Allocated, double Cpu, long Working, long Peak, IReadOnlyDictionary<string, long> Io);
}
