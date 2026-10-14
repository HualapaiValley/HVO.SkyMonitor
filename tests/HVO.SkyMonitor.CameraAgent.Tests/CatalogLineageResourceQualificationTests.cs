using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Explicit offline baseline/after evidence for issue #521: the same process-fresh workload is run once against each
/// installed approved catalog lineage, so HYG 4.2 (baseline) and HYG 4.4 (after) are measured on identical startup
/// validation, bounded and full-catalog queries, and a full-frame virtual-sky render drawn from the whole catalog.
/// Never substituted with a fixture catalog.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CatalogLineageResourceQualificationTests
{
    private const int Width = 1936;
    private const int Height = 1216;
    private static readonly DateTimeOffset SceneUtc = DateTimeOffset.Parse("2025-01-15T08:00:00Z", CultureInfo.InvariantCulture);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task InstalledLineageStartupQueryAndRenderResources()
    {
        var root = Environment.GetEnvironmentVariable("HVO_CATALOG_QUALIFICATION_ROOT");
        var catalogId = Environment.GetEnvironmentVariable("HVO_CATALOG_QUALIFICATION_ID");
        var packageVersion = Environment.GetEnvironmentVariable("HVO_CATALOG_QUALIFICATION_VERSION");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(catalogId) || string.IsNullOrWhiteSpace(packageVersion),
            "Supply the verified installed root, catalog ID and package version; this qualification cannot skip missing catalog evidence.");

        using var process = Process.GetCurrentProcess();
        var ioBefore = ReadProcessIo();
        var workingSetBefore = process.WorkingSet64;
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var snapshot = CatalogSnapshotResolver.Resolve(new(root!, catalogId!) { ExpectedPackageVersion = packageVersion });
        var startup = new
        {
            elapsedMs = clock.Elapsed.TotalMilliseconds,
            cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            io = Delta(ioBefore, ReadProcessIo()),
            workingSetDeltaBytes = Refreshed(process).WorkingSet64 - workingSetBefore
        };
        var specification = ApprovedCatalogSpecifications.Get(catalogId!);
        Assert.AreEqual(specification.Database.RowCount, snapshot.RowCount);
        Assert.AreEqual(specification.Database.Sha256, snapshot.DatabaseSha256, ignoreCase: true);

        var bounded = new List<object>();
        foreach (var magnitude in new[] { 5d, 5.5, 7d })
        {
            AstrometricCatalogData? selected = null;
            bounded.Add(await MeasureAsync($"bounded-read-m{magnitude.ToString(CultureInfo.InvariantCulture)}", 5, 30, async () =>
            {
                selected = await snapshot.Catalog.ReadAsync(magnitude, 2500).ConfigureAwait(false);
                return selected.Stars.Count;
            }).ConfigureAwait(false));
        }

        var full = await MeasureAsync("full-catalog-query", 2, 10, async () =>
            (await snapshot.Catalog.QueryCandidatesAsync(new(99)).ConfigureAwait(false)).Count).ConfigureAwait(false);

        var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(SceneUtc, TimeSpan.FromSeconds(1), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null));
        CameraFrame? frame = null;
        var render = await MeasureAsync("full-frame-render", 2, 10, async () =>
        {
            frame = (await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false)).Frame!;
            return frame.Metadata.Scene!.Objects!.Count;
        }).ConfigureAwait(false);
        Assert.IsNotNull(frame);
        Assert.AreEqual(catalogId, frame.Metadata.Scene!.CatalogId);
        Assert.AreEqual(packageVersion, frame.Metadata.Scene.CatalogPackageVersion);

        var evidence = new
        {
            schema = "catalog-lineage-resources-v1",
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            snapshot.CatalogId,
            snapshot.SnapshotVersion,
            snapshot.DatabaseSha256,
            snapshot.SchemaVersion,
            snapshot.PreprocessingVersion,
            snapshot.RowCount,
            databaseLength = specification.Database.Length,
            startup,
            bounded,
            full,
            render,
            renderedFrameSha256 = Convert.ToHexStringLower(SHA256.HashData(frame.PixelData.Span)),
            renderGeometry = new { width = Width, height = Height, format = "Mono16", sceneUtc = SceneUtc, exposureSeconds = 20, gain = 150 },
            processPeakWorkingSetBytes = Refreshed(process).PeakWorkingSet64,
            runtime = Environment.Version.ToString(),
            host = Environment.MachineName,
            processors = Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            backlog = "N/A: stateless startup, selection and render; no queue"
        };
        var path = Path.Combine(TestContext.TestRunDirectory!, $"catalog-lineage-resources-{catalogId}.json");
        var json = JsonSerializer.Serialize(evidence, JsonOptions);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        TestContext.WriteLine(json);
    }

    private static async Task<object> MeasureAsync(string route, int warmup, int measured, Func<Task<int>> operation)
    {
        var count = 0;
        for (var i = 0; i < warmup; i++) count = await operation().ConfigureAwait(false);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var times = new double[measured];
        var allocations = new long[measured];
        for (var i = 0; i < measured; i++)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            var current = await operation().ConfigureAwait(false);
            times[i] = clock.Elapsed.TotalMilliseconds;
            allocations[i] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Assert.AreEqual(count, current, $"{route} must be deterministic across iterations.");
        }
        var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        Array.Sort(times);
        Array.Sort(allocations);
        return new
        {
            route,
            resultCount = count,
            warmup,
            measured,
            medianMs = Percentile(times, 0.5),
            p95Ms = Percentile(times, 0.95),
            maxMs = times[^1],
            medianAllocatedBytes = allocations[measured / 2],
            cpuMs,
            workingSetBytes = Refreshed(process).WorkingSet64
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    private static Process Refreshed(Process process)
    {
        process.Refresh();
        return process;
    }

    private static Dictionary<string, long> ReadProcessIo()
        => File.ReadLines("/proc/self/io")
            .Select(static line => line.Split(':', 2))
            .ToDictionary(static parts => parts[0].Trim(), static parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));

    private static Dictionary<string, long> Delta(Dictionary<string, long> before, Dictionary<string, long> after)
        => after.ToDictionary(static pair => pair.Key, pair => pair.Value - before[pair.Key]);

    private static CameraModuleConfig CreateConfig() => new(
        new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"), new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(
            new SensorProfile("VirtualAsi174Mm", Width, Height, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
                SensorResponseMode.Monochrome, SensorRecipeVersion: "mono16-v1"),
            new OpticsProfile(
                "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, Width / 2d, Height / 2d, 0.98 * Math.Min(Width, Height) / 2d,
                CalibrationVersion: "virtual-fisheye-180-equidistant-v1"),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
        CapturePipelineConfig.Empty);
}
