using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[TestClass]
[TestCategory("Manual")]
public sealed class CaptureOrbitalPredictionResourceTests
{
    private static readonly JsonSerializerOptions EvidenceOptions = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task NearestEngineAndRetainedRecipeReportBoundedResourceCosts()
    {
        var observations = new List<object>();
        using var process = Process.GetCurrentProcess();
        foreach (var name in new[] { "iss", "starlink-56838", "starlink-65283" })
        {
            var f = new CaptureOrbitalPredictionFixture(name);
            var input = f.Input();
            var engine = new OrbitalPredictionEngine(f.Time);
            var executor = new ProcessingRecipeExecutor([new CaptureOrbitalPredictionRecipe(input, f.Time)]);
            foreach (var stage in new[] { "engine-and-track-serialization", "recipe-and-retained-envelope" })
            {
                var times = new List<double>();
                long bytes = 0; string? hash = null;
                async Task Operation(bool verify)
                {
                    if (stage == "engine-and-track-serialization")
                    {
                        var result = await engine.PredictExposureAsync(input.Query!, TestContext.CancellationToken).ConfigureAwait(false);
                        var payload = OrbitalPredictionJson.SerializeTrack(result.Products.Single());
                        if (verify)
                        {
                            Assert.AreEqual(f.Track.ProductSha256, result.Products.Single().ProductSha256);
                            bytes = payload.Length; hash = ProcessingIdentity.ComputePayloadSha256(payload);
                        }
                    }
                    else
                    {
                        var result = await executor.ExecuteAsync(f.Request(), TestContext.CancellationToken).ConfigureAwait(false);
                        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status);
                        if (verify)
                        {
                            var product = result.Products.Single();
                            var output = CaptureOrbitalPredictionJson.Parse(product.Payload.Span, input);
                            Assert.AreEqual(f.Track.ProductSha256, output.Tracks.Single().ProductSha256);
                            bytes = product.Payload.Length; hash = product.ChecksumSha256;
                            var path = Path.Combine(TestContext.TestRunResultsDirectory!, $"capture-orbital-{name}-v1.json");
                            await File.WriteAllBytesAsync(path, product.Payload.ToArray(), TestContext.CancellationToken).ConfigureAwait(false);
                            TestContext.AddResultFile(path);
                        }
                    }
                }
                for (var i = 0; i < 5; i++) await Operation(false).ConfigureAwait(false);
                process.Refresh();
                var working = process.WorkingSet64; var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < 30; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    await Operation(false).ConfigureAwait(false);
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                var allocation = GC.GetTotalAllocatedBytes(true) - allocated;
                process.Refresh();
                var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var workingDelta = process.WorkingSet64 - working;
                times.Sort();
                var p95 = times[28];
                Assert.IsLessThan(100d, p95, "Declared metadata-operation p95 bound, not a capture cadence claim.");
                Assert.IsLessThan(25L * 1024 * 1024, allocation / 30, "Declared allocated-byte bound.");
                Assert.IsLessThan(5000d, cpuMs, "Declared 30-operation CPU bound.");
                Assert.IsLessThan(64L * 1024 * 1024, workingDelta, "Declared process working-set growth bound.");
                await Operation(true).ConfigureAwait(false);
                observations.Add(new
                {
                    fixture = name,
                    stage,
                    warmup = 5,
                    operations = 30,
                    medianMilliseconds = times[15],
                    p95Milliseconds = p95,
                    maximumMilliseconds = times[^1],
                    cpuMilliseconds = cpuMs,
                    allocatedBytesPerOperation = allocation / 30,
                    workingSetStartBytes = working,
                    workingSetDeltaBytes = workingDelta,
                    processPeakWorkingSetBytes = process.PeakWorkingSet64,
                    operationsPerSecond = 30_000 / times.Sum(),
                    outputBytes = bytes,
                    checksumSha256 = hash
                });
            }
        }
        var evidence = JsonSerializer.Serialize(new
        {
            workload = "1640-capture-metadata-640-v1",
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture,
            runtime = Environment.Version.ToString(),
            concurrency = 1,
            rawBytesPerRecipeRead = 819200,
            bounds = new
            {
                p95Milliseconds = 100,
                allocatedBytesPerOperation = 25 * 1024 * 1024,
                cpuMillisecondsPer30Operations = 5000,
                workingSetGrowthBytes = 64 * 1024 * 1024
            },
            boundary = "Resolved snapshot/time/plan and raw fixture construction excluded. Engine includes prediction and canonical track serialization. Recipe includes executor validation, raw checksum read, identity, prediction, envelope validation/serialization and product hash. Assertions/export outside measured operations except Produced status. CPU/global allocation include instrumentation; working set is process-wide. Zero network/persistence I/O, pixel copies, queues, arrival rate, initial backlog or outages. No full five-second/native/physical qualification.",
            observations
        }, EvidenceOptions);
        TestContext.WriteLine(evidence);
        var report = Path.Combine(TestContext.TestRunResultsDirectory!, "capture-orbital-resources.json");
        await File.WriteAllTextAsync(report, evidence, TestContext.CancellationToken).ConfigureAwait(false);
        TestContext.AddResultFile(report);
    }
}
