using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>Reproducible contract-only workload for #1422; no network, provider bytes or storage I/O in measured stages.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class OrbitalSnapshotAllocationEvidenceTests
{
    /// <summary>Run in a fresh process per HVO_ISSUE1422_PHASE: Create, SerializePayload or Load (the Parse API).</summary>
    [TestMethod]
    public void SnapshotCanonicalStageReportsResourceCosts()
    {
        const int count = 16_754;
        const int objects = count - 64;
        var epoch = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var parser = new OrbitalSoftwareIdentity("synthetic-gp-parser", "1", new string('A', 64));
        var normalizer = new OrbitalSoftwareIdentity("synthetic-gp-normalizer", "1", new string('B', 64));
        var model = new OrbitalElementModel("EARTH", "TEME", "UTC", "SGP4", OrbitalContract.GpDerivativeConvention, OrbitalContract.CelesTrakGpProfile, parser, normalizer);
        var records = Enumerable.Range(0, count).Select(i =>
        {
            var repeat = i >= objects;
            var objectIndex = repeat ? (i - objects) * 241 : i;
            var catalog = objectIndex < objects - 826 ? 10_000 + objectIndex * 5 : 100_000 + (objectIndex - (objects - 826)) * 7;
            var time = epoch.AddSeconds(-((objectIndex * 7_919L) % (72 * 3_600))).AddTicks(objectIndex % 1_000_000 * 10L);
            if (repeat) time = time.AddHours(-12);
            return OrbitalContract.CreateRecord(new("", new("norad", catalog.ToString(CultureInfo.InvariantCulture)), "celestrak:gp:active", null,
                $"HVO SYNTHETIC {catalog}", $"{1990 + objectIndex % 36}-{objectIndex % 999 + 1:000}A", null, null, time, null, null, model,
                new(1.0027 + objectIndex % 16_000 / 1000.0, objectIndex * 37 % 99_991 / 1_000_000.0, objectIndex * 7 % 18_001 / 100.0,
                    objectIndex * 13 % 36_000 / 100.0, objectIndex * 17 % 36_000 / 100.0, i * 19 % 36_000 / 100.0,
                    (i % 2_001 - 1_000) / 10_000_000.0, (i % 1_001 - 500) / 100_000_000.0, 0, 0, "U", 999 - (repeat ? 1 : 0), objectIndex * 31 % 99_999 + (repeat ? 0 : 1))));
        }).ToArray();
        var source = new OrbitalSnapshotSource("celestrak:gp:active", "celestrak", "USSF public GP",
            new("https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON"), epoch, null, null, null, new string('C', 64), 7_057_239, parser, normalizer,
            new("https://celestrak.org/NORAD/documentation/gp-data-formats.php"), "Synthetic issue-1422 allocation workload; no provider bytes.");
        var coverage = new OrbitalPopulationCoverage("active-group", OrbitalPopulationCompleteness.CompleteForRequestedPopulation, ["inactive objects"]);
        var snapshot = OrbitalSnapshot.Create(records, [source], coverage);
        var manifest = snapshot.SerializeManifest();
        var payload = snapshot.SerializePayload();
        var stage = Environment.GetEnvironmentVariable("HVO_ISSUE1422_PHASE") ?? "Create";
        void Run()
        {
            switch (stage)
            {
                case "Create": GC.KeepAlive(OrbitalSnapshot.Create(records, [source], coverage)); break;
                case "SerializePayload": GC.KeepAlive(snapshot.SerializePayload()); break;
                case "Load":
                    var parsed = OrbitalSnapshot.Parse(manifest, payload);
                    if (!parsed.IsValid) throw new InvalidOperationException(parsed.Reason);
                    GC.KeepAlive(parsed); break;
                default: throw new ArgumentException(stage);
            }
        }
        Run(); // One warmup; ten measured repeats. Fresh process per stage and implementation.
        var observations = new List<object>();
        using var process = Process.GetCurrentProcess();
        for (var trial = 0; trial < 10; trial++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            process.Refresh();
            var beforeWorkingSet = process.WorkingSet64;
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            Run();
            var wall = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            process.Refresh();
            observations.Add(new
            {
                trial,
                wallMilliseconds = wall,
                cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                allocatedBytes,
                workingSetBeforeBytes = beforeWorkingSet,
                workingSetAfterBytes = process.WorkingSet64,
                peakWorkingSetBytes = process.PeakWorkingSet64
            });
        }
        var evidence = JsonSerializer.Serialize(new
        {
            stage,
            workload = "synthetic-gp-contract-1422-v1",
            count,
            objects,
            sixDigitObjects = 826,
            duplicateObjects = 64,
            payloadBytes = payload.Length,
            payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)),
            manifestSha256 = snapshot.Manifest.ManifestSha256,
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            serverGC = GCSettings.IsServerGC,
            observations
        });
        var output = Environment.GetEnvironmentVariable("HVO_ISSUE1422_EVIDENCE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-1422");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, $"{stage}.json"), evidence);
        Console.WriteLine(evidence);
    }
}
