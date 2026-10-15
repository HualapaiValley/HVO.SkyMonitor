using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests;

// The catalog-only leaf and measurement helpers reuse the corrected #525 source at 7827ffce.
// The common file deliberately contains no DSO type so it also compiles with the target-base catalog.
public sealed partial class CatalogPerformanceTests
{
    private const string FoundationStarsCatalogId = "hyg-v44-production";
    private const string FoundationStarsVersion = "hyg-v4.4-p4-s3-r1";
    private const string FoundationStarsDatabaseSha256 = "2CC06890DE1D4168F1EB91CA4EF5CDE1C4923F275A52A1AEDFEC002DF42A3321";
    private const long FoundationProcessPeakLimitBytes = 2L * 1024 * 1024 * 1024;

    [TestMethod]
    public Task W3CatFoundationStarsOnlyArmEvidenceAsync() =>
        FoundationArmEvidenceAsync("stars-only", FoundationStarsCatalogId, FoundationStarsVersion,
            FoundationStarsDatabaseSha256);

    private static async Task FoundationArmEvidenceAsync(
        string arm,
        string catalogId,
        string expectedPackageVersion,
        string expectedDatabaseSha256,
        Func<CatalogSnapshotResult, ValueTask<object?>>? measureDeepSky = null)
    {
        var root = RequiredFoundationEnvironment("HVO_CATALOG_DSO_ROOT");
        var packageVersion = RequiredFoundationEnvironment("HVO_CATALOG_DSO_VERSION");
        var revision = RequiredFoundationEnvironment("HVO_PERF_REVISION");
        var trial = RequiredFoundationEnvironment("HVO_PERF_TRIAL");
        var output = RequiredFoundationEnvironment("HVO_PERF_OUTPUT");
        var ticksPerSecond = RequiredFoundationEnvironment("HVO_ISSUE525_CLK_TCK");
        Assert.AreEqual(expectedPackageVersion, packageVersion, "The arm must select its pinned package.");

        using var process = Process.GetCurrentProcess();
        var ioBefore = ReadProcessIo();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var snapshot = CatalogSnapshotResolver.Resolve(
            new CatalogSnapshotResolverOptions(root, catalogId) { ExpectedPackageVersion = packageVersion });
        var startupMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var startupCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var startupAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var ioAfter = ReadProcessIo();
        process.Refresh();
        var memory = GC.GetGCMemoryInfo();

        // Assertions and package-length accounting follow the inclusive validation/load/index interval.
        Assert.AreEqual(CatalogSnapshotPackageKind.Production, snapshot.PackageKind);
        Assert.AreEqual(catalogId, snapshot.CatalogId);
        Assert.AreEqual(packageVersion, snapshot.SnapshotVersion);
        Assert.AreEqual(expectedDatabaseSha256, snapshot.DatabaseSha256.ToUpperInvariant());
        Assert.AreEqual(119_613, snapshot.Catalog.ObjectCount, "Both packages carry the same HYG 4.4 stars.");
        Assert.AreEqual(119_613L, snapshot.RowCount);
        Assert.IsLessThanOrEqualTo(FoundationProcessPeakLimitBytes, process.PeakWorkingSet64);
        var startup = new
        {
            process = "fresh",
            scope = "inclusive authenticated validation, eager row loading and immutable index construction",
            latencyMilliseconds = startupMilliseconds,
            cpuMilliseconds = startupCpuMilliseconds,
            allocatedBytes = startupAllocatedBytes,
            workingSetBytes = process.WorkingSet64,
            peakWorkingSetBytes = process.PeakWorkingSet64,
            managedHeapBytes = memory.HeapSizeBytes,
            storageReadBytes = IoDelta(ioBefore, ioAfter, "read_bytes"),
            logicalReadBytes = IoDelta(ioBefore, ioAfter, "rchar"),
            readSystemCalls = IoDelta(ioBefore, ioAfter, "syscr"),
            databaseLengthBytes = snapshot.DatabaseLength,
            packageBytes = Directory.EnumerateFiles(Path.GetDirectoryName(snapshot.ManifestPath)!)
                .Sum(static path => new FileInfo(path).Length),
            sqliteRowsLoaded = snapshot.RowCount,
            starObjectCount = snapshot.Catalog.ObjectCount,
            cache = "empty application cache; operating-system page cache uncontrolled"
        };

        var starAllSky = await MeasureLatencyAsync("stars-all-sky", MeasuredOperations,
            async _ => await snapshot.Catalog.QueryCandidatesAsync(new CatalogCandidateQuery(6.5)).ConfigureAwait(false),
            static (_, result) => (string.Empty, CheckStarQueryChecksum(result))).ConfigureAwait(false);
        var starBoundedCap = await MeasureLatencyAsync("stars-bounded-cap", MeasuredOperations,
            async _ => await snapshot.Catalog.QueryCandidatesAsync(
                new CatalogCandidateQuery(6.5, new J2000SphericalCap(6.752477, -16.716116, 20))).ConfigureAwait(false),
            static (_, result) => (string.Empty, CheckStarQueryChecksum(result))).ConfigureAwait(false);
        var deepSky = measureDeepSky is null ? null : await measureDeepSky(snapshot).ConfigureAwait(false);

        var evidence = new
        {
            schema = "hvo-catalog-foundation-performance-v1",
            revision,
            trial,
            workload = $"foundation-{arm}",
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
                externalIo = true,
                externalIoDescription = "local catalog/evidence filesystem I/O; no measured network I/O"
            },
            results = new
            {
                arm,
                snapshot.CatalogId,
                snapshot.SnapshotVersion,
                snapshot.SchemaVersion,
                snapshot.PreprocessingVersion,
                snapshot.ManifestVersion,
                snapshot.DatabaseSha256,
                startup,
                queries = new { starAllSky, starBoundedCap, deepSky },
                measurementContract = "hvo-issue525-measurement-v2",
                cpu = new
                {
                    counter = "Process.TotalProcessorTime",
                    scope = "process CPU between operation reads; result verification is separate",
                    ticksPerSecond,
                    limits = "Counter granularity, instrumentation, GC and other process threads; zero is below resolution, not free work."
                },
                processPeakLimitBytes = FoundationProcessPeakLimitBytes,
                finalMemory = ReadProcessMemory(process)
            }
        };
        Directory.CreateDirectory(output);
        var stream = new FileStream(Path.Combine(output, $"catalog-foundation-{arm}-{trial}.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var streamDisposal = stream.ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions).ConfigureAwait(false);
        GC.KeepAlive(snapshot.Catalog);
    }

    private static string RequiredFoundationEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{name} must identify the pinned foundation trial.");

    private static string CheckStarQueryChecksum(IReadOnlyList<CelestialCatalogObject> result)
    {
        Assert.IsGreaterThan(0, result.Count, "A production star workload must not be empty.");
        return ComputeResultChecksum(result);
    }

    private static string ComputeTextChecksum(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ComputeBytesChecksum(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<LatencyMeasurement> MeasureLatencyAsync<T>(
        string route,
        int repetitions,
        Func<int, ValueTask<T>> operation,
        Func<int, T, (string Key, string Checksum)> identify)
    {
        // Each result is checked against the first result for its key, outside the timed and counted region.
        var checksums = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Check(int index, T result)
        {
            var (key, checksum) = identify(index, result);
            if (checksums.TryGetValue(key, out var expected))
            {
                Assert.AreEqual(expected, checksum, $"{route} {key} must be deterministic.");
            }
            else
            {
                checksums.Add(key, checksum);
            }
        }

        var firstStarted = Stopwatch.GetTimestamp();
        var first = await operation(0).ConfigureAwait(false);
        var firstOperationMilliseconds = Stopwatch.GetElapsedTime(firstStarted).TotalMilliseconds;
        Check(0, first);
        first = default!;
        for (var warmup = 1; warmup <= WarmupOperations; warmup++)
        {
            Check(warmup, await operation(warmup).ConfigureAwait(false));
        }

        using var process = Process.GetCurrentProcess();
        var elapsed = new double[repetitions];
        long allocated = 0;
        double cpuMilliseconds = 0;
        double verificationCpuMilliseconds = 0;
        var memoryBefore = ReadProcessMemory(process);
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            var index = WarmupOperations + 1 + iteration;
            var cpuBefore = process.TotalProcessorTime;
            var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = await operation(index).ConfigureAwait(false);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated += GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
            cpuMilliseconds += (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var verificationCpuBefore = process.TotalProcessorTime;
            Check(index, result);
            verificationCpuMilliseconds += (process.TotalProcessorTime - verificationCpuBefore).TotalMilliseconds;
            // The async state machine must not retain a previous full-frame result across the next operation.
            result = default!;
        }
        var memoryAfter = ReadProcessMemory(process);

        Array.Sort(elapsed);
        return new LatencyMeasurement(
            route,
            WarmupOperations,
            repetitions,
            checksums.Count,
            firstOperationMilliseconds,
            elapsed[repetitions / 2],
            elapsed[(int)Math.Ceiling(repetitions * 0.95) - 1],
            elapsed[^1],
            cpuMilliseconds / repetitions,
            verificationCpuMilliseconds / repetitions,
            allocated / repetitions,
            repetitions / TimeSpan.FromMilliseconds(elapsed.Sum()).TotalSeconds,
            memoryBefore,
            memoryAfter,
            ComputeTextChecksum(string.Join('\n', checksums.Select(static pair => $"{pair.Key}:{pair.Value}"))));
    }

    private static ProcessMemoryMeasurement ReadProcessMemory(Process process)
    {
        process.Refresh();
        Assert.IsLessThanOrEqualTo(2L * 1024 * 1024 * 1024, process.PeakWorkingSet64,
            "W3-CAT-DSO cumulative process high-water must remain within its declared 2 GiB bound.");
        return new ProcessMemoryMeasurement(process.WorkingSet64, process.PeakWorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    // Null, never zero, when the kernel does not expose the counter.
    private static Dictionary<string, long>? ReadProcessIo()
    {
        if (!File.Exists("/proc/self/io"))
        {
            return null;
        }
        return File.ReadLines("/proc/self/io")
            .Select(static line => line.Split(':', 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(
                static parts => parts[0].Trim(),
                static parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
    }

    private static long? IoDelta(Dictionary<string, long>? before, Dictionary<string, long>? after, string counter) =>
        before is not null && after is not null && before.TryGetValue(counter, out var start) &&
        after.TryGetValue(counter, out var end)
            ? end - start
            : null;

    private sealed record LatencyMeasurement(
        string Route,
        int Warmups,
        int Repetitions,
        int DistinctResults,
        double FirstOperationMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double CpuMillisecondsPerOperation,
        double VerificationCpuMillisecondsPerOperation,
        long AllocatedBytesPerOperation,
        double OperationsPerSecond,
        ProcessMemoryMeasurement MemoryBefore,
        ProcessMemoryMeasurement MemoryAfter,
        string ResultChecksumSha256);

    // PeakWorkingSetBytes is the process-lifetime high-water, not a separately reset per-stage native allocation.
    private sealed record ProcessMemoryMeasurement(long WorkingSetBytes, long PeakWorkingSetBytes, long ManagedBytes);

}
