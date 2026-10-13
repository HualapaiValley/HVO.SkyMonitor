using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests;

public sealed partial class CatalogPerformanceTests
{
    // Opt-in diagnostic only: one warmup and five calls of unchanged public operations, not qualification.
    [TestMethod]
    public async Task CuratedLayerAllocationBreakdownAsync()
    {
        var root = RequiredFoundationEnvironment("HVO_CATALOG_DSO_ROOT");
        var output = RequiredFoundationEnvironment("HVO_CURATED_ALLOCATION_OUTPUT");
        var revision = RequiredFoundationEnvironment("HVO_PERF_REVISION");
        using var diagnosticProcess = Process.GetCurrentProcess();
        Assert.IsFalse(File.Exists(output), "Diagnostic output is create-only.");
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v44-openngc-production")
        {
            ExpectedPackageVersion = CuratedPackageVersion
        });
        var deepSky = snapshot.Catalog.DeepSky;
        Assert.IsNotNull(deepSky);
        Assert.AreEqual(117, CuratedDeepSkySelectionPlan.For(deepSky).Objects.Count);
        Assert.IsTrue(deepSky.TryGetObject("NGC1976", out var orion));
        var utc = new DateTimeOffset(2026, 1, 15, 4, 0, 0, TimeSpan.Zero);
        var observer = new ObserverLocation(35.347, -113.878, 1000);
        var direction = CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(
            new(orion.RightAscensionHours, orion.DeclinationDegrees), utc), utc,
            observer.LatitudeDegrees, observer.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, 242, 152, 484, 484, 484, 304,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: direction.AltitudeDegrees,
            BoresightAzimuthDegrees: direction.AzimuthDegrees);
        var visible = await new VisibleSceneBuilder(snapshot.Catalog).BuildAsync(new(utc, observer, projection,
            new CatalogQuery(6.5, 500), snapshot.Catalog.Metadata, projectionVersion: "curated-focused-v1"))
            .ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted,
            visible.WithCuratedDeepSky(deepSky, ProjectedSceneDeepSkySelection.Default),
            ProjectedSceneImageTransformV1.Identity(484, 304),
            new(Guid.Parse("52500000-0000-0000-0000-000000000001"),
                Guid.Parse("52500000-0000-0000-0000-000000000002"), new string('A', 64)),
            new string('B', 64), "curated-focused-v1");
        var layer = PresentationDeepSkyLayerProducer.Create(scene).Payload;
        var rows = new List<object>();
        Measure("scene-identity", () => ProjectedSceneJson.ComputeIdentity(scene), static value => value);
        Measure("scene-validate", () =>
        {
            ProjectedSceneJson.Validate(scene);
            return scene.SceneIdentitySha256;
        }, static value => value);
        Measure("scene-serialize", () => ProjectedSceneJson.Serialize(scene),
            static value => PayloadChecksum.ComputeSha256(value));
        Measure("layer-create", () => PresentationDeepSkyLayerProducer.Create(scene).Payload,
            static value => PayloadChecksum.ComputeSha256(PresentationLayerPayloadJson.Serialize(value)));
        Measure("payload-create", () => PresentationLayerPayloadJson.Create(layer.SourceIdentitySha256, 484, 304,
            layer.Markers, layer.Segments, layer.Ellipses, layer.TextBlocks, layer.TileMask),
            static value => PayloadChecksum.ComputeSha256(PresentationLayerPayloadJson.Serialize(value)));
        Measure("payload-identity", () => PresentationLayerPayloadJson.ComputeIdentity(layer), static value => value);
        Measure("payload-serialize", () => PresentationLayerPayloadJson.Serialize(layer),
            static value => PayloadChecksum.ComputeSha256(value));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "curated-allocation-diagnostic-v1", revision, snapshot.DatabaseSha256,
            scene.SceneIdentitySha256, layer.ContentIdentitySha256,
            sceneObjects = scene.Objects.Count, deepSkyObjects = scene.DeepSky!.Objects.Count,
            layerSegments = layer.Segments.Count, layerTextBlocks = layer.TextBlocks.Count,
            warmups = 1, measuredCalls = 5, rows, memory = ReadProcessMemory(diagnosticProcess),
            limits = "inclusive public-operation allocations; verification afterward; not full cost or qualifier"
        }, CuratedEvidenceJson);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes).ConfigureAwait(false);
        await file.FlushAsync().ConfigureAwait(false);

        void Measure<T>(string component, Func<T> operation, Func<T, string> identify)
        {
            using var process = Process.GetCurrentProcess();
            var expected = identify(operation()); // One warmup, including the operation's normal integrity work.
            var samples = new List<object>();
            for (var index = 0; index < 5; index++)
            {
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var cpu = process.TotalProcessorTime;
                var start = Stopwatch.GetTimestamp();
                var value = operation();
                var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - before;
                Assert.AreEqual(expected, identify(value));
                samples.Add(new { milliseconds, cpuMilliseconds, allocatedBytes });
                _ = ReadProcessMemory(process);
            }
            var row = new { component, expectedSha256 = expected, samples };
            rows.Add(row);
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
    }
}
