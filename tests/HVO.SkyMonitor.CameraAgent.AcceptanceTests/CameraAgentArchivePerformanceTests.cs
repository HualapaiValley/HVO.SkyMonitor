using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

// Representative large-history evidence for the #514 archive read models:
// observing-day calendar counts, product paging, and capture neighbours over
// the production SQLite schemas seeded with 1,000 and 10,000 captures.
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentArchivePerformanceTests
{
    private const int Iterations = 20;
    private const int PageSize = 50;
    private const double MaximumP95Milliseconds = 2_000;
    private static readonly int[] CaptureCounts = [1_000, 10_000];
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each fixture is owned by a using declaration for its workload iteration.")]
    public async Task ArchiveReadModelsRecordIssue514Evidence()
    {
        var measurements = new List<ArchiveMeasurement>();
        foreach (var captureCount in CaptureCounts)
        {
            using var fixture = await GalleryPerformanceFixture.CreateAsync(captureCount).ConfigureAwait(false);
            var archive = fixture.Gallery;
            var newest = await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: 1), CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, newest.Items);
            var calendar = fixture.Gallery.ObservingDays;
            var toDate = calendar.Resolve(newest.Items[0].ExposureStartedUtc).Date;
            var calendarQuery = new CameraAgentGalleryCalendarQuery(toDate.AddDays(-(ObservingDayCalendar.MaximumRangeDays - 1)), toDate);

            var firstCalendar = await archive.GetCalendarAsync(calendarQuery, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ObservingDayCalendar.MaximumRangeDays, firstCalendar.Days.Count);
            Assert.IsGreaterThan(0, firstCalendar.Days.Sum(static day => day.CaptureCount));
            Assert.IsLessThanOrEqualTo(captureCount, firstCalendar.Days.Sum(static day => day.CaptureCount));
            // The seeded history spans 62 days, so most nights in the range are populated.
            Assert.IsGreaterThan(ObservingDayCalendar.MaximumRangeDays / 2, firstCalendar.Days.Count(static day => day.CaptureCount > 0));
            foreach (var (name, sql, orderedWalkAllowed, sortAllowed) in new[]
            {
                // The execution-class scalar column is omitted: its tie-break sorts at most a few association rows per output and
                // would report a temporary b-tree of its own, while the page ordering below must come from the index alone.
                ("products-page", "SELECT output.output_identity_sha256, output.committed_unix_ms FROM processing_outputs output INDEXED BY ix_processing_outputs_retention_available WHERE output.availability_state = 'Available' AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association WHERE association.output_identity_sha256 = output.output_identity_sha256) OR EXISTS (SELECT 1 FROM processing_execution_outputs association WHERE association.output_identity_sha256 = output.output_identity_sha256 AND association.published_flag = 1)) AND output.role = 'Preview' ORDER BY output.committed_unix_ms DESC, output.output_identity_sha256 DESC LIMIT 51;", true, false),
                ("products-available", "SELECT output.output_identity_sha256, output.committed_unix_ms FROM processing_outputs output INDEXED BY ix_processing_outputs_retention_available WHERE output.availability_state = 'Available' AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association WHERE association.output_identity_sha256 = output.output_identity_sha256) OR EXISTS (SELECT 1 FROM processing_execution_outputs association WHERE association.output_identity_sha256 = output.output_identity_sha256 AND association.published_flag = 1)) ORDER BY output.committed_unix_ms DESC, output.output_identity_sha256 DESC LIMIT 51;", true, false),
                // Unavailable outputs are a retention-bounded set read through their own partial index and sorted.
                ("products-unavailable", "SELECT output.output_identity_sha256, output.committed_unix_ms FROM processing_outputs output INDEXED BY ix_processing_outputs_retention_unavailable WHERE output.availability_state <> 'Available' AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association WHERE association.output_identity_sha256 = output.output_identity_sha256) OR EXISTS (SELECT 1 FROM processing_execution_outputs association WHERE association.output_identity_sha256 = output.output_identity_sha256 AND association.published_flag = 1)) AND output.availability_state = 'Missing' ORDER BY output.committed_unix_ms DESC, output.output_identity_sha256 DESC LIMIT 51;", true, true),
                ("calendar-day", "SELECT COUNT(*), MIN(raw.exposure_started_unix_ms), MAX(raw.exposure_started_unix_ms) FROM raw_captures raw INDEXED BY ix_raw_captures_gallery_time WHERE raw.exposure_started_unix_ms >= 0 AND raw.exposure_started_unix_ms < 1 AND raw.state = 'committed' AND EXISTS (SELECT 1 FROM processing_nodes node WHERE node.capture_id = raw.capture_id AND node.status = 'Completed');", false, false)
            })
            {
                var plan = await fixture.ExplainAsync(sql).ConfigureAwait(false);
                Assert.IsTrue(sortAllowed || !plan.Any(static detail => detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase)), $"{name}: {string.Join(" | ", plan)}");
                // A page ordered by an index reports as "SCAN ... USING [COVERING] INDEX", which is the intended bounded walk;
                // a day-range probe must instead SEARCH the leading column, so any SCAN of its table is a regression.
                Assert.IsFalse(plan.Any(detail => detail.StartsWith("SCAN ", StringComparison.OrdinalIgnoreCase) &&
                    (!orderedWalkAllowed || !detail.Contains(" INDEX ", StringComparison.OrdinalIgnoreCase))), $"{name}: {string.Join(" | ", plan)}");
            }
            measurements.Add(await MeasureAsync("calendar-62-days", captureCount, async () =>
                (await archive.GetCalendarAsync(calendarQuery, CancellationToken.None).ConfigureAwait(false)).Days.Count).ConfigureAwait(false));

            var firstProducts = await archive.GetProductPageAsync(new CameraAgentProductQuery(PageSize: PageSize), CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(PageSize, firstProducts.Items);
            Assert.AreEqual(0, firstProducts.SkippedCount);
            measurements.Add(await MeasureAsync("products-first", captureCount, async () =>
                (await archive.GetProductPageAsync(new CameraAgentProductQuery(PageSize: PageSize), CancellationToken.None).ConfigureAwait(false)).Items.Count).ConfigureAwait(false));
            var cursor = firstProducts.NextCursor;
            for (var page = 0; page < 3 && cursor is not null; page++)
            {
                cursor = (await archive.GetProductPageAsync(new CameraAgentProductQuery(PageSize: PageSize, Cursor: cursor), CancellationToken.None).ConfigureAwait(false)).NextCursor;
            }
            if (cursor is not null)
            {
                var laterCursor = cursor;
                measurements.Add(await MeasureAsync("products-later", captureCount, async () =>
                    (await archive.GetProductPageAsync(new CameraAgentProductQuery(PageSize: PageSize, Cursor: laterCursor), CancellationToken.None).ConfigureAwait(false)).Items.Count).ConfigureAwait(false));
            }
            if (firstProducts.Items.Count > 0)
            {
                var artifactId = firstProducts.Items[^1].ArtifactId;
                measurements.Add(await MeasureAsync("product-detail", captureCount, async () =>
                    (await archive.GetProductAsync(artifactId, CancellationToken.None).ConfigureAwait(false)) is null ? 0 : 1).ConfigureAwait(false));
            }

            var middle = await fixture.Gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: null), CancellationToken.None).ConfigureAwait(false);
            var middleId = middle.Items[middle.Items.Count / 2].CaptureId;
            Assert.IsNotNull(await archive.GetNeighboursAsync(middleId, new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false));
            // The fixture seeds every third capture as a developer fixture, so the filtered read is a real filtered walk.
            Assert.IsNotNull(await archive.GetNeighboursAsync(middleId, new CameraAgentGalleryQuery(EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture), CancellationToken.None).ConfigureAwait(false));
            measurements.Add(await MeasureAsync("neighbours-unfiltered", captureCount, async () =>
                (await archive.GetNeighboursAsync(middleId, new CameraAgentGalleryQuery(), CancellationToken.None).ConfigureAwait(false)) is null ? 0 : 2).ConfigureAwait(false));
            measurements.Add(await MeasureAsync("neighbours-origin-filter", captureCount, async () =>
                (await archive.GetNeighboursAsync(middleId, new CameraAgentGalleryQuery(EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture), CancellationToken.None).ConfigureAwait(false)) is null ? 0 : 2).ConfigureAwait(false));
        }

        foreach (var measurement in measurements)
        {
            TestContext.WriteLine($"{measurement.Scenario} @ {measurement.CaptureCount}: median {measurement.MedianMilliseconds:0.0} ms, p95 {measurement.P95Milliseconds:0.0} ms, result {measurement.ResultCount}");
            Assert.IsLessThanOrEqualTo(MaximumP95Milliseconds, measurement.P95Milliseconds, measurement.Scenario);
        }
        // A 10x larger history must not cost 10x on the bounded, indexed reads.
        foreach (var scenario in measurements.Select(static measurement => measurement.Scenario).Distinct())
        {
            var byCount = measurements.Where(measurement => measurement.Scenario == scenario).OrderBy(static measurement => measurement.CaptureCount).ToArray();
            // Sub-millisecond medians compare against a one-millisecond floor instead of being skipped.
            Assert.HasCount(2, byCount, scenario);
            Assert.IsLessThanOrEqualTo(4d, Math.Max(byCount[1].MedianMilliseconds, 1d) / Math.Max(byCount[0].MedianMilliseconds, 1d), scenario);
        }
        var evidence = new
        {
            SchemaVersion = "cameraagent-archive-514-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Command = "dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter FullyQualifiedName~CameraAgentArchivePerformanceTests",
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                Environment.ProcessorCount
            },
            Workload = new { CaptureCounts, Iterations, PageSize, CalendarDays = ObservingDayCalendar.MaximumRangeDays },
            Thresholds = new { MaximumP95Milliseconds, MaximumMedianGrowthFactor = 4 },
            Measurements = measurements
        };
        var directory = TestContext.TestRunResultsDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "cameraagent-archive-514-performance.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceJson)).ConfigureAwait(false);
        TestContext.WriteLine($"Evidence written to {path}");
    }

    private static async Task<ArchiveMeasurement> MeasureAsync(string scenario, int captureCount, Func<Task<int>> read)
    {
        var latencies = new double[Iterations];
        var resultCount = 0;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var timer = Stopwatch.StartNew();
            resultCount = await read().ConfigureAwait(false);
            timer.Stop();
            latencies[iteration] = timer.Elapsed.TotalMilliseconds;
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        Array.Sort(latencies);
        return new ArchiveMeasurement(
            scenario,
            captureCount,
            resultCount,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            latencies[^1],
            allocated / Iterations);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var rank = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    private sealed record ArchiveMeasurement(
        string Scenario,
        int CaptureCount,
        int ResultCount,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        long AllocatedBytesPerRead);
}
