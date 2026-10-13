using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Catalog.Sqlite.PerformanceTests;

public sealed partial class CatalogPerformanceTests
{
    private const string CuratedPresetIdsSha256 = "825DE45BA0657CF98596642ADE77F669E51450ACC01321A1E2EC67BA41442CA1";
    private const string CuratedPackageVersion = "hyg-v4.4-openngc-v20260501-p5-s4-r1";
    private static readonly string[] CuratedUnresolvedAliases = ["M102"];
    private static readonly JsonSerializerOptions CuratedEvidenceJson = new() { WriteIndented = true };
    private static readonly CameraPixelFormat[] CuratedDisplayFormats = [CameraPixelFormat.Mono8, CameraPixelFormat.Rgb24];

    // Run only under the separately allocated #525 focused performance plan, never as a Unit/Integration gate.
    [TestMethod]
    public async Task CuratedAnnotationsWarmProjectionEvidenceAsync()
    {
        var root = RequiredFoundationEnvironment("HVO_CATALOG_DSO_ROOT");
        var output = RequiredFoundationEnvironment("HVO_PERF_OUTPUT");
        var revision = RequiredFoundationEnvironment("HVO_PERF_REVISION");
        Assert.IsFalse(File.Exists(output), "Evidence is create-only.");
        using var process = Process.GetCurrentProcess();
        var ioBefore = ReadProcessIo();
        Assert.IsNotNull(ioBefore, "This focused Linux run requires actual process I/O counters.");
        var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v44-openngc-production")
        {
            ExpectedPackageVersion = CuratedPackageVersion
        });
        var deepSky = snapshot.Catalog.DeepSky;
        Assert.IsNotNull(deepSky);
        var preset = CuratedDeepSkySelectionPlan.For(deepSky);
        var startupMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var startupCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var startupAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        var ioAfter = ReadProcessIo();
        Assert.IsNotNull(ioAfter);
        // Verification is outside the inclusive validation/load/index/preset operation interval.
        Assert.AreEqual("EEA1181FFAE1DCA2935AEED2C7790305B28045061F38CA6DDA5EAF19694A8F1F", snapshot.DatabaseSha256.ToUpperInvariant());
        Assert.AreEqual(117, preset.Objects.Count);
        Assert.AreEqual(CuratedPresetIdsSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', preset.Objects.Select(static item => item.Id))))));
        CollectionAssert.AreEqual(CuratedUnresolvedAliases, preset.UnresolvedAliases.ToArray());
        var startup = new
        {
            startupMilliseconds,
            startupCpuMilliseconds,
            startupAllocatedBytes,
            memory = ReadProcessMemory(process),
            io = ioAfter,
            storageReadBytes = IoDelta(ioBefore, ioAfter, "read_bytes"),
            logicalReadBytes = IoDelta(ioBefore, ioAfter, "rchar"),
            cache = "fresh application catalog and preset; OS page cache uncontrolled (prior copy/hash may warm it)"
        };
        Console.WriteLine(JsonSerializer.Serialize(startup));
        var utc = new DateTimeOffset(2026, 1, 15, 4, 0, 0, TimeSpan.Zero);
        var observer = new ObserverLocation(35.347, -113.878, 1000);
        Assert.IsTrue(deepSky.TryGetObject("NGC1976", out var orion));
        var direction = CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(
            new(orion.RightAscensionHours, orion.DeclinationDegrees), utc), utc, observer.LatitudeDegrees, observer.LongitudeDegrees);
        var source = new ProjectedSceneSource(Guid.Parse("52500000-0000-0000-0000-000000000001"),
            Guid.Parse("52500000-0000-0000-0000-000000000002"), new string('A', 64));
        var warmIoBefore = ReadProcessIo();
        var results = new List<object>();
        foreach (var view in new[] { (Width: 484, Height: 304, Model: ProjectionModel.Perspective),
            (Width: 1920, Height: 1080, Model: ProjectionModel.Perspective),
            (Width: 3552, Height: 3552, Model: ProjectionModel.EquidistantFisheye) })
        {
            var focal = view.Model == ProjectionModel.EquidistantFisheye ? view.Width * .98 / Math.PI : view.Width;
            var projection = new ProjectionContext(view.Model, view.Width / 2d, view.Height / 2d,
                focal, focal, view.Width, view.Height,
                view.Model == ProjectionModel.EquidistantFisheye ? ProjectionAperture.Circular : ProjectionAperture.Rectangular,
                ImageCircleRadiusPixels: view.Model == ProjectionModel.EquidistantFisheye ? view.Width * .49 : null,
                BoresightAltitudeDegrees: direction.AltitudeDegrees, BoresightAzimuthDegrees: direction.AzimuthDegrees);
            var visible = await new VisibleSceneBuilder(snapshot.Catalog).BuildAsync(new(utc, observer, projection,
                new CatalogQuery(6.5, 500), snapshot.Catalog.Metadata, projectionVersion: "curated-focused-v1")).ConfigureAwait(false);
            var enabled = visible.WithCuratedDeepSky(deepSky, ProjectedSceneDeepSkySelection.Default);
            Assert.IsTrue(enabled.DeepSky!.Objects.Any(static item => item.Id == "deep-sky:NGC1976"), "Each view has the pinned M42 target.");
            var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, enabled,
                ProjectedSceneImageTransformV1.Identity(view.Width, view.Height), source, new string('B', 64), "curated-focused-v1");
            var projectionCost = MeasureCuratedPair("projection", () => visible,
                () => visible.WithCuratedDeepSky(deepSky, ProjectedSceneDeepSkySelection.Default),
                value => PayloadChecksum.ComputeSha256(ProjectedSceneJson.Serialize(ProjectedSceneJson.Create(
                    ProjectedSceneKind.Predicted, value, ProjectedSceneImageTransformV1.Identity(view.Width, view.Height),
                    source, new string('B', 64), "curated-focused-v1"))));
            Console.WriteLine(JsonSerializer.Serialize(projectionCost));
            Assert.IsLessThanOrEqualTo(10d, projectionCost.Enabled.MedianMilliseconds);
            Assert.IsLessThanOrEqualTo(25d, projectionCost.Enabled.P95Milliseconds);
            Assert.IsLessThanOrEqualTo(10d, projectionCost.Enabled.CpuMillisecondsPerOperation);
            Assert.IsLessThanOrEqualTo(2d * 1024 * 1024, projectionCost.Enabled.AllocatedBytesPerOperation);
            var empty = PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, view.Width, view.Height);
            var layerCost = MeasureCuratedPair("layer", () => empty,
                () => PresentationDeepSkyLayerProducer.Create(scene).Payload,
                value => PayloadChecksum.ComputeSha256(PresentationLayerPayloadJson.Serialize(value)));
            Console.WriteLine(JsonSerializer.Serialize(layerCost));
            Assert.IsLessThanOrEqualTo(20d, layerCost.Enabled.MedianMilliseconds);
            Assert.IsLessThanOrEqualTo(50d, layerCost.Enabled.P95Milliseconds);
            Assert.IsLessThanOrEqualTo(20d, layerCost.Enabled.CpuMillisecondsPerOperation);
            Assert.IsLessThanOrEqualTo(4d * 1024 * 1024, layerCost.Enabled.AllocatedBytesPerOperation);
            var layer = PresentationDeepSkyLayerProducer.Create(scene).Payload;
            var compositions = new List<object>();
            foreach (var format in CuratedDisplayFormats)
            {
                var layout = new ImageLayout(view.Width, view.Height, format, view.Width * ImageLayout.BytesPerPixel(format));
                var raw = new byte[layout.RequiredByteLength];
                var compositeCost = MeasureCuratedPair("composition-" + format,
                    () => PresentationLayerCompositor.Composite(layout, raw, []),
                    () => PresentationLayerCompositor.Composite(layout, raw,
                        [new(layer, true, PresentationRasterBlendMode.Normal, 1_000_000)]), static value => PayloadChecksum.ComputeSha256(value));
                var composition = new { format, compositeCost };
                compositions.Add(composition);
                Console.WriteLine(JsonSerializer.Serialize(composition));
                Assert.IsLessThanOrEqualTo(view.Width == 3552 ? 250d : 100d, compositeCost.Enabled.P95Milliseconds);
                Assert.IsLessThanOrEqualTo(view.Width == 3552 ? 250d : 100d, compositeCost.Enabled.CpuMillisecondsPerOperation);
                Assert.IsLessThanOrEqualTo(16d * raw.Length + 4 * 1024 * 1024,
                    compositeCost.Enabled.AllocatedBytesPerOperation);
                Assert.IsTrue(raw.All(static value => value == 0), "The supplied base is immutable.");
            }
            var row = new
            {
                view.Width,
                view.Height,
                view.Model,
                objects = enabled.DeepSky.Objects.Count,
                footprints = enabled.ResolvedFootprints.Count,
                scene.SceneIdentitySha256,
                projectionCost,
                layerCost,
                compositions
            };
            results.Add(row);
            // Retain completed raw measurements in the ordinary test output even when the next criterion is red.
            Console.WriteLine(JsonSerializer.Serialize(row));
            process.Refresh();
            Assert.IsLessThanOrEqualTo(FoundationProcessPeakLimitBytes, process.PeakWorkingSet64);
        }
        var warmIoAfter = ReadProcessIo();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "curated-annotations-focused-performance-v1",
            revision,
            snapshot.CatalogId,
            snapshot.SnapshotVersion,
            snapshot.DatabaseSha256,
            presetIdsSha256 = CuratedPresetIdsSha256,
            startup,
            results,
            memory = ReadProcessMemory(process),
            warmIoBefore,
            warmIoAfter,
            warmStorageReadBytes = IoDelta(warmIoBefore, warmIoAfter, "read_bytes"),
            warmLogicalReadBytes = IoDelta(warmIoBefore, warmIoAfter, "rchar"),
            ioLimits = "warm interval includes fixture setup, metrics and output verification; no per-operation I/O attribution",
            scope = "cached curated projection, layer construction, Mono8/RGB24 composition; star scene built before timing; not whole capture, F7 or continuous native proof"
        }, CuratedEvidenceJson);
        using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes).ConfigureAwait(false);
        await file.FlushAsync().ConfigureAwait(false);
    }

    private static CuratedPairCost MeasureCuratedPair<T>(string stage, Func<T> disabled, Func<T> enabled, Func<T, string> identify)
    {
        using var process = Process.GetCurrentProcess();
        var off = new List<CuratedOperationCost>();
        var on = new List<CuratedOperationCost>();
        var expected = new string?[2];
        for (var iteration = -WarmupOperations; iteration < MeasuredOperations; iteration++)
        {
            // ABBA arm ordering within the same session. Five warmups plus 100 measured operations per arm.
            foreach (var arm in iteration % 4 is 0 or 3 ? new[] { 0, 1 } : new[] { 1, 0 })
            {
                var cpuBefore = process.TotalProcessorTime;
                var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
                var start = Stopwatch.GetTimestamp();
                var result = arm == 0 ? disabled() : enabled();
                var latency = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
                var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                // Checksums and their CPU cost are measured AFTER the operation interval ends.
                var verificationBefore = process.TotalProcessorTime;
                var checksum = identify(result);
                expected[arm] ??= checksum;
                Assert.AreEqual(expected[arm], checksum, stage + " must retain exact output.");
                var verificationCpu = (process.TotalProcessorTime - verificationBefore).TotalMilliseconds;
                if (iteration >= 0) (arm == 0 ? off : on).Add(new(latency, cpu, allocated, verificationCpu));
                process.Refresh();
                Assert.IsLessThanOrEqualTo(FoundationProcessPeakLimitBytes, process.PeakWorkingSet64);
            }
        }
        return new(stage, Summary(off), Summary(on), expected[0]!, expected[1]!, off, on);

        static CuratedSummary Summary(List<CuratedOperationCost> rows)
        {
            var ordered = rows.Select(static row => row.LatencyMilliseconds).Order().ToArray();
            return new(ordered[MeasuredOperations / 2], ordered[94], ordered[^1],
                rows.Average(static row => row.CpuMilliseconds), rows.Average(static row => row.AllocatedBytes),
                rows.Average(static row => row.VerificationCpuMilliseconds));
        }
    }

    private sealed record CuratedOperationCost(double LatencyMilliseconds, double CpuMilliseconds,
        long AllocatedBytes, double VerificationCpuMilliseconds);
    private sealed record CuratedSummary(double MedianMilliseconds, double P95Milliseconds, double MaximumMilliseconds,
        double CpuMillisecondsPerOperation, double AllocatedBytesPerOperation, double VerificationCpuMillisecondsPerOperation);
    private sealed record CuratedPairCost(string Stage, CuratedSummary Disabled, CuratedSummary Enabled,
        string DisabledSha256, string EnabledSha256, IReadOnlyList<CuratedOperationCost> DisabledRows,
        IReadOnlyList<CuratedOperationCost> EnabledRows);
}
