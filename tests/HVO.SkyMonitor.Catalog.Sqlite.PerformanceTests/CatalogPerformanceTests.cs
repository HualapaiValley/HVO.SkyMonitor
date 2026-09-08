using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CatalogPerformanceTests
{
    private const int WarmupOperations = 5;
    private const int MeasuredOperations = 100;
    private const string ExpectedVisibleSceneChecksum = "46B477061F419CCBEC5B35D42C26C2414119A99E0A63728C31999A3D4384BBBC";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W3CatStartupEvidenceAsync()
    {
        var process = Process.GetCurrentProcess();
        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();

        var snapshot = ResolveProductionSnapshot();

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        process.Refresh();
        var memory = GC.GetGCMemoryInfo();
        Assert.AreEqual(119_625, snapshot.Catalog.ObjectCount);
        await WriteEvidenceAsync("startup", new
        {
            latencyMilliseconds = elapsed,
            cpuMilliseconds = cpu,
            allocatedBytes = allocated,
            workingSetBytes = process.WorkingSet64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            managedHeapBytes = memory.HeapSizeBytes,
            fragmentedBytes = memory.FragmentedBytes,
            largeObjectHeapBytes = memory.GenerationInfo[3].SizeAfterBytes,
            databaseLengthBytes = snapshot.DatabaseLength,
            logicalDatabaseHashPasses = 2,
            sqliteIntegrityChecks = 1,
            orderedRowLoadPasses = 1,
            sqliteRowsLoaded = snapshot.RowCount,
            retainedObjectCount = snapshot.Catalog.ObjectCount,
            snapshot.DatabaseSha256
        }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task W3CatAllSkyAndBoundedCapEvidenceAsync()
    {
        var snapshot = ResolveProductionSnapshot();
        var allSky = await MeasureQueryAsync(
            "all-sky",
            snapshot.Catalog,
            new CatalogCandidateQuery(6.5),
            rowsScanned: null).ConfigureAwait(false);
        var boundedCap = await MeasureQueryAsync(
            "bounded-cap",
            snapshot.Catalog,
            new CatalogCandidateQuery(6.5, new J2000SphericalCap(6.752477, -16.716116, 20)),
            rowsScanned: allSky.ResultCount).ConfigureAwait(false);

        Assert.IsTrue(boundedCap.ResultCount < allSky.ResultCount);
        await WriteEvidenceAsync("queries", new { allSky, boundedCap }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task W3CatVisibleSceneEvidenceAsync()
    {
        var snapshot = ResolveProductionSnapshot();
        var request = new VisibleSceneRequest(
            new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
            new ObserverLocation(19.5362, -155.5763, 3397),
            new EquidistantProjectionContext(
                968, 608, 595.84 / (Math.PI / 2), 595.84, 90, 0, 0, true, 1936, 1216),
            new CatalogQuery(6.5, 2000),
            snapshot.Catalog.Metadata,
            new RefractionOptions(false),
            constellationIds: ["CAS", "ORI", "SCO", "UMA"],
            includeConstellationEndpointStars: true);
        var topology = StandardConstellationTopology.CreateD3Celestial();
        var builder = new VisibleSceneBuilder(snapshot.Catalog, topology);
        var uncappedBuilder = new VisibleSceneBuilder(new UncappedCatalog(snapshot.Catalog), topology);
        var capped = await builder.BuildAsync(request).ConfigureAwait(false);
        var uncapped = await uncappedBuilder.BuildAsync(request).ConfigureAwait(false);
        var expectedChecksum = ComputeSceneChecksum(capped);
        Assert.AreEqual(ExpectedVisibleSceneChecksum, expectedChecksum);
        Assert.AreEqual(expectedChecksum, ComputeSceneChecksum(uncapped));

        for (var warmup = 0; warmup < WarmupOperations; warmup++)
        {
            _ = await builder.BuildAsync(request).ConfigureAwait(false);
        }
        const int repetitions = 30;
        var elapsed = new double[repetitions];
        long allocated = 0;
        double cpuMilliseconds = 0;
        var process = Process.GetCurrentProcess();
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var scene = await builder.BuildAsync(request).ConfigureAwait(false);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            cpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            Assert.AreEqual(expectedChecksum, ComputeSceneChecksum(scene));
        }
        Array.Sort(elapsed);
        var totalMilliseconds = elapsed.Sum();
        await WriteEvidenceAsync("visible-scene", new
        {
            warmups = WarmupOperations,
            repetitions,
            objectCount = capped.Objects.Count,
            segmentCount = capped.Segments.Count,
            medianMilliseconds = elapsed[repetitions / 2],
            p95Milliseconds = elapsed[(int)Math.Ceiling(repetitions * 0.95) - 1],
            cpuMillisecondsPerOperation = cpuMilliseconds / repetitions,
            allocatedBytesPerOperation = allocated / repetitions,
            operationsPerSecond = repetitions / TimeSpan.FromMilliseconds(totalMilliseconds).TotalSeconds,
            resultChecksumSha256 = expectedChecksum,
            cappedAndUncappedResultsEqual = true
        }).ConfigureAwait(false);
    }

    private static CatalogSnapshotResult ResolveProductionSnapshot()
    {
        var root = Environment.GetEnvironmentVariable("HVO_CATALOG_PERF_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("HVO_CATALOG_PERF_ROOT must identify a verified production installation.");
        }
        return CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(root, "hyg-v42-production"));
    }

    private static async Task<QueryMeasurement> MeasureQueryAsync(
        string workload,
        SqliteCelestialCatalog catalog,
        CatalogCandidateQuery query,
        int? rowsScanned)
    {
        var firstStarted = Stopwatch.GetTimestamp();
        var firstResult = await catalog.QueryCandidatesAsync(query).ConfigureAwait(false);
        var firstOperationMilliseconds = Stopwatch.GetElapsedTime(firstStarted).TotalMilliseconds;
        for (var warmup = 0; warmup < WarmupOperations; warmup++)
        {
            _ = await catalog.QueryCandidatesAsync(query).ConfigureAwait(false);
        }

        var elapsed = new double[MeasuredOperations];
        long allocated = 0;
        double cpuMilliseconds = 0;
        string? checksum = null;
        var resultCount = 0;
        var process = Process.GetCurrentProcess();
        for (var iteration = 0; iteration < MeasuredOperations; iteration++)
        {
            var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var cpuBefore = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            var result = await catalog.QueryCandidatesAsync(query).ConfigureAwait(false);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            cpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            resultCount = result.Count;
            var resultChecksum = ComputeResultChecksum(result);
            checksum ??= resultChecksum;
            Assert.AreEqual(checksum, resultChecksum);
        }

        Array.Sort(elapsed);
        var totalMilliseconds = elapsed.Sum();
        return new QueryMeasurement(
            workload,
            WarmupOperations,
            MeasuredOperations,
            rowsScanned ?? firstResult.Count,
            resultCount,
            firstOperationMilliseconds,
            elapsed[MeasuredOperations / 2],
            elapsed[(int)Math.Ceiling(MeasuredOperations * 0.95) - 1],
            cpuMilliseconds / MeasuredOperations,
            allocated / MeasuredOperations,
            MeasuredOperations / TimeSpan.FromMilliseconds(totalMilliseconds).TotalSeconds,
            checksum!);
    }

    private static string ComputeResultChecksum(IReadOnlyList<CelestialCatalogObject> result)
    {
        var text = string.Join('\n', result.Select(static item => item.Id));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string ComputeSceneChecksum(VisibleScene scene)
    {
        var text = string.Join('\n', scene.Objects.Select(static item => item.Id)
            .Concat(scene.Segments.Select(static segment =>
                $"{segment.ConstellationId}:{segment.FromObjectId}:{segment.ToObjectId}:{segment.PartIndex}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static async Task WriteEvidenceAsync(string workload, object results)
    {
        var evidence = new
        {
            schema = "hvo-catalog-performance-v1",
            revision = Environment.GetEnvironmentVariable("HVO_PERF_REVISION") ?? "working-tree",
            trial = Environment.GetEnvironmentVariable("HVO_PERF_TRIAL") ?? "local",
            workload,
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                configuration = "Release",
                processorCount = Environment.ProcessorCount,
                concurrency = 1,
                initialBacklog = 0,
                serverGarbageCollection = GCSettings.IsServerGC,
                externalIo = false
            },
            results
        };
        var root = Environment.GetEnvironmentVariable("HVO_PERF_OUTPUT") ??
            Path.Combine("TestResults", "issue-111", "local");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, $"catalog-{workload}-{evidence.trial}.json"),
            JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
    }

    private sealed record QueryMeasurement(
        string Workload,
        int Warmups,
        int Repetitions,
        int RowsScanned,
        int ResultCount,
        double FirstOperationMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double CpuMillisecondsPerOperation,
        long AllocatedBytesPerOperation,
        double OperationsPerSecond,
        string ResultChecksumSha256);

    private sealed class UncappedCatalog(SqliteCelestialCatalog inner) : ICelestialCatalog, IHipparcosCatalog
    {
        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => inner.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default)
            => inner.QueryCandidatesAsync(new CatalogCandidateQuery(query.MaximumMagnitude), cancellationToken);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(
            IReadOnlyCollection<string> hipparcosIds,
            CancellationToken cancellationToken = default)
            => inner.GetByHipparcosIdsAsync(hipparcosIds, cancellationToken);
    }
}
