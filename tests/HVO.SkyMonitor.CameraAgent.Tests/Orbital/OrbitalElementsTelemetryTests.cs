using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;

namespace HVO.SkyMonitor.CameraAgent.Tests.Orbital;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalElementsTelemetryTests : OrbitalStoreTestBase
{
    private const string Prefix = "skymonitor.cameraagent.orbital_elements.";
    private static readonly string[] FetchDispositions = ["disposition=Retrieved", "disposition=HttpStatusHalt"];

    [TestMethod]
    public void AttemptsAndFetchesAreRecordedWithBoundedEnumLabelsOnly()
    {
        using var meter = new Meter(OrbitalElementsTelemetry.MeterName);
        using var telemetry = new OrbitalElementsTelemetry(TimeProvider.System, meter);
        using var recorder = new MeasurementRecorder(meter);

        telemetry.RecordAttempt(OrbitalAttemptOutcome.Activated);
        telemetry.RecordAttempt(OrbitalAttemptOutcome.ProviderHalted);
        telemetry.RecordFetch(OrbitalFetchDisposition.Retrieved, TimeSpan.FromMilliseconds(250), 4_096);
        telemetry.RecordFetch(OrbitalFetchDisposition.TimedOut, TimeSpan.FromMilliseconds(-5), null);

        var measurements = recorder.Snapshot();
        CollectionAssert.AreEqual(
            new[] { ("attempts", 1d, "outcome=Activated"), ("attempts", 1d, "outcome=ProviderHalted") },
            Select(measurements, "attempts"));
        CollectionAssert.AreEqual(
            new[] { ("fetch_duration", 250d, "disposition=Retrieved"), ("fetch_duration", 0d, "disposition=TimedOut") },
            Select(measurements, "fetch_duration"));
        CollectionAssert.AreEqual(new[] { ("raw_bytes", 4_096d, "") }, Select(measurements, "raw_bytes"));
        var allowed = new HashSet<string>(["outcome", "disposition"], StringComparer.Ordinal);
        Assert.IsTrue(measurements.SelectMany(static m => m.Tags.Keys).All(allowed.Contains));
    }

    [TestMethod]
    public void GaugesEmitNothingBeforeTheFirstObservedStateAndItsValuesAfter()
    {
        var clock = new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start);
        using var meter = new Meter(OrbitalElementsTelemetry.MeterName);
        using var telemetry = new OrbitalElementsTelemetry(clock, meter);
        using var recorder = new MeasurementRecorder(meter);

        recorder.Collect();
        Assert.AreEqual(0, recorder.Snapshot().Length);

        telemetry.Observe(State(records: 16_754, activated: clock.Now.AddMinutes(-2), halted: true, objects: 16_750,
            newest: clock.Now.AddHours(-6), oldest: clock.Now.AddHours(-30)));
        recorder.Collect();
        var observed = recorder.Snapshot();

        CollectionAssert.AreEquivalent(
            new[]
            {
                ("active_records", 16_754d, ""), ("active_objects", 16_750d, ""), ("newest_element_age", 21_600d, ""),
                ("oldest_element_age", 108_000d, ""), ("activation_age", 120d, ""), ("halted", 1d, "")
            },
            observed.Select(static m => (m.Name, m.Value, m.TagText)).ToArray());

        recorder.Clear();
        telemetry.Observe(State(records: null, activated: null, halted: false));
        recorder.Collect();

        CollectionAssert.AreEqual(new[] { ("halted", 0d, "") },
            recorder.Snapshot().Select(static m => (m.Name, m.Value, m.TagText)).ToArray());
    }

    [TestMethod]
    public async Task TheRefreshJobCountsOnlyDurablySettledAttemptsAndPublishesTheGaugeState()
    {
        using var meter = new Meter(OrbitalElementsTelemetry.MeterName);
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot(), meter: meter).ConfigureAwait(false);
        using var recorder = new MeasurementRecorder(meter);
        var raw = OrbitalRefreshTestHarness.ValidPayload();
        harness.Client.Enqueue(harness.Retrieved(raw));
        await harness.RunAsync().ConfigureAwait(false);
        harness.Intercept.HaltFault = static _ => new OrbitalStorageException("orbital.storage-io", "synthetic", null);
        harness.Clock.Advance(OrbitalElementsStoreTestData.Interval);
        harness.Client.Enqueue(harness.Halt(503));
        await harness.RunAsync().ConfigureAwait(false);

        recorder.Collect();
        var measurements = recorder.Snapshot();

        CollectionAssert.AreEqual(new[] { ("attempts", 1d, "outcome=Activated") }, Select(measurements, "attempts"),
            "A halt that never became durable is not counted.");
        CollectionAssert.AreEqual(FetchDispositions,
            measurements.Where(static m => m.Name == "fetch_duration").Select(static m => m.TagText).ToArray());
        CollectionAssert.AreEqual(new[] { ("raw_bytes", (double)raw.Length, "") }, Select(measurements, "raw_bytes"));
        Assert.AreEqual(2d, measurements.Single(static m => m.Name == "active_records").Value);
        Assert.AreEqual(0d, measurements.Single(static m => m.Name == "halted").Value);
    }

    [TestMethod]
    public async Task StartupPublishesTheReconciledHaltBeforeAnyAttemptAndAnAppliedResumeClearsIt()
    {
        var root = CreateRoot();
        var clock = new SignalingTimeProvider(OrbitalElementsStoreTestData.Start);
        var before = await OrbitalCompositionHost.StartAsync(root, clock).ConfigureAwait(false);
        await using (before.ConfigureAwait(false))
        {
            // An attempt left in flight by a crash: it may have reached a provider that refused it.
            var admission = await before.Store.TryBeginAttemptAsync("crash-probe", OrbitalAttemptTrigger.Scheduled,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admission.Status);
        }

        using var meter = new Meter(OrbitalElementsTelemetry.MeterName);
        using var recorder = new MeasurementRecorder(meter);
        var after = await OrbitalCompositionHost.StartAsync(root, clock, meter: meter).ConfigureAwait(false);
        await using var lifetime = after.ConfigureAwait(false);

        recorder.Collect();
        Assert.AreEqual(1d, Halted(recorder.Snapshot()),
            "Startup publishes the halt that restart reconciliation recorded, before any attempt.");
        var halt = (await after.StateAsync().ConfigureAwait(false)).Halt;
        Assert.IsNotNull(halt);
        Assert.IsNull(halt.HttpStatusCode);

        recorder.Clear();
        var resumed = await after.Operations.ResumeAsync(new OrbitalResumeRequest(halt.SinceUtc, "resume-gauge", "owner",
            "provider access confirmed"), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalElementsCommandStatus.Applied, resumed.Status, resumed.ReasonCode);
        recorder.Collect();
        Assert.AreEqual(0d, Halted(recorder.Snapshot()), "An applied resume publishes now, not after the next attempt.");
        Assert.AreEqual(0, after.Handler.Count, "No provider request was made.");
    }

    [TestMethod]
    public async Task ASkippedOccurrencePublishesTheGaugeState()
    {
        using var meter = new Meter(OrbitalElementsTelemetry.MeterName);
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot(), enabled: false, meter: meter)
            .ConfigureAwait(false);
        using var recorder = new MeasurementRecorder(meter);

        var execution = await harness.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, execution.Outcome);
        recorder.Collect();
        Assert.AreEqual(0d, Halted(recorder.Snapshot()));
        Assert.AreEqual(0, harness.Client.Calls);
    }

    [TestMethod]
    public async Task APublicationLeavesItToAnOccurrenceHoldingTheLockWhichPublishesALaterRead()
    {
        using var meter = new Meter(OrbitalElementsTelemetry.MeterName);
        using var harness = await OrbitalRefreshTestHarness.CreateAsync(CreateRoot(), meter: meter).ConfigureAwait(false);
        using var recorder = new MeasurementRecorder(meter);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.Enqueue(async _ =>
        {
            await release.Task.ConfigureAwait(false);
            return harness.Halt(429);
        });
        var run = harness.RunAsync().AsTask();
        await harness.Client.RequestStarted.Task.ConfigureAwait(false);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        await harness.Job.PublishStateAsync(CancellationToken.None).ConfigureAwait(false);
        waited.Stop();
        recorder.Collect();
        Assert.IsEmpty(recorder.Snapshot().Where(static m => m.Name == "halted"),
            "Nothing is published while an occurrence holds the lock.");
        Assert.IsGreaterThanOrEqualTo(OrbitalElementsRefreshJob.PublishLockWait - TimeSpan.FromMilliseconds(50), waited.Elapsed);
        Assert.IsFalse(run.IsCompleted);

        release.SetResult();
        await run.ConfigureAwait(false);
        recorder.Collect();
        Assert.AreEqual(1d, Halted(recorder.Snapshot()), "The occurrence publishes its own later read.");
    }

    private static double Halted(IEnumerable<Measurement> measurements)
        => measurements.Single(static m => m.Name == "halted").Value;

    private static (string, double, string)[] Select(IEnumerable<Measurement> measurements, string name)
        => measurements.Where(m => m.Name == name).Select(static m => (m.Name, m.Value, m.TagText)).ToArray();

    private static OrbitalProviderState State(int? records, DateTimeOffset? activated, bool halted, int? objects = null,
        DateTimeOffset? newest = null, DateTimeOffset? oldest = null)
        => new(1, null, null, records, objects, oldest, newest, null, null, null, null, null, activated, null,
            halted ? new OrbitalProviderHalt(OrbitalElementsStoreTestData.Start, 429, "synthetic") : null, null);

    private sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, string?> Tags)
    {
        public string TagText => string.Join(',', Tags.Select(static tag => tag.Key + "=" + tag.Value));
    }

    /// <summary>Listens to exactly one meter instance, so parallel tests with the same meter name never interfere.</summary>
    private sealed class MeasurementRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public MeasurementRecorder(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public void Collect() => _listener.RecordObservableInstruments();

        public void Clear() => _measurements.Clear();

        public Measurement[] Snapshot() => _measurements.ToArray();

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Assert.IsTrue(instrument.Name.StartsWith(Prefix, StringComparison.Ordinal), instrument.Name);
            var labels = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                labels[tag.Key] = tag.Value?.ToString();
            }
            _measurements.Enqueue(new Measurement(instrument.Name[Prefix.Length..], value, labels));
        }
    }
}
