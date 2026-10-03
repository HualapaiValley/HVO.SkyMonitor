using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

/// <summary>
/// Issue #993 evidence at W1 geometry (1936 x 1216) with the production option defaults.
/// <list type="bullet">
/// <item><description>
/// The producer-backed measurement commits real W1 Mono16 captures through raw ingress and the standard-lane Preview
/// node, then generates from the journal through <see cref="JournalNightlyProductSourceReader"/>: admission, gaps,
/// lineage back to the journal, durable files, and a reopened-store unchanged retry against an uninterrupted control.
/// </description></item>
/// <item><description>
/// The whole-day measurement scales that to 1440 frames at a 60 second cadence. Its source payloads are copied from a
/// declared in-memory pattern cache on every restore, so it measures admission, recipes, and publication, not reads.
/// </description></item>
/// </list>
/// Working set is sampled during each run, so a figure is the generator's own peak above its pre-run baseline rather
/// than the process's lifetime peak.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods do not require context-free continuations.")]
public sealed class NightlyProductGenerationPerformanceTests
{
    private const int Width = 1936;
    private const int Height = 1216;
    private const int FrameBytes = Width * Height;
    private const int DayFrameCount = 1440;
    private const int PatternCount = 16;
    private const int MaximumRunsToConverge = 64;
    private const int RestartSegmentsPerRun = 256;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly NightlyProductKind[] Kinds = [NightlyProductKind.Keogram, NightlyProductKind.StarTrail];

    private static readonly CameraRigConfig Rig = new(
        new SensorProfile("W1", Width, Height, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
            StrideBytes: Width * 2, ByteOrder: SampleByteOrder.LittleEndian),
        new OpticsProfile(
            "EquidistantFisheye", 2.5, 180, 0, LensKind.Fisheye,
            PrincipalPointX: Width / 2.0, PrincipalPointY: Height / 2.0, ImageCircleRadiusPixels: 600,
            FocalLengthXPixels: 600 * 2 / Math.PI, FocalLengthYPixels: 600 * 2 / Math.PI,
            CalibrationVersion: "issue-993-w1-calibration-v1"),
        new RigOrientation(90, 0, 0),
        new PipelineExposureProfile(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 1, 1),
        ProfileVersion: "issue-993-w1-rig-v1");

    private static readonly DateTimeOffset AfterRollover = NightlyProductFixture.DayEndUtc.AddMinutes(10);

    // Three dark hours (21:00-00:00 MST) with a keogram gap from 05:21Z to 05:39Z, plus three mid-afternoon frames.
    private static readonly DateTimeOffset NightStartUtc = new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset[] ProducerExposures =
    [
        .. Enumerable.Range(0, 3).Select(static minute => new DateTimeOffset(2026, 10, 1, 21, 30 + minute, 0, TimeSpan.Zero)),
        .. Enumerable.Range(0, 180)
            .Where(static minute => minute is < 81 or > 99)
            .Select(static minute => NightStartUtc.AddMinutes(minute))
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Issue993ProducerBackedSustainedNightEvidence()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-producer");
        var controlRoot = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-producer-control");
        try
        {
            using var provider = CreateProvider(root);
            var configuration = Configuration();
            var production = await ProduceAsync(provider, configuration);
            var reader = new CountingSourceReader(provider.GetRequiredService<INightlyProductSourceReader>());
            var published = await reader.ReadCandidatesAsync(
                NightlyProductFixture.NodeId, NightlyProductFixture.DayStartUtc, NightlyProductFixture.DayEndUtc,
                4096, static _ => Assert.Fail("A W1 preview was reported unsupported."), CancellationToken.None);
            Assert.HasCount(ProducerExposures.Length, published);
            Assert.HasCount(1, published.Select(static candidate => candidate.RigProfileSha256).Distinct());
            Assert.AreEqual(RigProjectionContextFactory.CreateProfileHashSha256(Rig), published[0].RigProfileSha256);
            var nightFrames = published.Where(static candidate => candidate.ExposureStartedUtc >= NightStartUtc)
                .Select(static candidate => candidate.ArtifactId)
                .ToArray();

            var control = await GenerateAsync(controlRoot, reader, configuration, restartEveryRun: false);
            var restarted = await GenerateAsync(root, reader, configuration, restartEveryRun: true);

            var kinds = new Dictionary<NightlyProductKind, object>();
            foreach (var kind in Kinds)
            {
                Assert.AreEqual(control[kind].Night.PayloadSha256, restarted[kind].Night.PayloadSha256, $"{kind} night changed across restarts.");
                Assert.AreEqual(control[kind].Night.ProvenanceSha256, restarted[kind].Night.ProvenanceSha256, $"{kind} provenance changed across restarts.");
                Assert.AreEqual(
                    restarted[kind].ProductsRecorded,
                    restarted[kind].ProductsPublished,
                    $"{kind} recomputed a product after a restart.");
                CollectionAssert.AreEqual(kind == NightlyProductKind.Keogram ? published.Select(static item => item.ArtifactId).ToArray() : nightFrames, restarted[kind].NightFrameLineage.ToArray(), $"{kind} lineage is not the journal's night frames.");
                Assert.AreEqual(
                    kind == NightlyProductKind.StarTrail ? ProducerExposures.Length - nightFrames.Length : 0,
                    restarted[kind].Exclusions.GetValueOrDefault(NightlyProductContract.ExcludedSolarAltitudeReasonCode));
                kinds[kind] = new { control = control[kind], restarted = restarted[kind] };
            }
            Assert.AreEqual((int)Math.Ceiling((NightlyProductFixture.DayEndUtc - NightlyProductFixture.DayStartUtc).TotalMinutes),
                restarted[NightlyProductKind.Keogram].Night.Width, "Missing coverage retains the complete planned axis.");

            await WriteEvidenceAsync("issue-993-nightly-product-producer-performance.json", new
            {
                schemaVersion = "issue-993-nightly-product-producer-performance-v1",
                revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
                trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"),
                measuredUtc = DateTimeOffset.UtcNow,
                workload = new
                {
                    geometry = "W1",
                    rawPixelFormat = nameof(CameraPixelFormat.Mono16),
                    rawFrameBytes = FrameBytes * 2,
                    previewFrameBytes = FrameBytes,
                    captures = ProducerExposures.Length,
                    nightFrames = nightFrames.Length,
                    daytimeFrames = ProducerExposures.Length - nightFrames.Length,
                    gap = "2026-10-02T05:21Z..05:39Z",
                    restartSegmentsPerRun = RestartSegmentsPerRun,
                    options = new { sourceNodeId = NightlyProductFixture.NodeId, sourceRecipeIdentitySha256 = published[0].RecipeIdentitySha256,
                        rigProfileSha256 = published[0].RigProfileSha256, maximumSegmentSources = 32, maximumRecipeExecutions = RestartSegmentsPerRun,
                        fixedTransfer = new FixedDisplayTransferOptions(), keogramColumnSeconds = 60 }
                },
                production,
                kinds,
                storage = new
                {
                    journalBytes = DirectoryBytes(Path.Combine(root, "journal")),
                    nightlyDatabaseBytes = DirectoryBytes(Path.Combine(root, SqliteNightlyProductStore.DirectoryName)),
                    nightlyProductFileBytes = DirectoryBytes(Path.Combine(root, SqliteNightlyProductStore.ProductDirectoryName)),
                    nightlyProductFiles = Directory.EnumerateFiles(
                        Path.Combine(root, SqliteNightlyProductStore.ProductDirectoryName), "*", SearchOption.AllDirectories).Count()
                },
                process = Process()
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            Directory.Delete(controlRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Issue993WholeObservingDayGenerationEvidence()
    {
        var first = await MeasureWholeDayAsync();
        var second = await MeasureWholeDayAsync();

        foreach (var kind in Kinds)
        {
            Assert.AreEqual(first[kind].Night.PayloadSha256, second[kind].Night.PayloadSha256, $"{kind} night is not reproducible.");
            Assert.AreEqual(first[kind].Night.ProvenanceSha256, second[kind].Night.ProvenanceSha256, $"{kind} provenance is not reproducible.");
        }

        await WriteEvidenceAsync("issue-993-nightly-product-generation-performance.json", new
        {
            schemaVersion = "issue-993-nightly-product-generation-performance-v1",
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"),
            measuredUtc = DateTimeOffset.UtcNow,
            workload = new
            {
                geometry = "W1",
                pixelFormat = nameof(CameraPixelFormat.Mono8),
                frameBytes = FrameBytes,
                frames = DayFrameCount,
                cadenceSeconds = 60,
                observingDate = NightlyProductFixture.ObservingDate,
                harnessPatternCacheBytes = (long)PatternCount * FrameBytes,
                options = new { sourceNodeId = NightlyProductFixture.NodeId, sourceRecipeIdentitySha256 = NightlyProductFixture.PreviewRecipe,
                    rigProfileSha256 = RigProjectionContextFactory.CreateProfileHashSha256(Rig), maximumSegmentSources = 32,
                    maximumRecipeExecutions = RestartSegmentsPerRun, keogramColumnSeconds = 60 }
            },
            passes = new[] { first, second },
            reproducible = true,
            process = Process()
        });
    }

    [TestMethod]
    public async Task Issue993WholeColorObservingDayGenerationEvidence()
    {
        var first = await MeasureWholeDayAsync(color: true);
        var second = await MeasureWholeDayAsync(color: true);
        foreach (var kind in Kinds)
        {
            Assert.AreEqual(first[kind].Night.PayloadSha256, second[kind].Night.PayloadSha256);
            Assert.AreEqual(first[kind].Night.ProvenanceSha256, second[kind].Night.ProvenanceSha256);
        }
        await WriteEvidenceAsync("issue-993-color-product-generation-performance.json", new
        {
            schemaVersion = "issue-993-color-product-generation-performance-v1",
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"), measuredUtc = DateTimeOffset.UtcNow,
            workload = new { geometry = "W2 processed RGB24", width = 3096, height = 2080, frameBytes = 19319040,
                frames = DayFrameCount, cadenceSeconds = 60, patternCacheBytes = (long)PatternCount * 19319040,
                inputMode = "Declared synthetic pattern cache, not captured sky and not a durable source-I/O measurement",
                maximumRecipeExecutions = RestartSegmentsPerRun, maximumResidentSourceBytes = 256L * 1024 * 1024 },
            passes = new[] { first, second }, reproducible = true, process = Process()
        });
    }

    private async Task WriteEvidenceAsync(string fileName, object evidence)
    {
        var json = JsonSerializer.Serialize(evidence, JsonOptions);
        TestContext.WriteLine(json);
        if (!string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE993_EVIDENCE"), "1", StringComparison.Ordinal))
        {
            return;
        }
        var directory = Path.Combine(
            RequiredEnvironment("HVO_ISSUE993_EVIDENCE_ROOT"),
            RequiredEnvironment("HVO_EVIDENCE_REVISION"),
            "trials",
            RequiredEnvironment("HVO_EVIDENCE_TRIAL"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), json);
    }

    private static async Task<Dictionary<NightlyProductKind, KindEvidence>> MeasureWholeDayAsync(bool color = false)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-performance");
        try
        {
            var rig = color ? Rig with
            {
                Sensor = new SensorProfile("W2", 3096, 2080, 2.4, SensorColorMode.Color, CameraPixelFormat.BayerRggb16,
                    SensorResponseMode.BayerRaw, StrideBytes: 6192, ByteOrder: SampleByteOrder.LittleEndian),
                Optics = new OpticsProfile("EquidistantFisheye", 2.5, 180, 0, LensKind.Fisheye,
                    1548, 1040, 1030, FocalLengthXPixels: 1030 * 2 / Math.PI, FocalLengthYPixels: 1030 * 2 / Math.PI,
                    CalibrationVersion: "issue-993-w2-calibration-v1")
            } : Rig;
            var reader = new CountingSourceReader(new SyntheticSourceReader(rig.Sensor.WidthPixels, rig.Sensor.HeightPixels,
                color ? 3 : 1, RigProjectionContextFactory.CreateProfileHashSha256(rig)));
            return await GenerateAsync(root, reader, Configuration() with { Rig = rig }, restartEveryRun: false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Runs each kind to convergence and once more unchanged. With <paramref name="restartEveryRun"/> the store and
    /// generator are rebuilt before the unchanged retry, as after a process restart. An occurrence must fit the
    /// <see cref="RestartSegmentsPerRun"/> recipe-execution budget before any sources are restored.
    /// </summary>
    private static async Task<Dictionary<NightlyProductKind, KindEvidence>> GenerateAsync(
        string root,
        CountingSourceReader reader,
        CameraModuleConfig configuration,
        bool restartEveryRun)
    {
        var nightly = new NightlyProductOptions
        {
            Enabled = true,
            SourceNodeId = NightlyProductFixture.NodeId,
            SourceRecipeIdentitySha256 = (await reader.ReadCandidatesAsync(NightlyProductFixture.NodeId,
                NightlyProductFixture.DayStartUtc, NightlyProductFixture.DayEndUtc, 4096, static _ => { }, CancellationToken.None))[0].RecipeIdentitySha256,
            RigProfileSha256 = RigProjectionContextFactory.CreateProfileHashSha256(configuration.Rig),
            MaximumSegmentsPerRun = RestartSegmentsPerRun
        };
        var options = NightlyProductFixture.HostOptions(root, nightly);
        var clock = new NightlyClock(AfterRollover);
        var store = new SqliteNightlyProductStore(options, clock);
        var generator = Generator(store);
        try
        {
            var result = new Dictionary<NightlyProductKind, KindEvidence>();
            foreach (var kind in Kinds)
            {
                var runs = new List<RunEvidence>();
                NightlyProductRunReport report;
                do
                {
                    Assert.IsLessThan(MaximumRunsToConverge, runs.Count, $"{kind} did not converge.");
                    if (restartEveryRun && runs.Count > 0)
                    {
                        generator.Dispose();
                        store.Dispose();
                        SqliteConnection.ClearAllPools();
                        store = new SqliteNightlyProductStore(options, clock);
                        generator = Generator(store);
                    }
                    (report, var run) = await MeasureRunAsync(generator, reader, kind, NightlyProductFixture.Occurrence(kind, nightly));
                    runs.Add(run);
                    Assert.IsTrue(report.Ready, report.Note);
                    Assert.AreEqual(0, report.FailedWindows, report.Describe());
                }
                while (report.PendingWindows > 0);

                if (restartEveryRun)
                {
                    generator.Dispose(); store.Dispose(); SqliteConnection.ClearAllPools();
                    store = new SqliteNightlyProductStore(options, clock); generator = Generator(store);
                }
                var (unchanged, unchangedRun) = await MeasureRunAsync(generator, reader, kind, NightlyProductFixture.Occurrence(kind, nightly));
                Assert.AreEqual(0, unchanged.ProductsPublished, unchanged.Describe());
                Assert.AreEqual(0, unchangedRun.Restores, "An unchanged re-run restored source payloads.");
                result[kind] = await DescribeAsync(root, store, kind, runs, unchangedRun);
            }
            return result;
        }
        finally
        {
            generator.Dispose();
            store.Dispose();
        }

        NightlyProductGenerator Generator(SqliteNightlyProductStore current) => new(
            options,
            new FixedConfigurationAccessor(configuration),
            reader,
            current,
            new AstronomyEnginePlanetEphemeris(),
            static () => null,
            clock);
    }

    private static async Task<KindEvidence> DescribeAsync(
        string root,
        SqliteNightlyProductStore store,
        NightlyProductKind kind,
        List<RunEvidence> runs,
        RunEvidence unchanged)
    {
        var nightWindow = await store.ReadWindowAsync(
            kind, NightlyProductScope.Final, NightlyProductFixture.DayStartUtc, CancellationToken.None);
        Assert.IsNotNull(nightWindow);
        Assert.AreEqual(NightlyProductWindowDisposition.Produced, nightWindow.Status.Disposition);
        var night = await store.GetAsync(nightWindow.ProductIds.Single(), CancellationToken.None);
        Assert.IsNotNull(night);
        var directory = Path.Combine(
            root, SqliteNightlyProductStore.ProductDirectoryName, "2026", "10", "01", NightlyProductContract.TargetFor(kind));
        var stem = Path.Combine(directory, night.Summary.ProductId.ToString("N"));
        Assert.AreEqual(night.PayloadSha256, await FileSha256Async(stem + ".bin"), "The night payload file does not match its record.");
        Assert.AreEqual(night.RenditionSha256, await FileSha256Async(stem + ".jpg"), "The night rendition file does not match its record.");
        Assert.AreEqual(night.ProvenanceSha256, await FileSha256Async(stem + ".provenance.json"), "The night provenance file does not match its record.");

        var lineage = await FrameLineageAsync(store, night);
        var windows = (await store.ListWindowsAsync(NightlyProductFixture.ObservingDate, CancellationToken.None))
            .Where(window => window.Kind == kind)
            .ToArray();
        var products = (await store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None))
            .Count(product => product.Kind == kind);
        return new KindEvidence(
            runs,
            unchanged,
            runs.Sum(static run => run.ProductsPublished),
            products,
            windows.GroupBy(static window => window.Disposition.ToString())
                .ToDictionary(static group => group.Key, static group => group.Count()),
            windows.Where(static window => window.Scope == NightlyProductScope.Segment)
                .SelectMany(static window => window.Exclusions)
                .GroupBy(static pair => pair.Key)
                .ToDictionary(static group => group.Key, static group => group.Sum(static pair => pair.Value)),
            lineage.Count,
            lineage,
            new NightEvidence(
                night.Summary.Width,
                night.Summary.Height,
                night.Sources.Count,
                night.Summary.FirstObservationUtc,
                night.Summary.LastObservationUtc,
                night.OutputIdentitySha256,
                night.PayloadSha256,
                night.PayloadBytes,
                night.RenditionSha256,
                night.RenditionBytes,
                night.ProvenanceSha256));
    }

    /// <summary>Resolves a product's ordered lineage down to the preview frames it was composed from.</summary>
    private static async Task<List<Guid>> FrameLineageAsync(SqliteNightlyProductStore store, NightlyProductDetail product)
    {
        var frames = new List<Guid>();
        foreach (var source in product.Sources)
        {
            if (source.SourceKind != NightlyProductSourceKind.NightlyProduct)
            {
                frames.Add(source.ArtifactId);
                continue;
            }
            var referenced = await store.GetAsync(source.ArtifactId, CancellationToken.None);
            Assert.IsNotNull(referenced);
            Assert.AreEqual(source.OutputIdentitySha256, referenced.OutputIdentitySha256);
            frames.AddRange(await FrameLineageAsync(store, referenced));
        }
        return frames;
    }

    private static async Task<(NightlyProductRunReport Report, RunEvidence Run)> MeasureRunAsync(
        NightlyProductGenerator generator,
        CountingSourceReader reader,
        NightlyProductKind kind,
        LocalAutomationOccurrence occurrence)
    {
        var (queries, restores, frames) = (reader.Queries, reader.Restores, reader.RestoredFrames);
        reader.LargestRestore = 0;
        reader.LargestRestoreBytes = 0;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        await using var sampler = WorkingSetSampler.Start();
        using var measuredProcess = System.Diagnostics.Process.GetCurrentProcess();
        var cpu = measuredProcess.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        var report = await generator.RunAsync(occurrence, CancellationToken.None);
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var (baseline, peak, peakHeap) = await sampler.StopAsync();
        var run = new RunEvidence(
            milliseconds,
            (measuredProcess.TotalProcessorTime - cpu).TotalSeconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            GC.CollectionCount(2) - gen2,
            baseline,
            peak,
            peak - baseline,
            peakHeap,
            report.WindowsEvaluated,
            report.WindowsUnchanged,
            report.ProductsPublished,
            report.ProductsReused,
            report.PendingWindows,
            report.FinalsRecorded,
            reader.Queries - queries,
            reader.Restores - restores,
            reader.RestoredFrames - frames,
            reader.LargestRestore,
            reader.LargestRestoreBytes);
        Assert.IsLessThanOrEqualTo(
            new NightlyProductOptions().MaximumSegmentSources,
            reader.LargestRestore,
            "A restore exceeded the per-part source bound.");
        Assert.IsLessThanOrEqualTo(256L * 1024 * 1024, reader.LargestRestoreBytes, "Resident source bytes exceeded the recipe bound.");
        return (report, run);
    }

    /// <summary>Commits every producer exposure through raw ingress and drains the standard lane's Preview node.</summary>
    private static async Task<object> ProduceAsync(ServiceProvider provider, CameraModuleConfig configuration)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None);
        await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(configuration, CancellationToken.None);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        await laneStore.InitializeLanesAsync(CancellationToken.None);
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static definition => definition.Name == "standard");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "standard");

        var ingressMilliseconds = new List<double>();
        var previewMilliseconds = new List<double>();
        var pixels = new byte[FrameBytes * 2];
        for (var index = 0; index < ProducerExposures.Length; index++)
        {
            FillRaw(pixels, index);
            var started = Stopwatch.GetTimestamp();
            Assert.IsNotNull(await ingress.AcceptAsync(
                configuration, Submission(ProducerExposures[index], pixels), CancellationToken.None));
            ingressMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            started = Stopwatch.GetTimestamp();
            var lease = await laneStore.ClaimAsync(standard, "issue-993-performance", configuration, CancellationToken.None);
            Assert.IsNotNull(lease);
            var result = await handler.HandleAsync(lease.Context, CancellationToken.None);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            await laneStore.CompleteAsync(lease, CancellationToken.None);
            previewMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return new
        {
            captures = ProducerExposures.Length,
            ingressMedianMilliseconds = Median(ingressMilliseconds),
            previewMedianMilliseconds = Median(previewMilliseconds),
            totalMilliseconds = ingressMilliseconds.Sum() + previewMilliseconds.Sum()
        };
    }

    // A dim little-endian Mono16 sky with 200 stars that drift one pixel per frame.
    private static void FillRaw(byte[] pixels, int index)
    {
        for (var i = 0; i < FrameBytes; i++)
        {
            var value = (ushort)(600 + (i * 7 + index * 31) % 251);
            pixels[i * 2] = (byte)value;
            pixels[i * 2 + 1] = (byte)(value >> 8);
        }
        for (var star = 0; star < 200; star++)
        {
            var x = (star * 97 + index) % Width;
            var y = star * 53 % Height;
            var offset = (y * Width + x) * 2;
            pixels[offset] = 0x30;
            pixels[offset + 1] = 0x75;
        }
    }

    private static CaptureLoopSubmission Submission(DateTimeOffset startedUtc, byte[] pixels)
    {
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(60), 1, null, null);
        var frame = new CameraFrame(
            startedUtc,
            Width,
            Height,
            CameraPixelFormat.Mono16,
            pixels.ToArray(),
            new FrameMetadata(TimeSpan.FromSeconds(60), 1, 10, "issue-993"));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(60), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(60), startedUtc.AddSeconds(60.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(60));
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig Configuration() =>
        new(NightlyProductFixture.Observatory,
            new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new { })),
            Rig,
            new CapturePipelineConfig(
                [new CaptureProcessingStepConfig("Preview", NightlyProductFixture.NodeId,
                    Options: JsonSerializer.SerializeToElement(new { fixedTransfer = new FixedDisplayTransferOptions(), recipeVersion = "fixed-native-gamma-v1" }), DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "issue-993-performance");

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static long DirectoryBytes(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static object Process() => new
    {
        processorCount = Environment.ProcessorCount,
        lifetimePeakWorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64,
        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
    };

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for issue #993 evidence.");

    private sealed record RunEvidence(
        double Milliseconds,
        double CpuSeconds,
        long AllocatedBytes,
        int Gen2Collections,
        long BaselineWorkingSetBytes,
        long PeakWorkingSetBytes,
        long PeakWorkingSetAboveBaselineBytes,
        long PeakManagedHeapBytes,
        int WindowsEvaluated,
        int WindowsUnchanged,
        int ProductsPublished,
        int ProductsReused,
        int PendingWindows,
        int FinalsRecorded,
        int Queries,
        int Restores,
        int RestoredFrames,
        int LargestRestore,
        long LargestRestoreBytes);

    private sealed record NightEvidence(
        int Width,
        int Height,
        int SourceCount,
        DateTimeOffset FirstObservationUtc,
        DateTimeOffset LastObservationUtc,
        string OutputIdentitySha256,
        string PayloadSha256,
        long PayloadBytes,
        string RenditionSha256,
        long RenditionBytes,
        string ProvenanceSha256);

    private sealed record KindEvidence(
        IReadOnlyList<RunEvidence> RunsToConverge,
        RunEvidence UnchangedRerun,
        int ProductsPublished,
        int ProductsRecorded,
        IReadOnlyDictionary<string, int> WindowDispositions,
        IReadOnlyDictionary<string, int> Exclusions,
        int NightFrames,
        IReadOnlyList<Guid> NightFrameLineage,
        NightEvidence Night);

    /// <summary>Samples the process working set and managed heap until stopped.</summary>
    private sealed class WorkingSetSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly long _baseline;
        private readonly Task _loop;
        private long _peak;
        private long _peakHeap;

        private WorkingSetSampler()
        {
            _baseline = Sample();
            _peak = _baseline;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    _peak = Math.Max(_peak, Sample());
                    _peakHeap = Math.Max(_peakHeap, GC.GetTotalMemory(forceFullCollection: false));
                    try
                    {
                        await Task.Delay(10, _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        internal static WorkingSetSampler Start() => new();

        internal async Task<(long Baseline, long Peak, long PeakHeap)> StopAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _peak = Math.Max(_peak, Sample());
            return (_baseline, _peak, _peakHeap);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync();
                await _loop;
            }
            _stop.Dispose();
        }

        private static long Sample()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
    }

    /// <summary>Counts how often the generator queried and restored through the reader it decorates.</summary>
    private sealed class CountingSourceReader(INightlyProductSourceReader inner) : INightlyProductSourceReader
    {
        internal int Queries { get; private set; }

        internal int Restores { get; private set; }

        internal int RestoredFrames { get; private set; }

        internal int LargestRestore { get; set; }

        internal long LargestRestoreBytes { get; set; }

        public ValueTask<IReadOnlyList<NightlyProductCandidate>> ReadCandidatesAsync(
            string nodeId,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maximumCount,
            Action<string> unsupported,
            CancellationToken cancellationToken)
        {
            Queries++;
            return inner.ReadCandidatesAsync(nodeId, startUtc, endUtc, maximumCount, unsupported, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreAsync(
            IReadOnlyList<NightlyProductCandidate> candidates,
            CancellationToken cancellationToken)
        {
            Restores++;
            RestoredFrames += candidates.Count;
            LargestRestore = Math.Max(LargestRestore, candidates.Count);
            LargestRestoreBytes = Math.Max(LargestRestoreBytes, candidates.Sum(static candidate => candidate.PayloadBytes));
            return inner.RestoreAsync(candidates, cancellationToken);
        }
    }

    /// <summary>
    /// A whole observing day of W1 previews. Pixels come from <see cref="PatternCount"/> precomputed patterns copied
    /// into a fresh buffer on every restore, as a durable read would allocate, so only a restored part is resident.
    /// </summary>
    private sealed class SyntheticSourceReader : INightlyProductSourceReader
    {
        private readonly ProcessingCompatibilityIdentity _compatibility;
        private readonly FrameLayoutDescriptor _layout;
        private readonly int _frameBytes;
        private readonly byte[][] _patterns;
        private readonly NightlyProductCandidate[] _candidates;

        internal SyntheticSourceReader(int width, int height, int channels, string rigHash)
        {
            _frameBytes = checked(width * height * channels);
            _layout = new(width, height, width * channels, channels == 3 ? CameraPixelFormat.Rgb24 : CameraPixelFormat.Mono8,
                FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
                null, byte.MaxValue, _frameBytes);
            _compatibility = new(rigHash, "zenith-v1", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");
            _patterns = [.. Enumerable.Range(0, PatternCount).Select(Pattern)];
            _candidates = [.. Enumerable.Range(0, DayFrameCount).Select(Candidate)];
        }

        public ValueTask<IReadOnlyList<NightlyProductCandidate>> ReadCandidatesAsync(
            string nodeId,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int maximumCount,
            Action<string> unsupported,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<NightlyProductCandidate>>(
            [
                .. _candidates
                    .Where(candidate => candidate.ExposureStartedUtc >= startUtc && candidate.ExposureStartedUtc < endUtc)
                    .Take(maximumCount + 1)
            ]);

        public ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreAsync(
            IReadOnlyList<NightlyProductCandidate> candidates,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessingArtifact>>([.. candidates.Select(Artifact)]);

        private ProcessingArtifact Artifact(NightlyProductCandidate candidate)
        {
            var index = Array.FindIndex(_candidates, other => other.ArtifactId == candidate.ArtifactId);
            Assert.IsGreaterThanOrEqualTo(0, index);
            var pixels = new byte[_frameBytes];
            _patterns[index % PatternCount].CopyTo(pixels, 0);
            return new ProcessingArtifact(
                candidate.ArtifactId,
                FrameArtifactRole.Preview,
                candidate.Variant,
                candidate.RecipeIdentitySha256,
                candidate.MediaType,
                _layout,
                pixels,
                candidate.ExposureStartedUtc.AddSeconds(61),
                TimeSpan.FromSeconds(60),
                _compatibility,
                ObservationStartedUtc: candidate.ExposureStartedUtc);
        }

        private byte[] Pattern(int pattern)
        {
            var pixels = new byte[_frameBytes];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)((i * 7 + pattern * 31) % 251);
            }
            return pixels;
        }

        private NightlyProductCandidate Candidate(int index)
        {
            var artifactId = Guid.Parse($"99300000-0000-0000-0000-{index:D12}");
            var captureId = Guid.Parse($"99310000-0000-0000-0000-{index:D12}");
            return new NightlyProductCandidate(
                artifactId,
                captureId,
                ProcessingIdentity.CreateOutputIdentity(
                    FrameArtifactRole.Preview, NightlyProductFixture.PreviewVariant, NightlyProductFixture.PreviewRecipe,
                    [captureId]),
                NightlyProductFixture.PreviewVariant,
                NightlyProductFixture.PreviewRecipe,
                JournalNightlyProductSourceReader.PackedImageMediaType,
                NightlyProductFixture.DayStartUtc.AddMinutes(index),
                _compatibility.Rig,
                null) { UsesFixedDisplayTransfer = true, PayloadBytes = _frameBytes };
        }
    }
}
