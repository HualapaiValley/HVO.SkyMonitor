using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// Issue #528 slice 5 evidence for CameraAgent orbital-elements acquisition at the #527-measured active-group size.
/// </summary>
/// <remarks>
/// <para>
/// A deterministic synthetic GP payload of 16,754 records (six-digit catalog numbers, objects carrying two element
/// sets, future and week-old epochs) is generated in memory and served by an in-process handler, so no CelesTrak
/// bytes are used and nothing reaches the network. Every measured repeat runs on a fresh root through the real
/// <see cref="CelesTrakGpClient"/>, parser, normalizer, <see cref="SqliteOrbitalElementsStore"/> and
/// <see cref="OrbitalSnapshotReader"/>: fetch, parse, normalize, publish and activate, then cold current and exact
/// materialization, a run of warm current reads, an identical-body refresh (the unchanged-payload path), a changed
/// refresh and a retention pass that retires the oldest snapshot.
/// </para>
/// <para>
/// The scheduler half measures what the refresh costs the automation runner when it is not due: adapter validation
/// and initial-epoch resolution, and a runner sweep with the reserved definition saved against the same sweep with no
/// orbital definition. There is no prior orbital implementation, so these figures are a first measurement and carry no
/// regression baseline. Manual because a full active-group pipeline is too heavy for the Unit or Integration gate.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OrbitalElementsRefreshPerformanceTests : OrbitalStoreTestBase
{
    private const int WarmupRepeats = 1;
    private const int MeasuredRepeats = 10;
    private const int WarmReads = 1_000;
    private const int SchedulerWarmupCalls = 200;
    private const int SchedulerMeasuredCalls = 2_000;
    private const int SweepWarmup = 50;
    private const int SweepMeasured = 300;

    /// <summary>Lowered from the 12-snapshot / 48-hour defaults so the measured retention pass retires a snapshot.</summary>
    private const int RetentionPassMaximumRetained = 1;

    private static readonly DateTimeOffset Start = OrbitalElementsStoreTestData.Start;
    private static readonly TimeSpan Floor = SqliteOrbitalElementsStore.MinimumProviderAttemptInterval;
    private static readonly CelesTrakGpClientSettings ClientSettings =
        OrbitalElementsServiceCollectionExtensions.CreateClientSettings(new OrbitalElementsOptions());
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheActiveGroupRefreshPipelineIsMeasuredStageByStage()
    {
        var workload = OrbitalPerformanceWorkload.Instance;
        using var recorder = new StageRecorder();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;

        RepeatOutcome? outcome = null;
        TimeSpan cpuBefore = default;
        long allocatedBefore = 0;
        var collectionsBefore = new int[3];
        for (var index = 0; index < WarmupRepeats + MeasuredRepeats; index++)
        {
            if (index == WarmupRepeats)
            {
                cpuBefore = Environment.CpuUsage.TotalTime;
                allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                for (var generation = 0; generation < collectionsBefore.Length; generation++)
                {
                    collectionsBefore[generation] = GC.CollectionCount(generation);
                }
                recorder.Recording = true;
            }
            outcome = await RunRepeatAsync(workload, recorder, index).ConfigureAwait(false);
        }
        var cpu = Environment.CpuUsage.TotalTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var collections = collectionsBefore.Select(static (before, generation) => GC.CollectionCount(generation) - before)
            .ToArray();
        process.Refresh();
        var peakWorkingSet = process.PeakWorkingSet64;

        await MeasureGzipFetchAsync(workload, recorder).ConfigureAwait(false);
        var scheduler = await MeasureSchedulerAsync().ConfigureAwait(false);
        var stages = recorder.Summarize();

        Assert.IsNotNull(outcome);
        foreach (var stage in new[] { "initialize", "admit", "fetch", "parse", "candidate", "publish", "readCurrentCold",
            "readExactCold", "unchangedPublish", "changedPublish", "retention", "endToEndFirstActivation" })
        {
            Assert.IsGreaterThanOrEqualTo(MeasuredRepeats, stages[stage].Wall.Count, stage);
        }
        Assert.IsLessThanOrEqualTo(CelesTrakGpSource.MaximumRawBytes, workload.Raw.Length);
        Assert.IsLessThanOrEqualTo(CelesTrakGpSource.MaximumWireBytes, workload.GzipWire.Length);
        Assert.IsLessThanOrEqualTo(OrbitalContract.MaximumPayloadBytes, outcome.PayloadBytes);
        Assert.IsLessThan(ClientSettings.RequestTimeout.TotalMilliseconds, stages["fetch"].Wall.Maximum,
            "An in-process fetch of the full active group must stay far inside the request timeout.");

        await WriteEvidenceAsync(workload, outcome, stages, scheduler, new ProcessEvidence(
            MeasuredRepeats,
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds / MeasuredRepeats,
            allocated,
            allocated / MeasuredRepeats,
            collections[0],
            collections[1],
            collections[2],
            workingSetBefore,
            recorder.MaximumSampledWorkingSetBytes,
            peakWorkingSet)).ConfigureAwait(false);
    }

    /// <summary>
    /// The frame path: once a snapshot is materialized, current and exact reads are served from the reader's cache.
    /// The manifest and payload files are moved out of the store first, so any read that reached the disk would fail
    /// integrity instead of returning the cached instance.
    /// </summary>
    [TestMethod]
    public async Task AWarmReadOfTheActiveGroupOpensNoSnapshotFile()
    {
        var workload = OrbitalPerformanceWorkload.Instance;
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(Start);
        using var handler = new InMemoryProviderHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new CelesTrakGpClient(http, clock, ClientSettings);
        using var store = new SqliteOrbitalElementsStore(new OrbitalElementsStoreSettings(root, Floor, 12,
            TimeSpan.FromHours(48), 4), clock, NullLogger<SqliteOrbitalElementsStore>.Instance);
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var admission = await store.TryBeginAttemptAsync("frame-path", OrbitalAttemptTrigger.Scheduled, CancellationToken.None)
            .ConfigureAwait(false);
        handler.Body = workload.Raw;
        var fetch = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);
        var snapshot = CelesTrakGpNormalizer.CreateCandidate(fetch).Snapshot!;
        var publication = await store.PublishAndActivateAsync(admission.AttemptId!, snapshot, fetch.RawBytes,
            CancellationToken.None).ConfigureAwait(false);
        var reader = new OrbitalSnapshotReader(store, configured: true, NullLogger<OrbitalSnapshotReader>.Instance);
        var cold = await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(cold.Snapshot, cold.UnavailableReason?.ToString());
        Assert.AreEqual(OrbitalPerformanceWorkload.RecordCount, cold.Snapshot.Records.Count);
        Assert.AreEqual(1, reader.LoadCount);

        var hidden = Path.Combine(Path.GetDirectoryName(root)!, "hidden");
        Directory.CreateDirectory(hidden);
        var moved = new[] { store.ManifestPath(publication.ManifestSha256), store.PayloadPath(publication.PayloadSha256) }
            .Select(path => (Original: path, Hidden: Path.Combine(hidden, Path.GetFileName(Path.GetDirectoryName(path))!)))
            .ToArray();
        foreach (var (original, hiddenPath) in moved)
        {
            File.Move(original, hiddenPath);
        }
        try
        {
            for (var read = 0; read < WarmReads; read++)
            {
                var current = await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                var exact = await reader.ReadExactAsync(publication.ManifestSha256, CancellationToken.None).ConfigureAwait(false);
                Assert.AreSame(cold.Snapshot, current.Snapshot, current.UnavailableReason?.ToString());
                Assert.AreSame(cold.Snapshot, exact.Snapshot, exact.UnavailableReason?.ToString());
            }
            Assert.AreEqual(1, reader.LoadCount, "A warm read materializes nothing from disk.");
        }
        finally
        {
            foreach (var (original, hiddenPath) in moved)
            {
                File.Move(hiddenPath, original);
            }
        }
        Assert.AreEqual(1, handler.Requests, "Reads never reach the provider.");
        Assert.IsFalse(typeof(OrbitalSnapshotReader)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(static field => typeof(HttpClient).IsAssignableFrom(field.FieldType) ||
                typeof(IOrbitalElementsProviderClient).IsAssignableFrom(field.FieldType)),
            "The reader holds no provider client.");
    }

    private async Task<RepeatOutcome> RunRepeatAsync(OrbitalPerformanceWorkload workload, StageRecorder recorder, int index)
    {
        var root = CreateRoot();
        var clock = new OrbitalElementsStoreTestData.Clock(Start);
        using var handler = new InMemoryProviderHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new CelesTrakGpClient(http, clock, ClientSettings);
        var settings = new OrbitalElementsStoreSettings(root, Floor, RetentionPassMaximumRetained, TimeSpan.Zero, 4);
        RepeatOutcome outcome;
        using (var store = new SqliteOrbitalElementsStore(settings, clock, NullLogger<SqliteOrbitalElementsStore>.Instance))
        {
            await recorder.MeasureAsync("initialize", async () =>
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);

            // First activation: the end-to-end figure covers admission through the first cold current read.
            handler.Body = workload.Raw;
            var endToEnd = Stopwatch.GetTimestamp();
            var attemptId = await AdmitAsync(recorder, "admit", store, index, 1).ConfigureAwait(false);
            var fetch = await recorder.MeasureAsync("fetch",
                () => client.FetchAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(OrbitalFetchDisposition.Retrieved, fetch.Disposition, fetch.ReasonCode);
            var candidate = recorder.Measure("candidate", () => CelesTrakGpNormalizer.CreateCandidate(fetch));
            Assert.IsNotNull(candidate.Snapshot, candidate.RejectionReasonCode);
            var first = await recorder.MeasureAsync("publish", () => store.PublishAndActivateAsync(
                attemptId, candidate.Snapshot, fetch.RawBytes, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsFalse(first.PayloadUnchanged);
            Assert.AreEqual(OrbitalPerformanceWorkload.RecordCount, first.RecordCount);
            Assert.AreEqual(OrbitalPerformanceWorkload.ObjectCount, first.ObjectCount);
            var reader = new OrbitalSnapshotReader(store, configured: true, NullLogger<OrbitalSnapshotReader>.Instance);
            var current = await recorder.MeasureAsync("readCurrentCold",
                () => reader.ReadCurrentAsync(CancellationToken.None)).ConfigureAwait(false);
            recorder.RecordWall("endToEndFirstActivation", Stopwatch.GetElapsedTime(endToEnd));
            Assert.AreEqual(first.ManifestSha256, current.Snapshot?.Manifest.ManifestSha256,
                current.UnavailableReason?.ToString());

            var exactReader = new OrbitalSnapshotReader(store, configured: true, NullLogger<OrbitalSnapshotReader>.Instance);
            var exact = await recorder.MeasureAsync("readExactCold",
                () => exactReader.ReadExactAsync(first.ManifestSha256, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(first.ManifestSha256, exact.Snapshot?.Manifest.ManifestSha256, exact.UnavailableReason?.ToString());

            // The parser alone, on the same bytes, so the normalizer's share of "candidate" can be read off.
            var parsed = recorder.Measure("parse", () => CelesTrakGpParser.Parse(fetch.RawBytes, fetch.CompletedUtc));
            Assert.IsNull(parsed.ReasonCode, parsed.Detail);
            Assert.AreEqual(OrbitalPerformanceWorkload.RecordCount, parsed.ElementSets!.Count);

            for (var read = 0; read < WarmReads; read++)
            {
                var started = Stopwatch.GetTimestamp();
                var warm = await reader.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                recorder.RecordWall("readCurrentWarm", Stopwatch.GetElapsedTime(started), microseconds: true);
                Assert.AreSame(current.Snapshot, warm.Snapshot);
            }
            Assert.AreEqual(1, reader.LoadCount, "Warm current reads perform no materialization.");
            var afterFirst = Storage(root);
            var manifestBytes = FileBytes(store.ManifestPath(first.ManifestSha256));
            var rawBytes = FileBytes(store.RawPath(OrbitalElementsStoreTestData.Hash(workload.Raw)));

            // An identical body two hours later: a new manifest activates over the unchanged payload.
            clock.Advance(Floor);
            attemptId = await AdmitAsync(recorder, "unchangedAdmit", store, index, 2).ConfigureAwait(false);
            fetch = await recorder.MeasureAsync("unchangedFetch",
                () => client.FetchAsync(CancellationToken.None)).ConfigureAwait(false);
            candidate = recorder.Measure("unchangedCandidate", () => CelesTrakGpNormalizer.CreateCandidate(fetch));
            var unchanged = await recorder.MeasureAsync("unchangedPublish", () => store.PublishAndActivateAsync(
                attemptId, candidate.Snapshot!, fetch.RawBytes, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsTrue(unchanged.PayloadUnchanged);
            var afterUnchanged = await recorder.MeasureAsync("readCurrentAfterUnchanged",
                () => reader.ReadCurrentAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(unchanged.ManifestSha256, afterUnchanged.Snapshot?.Manifest.ManifestSha256);
            Assert.AreEqual(2, reader.LoadCount, "A new active manifest is materialized once.");

            // One changed record two hours later, then the retention pass the job runs after every activation.
            clock.Advance(Floor);
            handler.Body = workload.ChangedRaw;
            attemptId = await AdmitAsync(recorder, "changedAdmit", store, index, 3).ConfigureAwait(false);
            fetch = await client.FetchAsync(CancellationToken.None).ConfigureAwait(false);
            candidate = CelesTrakGpNormalizer.CreateCandidate(fetch);
            var changed = await recorder.MeasureAsync("changedPublish", () => store.PublishAndActivateAsync(
                attemptId, candidate.Snapshot!, fetch.RawBytes, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsFalse(changed.PayloadUnchanged);
            var retention = await recorder.MeasureAsync("retention",
                () => store.ApplyRetentionAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(1, retention.Retired, "The oldest snapshot is outside the lowered bound and unprotected.");
            Assert.AreEqual(3, handler.Requests);

            outcome = new RepeatOutcome(
                first.RecordCount,
                first.ObjectCount,
                current.Snapshot!.Manifest.NormalizedPayloadLengthBytes,
                manifestBytes,
                rawBytes,
                retention,
                afterFirst,
                Storage(root));
        }
        Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        return outcome;
    }

    private static async Task<string> AdmitAsync(StageRecorder recorder, string stage, SqliteOrbitalElementsStore store,
        int repeat, int attempt)
    {
        var admission = await recorder.MeasureAsync(stage, () => store.TryBeginAttemptAsync(
            string.Create(CultureInfo.InvariantCulture, $"perf-{repeat}-{attempt}"), OrbitalAttemptTrigger.Scheduled,
            CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admission.Status);
        return admission.AttemptId!;
    }

    private static async Task MeasureGzipFetchAsync(OrbitalPerformanceWorkload workload, StageRecorder recorder)
    {
        var clock = new OrbitalElementsStoreTestData.Clock(Start);
        using var handler = new InMemoryProviderHandler { Body = workload.GzipWire, ContentEncoding = "gzip" };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new CelesTrakGpClient(http, clock, ClientSettings);
        for (var index = 0; index < WarmupRepeats + MeasuredRepeats; index++)
        {
            recorder.Recording = index >= WarmupRepeats;
            var fetch = await recorder.MeasureAsync("fetchGzip",
                () => client.FetchAsync(CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(OrbitalFetchDisposition.Retrieved, fetch.Disposition, fetch.ReasonCode);
            Assert.AreEqual(workload.Raw.Length, fetch.RawBytes!.Length);
        }
    }

    private async Task<SchedulerEvidence> MeasureSchedulerAsync()
    {
        var clock = new SignalingTimeProvider(Start);
        var host = await OrbitalCompositionHost.StartAsync(CreateRoot(), clock).ConfigureAwait(false);
        await using var lifetime = host.ConfigureAwait(false);
        var adapter = host.Provider.GetRequiredService<OrbitalElementsAutomationAdapter>();
        var definition = new LocalAutomationDefinition(OrbitalElementsAutomationAdapter.DefinitionId,
            "Orbital elements refresh", true, LocalAutomationTaskKind.OrbitalElementsRefresh, CelesTrakGpSource.DatasetKey,
            LocalAutomationTriggerKind.Periodic, OrbitalElementsAutomationAdapter.MinimumIntervalSeconds, Start, null);

        var validate = MeasureCalls(() => Assert.IsNull(adapter.Validate(definition)));
        var resolve = MeasureCalls(() => adapter.ResolveInitialTriggerEpoch(clock.GetUtcNow()));

        var baseline = await MeasureSweepsAsync(host).ConfigureAwait(false);
        var saved = await host.SaveDefinitionAsync().ConfigureAwait(false);
        var withOrbital = await MeasureSweepsAsync(host).ConfigureAwait(false);

        Assert.AreEqual(0, host.Handler.Count, "A sweep before the definition is due makes no provider request.");
        Assert.IsEmpty(await host.RunsAsync().ConfigureAwait(false), "No orbital run was executed while measuring.");
        return new SchedulerEvidence(
            validate,
            resolve,
            baseline,
            withOrbital,
            withOrbital.Median - baseline.Median,
            saved.Definition.TriggerEpochUtc,
            host.Handler.Count);
    }

    private static Distribution MeasureCalls(Action call)
    {
        for (var index = 0; index < SchedulerWarmupCalls; index++)
        {
            call();
        }
        var samples = new List<double>(SchedulerMeasuredCalls);
        for (var index = 0; index < SchedulerMeasuredCalls; index++)
        {
            var started = Stopwatch.GetTimestamp();
            call();
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        }
        return Distribution.From("microseconds", samples);
    }

    private static async Task<Distribution> MeasureSweepsAsync(OrbitalCompositionHost host)
    {
        for (var index = 0; index < SweepWarmup; index++)
        {
            await host.SweepAsync().ConfigureAwait(false);
        }
        var samples = new List<double>(SweepMeasured);
        for (var index = 0; index < SweepMeasured; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await host.SweepAsync().ConfigureAwait(false);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return Distribution.From("milliseconds", samples);
    }

    private static StorageEvidence Storage(string root)
    {
        var entries = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            entries[Path.GetFileName(directory) + "/"] = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(static file => new FileInfo(file).Length);
        }
        foreach (var file in Directory.EnumerateFiles(root))
        {
            entries[Path.GetFileName(file)] = new FileInfo(file).Length;
        }
        return new StorageEvidence(entries, entries.Values.Sum());
    }

    private static long FileBytes(string path) => new FileInfo(path).Length;

    private static string FileSystemFormat()
    {
        try
        {
            return new DriveInfo(Path.GetTempPath()).DriveFormat;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private async Task WriteEvidenceAsync(OrbitalPerformanceWorkload workload, RepeatOutcome outcome,
        IReadOnlyDictionary<string, StageEvidence> stages, SchedulerEvidence scheduler, ProcessEvidence process)
    {
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE528_EVIDENCE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-528");
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, "orbital-refresh-performance.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = "issue-528-orbital-refresh-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Baseline = "none: there is no prior orbital acquisition implementation, so this is a first measurement",
            Workload = new
            {
                Description = "deterministic synthetic CelesTrak-GP-shaped JSON generated in memory; no provider bytes, no network",
                OrbitalPerformanceWorkload.RecordCount,
                OrbitalPerformanceWorkload.ObjectCount,
                SixDigitCatalogRecords = OrbitalPerformanceWorkload.SixDigitObjects,
                ObjectsWithTwoElementSets = OrbitalPerformanceWorkload.RepeatedObjects,
                OrbitalPerformanceWorkload.FutureRecords,
                RecordsOlderThanSevenDays = OrbitalPerformanceWorkload.StaleRecords,
                Issue527ReferenceRawBytes = OrbitalPerformanceWorkload.Issue527RawBytes,
                WarmupRepeats,
                MeasuredRepeats,
                WarmReadsPerRepeat = WarmReads,
                RetentionPassMaximumRetainedSnapshots = RetentionPassMaximumRetained,
                RetentionPassMinimumRetentionHours = 0
            },
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                System.Environment.ProcessorCount,
                Configuration = typeof(OrbitalElementsRefreshPerformanceTests).Assembly
                    .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
                GCSettings.IsServerGC,
                TemporaryFileSystem = FileSystemFormat()
            },
            Bounds = new
            {
                RawLimitBytes = CelesTrakGpSource.MaximumRawBytes,
                WireLimitBytes = CelesTrakGpSource.MaximumWireBytes,
                PayloadLimitBytes = OrbitalContract.MaximumPayloadBytes,
                RequestTimeoutSeconds = ClientSettings.RequestTimeout.TotalSeconds
            },
            Sizes = new
            {
                RawBytes = workload.Raw.Length,
                GzipWireBytes = workload.GzipWire.Length,
                outcome.PayloadBytes,
                outcome.ManifestBytes,
                outcome.RetainedRawBytes,
                outcome.RecordCount,
                outcome.ObjectCount,
                RawShareOfLimit = (double)workload.Raw.Length / CelesTrakGpSource.MaximumRawBytes,
                PayloadShareOfLimit = (double)outcome.PayloadBytes / OrbitalContract.MaximumPayloadBytes
            },
            Stages = stages,
            Process = process,
            Storage = new
            {
                AfterFirstActivation = outcome.AfterFirstActivation,
                AfterUnchangedChangedAndRetention = outcome.AfterSequence
            },
            Retention = outcome.Retention,
            Scheduler = scheduler
        }, EvidenceOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Evidence written to {path}");
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"raw {workload.Raw.Length} B, payload {outcome.PayloadBytes} B; fetch p50 {stages["fetch"].Wall.Median:F1} ms, " +
            $"candidate p50 {stages["candidate"].Wall.Median:F1} ms, publish p50 {stages["publish"].Wall.Median:F1} ms, " +
            $"cold read p50 {stages["readCurrentCold"].Wall.Median:F1} ms, end-to-end p50 " +
            $"{stages["endToEndFirstActivation"].Wall.Median:F1} ms; sweep p50 baseline {scheduler.SweepWithoutOrbitalDefinition.Median:F3} ms, " +
            $"with orbital {scheduler.SweepWithOrbitalDefinition.Median:F3} ms"));
    }

    private sealed record RepeatOutcome(
        int RecordCount,
        int ObjectCount,
        long PayloadBytes,
        long ManifestBytes,
        long RetainedRawBytes,
        OrbitalRetentionResult Retention,
        StorageEvidence AfterFirstActivation,
        StorageEvidence AfterSequence);

    private sealed record StorageEvidence(IReadOnlyDictionary<string, long> Entries, long TotalBytes);

    private sealed record ProcessEvidence(
        int MeasuredRepeats,
        double CpuMilliseconds,
        double CpuMillisecondsPerRepeat,
        long AllocatedBytes,
        long AllocatedBytesPerRepeat,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        long WorkingSetBeforeBytes,
        long MaximumSampledWorkingSetBytes,
        long PeakWorkingSetBytes);

    private sealed record SchedulerEvidence(
        Distribution AdapterValidate,
        Distribution AdapterResolveInitialTriggerEpoch,
        Distribution SweepWithoutOrbitalDefinition,
        Distribution SweepWithOrbitalDefinition,
        double MedianSweepOverheadMilliseconds,
        DateTimeOffset SavedTriggerEpochUtc,
        int ProviderRequests);

    private sealed record StageEvidence(Distribution Wall, Distribution? Cpu, Distribution? Allocated);

    private sealed record Distribution(string Unit, int Count, double Minimum, double Median, double P95, double Maximum,
        double Mean)
    {
        public static Distribution From(string unit, IReadOnlyCollection<double> values)
        {
            var ordered = values.Order().ToArray();
            if (ordered.Length == 0)
            {
                return new(unit, 0, 0, 0, 0, 0, 0);
            }
            var median = ordered.Length % 2 == 1
                ? ordered[ordered.Length / 2]
                : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
            var rank = (int)Math.Ceiling(0.95 * ordered.Length) - 1;
            return new(unit, ordered.Length, ordered[0], median, ordered[Math.Clamp(rank, 0, ordered.Length - 1)],
                ordered[^1], ordered.Average());
        }
    }

    /// <summary>
    /// Wall time, process CPU time and allocated bytes per stage. CPU and allocation are process-wide deltas, so run the
    /// filtered invocation alone; they are not recorded for the high-rate warm-read and end-to-end series.
    /// </summary>
    private sealed class StageRecorder : IDisposable
    {
        private readonly Dictionary<string, List<(double Wall, double Cpu, double Allocated)>> _stages =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Unit, List<double> Values)> _wallOnly = new(StringComparer.Ordinal);
        private readonly Process _process = Process.GetCurrentProcess();

        public bool Recording { get; set; }

        public long MaximumSampledWorkingSetBytes { get; private set; }

        public async ValueTask<T> MeasureAsync<T>(string stage, Func<ValueTask<T>> action)
        {
            var cpu = Environment.CpuUsage.TotalTime;
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = await action().ConfigureAwait(false);
            Record(stage, Stopwatch.GetElapsedTime(started), GC.GetTotalAllocatedBytes(precise: true) - allocated,
                Environment.CpuUsage.TotalTime - cpu);
            return result;
        }

        public T Measure<T>(string stage, Func<T> action)
        {
            var cpu = Environment.CpuUsage.TotalTime;
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var result = action();
            Record(stage, Stopwatch.GetElapsedTime(started), GC.GetTotalAllocatedBytes(precise: true) - allocated,
                Environment.CpuUsage.TotalTime - cpu);
            return result;
        }

        public void RecordWall(string stage, TimeSpan elapsed, bool microseconds = false)
        {
            if (!Recording)
            {
                return;
            }
            if (!_wallOnly.TryGetValue(stage, out var series))
            {
                series = (microseconds ? "microseconds" : "milliseconds", []);
                _wallOnly[stage] = series;
            }
            series.Values.Add(microseconds ? elapsed.TotalMicroseconds : elapsed.TotalMilliseconds);
        }

        public SortedDictionary<string, StageEvidence> Summarize()
        {
            var summary = new SortedDictionary<string, StageEvidence>(StringComparer.Ordinal);
            foreach (var (stage, samples) in _stages)
            {
                summary[stage] = new StageEvidence(
                    Distribution.From("milliseconds", samples.Select(static sample => sample.Wall).ToArray()),
                    Distribution.From("milliseconds", samples.Select(static sample => sample.Cpu).ToArray()),
                    Distribution.From("bytes", samples.Select(static sample => sample.Allocated).ToArray()));
            }
            foreach (var (stage, series) in _wallOnly)
            {
                summary[stage] = new StageEvidence(Distribution.From(series.Unit, series.Values), null, null);
            }
            return summary;
        }

        public void Dispose() => _process.Dispose();

        private void Record(string stage, TimeSpan wall, long allocated, TimeSpan cpu)
        {
            _process.Refresh();
            MaximumSampledWorkingSetBytes = Math.Max(MaximumSampledWorkingSetBytes, _process.WorkingSet64);
            if (!Recording)
            {
                return;
            }
            if (!_stages.TryGetValue(stage, out var samples))
            {
                samples = [];
                _stages[stage] = samples;
            }
            samples.Add((wall.TotalMilliseconds, cpu.TotalMilliseconds, allocated));
        }
    }

    /// <summary>Serves the configured body with HTTP 200 from memory; it has no network path at all.</summary>
    private sealed class InMemoryProviderHandler : HttpMessageHandler
    {
        private int _requests;

        public byte[] Body { get; set; } = [];

        public string? ContentEncoding { get; set; }

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(CelesTrakGpTestData.Response(HttpStatusCode.OK, new TrackingContent(Body),
                contentEncoding: ContentEncoding));
        }
    }

}
