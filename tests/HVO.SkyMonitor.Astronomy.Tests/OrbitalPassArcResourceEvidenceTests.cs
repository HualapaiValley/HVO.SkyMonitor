using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Manual")]
public sealed class OrbitalPassArcResourceEvidenceTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Selected-pass engineering baseline; no complete frame latency or performance-improvement claim.</summary>
    [TestMethod]
    public async Task SelectedFullPassArcsReportActualResourceCostsAndRetainCanonicalOutputs()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital", "pass-arc-v1", "independent");
        using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "cases.json"),
            TestContext.CancellationToken).ConfigureAwait(false));
        using var process = Process.GetCurrentProcess();
        var observations = new List<object>();
        var caseIndex = 0;
        foreach (var item in data.RootElement.GetProperty("cases").EnumerateArray())
        {
            var (search, time) = OrbitalPassArcNumericalTests.Search(item);
            var engine = new OrbitalPredictionEngine(time);
            var passes = await engine.FindPassesAsync(search, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, passes.Status);
            var query = new OrbitalPassArcQuery(search, passes.Products.Single(), new(100000, 100000, 1500, 1, .001));
            string? priorHash = null;
            for (var trial = 0; trial < 2; trial++)
            {
                process.Refresh();
                var before = process.WorkingSet64;
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var generations = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                var start = Stopwatch.GetTimestamp();
                var result = await engine.PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
                var boundaryMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var allocationBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                process.Refresh();
                Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status, $"{item.GetProperty("name").GetString()}: {result.Reason}");
                var arc = result.Products.Single();
                if (priorHash is not null) Assert.AreEqual(priorHash, arc.ProductSha256);
                priorHash = arc.ProductSha256;
                var bytes = OrbitalPassArcJson.Serialize(arc); // Wire serialization is outside the public-method observation.
                var path = Path.Combine(TestContext.TestRunResultsDirectory!, $"pass-arc-{caseIndex}-{trial}.json");
                await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken).ConfigureAwait(false);
                TestContext.AddResultFile(path);
                observations.Add(new
                {
                    name = item.GetProperty("name").GetString(),
                    trial,
                    arc.ProductSha256,
                    parentPassSha256 = arc.Pass.ProductSha256,
                    boundaryMilliseconds,
                    result.ElapsedMilliseconds,
                    cpuMilliseconds,
                    allocationBytes,
                    workingSetBeforeBytes = before,
                    workingSetAfterBytes = process.WorkingSet64,
                    peakProcessWorkingSetBytes = process.PeakWorkingSet64,
                    collections = new[] { GC.CollectionCount(0) - generations[0], GC.CollectionCount(1) - generations[1], GC.CollectionCount(2) - generations[2] },
                    result.PropagationEvaluations,
                    samples = arc.Samples.Count,
                    serializedBytes = bytes.Length,
                    status = result.Status.ToString(),
                    reason = result.Reason.ToString()
                });
            }
            caseIndex++;
        }
        var evidence = JsonSerializer.Serialize(new
        {
            workload = "selected-full-pass-arc-resource-v1",
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            operatingSystem = RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            astronomyBinary = typeof(OrbitalPredictionEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            bounds = new { maximumEvaluations = 100000, maximumSamples = 100000, maximumElapsedMilliseconds = 1500, maximumStepSeconds = 1, maximumAngularErrorDegrees = .001 },
            caveats = "First/repeat diagnostic observations, not isolated cold/warm trials or an improvement claim. Query/time/snapshot loading and parent pass search are outside observation. The public method includes result validation/freezing/hashing; wire serialization, artifact I/O and assertion/reporting are outside. Process CPU/global allocations may include instrumentation; process peak includes fixture/JIT/prior cases. No full-population completeness, physical visibility or integrated five-second frame claim.",
            observations
        });
        TestContext.WriteLine(evidence);
        var summary = Path.Combine(TestContext.TestRunResultsDirectory!, "pass-arc-resources.json");
        await File.WriteAllTextAsync(summary, evidence, TestContext.CancellationToken).ConfigureAwait(false);
        TestContext.AddResultFile(summary);
    }
}
