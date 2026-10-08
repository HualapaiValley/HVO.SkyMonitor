using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class AstrometricPerformanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    [DataRow(1936, 1216, false, 20d)]
    [DataRow(3096, 2080, true, 60d)]
    public void FullFrame_DetectionColdWarmResourcesAreMeasured(int width, int height, bool color, double seconds)
    {
        var radius = color ? 1187.5 : 568; var focal = radius / (185 * Math.PI / 360);
        var truth = new ProjectionContext(ProjectionModel.EquidistantFisheye, width / 2d, height / 2d, focal, focal, width, height,
            ProjectionAperture.Circular, radius, 72, 243, 17, true);
        var catalog = AstrometricTestFixture.Catalog(); var calibration = AstrometricTestFixture.Calibration(truth); var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc, seconds);
        var beforeFixture = GC.GetTotalAllocatedBytes(true); var image = AstrometricTestFixture.Render(catalog, truth, frame.MidpointUtc, seconds, color, 781);
        var fixtureAllocated = GC.GetTotalAllocatedBytes(true) - beforeFixture;
        var detectOptions = new StellarDetectionOptions(MaximumMajorSigma: color ? 2.15 : 1.65);
        AstrometricDetection[] Detect() => StellarDetector.Detect(image.Pixels, image.Mask, width, height, detectOptions).Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
        var coldAllocatedBefore = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew(); var detections = Detect(); var coldDetectionMs = clock.Elapsed.TotalMilliseconds;
        var cold = AstrometricSolver.Solve(frame, calibration, catalog, detections, new(ColdBudgetMilliseconds: 30000)); var coldTotalMs = clock.Elapsed.TotalMilliseconds;
        var coldAllocation = GC.GetAllocatedBytesForCurrentThread() - coldAllocatedBefore;
        Assert.IsTrue(cold.Assessment.HasMeasuredMapping, cold.Assessment.Reason);
        var settings = new AstrometricSolverOptions(ColdBudgetMilliseconds: 30000);
        for (var i = 0; i < 5; i++) Assert.IsTrue(AstrometricSolver.Refine(frame, calibration, catalog, Detect(), cold.Assessment, settings).Assessment.HasMeasuredMapping);
        var reports = new List<object>(); var totals = new List<double>(); var fitTimes = new List<double>(); var detectionTimes = new List<double>(); var allocations = new List<long>();
        using var process = Process.GetCurrentProcess(); var cpuStart = process.TotalProcessorTime;
        for (var i = 0; i < 30; i++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread(); clock.Restart(); var measured = Detect(); var detectionMs = clock.Elapsed.TotalMilliseconds;
            var warm = AstrometricSolver.Refine(frame, calibration, catalog, measured, cold.Assessment, settings); var totalMs = clock.Elapsed.TotalMilliseconds;
            Assert.IsTrue(warm.Assessment.HasMeasuredMapping, warm.Assessment.Reason); Assert.AreEqual(0, warm.Metrics.Hypotheses);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            totals.Add(totalMs); fitTimes.Add(warm.Metrics.ElapsedMilliseconds); detectionTimes.Add(detectionMs); allocations.Add(allocated);
            reports.Add(new { iteration = i, detectionMs, fitMs = warm.Metrics.ElapsedMilliseconds, totalMs, allocatedBytes = allocated, measured = measured.Length, quality = warm.Assessment.Quality });
        }
        process.Refresh();
        double Median(IEnumerable<double> values) => values.Order().ElementAt(15);
        double P95(IEnumerable<double> values) => values.Order().ElementAt(28);
        var evidence = new
        {
            schema = "shared-astrometry-resource-v1",
            width,
            height,
            color,
            seconds,
            warmup = 5,
            measured = 30,
            concurrency = 1,
            runtime = Environment.Version.ToString(),
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            inputModel = "time-integrated synthetic Gaussian stars + once-applied4-code read noise; no photon noise in this fixture",
            fixtureAllocatedBytes = fixtureAllocated,
            retainedImageBytes = (long)width * height * 17,
            coldDetectionMs,
            coldTotalMs,
            coldFitMs = cold.Metrics.ElapsedMilliseconds,
            coldAllocatedBytes = coldAllocation,
            warmDetectionMedianMs = Median(detectionTimes),
            warmDetectionP95Ms = P95(detectionTimes),
            warmFitMedianMs = Median(fitTimes),
            warmFitP95Ms = P95(fitTimes),
            warmTotalMedianMs = Median(totals),
            warmTotalP95Ms = P95(totals),
            warmAllocatedMedianBytes = allocations.Order().ElementAt(15),
            processPeakWorkingSetBytes = process.PeakWorkingSet64,
            cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            processedFramesPerSecond = 30000 / totals.Sum(),
            io = "No I/O in measured operations",
            backlog = "N/A stateless in-process library; no queue",
            baseline = "Cold versus warm on identical immutable frame; no previous production solver exists",
            reports
        };
        var json = JsonSerializer.Serialize(evidence, JsonOptions);
        var file = Path.Combine(TestContext.TestRunDirectory!, $"astrometry-{width}x{height}-{(color ? "color" : "mono")}.json"); File.WriteAllText(file, json); TestContext.AddResultFile(file); TestContext.WriteLine(json);
    }
}
