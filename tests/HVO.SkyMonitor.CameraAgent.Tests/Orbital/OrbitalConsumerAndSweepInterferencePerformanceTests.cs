using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

/// <summary>
/// Refs #1425. Process-scoped consumer and same-sweep interference evidence over the unchanged #528 synthetic body.
/// Run this class alone in Release. All HTTP responses are in-process; no provider can be contacted.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class OrbitalConsumerAndSweepInterferencePerformanceTests : OrbitalStoreTestBase
{
    private const int Warmups = 1;
    private const int Repeats = 10;
    private const int UnavailableReads = 10;
    private const string OtherId = "z-environmental-probe";
    private const string SourceCommit = "939b46d6";
    private static readonly TimeSpan Floor = SqliteOrbitalElementsStore.MinimumProviderAttemptInterval;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly long ClockTicksPerSecond = ReadClockTicksPerSecond();
    private readonly Dictionary<string, List<Sample>> _samples = new(StringComparer.Ordinal);
    private readonly object _sampleSync = new();
    private readonly List<object> _facts = [];
    private readonly List<object> _boundaries = [];
    private readonly List<WeakReference<OrbitalSnapshotReader>> _disposedReaders = [];
    private readonly List<ThreadBoundary> _threadBoundaries = [];
    private readonly List<object> _settles = [];
    private readonly List<object> _changedJobConditions = [];
    private bool _attributingChangedJob;
    private bool _recording;
    private int _repeat;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ConsumerActivationUnavailableReadsAndDueSweepInterferenceAreMeasured()
    {
        var settleMode = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE");
        if (settleMode == "alternate")
        {
            Assert.IsTrue(Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE_FIRST") is "settled" or "unsettled",
                "Alternate settling requires SETTLE_FIRST=settled|unsettled.");
        }
        var workload = OrbitalPerformanceWorkload.Instance;
        RecordBoundary("before-warmup", -1);
        for (var repeat = -Warmups; repeat < Repeats; repeat++)
        {
            _recording = repeat >= 0;
            _repeat = repeat;
            if (Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE") == "1")
            {
                await SettleAsync(repeat, "before-repetition").ConfigureAwait(false);
            }
            await MeasureConsumerAsync(workload, repeat).ConfigureAwait(false);
            await MeasureUnavailableAsync(repeat).ConfigureAwait(false);
            foreach (var scenario in Enum.GetValues<SweepCase>())
            {
                await MeasureSweepAsync(workload, scenario, repeat).ConfigureAwait(false);
            }
            RecordBoundary("after-repeat", repeat);
            TestContext.WriteLine($"Completed repeat {repeat + 1}/{Repeats} (zero is warmup).");
        }
        // All per-root async methods have returned and all their stores/workers have been disposed.
        // This final diagnostic is outside every timed sample; the opt-in control also collects before repetitions.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        RecordBoundary("final-forced-full-collection", Repeats);
        var retainedDisposedReaders = _disposedReaders.Count(static reference => reference.TryGetTarget(out _));
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE1425_EVIDENCE_ROOT")
            ?? Path.Combine("TestResults", "issue-1425");
        Directory.CreateDirectory(root);
        var evidence = new
        {
            baselineReferenceCommit = SourceCommit,
            sourceCommit = Environment.GetEnvironmentVariable("HVO_ISSUE1425_SOURCE_COMMIT") ?? SourceCommit,
            productionAssemblyVersion = typeof(OrbitalSnapshotReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            recordedAtUtc = DateTimeOffset.UtcNow,
            warmups = Warmups,
            repeats = Repeats,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount,
            gcConfiguration = new
            {
                GCSettings.IsServerGC,
                latencyMode = GCSettings.LatencyMode.ToString(),
                concurrent = ReadGcSwitch("System.GC.Concurrent"),
                server = ReadGcSwitch("System.GC.Server"),
                linuxClockTicksPerSecond = ClockTicksPerSecond,
                effectiveRuntimeConfig = ReadEffectiveRuntimeConfig(),
                runtimeConfigs = ReadRuntimeConfigs()
            },
            settleEnabled = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE") == "1",
            settleMode = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE"),
            settleFirst = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE_FIRST"),
            changedJobConditions = _changedJobConditions,
            settles = _settles,
            threadBoundaries = _threadBoundaries,
            workload = new
            {
                records = OrbitalPerformanceWorkload.RecordCount,
                rawBytes = workload.Raw.Length,
                rawSha256 = Convert.ToHexString(SHA256.HashData(workload.Raw)),
                changedSha256 = Convert.ToHexString(SHA256.HashData(workload.ChangedRaw))
            },
            methodology = new[]
            {
                "One warmup plus ten fresh-root measured repetitions; p95 uses nearest rank (with ten samples, the maximum).",
                "Samples are process-wide: GC.GetTotalAllocatedBytes(true), Environment.CpuUsage.TotalTime and Process.PeakWorkingSet64 deltas.",
                "Peak deltas are process lifetime high-water increases, not stage-local resident memory; zero does not mean no memory use.",
                "Linux /proc/self/io read_bytes/write_bytes are physical I/O; syscr/syscw are syscall counts, not byte counts. Null means unavailable.",
                "Page cache is not evicted. Restart means new store/reader, not a new process or cold operating-system page cache.",
                "M1 publish overlap includes a controlled AfterRaw barrier; it proves blocking behavior and is not natural publish duration.",
                "M3 samples stop at entry to the second task registry, before its fake environmental dependency executes; full sweep has separate samples.",
                "GC counts and total pause are process-wide deltas. Promoted bytes and GC index describe the last completed collection, not sums.",
                "Boundary heap/generation/fragmentation/commit values describe the last completed GC; totalMemoryBytes is an unforced current estimate.",
                "The diagnostic follow-up also samples allocation-free integer and SHA-256 CPU controls at repetition boundaries; these are outside all timed workload windows.",
                "Boundary diagnostics and the final forced full collection are outside all timed windows; CPU frequency is sampled from readable Linux policies.",
                "Phase intervals: provider fetch; fetch return to settled event (retrieval/normalize/publish); settled to retention event; reader 7722/7723 preparation.",
                "Changed-job thread boundaries read Linux schedstat (ns), falling back to stat CPU ticks; counters are sequential, with per-read timestamps and measured instrumentation cost.",
                "GC-background includes GC-named threads; foreground GC executed on a mutator remains in mutator CPU. CPU classes overlap wall/pause and are not additive causal wall attribution.",
                "HVO_ORBITAL_PERF_SETTLE=1 forces compacting full collections and waits for less than 5 ms GC-thread CPU per 500 ms, bounded at 10 s, before each repetition outside timed windows; default off.",
                "HVO_ORBITAL_PERF_SETTLE=alternate alternates S/U immediately before M1.job.changed, after first activation/consumer; SETTLE_FIRST selects order, and warmup is never settled.",
                "Thread stat user/system ticks use AT_CLKTCK; process stat minflt/majflt and statm RSS plus last-GC heap counters are read at each changed-job boundary. Added-counter wall and full boundary cost are recorded.",
                "Virtual timers are advanced only after timer registration (and request entry for timeout); real wall cost excludes simulated passage of time."
            },
            measurements = _samples.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(
                static pair => pair.Key, static pair => new
                {
                    wallMs = Distribution.Of(pair.Value.Select(static sample => sample.WallMs)),
                    allocatedBytes = Distribution.Of(pair.Value.Select(static sample => (double)sample.AllocatedBytes)),
                    cpuMs = Distribution.Of(pair.Value.Select(static sample => sample.CpuMs)),
                    peakWorkingSetBytes = Distribution.Of(pair.Value.Select(static sample => (double)sample.PeakWorkingSetBytes)),
                    peakWorkingSetIncreaseBytes = Distribution.Of(pair.Value.Select(static sample => (double)sample.PeakWorkingSetIncreaseBytes)),
                    readBytes = Distribution.Optional(pair.Value.Select(static sample => sample.Io?.ReadBytes)),
                    writeBytes = Distribution.Optional(pair.Value.Select(static sample => sample.Io?.WriteBytes)),
                    syscr = Distribution.Optional(pair.Value.Select(static sample => sample.Io?.Syscr)),
                    syscw = Distribution.Optional(pair.Value.Select(static sample => sample.Io?.Syscw)),
                    samples = pair.Value
                }),
            boundaries = _boundaries,
            disposedReaders = _disposedReaders.Count,
            retainedDisposedReaders,
            facts = _facts
        };
        var path = Path.Combine(root, "orbital-consumer-and-sweep-performance.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
    }

    private async Task MeasureConsumerAsync(OrbitalPerformanceWorkload workload, int repeat)
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        var faults = new OrbitalElementsStoreTestData.Faults();
        string lastManifest;
        using (var host = await Harness.CreateAsync(root, clock, faults: faults, diagnostics: new PhaseObserver(this, "M1")).ConfigureAwait(false))
        {
            await MeasureActivationAsync(host, workload.Raw, "first").ConfigureAwait(false);
            var first = await ReadPairAsync(host.Reader, "M1.a.first").ConfigureAwait(false);
            clock.Advance(Floor);
            // The alternate control acts after the first activation/consumer, immediately before the changed job.
            // Legacy SETTLE=1 remains a before-whole-repetition control, including its warmup.
            var alternate = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE") == "alternate";
            var firstSettled = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE_FIRST") == "settled";
            var settled = alternate && repeat >= 0 && ((repeat % 2 == 0) == firstSettled);
            var settleWaitMs = settled ? await SettleAsync(repeat, "before-changed-job").ConfigureAwait(false) : 0;
            _changedJobConditions.Add(new
            {
                repeat,
                condition = repeat < 0 ? "warmup" : settled ? "S" : "U",
                settleWaitMs,
                legacyRepetitionSettle = Environment.GetEnvironmentVariable("HVO_ORBITAL_PERF_SETTLE") == "1"
            });
            await MeasureActivationAsync(host, workload.ChangedRaw, "changed").ConfigureAwait(false);
            var changed = await ReadPairAsync(host.Reader, "M1.b.changed").ConfigureAwait(false);
            Assert.AreNotEqual(first.Manifest.ManifestSha256, changed.Manifest.ManifestSha256);
            clock.Advance(Floor);
            await MeasureActivationAsync(host, workload.ChangedRaw, "unchanged").ConfigureAwait(false);
            var reacquired = await ReadPairAsync(host.Reader, "M1.c.unchanged-body").ConfigureAwait(false);
            Assert.AreNotEqual(changed.Manifest.ManifestSha256, reacquired.Manifest.ManifestSha256);
            Assert.AreEqual(changed.Manifest.NormalizedPayloadSha256, reacquired.Manifest.NormalizedPayloadSha256);

            // Hold publication while it owns the write gate, with an old snapshot still active.
            clock.Advance(Floor);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            faults.ArmedPoint = OrbitalStorageFaultPoint.AfterRaw;
            faults.OnPoint = () =>
            {
                entered.TrySetResult();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(30)), "Publish barrier was not released.");
            };
            host.Handler.EnqueueBody(workload.Raw);
            var publication = Task.Run(async () => await host.ExecuteAsync("overlap").ConfigureAwait(false));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var warm = await MeasureAsync("M1.e.publish-in-progress.warm", () => host.Reader.ReadCurrentAsync(default))
                    .ConfigureAwait(false);
                var coldReader = new OrbitalSnapshotReader(host.Store, true, NullLogger<OrbitalSnapshotReader>.Instance);
                var cold = await MeasureAsync("M1.e.publish-in-progress.cold", () => coldReader.ReadCurrentAsync(default))
                    .WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                Assert.AreSame(reacquired, warm.Snapshot);
                Assert.AreEqual(reacquired.Manifest.ManifestSha256, cold.Snapshot!.Manifest.ManifestSha256);
                Assert.IsFalse(publication.IsCompleted, "Both reads finish while publication still holds the write gate.");
            }
            finally
            {
                release.Set();
                await publication.ConfigureAwait(false);
            }
            var completed = await publication.ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, completed.Outcome, completed.Detail);
            lastManifest = host.Store.ActiveManifestSha256!;
            await ReadPairAsync(host.Reader, "M1.e.after-publish").ConfigureAwait(false);
            if (_recording)
            {
                _facts.Add(new
                {
                    repeat,
                    measurement = "M1",
                    first = first.Manifest.ManifestSha256,
                    changed = changed.Manifest.ManifestSha256,
                    reacquired = reacquired.Manifest.ManifestSha256,
                    unchangedPayload = reacquired.Manifest.NormalizedPayloadSha256,
                    requests = host.Handler.Count,
                    overlap = "Warm and cold old-identity reads finish before publisher releases write gate; publication never acquires load lease."
                });
            }
        }
        using var restarted = OrbitalElementsStoreTestData.Open(Path.Combine(root, OrbitalElementsServiceCollectionExtensions.StoreDirectoryName), clock);
        Assert.IsFalse(restarted.IsInitialized);
        var reader = new OrbitalSnapshotReader(restarted, true, NullLogger<OrbitalSnapshotReader>.Instance);
        await PrepareIfSupportedAsync(reader).ConfigureAwait(false);
        var recovered = await ReadPairAsync(reader, "M1.d.restart").ConfigureAwait(false);
        Assert.AreEqual(lastManifest, recovered.Manifest.ManifestSha256);
        Assert.IsTrue(restarted.IsInitialized);
    }

    [SuppressMessage("Performance", "CA1859:Use concrete types when possible",
        Justification = "Measures calls through the public consumer interface.")]
    private async Task<OrbitalSnapshot> ReadPairAsync(IOrbitalSnapshotReader reader, string name)
    {
        var cold = await MeasureAsync(name + ".first", () => reader.ReadCurrentAsync(default)).ConfigureAwait(false);
        Assert.IsNotNull(cold.Snapshot, cold.UnavailableReason?.ToString());
        Assert.AreEqual(OrbitalPerformanceWorkload.RecordCount, cold.Snapshot.Records.Count);
        var warm = await MeasureAsync(name + ".second", () => reader.ReadCurrentAsync(default)).ConfigureAwait(false);
        Assert.AreSame(cold.Snapshot, warm.Snapshot);
        return cold.Snapshot;
    }

    private async Task MeasureActivationAsync(Harness host, byte[] body, string key)
    {
        host.Handler.EnqueueBody(body);
        var execution = await MeasureAsync("M1.job." + key, () => new ValueTask<LocalAutomationExecution>(host.ExecuteAsync(key))).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, execution.Outcome, execution.Detail);
    }

    private async Task MeasureUnavailableAsync(int repeat)
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        using var owner = OrbitalElementsStoreTestData.Open(root, clock);
        await owner.InitializeAsync(default).ConfigureAwait(false);
        using var contender = OrbitalElementsStoreTestData.Open(root, clock);
        var reader = new OrbitalSnapshotReader(contender, true, NullLogger<OrbitalSnapshotReader>.Instance);
        var failures = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        void Count(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is OrbitalStorageException { ReasonCode: "orbital.storage-locked" })
            {
                failures.Add(args.Exception);
            }
        }
        var attemptsPerRead = new List<int>();
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            for (var index = 0; index < UnavailableReads; index++)
            {
                var before = failures.Count;
                var result = await MeasureAsync($"M2.unavailable.read-{index + 1:00}", () => reader.ReadCurrentAsync(default))
                    .ConfigureAwait(false);
                Assert.AreEqual(OrbitalSnapshotUnavailableReason.StorageUnavailable, result.UnavailableReason);
                attemptsPerRead.Add(failures.Count - before);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
        owner.Dispose();
        var recoveryReads = 0;
        var recoveryWait = TimeSpan.Zero;
        OrbitalSnapshotReadResult recovery;
        do
        {
            recoveryReads++;
            recovery = await MeasureAsync("M2.recovery.read", () => reader.ReadCurrentAsync(default)).ConfigureAwait(false);
            if (!contender.IsInitialized)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                recoveryWait += TimeSpan.FromSeconds(1);
            }
        } while (!contender.IsInitialized && recoveryReads < 121);
        Assert.IsTrue(contender.IsInitialized, "A repaired store must recover within the bounded observation window.");
        Assert.AreEqual(OrbitalSnapshotUnavailableReason.NoActiveSnapshot, recovery.UnavailableReason);
        if (_recording)
        {
            _facts.Add(new
            {
                repeat,
                measurement = "M2",
                reads = UnavailableReads,
                initializationAttempts = failures.Count,
                attemptsPerRead,
                unavailableReason = "StorageUnavailable",
                readsUntilRecovery = recoveryReads,
                recoveryVirtualWaitSeconds = recoveryWait.TotalSeconds,
                recoveryReason = "NoActiveSnapshot",
                fault = "Exclusive root lock held by another real store; distinct storage-locked exceptions count failed initialization attempts."
            });
        }
    }

    private async Task MeasureSweepAsync(OrbitalPerformanceWorkload workload, SweepCase scenario, int repeat)
    {
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        using var host = await Harness.CreateAsync(CreateRoot(), clock, enabled: scenario != SweepCase.Disabled, diagnostics: new PhaseObserver(this, "M3." + scenario)).ConfigureAwait(false);
        var saved = await host.SaveOrbitalAsync().ConfigureAwait(false);
        var orbitalDue = LocalAutomationSchedule.NextPeriodicDueUtc(saved.Definition.TriggerEpochUtc,
            saved.Definition.TriggerInterval, null);
        var sweepAt = scenario switch
        {
            SweepCase.NotDue => clock.GetUtcNow().AddSeconds(60),
            SweepCase.RunNow => orbitalDue.AddSeconds(-1),
            _ => orbitalDue
        };
        if (scenario is SweepCase.FullBody or SweepCase.PrecisionWait or SweepCase.FloorSkip)
        {
            // A prior transport failure establishes the real durable floor. For the shortfall cases,
            // align its start to the definition's actual first due time (epoch plus one interval).
            var shortfall = scenario switch
            {
                SweepCase.PrecisionWait => TimeSpan.FromSeconds(120),
                SweepCase.FloorSkip => TimeSpan.FromSeconds(121),
                _ => TimeSpan.Zero
            };
            var priorAt = scenario == SweepCase.FullBody ? clock.GetUtcNow() : sweepAt - Floor + shortfall;
            clock.Advance(priorAt - clock.GetUtcNow());
            var admitted = await host.Store.TryBeginAttemptAsync("prior-attempt", OrbitalAttemptTrigger.Scheduled, default).ConfigureAwait(false);
            Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admitted.Status);
            await host.Store.RecordTransportFailureAsync(admitted.AttemptId!, "orbital.synthetic-prior", "synthetic prior attempt", default)
                .ConfigureAwait(false);
        }
        if (scenario == SweepCase.Halted)
        {
            var admitted = await host.Store.TryBeginAttemptAsync("prior-halt", OrbitalAttemptTrigger.Scheduled, default).ConfigureAwait(false);
            await host.Store.RecordHaltAsync(admitted.AttemptId!, 429, "synthetic provider refusal", default).ConfigureAwait(false);
        }
        clock.Advance(sweepAt.AddSeconds(-60) - clock.GetUtcNow());
        await host.SaveOtherAsync().ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(60));
        if (scenario == SweepCase.RunNow)
        {
            var queued = await host.Automation.RunNowAsync(new(OrbitalElementsAutomationAdapter.DefinitionId,
                saved.Version, "run-now", "measurement", "synthetic run-now"), default).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status, queued.ReasonCode);
        }
        var entries = await host.Automation.GetRunnerViewAsync(default).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsAutomationAdapter.DefinitionId, entries[0].Definition.DefinitionId);
        Assert.AreEqual(OtherId, entries[1].Definition.DefinitionId);
        Assert.AreEqual(sweepAt, LocalAutomationSchedule.NextPeriodicDueUtc(entries[1].Definition.TriggerEpochUtc,
            entries[1].Definition.TriggerInterval, entries[1].LastOccurrenceUtc), "The following definition must be due in this sweep.");
        Assert.AreEqual(scenario is not (SweepCase.NotDue or SweepCase.RunNow),
            LocalAutomationSchedule.ResolvePeriodic(entries[0].Definition.TriggerEpochUtc, entries[0].Definition.TriggerInterval,
                entries[0].LastOccurrenceUtc, sweepAt).HasValue, "Verify the orbital due/not-due precondition before measuring.");
        var expectsRequest = scenario is SweepCase.FullBody or SweepCase.PrecisionWait or SweepCase.Timeout or SweepCase.RunNow;
        if (scenario == SweepCase.Timeout)
        {
            host.Handler.EnqueueHang();
        }
        else if (expectsRequest)
        {
            host.Handler.EnqueueBody(workload.Raw);
        }
        var priorTerminalKeys = (await host.Automation.GetStateAsync(default).ConfigureAwait(false)).Runs
            .Where(static run => run.Outcome is not (LocalAutomationRunOutcome.Queued or LocalAutomationRunOutcome.Running))
            .Select(static run => run.RunKey).ToHashSet(StringComparer.Ordinal);
        clock.DrainCreatedTimers();
        var start = Capture();
        host.Probe.BeforeExecution = () => Record("M3." + scenario + ".delay-to-other", start);
        var sweep = host.Runner.SweepAsync(default);
        var virtualWait = TimeSpan.Zero;
        if (scenario == SweepCase.PrecisionWait)
        {
            virtualWait = await clock.WaitForTimerAsync().ConfigureAwait(false);
            Assert.AreEqual(OrbitalElementsRefreshJob.MaximumPrecisionWait, virtualWait);
            clock.Advance(virtualWait);
        }
        else if (scenario == SweepCase.Timeout)
        {
            virtualWait = await clock.WaitForTimerAsync().ConfigureAwait(false);
            Assert.AreEqual(TimeSpan.FromSeconds(30), virtualWait);
            await host.Handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            clock.Advance(virtualWait);
        }
        await sweep.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        Record("M3." + scenario + ".full-sweep", start);
        await host.DrainAsync().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (scenario != SweepCase.NotDue)
        {
            Record("M3." + scenario + ".worker-completion", start);
        }
        Assert.AreEqual(expectsRequest ? 1 : 0, host.Handler.Count);
        Assert.AreEqual(1, host.Probe.ExecutedRunKeys.Count);
        var state = await host.Automation.GetStateAsync(default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded,
            state.Runs.Single(static run => run.DefinitionId == OtherId).Outcome);
        var orbital = state.Runs.Where(run => run.DefinitionId == OrbitalElementsAutomationAdapter.DefinitionId
            && run.Outcome != LocalAutomationRunOutcome.Missed && !priorTerminalKeys.Contains(run.RunKey)).ToArray();
        if (scenario == SweepCase.NotDue)
        {
            Assert.IsEmpty(orbital);
        }
        else
        {
            Assert.HasCount(1, orbital);
            var expected = scenario switch
            {
                SweepCase.Disabled => "orbital.disabled",
                SweepCase.Halted => "orbital.provider-halted",
                SweepCase.FloorSkip => "orbital.floor-not-elapsed",
                SweepCase.Timeout => "orbital.request-timeout",
                _ => "orbital.activated"
            };
            StringAssert.StartsWith(orbital[0].Detail, expected + ":", StringComparison.Ordinal);
            Assert.AreEqual(scenario == SweepCase.RunNow, LocalAutomationRunKeys.IsRunNow(orbital[0].RunKey));
        }
        if (_recording)
        {
            _facts.Add(new
            {
                repeat,
                measurement = "M3",
                scenario = scenario.ToString(),
                orbitalFirstDueUtc = orbitalDue,
                sweepAtUtc = sweepAt,
                otherDueUtc = LocalAutomationSchedule.NextPeriodicDueUtc(entries[1].Definition.TriggerEpochUtc,
                    entries[1].Definition.TriggerInterval, entries[1].LastOccurrenceUtc),
                virtualWaitSeconds = virtualWait.TotalSeconds,
                requests = host.Handler.Count,
                orbitalOutcome = orbital.FirstOrDefault()?.Outcome.ToString(),
                orbitalDetail = orbital.FirstOrDefault()?.Detail,
                journalRunNow = orbital.FirstOrDefault() is { } run && LocalAutomationRunKeys.IsRunNow(run.RunKey)
            });
        }
    }

    private async Task<T> MeasureAsync<T>(string name, Func<ValueTask<T>> action)
    {
        if (name == "M1.job.changed")
        {
            _attributingChangedJob = true;
            ObserveThreadBoundary("job-start");
        }
        var start = Capture();
        var result = await action().ConfigureAwait(false);
        Record(name, start);
        if (name == "M1.job.changed")
        {
            ObserveThreadBoundary("job-end");
            _attributingChangedJob = false;
        }
        return result;
    }

    private static object ReadGcSwitch(string name) => new
    {
        found = AppContext.TryGetSwitch(name, out var enabled),
        enabled,
        data = AppContext.GetData(name)?.ToString()
    };

    private static object[] ReadRuntimeConfigs() => Directory.GetFiles(AppContext.BaseDirectory, "*.runtimeconfig.json")
        .Select(static path => (object)new { name = Path.GetFileName(path), contents = File.ReadAllText(path) }).ToArray();

    private static object? ReadEffectiveRuntimeConfig()
    {
        if (!OperatingSystem.IsLinux()) { return null; }
        var arguments = File.ReadAllText("/proc/self/cmdline").Split('\0');
        var index = Array.IndexOf(arguments, "--runtimeconfig");
        return index >= 0 && index + 1 < arguments.Length
            ? new { name = Path.GetFileName(arguments[index + 1]), contents = File.ReadAllText(arguments[index + 1]) }
            : null;
    }

    private void ObserveThreadBoundary(string name)
    {
        if (!_attributingChangedJob) { return; }
        var started = Stopwatch.GetTimestamp();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var current = ReadThreadCounter("/proc/thread-self");
        var threads = ReadThreadCounters(current?.Tid);
        var additionalStarted = Stopwatch.GetTimestamp();
        var faults = ReadFaultCounters();
        var heap = ReadHeapCounters();
        var additionalCountersWallMs = Stopwatch.GetElapsedTime(additionalStarted).TotalMilliseconds;
        var end = ReadThreadCounter("/proc/thread-self");
        if (end is not null) { threads.Add(end); }
        var timestamp = Stopwatch.GetTimestamp();
        var gc = CaptureGc();
        _threadBoundaries.Add(new(name, _repeat, timestamp, DateTimeOffset.UtcNow, gc,
            Environment.CpuUsage.TotalTime.TotalMilliseconds, threads,
            Stopwatch.GetElapsedTime(started, timestamp).TotalMilliseconds,
            current is not null && end is not null && current.Tid == end.Tid ? (end.CpuNs - current.CpuNs) / 1_000_000d : null,
            GC.GetTotalAllocatedBytes(precise: true) - allocated, end?.Tid,
            current is not null && end is not null && current.Tid == end.Tid ? end.UserTicks - current.UserTicks : null,
            current is not null && end is not null && current.Tid == end.Tid ? end.SystemTicks - current.SystemTicks : null,
            faults, heap, additionalCountersWallMs));
    }

    private static List<ThreadCounter> ReadThreadCounters(int? excludingTid = null)
    {
        List<ThreadCounter> result = [];
        if (!OperatingSystem.IsLinux()) { return result; }
        foreach (var path in Directory.EnumerateDirectories("/proc/self/task"))
        {
            if (int.TryParse(Path.GetFileName(path), CultureInfo.InvariantCulture, out var tid) && tid != excludingTid
                && ReadThreadCounter(path) is { } counter)
            {
                result.Add(counter);
            }
        }
        return result;
    }

    private static ThreadCounter? ReadThreadCounter(string path)
    {
        if (!OperatingSystem.IsLinux()) { return null; }
        try
        {
            var stat = File.ReadAllText(Path.Combine(path, "stat"));
            var tid = int.Parse(stat.AsSpan(0, stat.IndexOf(' ', StringComparison.Ordinal)), CultureInfo.InvariantCulture);
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = File.ReadAllText(Path.Combine(path, "comm")).Trim();
            long cpuNs;
            var source = "schedstat";
            try
            {
                var schedstat = File.ReadAllText(Path.Combine(path, "schedstat"));
                cpuNs = long.Parse(schedstat.AsSpan(0, schedstat.IndexOf(' ', StringComparison.Ordinal)), CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                source = "stat";
                if (ClockTicksPerSecond <= 0) { return null; }
                cpuNs = (long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture))
                    * 1_000_000_000 / ClockTicksPerSecond;
            }
            return new(tid, name, ClassifyThread(name), cpuNs,
                long.Parse(fields[19], CultureInfo.InvariantCulture), Stopwatch.GetTimestamp(), source,
                long.Parse(fields[11], CultureInfo.InvariantCulture), long.Parse(fields[12], CultureInfo.InvariantCulture));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static long ReadClockTicksPerSecond()
    {
        if (!OperatingSystem.IsLinux()) { return 0; }
        try
        {
            // Linux AT_CLKTCK (17) supplies the stat utime/stime clock tick in the process auxiliary vector.
            var auxv = File.ReadAllBytes("/proc/self/auxv");
            for (var offset = 0; offset + 2 * IntPtr.Size <= auxv.Length; offset += 2 * IntPtr.Size)
            {
                var kind = IntPtr.Size == 8 ? BitConverter.ToUInt64(auxv, offset) : BitConverter.ToUInt32(auxv, offset);
                if (kind == 17)
                {
                    return (long)(IntPtr.Size == 8 ? BitConverter.ToUInt64(auxv, offset + IntPtr.Size)
                        : BitConverter.ToUInt32(auxv, offset + IntPtr.Size));
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }

    private static string ClassifyThread(string name)
    {
        if (name.Contains("GC", StringComparison.OrdinalIgnoreCase)) { return "gc-background"; }
        if (name.Contains("Finalizer", StringComparison.OrdinalIgnoreCase)) { return "finalizer"; }
        if (name.Contains("Tiered", StringComparison.OrdinalIgnoreCase) || name.Contains("JIT", StringComparison.OrdinalIgnoreCase)) { return "jit"; }
        return "mutator";
    }

    private async Task<double> SettleAsync(int repeat, string stage)
    {
        var started = Stopwatch.GetTimestamp();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var collectionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var waitStart = Stopwatch.GetTimestamp();
        List<object> windows = [];
        var quiet = false;
        do
        {
            var before = ReadThreadCounters().Where(static thread => thread.Class == "gc-background").ToArray();
            var window = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            var after = ReadThreadCounters().Where(static thread => thread.Class == "gc-background").ToArray();
            var cpuMs = after.Sum(thread => Math.Max(0, thread.CpuNs - (before.FirstOrDefault(previous =>
                previous.Tid == thread.Tid && previous.StartTimeTicks == thread.StartTimeTicks)?.CpuNs ?? 0))) / 1_000_000d;
            quiet = cpuMs < 5;
            windows.Add(new { wallMs = Stopwatch.GetElapsedTime(window).TotalMilliseconds, cpuMs, quiet, before, after });
        } while (!quiet && Stopwatch.GetElapsedTime(waitStart) < TimeSpan.FromSeconds(10));
        var waitMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        _settles.Add(new { repeat, stage, collectionMs, waitMs, quiet, windows });
        Assert.IsTrue(quiet, "GC background did not settle within the bounded control window.");
        return waitMs;
    }

    private static FaultCounters? ReadFaultCounters()
    {
        if (!OperatingSystem.IsLinux()) { return null; }
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            // Field 3 starts at index 0 after the final comm parenthesis; fields 10/12 are minflt/majflt.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new(long.Parse(fields[7], CultureInfo.InvariantCulture), long.Parse(fields[9], CultureInfo.InvariantCulture),
                Stopwatch.GetTimestamp());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static HeapCounters ReadHeapCounters()
    {
        var gc = GC.GetGCMemoryInfo(GCKind.Any);
        long? rssBytes = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var fields = File.ReadAllText("/proc/self/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                rssBytes = long.Parse(fields[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new(gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes, gc.PromotedBytes,
            GC.GetTotalAllocatedBytes(precise: false), rssBytes, gc.Index, Stopwatch.GetTimestamp());
    }

    private sealed record FaultCounters(long MinorFaults, long MajorFaults, long Timestamp);
    private sealed record HeapCounters(long HeapSizeBytes, long CommittedBytes, long FragmentedBytes, long PromotedBytes,
        long TotalAllocatedBytes, long? RssBytes, long GcIndex, long Timestamp);
    private sealed record ThreadCounter(int Tid, string Name, string Class, long CpuNs, long StartTimeTicks, long Timestamp,
        string Source, long UserTicks, long SystemTicks);
    private sealed record ThreadBoundary(string Name, int Repeat, long Timestamp, DateTimeOffset Utc, GcStamp Gc,
        double ProcessCpuMs, List<ThreadCounter> Threads, double InstrumentationWallMs, double? InstrumentationCpuMs,
        long InstrumentationAllocatedBytes, int? InstrumentationTid, long? InstrumentationUserTicks, long? InstrumentationSystemTicks,
        FaultCounters? Faults, HeapCounters Heap, double AdditionalCountersWallMs);

    private static Stamp Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = CaptureGc();
        return new(process.PeakWorkingSet64, ReadIo(), Environment.CpuUsage.TotalTime,
            GC.GetTotalAllocatedBytes(precise: true), Stopwatch.GetTimestamp(), gc);
    }

    private void Record(string name, Stamp start)
    {
        var wall = Stopwatch.GetElapsedTime(start.Timestamp).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - start.Allocated;
        var cpu = (Environment.CpuUsage.TotalTime - start.Cpu).TotalMilliseconds;
        var gc = CaptureGc();
        var io = ReadIo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        if (!_recording)
        {
            return;
        }
        lock (_sampleSync)
        {
            if (!_samples.TryGetValue(name, out var samples))
            {
                samples = [];
                _samples.Add(name, samples);
            }
            samples.Add(new(wall, allocated, cpu, process.PeakWorkingSet64, Math.Max(0, process.PeakWorkingSet64 - start.Peak),
                io is not null && start.Io is not null ? io - start.Io : null, _repeat,
                gc.Gen0 - start.Gc.Gen0, gc.Gen1 - start.Gc.Gen1, gc.Gen2 - start.Gc.Gen2,
                gc.PauseMs - start.Gc.PauseMs, start.Gc.Index, gc.Index, gc.PromotedBytes));
        }
    }

    private static Io? ReadIo()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        try
        {
            var counters = File.ReadLines("/proc/self/io").Select(static line => line.Split(':', 2))
                .ToDictionary(static parts => parts[0], static parts => long.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
            return new(counters["read_bytes"], counters["write_bytes"], counters["syscr"], counters["syscw"]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private sealed record Io(long ReadBytes, long WriteBytes, long Syscr, long Syscw)
    {
        public static Io operator -(Io end, Io start) => new(end.ReadBytes - start.ReadBytes, end.WriteBytes - start.WriteBytes,
            end.Syscr - start.Syscr, end.Syscw - start.Syscw);
    }

    private static GcStamp CaptureGc()
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        return new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds, info.Index, info.PromotedBytes);
    }

    private void RecordBoundary(string name, int repeat)
    {
        var cpuProbe = name == "after-repeat" ? MeasureCpuProbe() : null;
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var generations = info.GenerationInfo.ToArray().Select(static (generation, index) => new
        {
            index,
            name = index switch { 3 => "LOH", 4 => "POH", _ => "Gen" + index },
            generation.SizeBeforeBytes,
            generation.SizeAfterBytes,
            generation.FragmentationBeforeBytes,
            generation.FragmentationAfterBytes
        }).ToArray();
        _boundaries.Add(new
        {
            name,
            repeat,
            utc = DateTimeOffset.UtcNow,
            info.Index,
            info.HeapSizeBytes,
            info.FragmentedBytes,
            info.TotalCommittedBytes,
            info.PromotedBytes,
            totalMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
            gc = CaptureGc(),
            generations,
            cpuFrequencyKhz = ReadCpuFrequency(),
            cpuProbe
        });
    }

    private static object MeasureCpuProbe()
    {
        var raw = OrbitalPerformanceWorkload.Instance.Raw;
        var processor = Thread.GetCurrentProcessorId();
        var gc = CaptureGc();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var cpu = Environment.CpuUsage.TotalTime;
        var start = Stopwatch.GetTimestamp();
        var result = ComputeIntegerProbe((uint)raw.Length);
        var integerMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var integerCpuMs = (Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds;
        Span<byte> hash = stackalloc byte[32];
        cpu = Environment.CpuUsage.TotalTime;
        start = Stopwatch.GetTimestamp();
        for (var index = 0; index < 16; index++)
        {
            SHA256.HashData(raw, hash);
        }
        var sha256Ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var sha256CpuMs = (Environment.CpuUsage.TotalTime - cpu).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        return new
        {
            integerIterations = 64_000_000,
            integerResult = result,
            integerMs,
            integerCpuMs,
            allocatedBytes,
            sha256Bytes = 16L * raw.Length,
            sha256 = Convert.ToHexString(hash),
            sha256Ms,
            sha256CpuMs,
            processorBefore = processor,
            processorAfter = Thread.GetCurrentProcessorId(),
            gcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - gc.PauseMs,
            gen0Collections = GC.CollectionCount(0) - gc.Gen0,
            gen1Collections = GC.CollectionCount(1) - gc.Gen1,
            gen2Collections = GC.CollectionCount(2) - gc.Gen2
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint ComputeIntegerProbe(uint state)
    {
        for (uint index = 0; index < 64_000_000; index++)
        {
            state = BitOperations.RotateLeft(unchecked(state + index), 13) ^ 0x9E3779B9;
        }
        return state;
    }

    private static object? ReadCpuFrequency()
    {
        if (!OperatingSystem.IsLinux()) { return null; }
        try
        {
            var values = Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*")
                .Select(static directory => Path.Combine(directory, "cpufreq", "scaling_cur_freq"))
                .Where(File.Exists).Select(static path => long.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture))
                .ToArray();
            return values.Length == 0 ? null : new { count = values.Length, min = values.Min(), max = values.Max(), mean = values.Average() };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private readonly record struct GcStamp(int Gen0, int Gen1, int Gen2, double PauseMs, long Index, long PromotedBytes);
    private readonly record struct Stamp(long Peak, Io? Io, TimeSpan Cpu, long Allocated, long Timestamp, GcStamp Gc);
    private sealed record Sample(double WallMs, long AllocatedBytes, double CpuMs, long PeakWorkingSetBytes, long PeakWorkingSetIncreaseBytes, Io? Io,
        int Repeat, int Gen0Collections, int Gen1Collections, int Gen2Collections, double GcPauseMs,
        long GcIndexBefore, long GcIndexAfter, long LastGcPromotedBytes);
    private sealed record Distribution(int Count, double P50, double P95, double Max)
    {
        public static Distribution Of(IEnumerable<double> values)
        {
            var ordered = values.Order().ToArray();
            return new(ordered.Length, ordered[(ordered.Length - 1) / 2], ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
        }

        public static Distribution? Optional(IEnumerable<long?> values)
        {
            var present = values.Where(static value => value.HasValue).Select(static value => (double)value!.Value).ToArray();
            return present.Length == 0 ? null : Of(present);
        }
    }

    // Reflection keeps the exact same harness buildable against the unchanged production baseline.
    private static object ConstructWithOptionalReader(Type type, object?[] arguments, object? extra)
        => Activator.CreateInstance(type, type.GetConstructors().Single().GetParameters().Length == arguments.Length
            ? arguments : [.. arguments, extra])!;

    private static async Task PrepareIfSupportedAsync(OrbitalSnapshotReader reader)
    {
        if (reader.GetType().GetMethod("PrepareCurrentAsync") is { } method)
        {
            await ((ValueTask<bool>)method.Invoke(reader, new object[] { CancellationToken.None })!).ConfigureAwait(false);
        }
    }

    private sealed class PhaseObserver(OrbitalConsumerAndSweepInterferencePerformanceTests owner, string series)
    {
        private Stamp _preparation;
        private Stamp _normalization;
        private Stamp _retention;

        public void Fetched(Stamp start)
        {
            owner.ObserveThreadBoundary("fetch-return");
            owner.Record(series + ".phase.fetch", start);
            _normalization = Capture();
        }

        public void Log(int eventId)
        {
            if (eventId is 7741 or 7749 or 7722 or 7723)
            {
                owner.ObserveThreadBoundary("event-" + eventId.ToString(CultureInfo.InvariantCulture));
            }
            switch (eventId)
            {
                case 7741:
                    owner.Record(series + ".phase.normalize-publish", _normalization);
                    _retention = Capture();
                    break;
                case 7749:
                    owner.Record(series + ".phase.retention", _retention);
                    break;
                case 7722: _preparation = Capture(); break;
                case 7723:
                    owner.Record(series == "M1" ? "M1.job.handoff" : series + ".phase.preparation", _preparation);
                    break;
            }
        }

        public void Disposed(OrbitalSnapshotReader reader) => owner._disposedReaders.Add(new(reader));
    }

    private sealed class PhaseLogger<T>(PhaseObserver? observer) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => observer is not null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => observer?.Log(eventId.Id);
    }

    private sealed class MeasuredClient(IOrbitalElementsProviderClient inner, PhaseObserver? observer) : IOrbitalElementsProviderClient
    {
        public async ValueTask<OrbitalFetchResult> FetchAsync(CancellationToken cancellationToken)
        {
            var start = Capture();
            var result = await inner.FetchAsync(cancellationToken).ConfigureAwait(false);
            observer?.Fetched(start);
            return result;
        }
    }

    private enum SweepCase { NotDue, FullBody, PrecisionWait, Timeout, Halted, Disabled, FloorSkip, RunNow }

    /// <summary>Real acquisition/automation components; only HTTP, time and the following environmental command are synthetic.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly PhaseObserver? _diagnostics;
        private readonly HttpClient _http;
        private readonly BackgroundService? _worker;
        private readonly OrbitalElementsTelemetry _telemetry;
        private readonly OrbitalElementsAutomationAdapter _adapter;
        private readonly SignalingTimeProvider _clock;
        private readonly EnvironmentalAcquisitionCoordinator _environmental;
        private readonly ServiceProvider _services;

        private Harness(string root, SignalingTimeProvider clock, bool enabled, OrbitalElementsStoreTestData.Faults? faults, PhaseObserver? diagnostics)
        {
            _clock = clock;
            _diagnostics = diagnostics;
            Handler = new ScriptedCelesTrakHandler();
            _http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new CelesTrakGpClient(_http, clock,
                OrbitalElementsServiceCollectionExtensions.CreateClientSettings(new OrbitalElementsOptions()));
            var options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                OrbitalElements = new OrbitalElementsOptions { Enabled = enabled },
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    Sources = [new EnvironmentalSourceConfiguration
                    {
                        Id = "temperature", Type = "VirtualEnvironment", Kind = EnvironmentalObservationKind.AirTemperature,
                        Triggers = [EnvironmentalAcquisitionTrigger.OnDemand],
                        ScheduleEpochUtc = OrbitalElementsStoreTestData.Start,
                        PeriodSeconds = 30, EveryNthCapture = 3, ValidForSeconds = 120, StaleAfterSeconds = 45,
                        Options = CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(
                            209, OrbitalElementsStoreTestData.Start, 12.5, null, 0.1, 0.2))
                    }]
                }
            });
            Store = OrbitalElementsStoreTestData.Open(
                OrbitalElementsServiceCollectionExtensions.CreateStoreSettings(options.Value).RootDirectory,
                clock, OrbitalElementsServiceCollectionExtensions.CreateStoreSettings(options.Value), faults);
            _telemetry = new OrbitalElementsTelemetry(clock);
            Reader = new OrbitalSnapshotReader(Store, true, new PhaseLogger<OrbitalSnapshotReader>(diagnostics));
            var jobArgs = new object?[] { Store, new MeasuredClient(client, diagnostics), options, clock, _telemetry, new PhaseLogger<OrbitalElementsRefreshJob>(diagnostics) };
            var job = (OrbitalElementsRefreshJob)ConstructWithOptionalReader(typeof(OrbitalElementsRefreshJob), jobArgs, Reader);
            _adapter = new(new OrbitalElementsAvailability(Store, NullLogger<OrbitalElementsAvailability>.Instance), job);
            Probe = new ExecutionProbe();
            _services = new ServiceCollection().BuildServiceProvider();
            var sourceFactory = new EnvironmentalSourceFactory(_services,
                [new EnvironmentalSourceRegistration("VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
            _environmental = new(sourceFactory, new ObservationPublisher(), new LocationStore(), options, clock);
            var environmentalRegistry = new EnvironmentalLocalAutomationTaskRegistry(_environmental,
                new EnvironmentalOnDemandAcquisitionService(_environmental, new CommandStore(), options, clock), options);
            var registry = new ProbeRegistry(new LocalAutomationTaskRegistry(environmentalRegistry, [], clock, [_adapter]), Probe);
            var captures = new StubCaptureSequenceSource();
            Automation = new(registry, captures, options, clock, NullLogger<SqliteLocalAutomationStore>.Instance);
            var workerType = typeof(LocalAutomationRunnerService).Assembly.GetType(
                "HVO.SkyMonitor.CameraAgent.Common.Automation.LocalAutomationOffSweepWorker");
            _worker = workerType is null ? null : (BackgroundService)Activator.CreateInstance(workerType)!;
            var runnerArgs = new object?[] { Automation, registry, captures, options, clock,
                NullLogger<LocalAutomationRunnerService>.Instance, null, null };
            Runner = (LocalAutomationRunnerService)ConstructWithOptionalReader(typeof(LocalAutomationRunnerService), runnerArgs, _worker);
        }

        public SqliteOrbitalElementsStore Store { get; }
        public SqliteLocalAutomationStore Automation { get; }
        public LocalAutomationRunnerService Runner { get; }
        public OrbitalSnapshotReader Reader { get; }
        public ScriptedCelesTrakHandler Handler { get; }
        public ExecutionProbe Probe { get; }

        public static async Task<Harness> CreateAsync(string root, SignalingTimeProvider clock, bool enabled = true,
            OrbitalElementsStoreTestData.Faults? faults = null, PhaseObserver? diagnostics = null)
        {
            var host = new Harness(root, clock, enabled, faults, diagnostics);
            await host.Store.InitializeAsync(default).ConfigureAwait(false);
            await host.Automation.InitializeAsync(default).ConfigureAwait(false);
            return host;
        }

        public Task DrainAsync() => _worker is null ? Task.CompletedTask
            : (Task)_worker.GetType().GetMethod("WhenIdleAsync")!.Invoke(_worker, null)!;

        public async Task<LocalAutomationExecution> ExecuteAsync(string key)
        {
            var definition = Definition();
            var entry = new LocalAutomationRunnerEntry(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition), null, null);
            return await _adapter.ExecuteAsync(LocalAutomationOccurrence.Create(entry, key, _clock.GetUtcNow()), default).ConfigureAwait(false);
        }

        public async Task<LocalAutomationDefinitionState> SaveOrbitalAsync()
        {
            var saved = await Automation.SaveAsync(new(OrbitalElementsAutomationAdapter.DefinitionId, "Orbital refresh", true,
                LocalAutomationTaskKind.OrbitalElementsRefresh, CelesTrakGpSource.DatasetKey, LocalAutomationTriggerKind.Periodic,
                7_200, 0, "save-orbital", "measurement", "synthetic workload"), default).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status, saved.ReasonCode);
            return saved.State.Definitions.Single();
        }

        public async Task SaveOtherAsync()
        {
            var saved = await Automation.SaveAsync(new(OtherId, "Environmental probe", true,
                LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, "temperature", LocalAutomationTriggerKind.Periodic,
                60, 0, "save-other", "measurement", "synthetic following command"), default).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status, saved.ReasonCode);
        }

        private LocalAutomationDefinition Definition() => new(OrbitalElementsAutomationAdapter.DefinitionId, "Orbital refresh", true,
            LocalAutomationTaskKind.OrbitalElementsRefresh, CelesTrakGpSource.DatasetKey, LocalAutomationTriggerKind.Periodic,
            7_200, _clock.GetUtcNow(), null);

        public void Dispose()
        {
            _worker?.StopAsync(default).GetAwaiter().GetResult();
            _worker?.Dispose();
            Runner.Dispose();
            _environmental.Dispose();
            _services.Dispose();
            Automation.Dispose();
            _telemetry.Dispose();
            Store.Dispose();
            _http.Dispose();
            _diagnostics?.Disposed(Reader);
        }
    }

    private sealed class ExecutionProbe
    {
        public Action? BeforeExecution { get; set; }
        public List<string> ExecutedRunKeys { get; } = [];
    }

    /// <summary>Observes entry to the following task, forwarding all routing and validation to the real registry.</summary>
    private sealed class ProbeRegistry(ILocalAutomationTaskRegistry inner, ExecutionProbe probe) : ILocalAutomationTaskRegistry
    {
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() => inner.Describe();
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => inner.Validate(definition);
        public DateTimeOffset ResolveInitialTriggerEpoch(LocalAutomationDefinition proposed, DateTimeOffset nowUtc)
            => inner.ResolveInitialTriggerEpoch(proposed, nowUtc);
        public bool SupportsRunNow(LocalAutomationDefinition definition) => inner.SupportsRunNow(definition);
        public bool RunsOffSweep(LocalAutomationDefinition definition)
            => (bool?)inner.GetType().GetMethod("RunsOffSweep")?.Invoke(inner, [definition]) ?? false;
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => inner.ExecuteAsync(definition, runKey, cancellationToken);
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            if (occurrence.Definition.DefinitionId == OtherId)
            {
                probe.BeforeExecution?.Invoke();
                probe.ExecutedRunKeys.Add(occurrence.RunKey);
            }
            return inner.ExecuteAsync(occurrence, cancellationToken);
        }
    }

    private sealed class ObservationPublisher : IEnvironmentalObservationPublisher
    {
        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new EnvironmentalObservationPublishResult(EnvironmentalObservationPublishDisposition.Enqueued, null));
    }

    private sealed class LocationStore : IDeploymentLocationStore
    {
        public DeploymentLocationSnapshot? Active { get; } = DeploymentLocationSnapshot.Create("location", 1, "measurement", null,
            OrbitalElementsStoreTestData.Start.AddDays(-1), null, 35.5599378, -113.9119818, 520, "America/Phoenix");
        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(DeploymentLocationSeed seed, CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);
        public DeploymentLocationSnapshot Resolve(CaptureLocationProvenance provenance, DateTimeOffset? effectiveUtc = null) => Active!;
    }

    private sealed class CommandStore : IEnvironmentalOnDemandCommandStore
    {
        public ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(string root, string idempotencyKey,
            string payloadSha256, string sourceId, string actorId, string? reason, DateTimeOffset observedAtUtc,
            TimeSpan leaseDuration, CancellationToken cancellationToken)
            => ValueTask.FromResult(new EnvironmentalOnDemandCommandClaim(EnvironmentalOnDemandClaimDisposition.Claimed,
                "synthetic-lease", observedAtUtc, null));
        public ValueTask CompleteOnDemandAsync(string root, string idempotencyKey, string leaseToken,
            EnvironmentalAcquisitionReceipt receipt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ReleaseOnDemandAsync(string root, string idempotencyKey, string leaseToken,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
