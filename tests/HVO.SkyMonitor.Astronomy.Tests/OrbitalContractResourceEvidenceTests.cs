using System.Diagnostics;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Manual")]
public sealed class OrbitalContractResourceEvidenceTests
{
    /// <summary>Engineering cost only: an explicitly synthetic population, no propagation or frame-cadence claim.</summary>
    [TestMethod]
    public void RetainedSnapshotValidationAndCachedSelectionReportBoundedResourceCosts()
    {
        const int count = 16_754;
        var records = Enumerable.Range(1, count).Select(i => OrbitalContractExamples.Record((100_000_000 + i)
            .ToString(System.Globalization.CultureInfo.InvariantCulture), name: "Synthetic resource workload")).ToArray();
        var snapshot = OrbitalSnapshot.Create(records, [OrbitalContractExamples.Source()], OrbitalContractExamples.Coverage);
        var manifest = snapshot.SerializeManifest(); var payload = snapshot.SerializePayload();
        var policy = OrbitalContractExamples.Policy(); var interval = OrbitalContractExamples.Exposure;
        // JIT warm-up is outside the three observations. Baseline omits integrity checks deliberately.
        Assert.AreEqual(count, JsonSerializer.Deserialize<OrbitalRecordV1[]>(payload, OrbitalContract.JsonOptions)!.Length);
        Assert.IsTrue(OrbitalSnapshot.Parse(manifest, payload).IsValid);
        OrbitalRecordSelector.Select(snapshot, snapshot.ObjectIdentities[0], interval, policy);
        using var process = Process.GetCurrentProcess();
        var observations = new List<object>();
        void Measure(string phase, Action action)
        {
            for (var trial = 0; trial < 3; trial++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                process.Refresh(); var workingSetBefore = process.WorkingSet64;
                var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var start = Stopwatch.GetTimestamp(); action(); var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                process.Refresh();
                observations.Add(new
                {
                    phase,
                    trial,
                    elapsedMilliseconds = elapsed,
                    cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
                    workingSetBeforeBytes = workingSetBefore,
                    workingSetAfterBytes = process.WorkingSet64,
                    peakWorkingSetBytes = process.PeakWorkingSet64
                });
            }
        }
        Measure("baseline-unchecked-typed-json-not-production", () =>
            Assert.AreEqual(count, JsonSerializer.Deserialize<OrbitalRecordV1[]>(payload, OrbitalContract.JsonOptions)!.Length));
        Measure("validated-materialization-outside-frame-path", () =>
        {
            var parsed = OrbitalSnapshot.Parse(manifest, payload);
            Assert.IsTrue(parsed.IsValid, parsed.Reason); Assert.AreEqual(count, parsed.Snapshot!.Records.Count);
        });
        Measure("cached-indexed-selection-entire-population-no-io", () =>
        {
            var selected = 0;
            foreach (var id in snapshot.ObjectIdentities)
                if (OrbitalRecordSelector.Select(snapshot, id, interval, policy).SelectedRecord is not null) selected++;
            Assert.AreEqual(count, selected);
        });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            workload = "synthetic-contract-resource-v1",
            count,
            payloadBytes = payload.Length,
            snapshot.Manifest.NormalizedPayloadSha256,
            snapshot.Manifest.ManifestSha256,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processors = Environment.ProcessorCount,
            caveats = "Three diagnostic observations after JIT warm-up and forced GC; raw JSON baseline excludes validation. No physical accuracy, detector, projection, complete frame latency or production qualification.",
            observations
        }));
    }
}
