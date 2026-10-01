using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

/// <summary>Explicit offline full-snapshot evidence, never substituted with the nine-star fixture.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "MSTest constructs this class through DiscoverInternals.")]
internal sealed class AstrometricCatalogQualificationTests
{
    private const int EntryLimit = 2500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FullSnapshotSelectionResources()
    {
        var root = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "Supply the verified offline installation; this qualification cannot skip missing catalog evidence.");
        var startupClock = Stopwatch.StartNew();
        var startupAllocated = GC.GetTotalAllocatedBytes(precise: true);
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v42-production"));
        var startupMs = startupClock.Elapsed.TotalMilliseconds;
        startupAllocated = GC.GetTotalAllocatedBytes(precise: true) - startupAllocated;
        Assert.AreEqual(119625L, snapshot.RowCount);
        Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);

        var reports = new List<object>();
        foreach (var (magnitude, expectedCount) in new[] { (5d, 1637), (5.5, 2865), (7d, 15598) })
        {
            var all = await snapshot.Catalog.QueryCandidatesAsync(new(magnitude)).ConfigureAwait(false);
            Assert.HasCount(expectedCount, all);
            var reference = new AstrometricCatalogData(snapshot.Catalog.Metadata, all.Take(EntryLimit), all.Count <= EntryLimit, magnitude);
            var baseline = await MeasureAsync("baseline-full-query", magnitude, expectedCount, reference,
                async () =>
                {
                    var rows = await snapshot.Catalog.QueryCandidatesAsync(new(magnitude)).ConfigureAwait(false);
                    return new AstrometricCatalogData(snapshot.Catalog.Metadata, rows.Take(EntryLimit), rows.Count <= EntryLimit, magnitude);
                }).ConfigureAwait(false);
            var candidate = await MeasureAsync("bounded-adapter", magnitude, expectedCount, reference,
                () => snapshot.Catalog.ReadAsync(magnitude, EntryLimit)).ConfigureAwait(false);
            reports.Add(baseline.Evidence);
            reports.Add(candidate.Evidence);
            Assert.IsLessThanOrEqualTo(20d, candidate.P95Ms, "Declared measured-host p95 budget.");
            Assert.IsLessThanOrEqualTo(baseline.AllocatedBytes + 32768, candidate.AllocatedBytes,
                "Declared allocation budget relative to the honest full-query baseline.");
        }

        var exact = await snapshot.Catalog.ReadAsync(5, 1637).ConfigureAwait(false);
        var overflow = await snapshot.Catalog.ReadAsync(5, 1636).ConfigureAwait(false);
        Assert.IsTrue(exact.IsCompleteForRequestedMagnitude);
        Assert.IsFalse(overflow.IsCompleteForRequestedMagnitude);
        Assert.HasCount(1636, overflow.Stars);
        Assert.AreEqual(new AstrometricCatalogProvenance(snapshot.CatalogId, snapshot.SnapshotVersion,
            "production", snapshot.PreprocessingVersion), exact.Provenance);

        var hash = new string('0', 64);
        var utc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var frame = new AstrometricFrameContext(Guid.NewGuid(), Guid.NewGuid(), hash, hash, hash,
            new(35, -114, 1000), utc, utc.AddSeconds(1), "catalog-qualification", hash);
        var calibration = new AstrometricCalibration(new(ProjectionModel.EquidistantFisheye,
            250, 250, 150, 150, 500, 500, ProjectionAperture.Circular, 240, EnforceSensorBounds: true),
            "catalog-qualification", hash);
        var refused = await AstrometricSolver.SolveAsync(frame, calibration, snapshot.Catalog, [],
            new(MaximumCatalogMagnitude: 7)).ConfigureAwait(false);
        Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, refused.Assessment.Status);
        Assert.AreEqual("catalog-incomplete", refused.Assessment.ReasonCode);
        Assert.AreEqual(0, refused.Metrics.Hypotheses);

        var evidence = new
        {
            schema = "astrometric-catalog-resources-v1",
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            snapshot.CatalogId,
            snapshot.SnapshotVersion,
            snapshot.DatabaseSha256,
            snapshot.SchemaVersion,
            snapshot.PreprocessingVersion,
            snapshot.RowCount,
            startupMs,
            startupAllocatedBytes = startupAllocated,
            warmup = 5,
            measured = 30,
            concurrency = 1,
            maximumEntries = EntryLimit,
            runtime = Environment.Version.ToString(),
            host = Environment.MachineName,
            processors = Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            io = "Warm reads use the loaded immutable cache; zero disk/network I/O",
            backlog = "N/A: stateless selection, no queue",
            reports
        };
        var path = Path.Combine(TestContext.TestRunDirectory!, "astrometric-catalog-resources.json");
        var json = JsonSerializer.Serialize(evidence, JsonOptions);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        TestContext.WriteLine(json);
    }

    private static async Task<(object Evidence, double P95Ms, long AllocatedBytes)> MeasureAsync(string route, double magnitude, int expectedCount,
        AstrometricCatalogData reference, Func<ValueTask<AstrometricCatalogData>> read)
    {
        for (var i = 0; i < 5; i++) _ = await read().ConfigureAwait(false);
        var samples = new List<object>();
        var times = new List<double>();
        var allocations = new List<long>();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        for (var i = 0; i < 30; i++)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            var selected = await read().ConfigureAwait(false);
            var elapsedMs = clock.Elapsed.TotalMilliseconds;
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Assert.HasCount(Math.Min(expectedCount, EntryLimit), selected.Stars);
            Assert.AreEqual(expectedCount <= EntryLimit, selected.IsCompleteForRequestedMagnitude);
            Assert.AreEqual(reference.SelectionIdentitySha256, selected.SelectionIdentitySha256);
            times.Add(elapsedMs);
            allocations.Add(allocated);
            samples.Add(new { iteration = i, elapsedMs, allocatedBytes = allocated });
        }
        var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        process.Refresh();
        var sorted = times.Order().ToArray();
        var report = new
        {
            route,
            magnitude,
            expectedCount,
            reference.SelectionIdentitySha256,
            complete = reference.IsCompleteForRequestedMagnitude,
            medianMs = (sorted[14] + sorted[15]) / 2,
            p95Ms = sorted[28],
            medianAllocatedBytes = allocations.Order().ElementAt(15),
            cpuMs,
            operationsPerSecond = 30000 / times.Sum(),
            processWorkingSetBytes = process.WorkingSet64,
            processPeakWorkingSetBytes = process.PeakWorkingSet64,
            samples
        };
        return (report, report.p95Ms, report.medianAllocatedBytes);
    }
}
