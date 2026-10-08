using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class CatalogPerformanceTests
{
    private const int WarmupOperations = 5;
    private const int MeasuredOperations = 100;
    private const string ExpectedVisibleSceneChecksum = "46B477061F419CCBEC5B35D42C26C2414119A99E0A63728C31999A3D4384BBBC";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string DeepSkyStarsOnlyCatalogId = "hyg-v44-production";
    private const string DeepSkyComposedCatalogId = "hyg-v44-openngc-production";
    private const int DeepSkySceneRepetitions = 30;
    private const int DeepSkyPointingCount = 50;
    private const double DeepSkyRegionRadiusDegrees = 20;
    // The W6 workload of docs/planning/performance-validation.md: the ASI676MC 3552 x 3552 frame behind the provisional
    // 2.5 mm equidistant fisheye of cameraagent.standalone-w6.json, at the observatory appsettings.StandaloneW6.json
    // pins, with the selection of that graph's projected-scene step.
    private const int W6Size = 3552;
    private const string W6CalibrationVersion = "asi676-supplied-2p5mm-equidistant-placeholder-v1";
    private static readonly ProjectionContext W6Projection = new(ProjectionModel.EquidistantFisheye, 1776, 1776,
        1097.0456, 1097.0456, W6Size, W6Size, ProjectionAperture.Circular, 1627.5, BoresightAltitudeDegrees: 90);
    private static readonly ObserverLocation W6Observer = new(35.5599378, -113.9119818, 520);
    // Three instants that each place one large object high above the W6 observatory.
    private static readonly (string Name, DateTimeOffset Utc, string ObjectId)[] DeepSkyInstants =
    [
        ("m31-high", new DateTimeOffset(2026, 10, 15, 6, 30, 0, TimeSpan.Zero), "deep-sky:NGC0224"),
        ("m45-high", new DateTimeOffset(2026, 12, 1, 6, 30, 0, TimeSpan.Zero), "deep-sky:Mel022"),
        ("m42-high", new DateTimeOffset(2027, 1, 10, 6, 0, 0, TimeSpan.Zero), "deep-sky:NGC1976")
    ];
    // Every count bound at its accepted maximum, with a one-pixel resolvable threshold so every object at least a
    // pixel across takes geometry.
    private static readonly ProjectedSceneDeepSkySelection DeepSkyStressSelection = new(
        ProjectedSceneDeepSkySelection.MaximumObjectLimit, ProjectedSceneJson.MaximumResolvedFootprintCount,
        ProjectedSceneDeepSkySelection.MaximumOutlineLimit, DeepSkyOutline.WidestLevel, 1);
    private static readonly ProjectedSceneSource DeepSkySceneSource = new(
        Guid.Parse("52500000-0000-0000-0000-000000000001"), Guid.Parse("52500000-0000-0000-0000-000000000002"),
        new string('A', 64));

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

    // W3-CAT-DSO (#525) compares, on one binary, the composed HYG 4.4 + OpenNGC package with the HYG 4.4 package whose
    // stars it carries. A driver runs one arm per fresh test process, so the resolve that opens each arm is the first
    // catalog access in its process. The page cache is not controlled: startup reports the bytes the process read from
    // storage (read_bytes) beside the bytes it read through system calls (rchar), and storage reads far below the
    // database length mean the run was served from a warm cache.
    [TestMethod]
    public Task W3CatDsoStarsOnlyArmEvidenceAsync() =>
        DeepSkyArmEvidenceAsync("stars-only", DeepSkyStarsOnlyCatalogId, composed: false);

    [TestMethod]
    public Task W3CatDsoComposedArmEvidenceAsync() =>
        DeepSkyArmEvidenceAsync("composed", DeepSkyComposedCatalogId, composed: true);

    private static async Task DeepSkyArmEvidenceAsync(string arm, string catalogId, bool composed)
    {
        var root = Environment.GetEnvironmentVariable("HVO_CATALOG_DSO_ROOT");
        var packageVersion = Environment.GetEnvironmentVariable("HVO_CATALOG_DSO_VERSION");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(packageVersion))
        {
            throw new InvalidOperationException(
                "HVO_CATALOG_DSO_ROOT and HVO_CATALOG_DSO_VERSION must identify a verified production installation.");
        }

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
        var deepSky = snapshot.Catalog.DeepSky;
        Assert.AreEqual(composed, deepSky is not null, arm);
        Assert.AreEqual(composed, snapshot.DeepSkyCounts is not null, arm);
        var startup = new
        {
            process = "fresh",
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
            deepSkyCounts = snapshot.DeepSkyCounts
        };

        var starAllSky = await MeasureLatencyAsync("stars-all-sky", MeasuredOperations,
            async _ => await snapshot.Catalog.QueryCandidatesAsync(new CatalogCandidateQuery(6.5)).ConfigureAwait(false),
            static (_, result) => (string.Empty, CheckStarQueryChecksum(result))).ConfigureAwait(false);
        var starBoundedCap = await MeasureLatencyAsync("stars-bounded-cap", MeasuredOperations,
            async _ => await snapshot.Catalog.QueryCandidatesAsync(
                new CatalogCandidateQuery(6.5, new J2000SphericalCap(6.752477, -16.716116, 20))).ConfigureAwait(false),
            static (_, result) => (string.Empty, CheckStarQueryChecksum(result))).ConfigureAwait(false);
        object? deepSkyRegion = null;
        object? deepSkyWorkloads = null;
        if (deepSky is not null)
        {
            var pointings = CreateDeepSkyPointings();
            var region = await MeasureLatencyAsync("deep-sky-region", MeasuredOperations,
                index => ValueTask.FromResult(deepSky.Query(
                    new DeepSkyQuery(deepSky.Objects.Count, pointings[index % DeepSkyPointingCount]))),
                static (index, result) => ((index % DeepSkyPointingCount).ToString(CultureInfo.InvariantCulture),
                    ComputeDeepSkyResultChecksum(result))).ConfigureAwait(false);
            var matches = pointings.Select(cap => deepSky.Query(new DeepSkyQuery(deepSky.Objects.Count, cap)).MatchCount)
                .ToArray();
            Assert.AreEqual(DeepSkyPointingCount, region.DistinctResults);
            deepSkyRegion = new
            {
                measurement = region,
                pointings = DeepSkyPointingCount,
                radiusDegrees = DeepSkyRegionRadiusDegrees,
                minimumMatches = matches.Min(),
                maximumMatches = matches.Max(),
                totalMatches = matches.Sum()
            };
            deepSkyWorkloads = await MeasurePinnedDeepSkyQueriesAsync(snapshot, deepSky).ConfigureAwait(false);
        }

        var builder = new VisibleSceneBuilder(snapshot.Catalog, StandardConstellationTopology.CreateD3Celestial(),
            new AstronomyEnginePlanetEphemeris());
        var style = new PresentationDeepSkyStyleV1(MaximumLabels: 24, MaximumLabelCharacters: 24, LabelScale: 2);
        var layout = new ImageLayout(W6Size, W6Size, CameraPixelFormat.Rgb24, W6Size * 3);
        var basePixels = CreateW6Base();
        var baseChecksum = ComputeBytesChecksum(basePixels);
        object? baseComposite = null;
        if (deepSky is not null)
        {
            baseComposite = await MeasureLatencyAsync("composite-base-only", DeepSkySceneRepetitions,
                _ => ValueTask.FromResult(PresentationLayerCompositor.CompositeDisplay(layout, basePixels, [])),
                (_, result) => (string.Empty, CheckCompositePixels(layout, basePixels, result))).ConfigureAwait(false);
        }

        var scenes = new List<object>();
        foreach (var (name, utc, objectId) in DeepSkyInstants)
        {
            var request = CreateW6Request(utc, snapshot.Catalog.Metadata);
            var stars = await builder.BuildAsync(request).ConfigureAwait(false);
            var starScene = await MeasureLatencyAsync($"stars-scene-{name}", DeepSkySceneRepetitions,
                async _ => await builder.BuildAsync(request).ConfigureAwait(false),
                static (_, scene) => (string.Empty, ComputeSceneChecksum(scene))).ConfigureAwait(false);
            if (deepSky is null)
            {
                scenes.Add(new
                {
                    instant = name,
                    utc,
                    starScene,
                    projection = await MeasureDeepSkyStagesAsync(name, stars, null, null, null, style, layout, basePixels)
                        .ConfigureAwait(false)
                });
                continue;
            }

            scenes.Add(new
            {
                instant = name,
                utc,
                starScene,
                projection = await MeasureDeepSkyStagesAsync(name, stars, deepSky,
                    ProjectedSceneDeepSkySelection.Default, objectId, style, layout, basePixels).ConfigureAwait(false),
                stress = await MeasureDeepSkyStagesAsync($"{name}-stress", stars, deepSky, DeepSkyStressSelection,
                    objectId, style, layout, basePixels).ConfigureAwait(false)
            });
        }

        Assert.AreEqual(baseChecksum, ComputeBytesChecksum(basePixels), "Composition must preserve the borrowed base.");
        await WriteEvidenceAsync($"dso-{arm}", new
        {
            arm,
            snapshot.CatalogId,
            snapshot.SnapshotVersion,
            snapshot.DatabaseSha256,
            startup,
            queries = new { starAllSky, starBoundedCap, deepSkyRegion, deepSkyWorkloads },
            measurementContract = "hvo-issue525-measurement-v2",
            cpu = new
            {
                counter = "Process.TotalProcessorTime",
                scope = "process CPU between operation counter reads; verification is separate",
                ticksPerSecond = Environment.GetEnvironmentVariable("HVO_ISSUE525_CLK_TCK"),
                limits = "Platform counter granularity, counter-read overhead, GC and other process threads; zero is below resolution, not free work."
            },
            fullResolutionBuffers = new
            {
                borrowedBaseCount = 1,
                borrowedBaseBytes = basePixels.Length,
                borrowedBaseChecksumSha256 = baseChecksum,
                ownedOutputCountPerOperation = 1,
                ownedOutputBytes = layout.RequiredByteLength,
                maximumNativeTileBytes = 1024 * 1024 * 4,
                fullFrameLayerBuffers = 0,
                source = "PresentationLayerPayload.cs CompositeCore/DrawSemanticLayer; native geometry and GC retention are covered by process high-water evidence",
                processPeakLimitBytes = 2L * 1024 * 1024 * 1024,
                baseUnchanged = true
            },
            geometry = new
            {
                workload = "W6",
                widthPixels = W6Size,
                heightPixels = W6Size,
                model = W6Projection.Model.ToString(),
                focalLengthPixels = W6Projection.FocalLengthXPixels,
                imageCircleRadiusPixels = W6Projection.ImageCircleRadiusPixels,
                calibrationVersion = W6CalibrationVersion,
                observer = W6Observer,
                compositeBase = "synthetic-3552x3552-rgb24"
            },
            defaultSelection = deepSky is null ? null : ProjectedSceneDeepSkySelection.Default,
            stressSelection = deepSky is null ? null : DeepSkyStressSelection,
            style = deepSky is null ? null : style,
            baseComposite,
            scenes
        }).ConfigureAwait(false);
    }

    private static async Task<object> MeasureDeepSkyStagesAsync(
        string route,
        VisibleScene stars,
        DeepSkyCatalog? deepSky,
        ProjectedSceneDeepSkySelection? selection,
        string? expectedObjectId,
        PresentationDeepSkyStyleV1 style,
        ImageLayout layout,
        byte[] basePixels)
    {
        var visible = stars;
        LatencyMeasurement? deepSkyScene = null;
        if (deepSky is not null)
        {
            deepSkyScene = await MeasureLatencyAsync($"deep-sky-scene-{route}", DeepSkySceneRepetitions,
                _ => ValueTask.FromResult(stars.WithDeepSky(deepSky, selection!)),
                static (_, scene) => (string.Empty, ComputeDeepSkySceneChecksum(scene))).ConfigureAwait(false);
            visible = stars.WithDeepSky(deepSky, selection!);
            Assert.IsTrue(visible.DeepSky!.Objects.Any(item => item.Id == expectedObjectId && item.Pixel is not null),
                $"{route} places {expectedObjectId}.");
        }

        var projected = CreateW6ProjectedScene(visible);
        var sceneBytes = ProjectedSceneJson.Serialize(projected);
        Assert.IsLessThanOrEqualTo(ProjectedSceneJson.MaximumPayloadBytes, sceneBytes.Length, route);
        var projectedScene = await MeasureLatencyAsync($"projected-scene-{route}", DeepSkySceneRepetitions,
            _ => ValueTask.FromResult(ProjectedSceneJson.Serialize(CreateW6ProjectedScene(visible))),
            static (_, bytes) => (string.Empty, ComputeBytesChecksum(bytes))).ConfigureAwait(false);
        if (projected.DeepSky is { } projectedDeepSky)
        {
            var layer = PresentationDeepSkyLayerProducer.Create(projected, style);
            var layerMeasurement = await MeasureLatencyAsync($"deep-sky-layer-{route}", DeepSkySceneRepetitions,
                _ => ValueTask.FromResult(PresentationDeepSkyLayerProducer.Create(projected, style)),
                static (_, result) => (string.Empty, result.Payload.ContentIdentitySha256)).ConfigureAwait(false);
            PresentationCompositorLayer[] layers =
                [new PresentationCompositorLayer(layer.Payload, true, PresentationRasterBlendMode.Normal, 1_000_000)];
            var composite = await MeasureLatencyAsync($"composite-deep-sky-{route}", DeepSkySceneRepetitions,
                _ => ValueTask.FromResult(PresentationLayerCompositor.CompositeDisplay(layout, basePixels, layers)),
                (_, result) => (string.Empty, CheckCompositePixels(layout, basePixels, result))).ConfigureAwait(false);
            // The compositor refuses a layer over its primitive or work budget, so a completed composite is the bound
            // check.
            var payload = layer.Payload;
            var presentation = new
            {
                layer = layerMeasurement,
                composite,
                payloadBytes = PresentationLayerPayloadJson.Serialize(payload).Length,
                payloadContentIdentitySha256 = payload.ContentIdentitySha256,
                layerBounds = MeasureLayerBounds(payload),
                markers = payload.Markers.Count,
                segments = payload.Segments.Count,
                ellipses = payload.Ellipses.Count,
                textBlocks = payload.TextBlocks.Count,
                textLines = payload.TextBlocks.Sum(static block => block.Lines.Count),
                drawings = CountBy(layer.Objects.Select(static item => item.Drawing.ToString()))
            };
            return new
            {
                route,
                deepSkyScene,
                projectedScene,
                projected.SchemaVersion,
                sceneBytes = sceneBytes.Length,
                projected.SceneIdentitySha256,
                starAndBodyObjects = projected.Objects.Count,
                segments = projected.Segments.Count,
                deepSky = new
                {
                    objects = projectedDeepSky.Objects.Count,
                    representations = CountBy(
                        projectedDeepSky.Objects.Select(static item => item.Representation.ToString())),
                    footprints = (projected.ResolvedFootprints ?? [])
                        .Count(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject),
                    outlines = projectedDeepSky.Outlines.Count,
                    outlinePoints = projectedDeepSky.Outlines.Sum(static outline =>
                        outline.Parts.Sum(static part => part.Points.Count))
                },
                presentation
            };
        }

        Assert.IsNull(deepSky, $"{route} placed no deep-sky object.");
        return new
        {
            route,
            projectedScene,
            projected.SchemaVersion,
            sceneBytes = sceneBytes.Length,
            projected.SceneIdentitySha256,
            starAndBodyObjects = projected.Objects.Count,
            segments = projected.Segments.Count
        };
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

    private static VisibleSceneRequest CreateW6Request(DateTimeOffset utc, CatalogMetadata metadata) => new(
        utc,
        W6Observer,
        W6Projection,
        new CatalogQuery(6.5, 300),
        metadata,
        horizonPolicy: HorizonPolicy.GeometricHorizon,
        projectionVersion: W6CalibrationVersion,
        constellationIds: ["ORI", "UMA", "UMI", "CAS", "CYG", "LYR"],
        solarSystemBodies:
        [
            SolarSystemBody.Sun, SolarSystemBody.Moon, SolarSystemBody.Mercury, SolarSystemBody.Venus,
            SolarSystemBody.Mars, SolarSystemBody.Jupiter, SolarSystemBody.Saturn, SolarSystemBody.Uranus,
            SolarSystemBody.Neptune
        ],
        includeConstellationEndpointStars: true);

    private static ProjectedSceneV1 CreateW6ProjectedScene(VisibleScene scene) => ProjectedSceneJson.Create(
        ProjectedSceneKind.Predicted,
        scene,
        ProjectedSceneImageTransformV1.Identity(W6Size, W6Size),
        DeepSkySceneSource,
        W6CalibrationVersion,
        W6CalibrationVersion);

    private static byte[] CreateW6Base()
    {
        var pixels = new byte[W6Size * W6Size * 3];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index % 251);
        }
        return pixels;
    }

    // A Fibonacci lattice spreads the region pointings evenly over the sphere.
    private static J2000SphericalCap[] CreateDeepSkyPointings() => Enumerable.Range(0, DeepSkyPointingCount)
        .Select(static index => new J2000SphericalCap(
            index * 180 * (3 - Math.Sqrt(5)) % 360 / 15,
            Math.Asin(1 - (2 * (index + 0.5) / DeepSkyPointingCount)) * 180 / Math.PI,
            DeepSkyRegionRadiusDegrees))
        .ToArray();

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

    private static string ComputeDeepSkyResultChecksum(DeepSkyQueryResult result) => ComputeTextChecksum(
        $"{result.MatchCount}\n{string.Join('\n', result.Objects.Select(static item => item.Id))}");

    private static string CheckStarQueryChecksum(IReadOnlyList<CelestialCatalogObject> result)
    {
        Assert.IsGreaterThan(0, result.Count, "A production star workload must not be empty.");
        return ComputeResultChecksum(result);
    }

    private static string ComputeDeepSkySceneChecksum(VisibleScene scene) => ComputeTextChecksum(string.Join('\n',
        (scene.DeepSky?.Objects ?? []).Select(static item => $"{item.Id}:{item.Representation}")));

    private static string ComputeTextChecksum(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string ComputeBytesChecksum(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string CheckCompositePixels(ImageLayout layout, byte[] immutableBase,
        (ImageLayout Layout, byte[] Pixels) result)
    {
        Assert.AreEqual(layout, result.Layout);
        Assert.AreEqual(layout.RequiredByteLength, result.Pixels.Length);
        Assert.AreNotSame(immutableBase, result.Pixels, "The compositor must own its output.");
        return ComputeBytesChecksum(result.Pixels);
    }

    private static ProcessMemoryMeasurement ReadProcessMemory(Process process)
    {
        process.Refresh();
        Assert.IsLessThanOrEqualTo(2L * 1024 * 1024 * 1024, process.PeakWorkingSet64,
            "W3-CAT-DSO cumulative process high-water must remain within its declared 2 GiB bound.");
        return new ProcessMemoryMeasurement(process.WorkingSet64, process.PeakWorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    private static object MeasureLayerBounds(PresentationLayerPayloadV1 payload)
    {
        Assert.AreNotEqual(PresentationLayerPayloadV1.PreviousSchemaVersion, payload.SchemaVersion,
            "The declared 4 MiB tile bound applies to semantic-layer rendering.");
        var primitives = payload.Markers.Count + payload.Segments.Count + payload.Ellipses.Count +
            payload.TextBlocks.Sum(static block => block.Lines.Count + (block.Backplate is null ? 0 : 2) +
                (block.Backplate?.Style?.HeadingRule == true && block.Lines.Count > 1 ? 1 : 0));
        var textCharacters = payload.TextBlocks.Sum(static block => block.Lines.Sum(static line => line.Length));
        var geometryWork = payload.Segments.Sum(static segment =>
            (Math.Abs(segment.From.X - segment.To.X) + Math.Abs(segment.From.Y - segment.To.Y) + 1) * segment.Thickness) +
            payload.Ellipses.Sum(static ellipse => 4 * (ellipse.RadiusX + ellipse.RadiusY)) +
            payload.Markers.Sum(static marker => 16 * (marker.Radius + 4)) +
            payload.TextBlocks.Sum(static block => block.Lines.Sum(line => (long)line.Length * 49 * block.Scale * block.Scale));
        Assert.IsNull(payload.TileMask, "Deep-sky layers have no tile-mask buffer.");
        Assert.IsLessThanOrEqualTo(PresentationDeepSkyLayerProducer.MaximumPrimitives, primitives);
        Assert.IsLessThanOrEqualTo(PresentationDeepSkyLayerProducer.MaximumWork, geometryWork);
        return new
        {
            primitives,
            textCharacters,
            geometryWork,
            maximumPrimitives = PresentationDeepSkyLayerProducer.MaximumPrimitives,
            maximumGeometryWork = PresentationDeepSkyLayerProducer.MaximumWork,
            semanticLayerSchema = payload.SchemaVersion
        };
    }

    private static SortedDictionary<string, int> CountBy(IEnumerable<string> values)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            counts[value] = counts.GetValueOrDefault(value) + 1;
        }
        return counts;
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

    private static async Task WriteEvidenceAsync(string workload, object results)
    {
        var evidence = new
        {
            schema = workload.StartsWith("dso-", StringComparison.Ordinal)
                ? "hvo-catalog-performance-v2" : "hvo-catalog-performance-v1",
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
                externalIo = workload.StartsWith("dso-", StringComparison.Ordinal),
                externalIoDescription = "DSO trials include local catalog/evidence filesystem I/O; no measured network I/O."
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
