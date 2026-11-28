using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow("continuous", 0, 600)]
    [DataRow("continuous", 2000, 600)]
    [DataRow("continuous", 30000, 5000)]
    [DataRow("continuous-night", 0, 20001)]
    [DataRow("continuous-stopped", 0, 20001)]
    [DataRow("overrun", 2000, 3000)]
    [DataRow("overrun", 2000, 3900)]
    [DataRow("busy", 2000, 4500)]
    [DataRow("busy", 2000, 6000)]
    [DataRow("idle-gap", 2000, 4500)]
    [DataRow("override", 2000, 0)]
    [DataRow("override-faster", 2000, 0)]
    [DataRow("override-pause", 2000, 0)]
    [DataRow("override-two-misses", 2000, 0)]
    [DataRow("mixed", 80, 0)]
    [DataRow("mixed-late", 80, 0)]
    [DataRow("pause", 2000, 0)]
    public async Task CenteredRunnerUsesCommittedShutterCalendar(string scenario, int configuredMs, int cycleMs)
    {
        var root = CreateRoot();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var token = timeout.Token;
        var start = Timestamp(1);
        var clock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var config = CenteredConfiguration("runner-calendar");
        var longContinuous = scenario is "continuous-night" or "continuous-stopped";
        var cadence = scenario == "continuous" || longContinuous
            ? CaptureCadenceMode.Continuous : CaptureCadenceMode.MinimumStartInterval;
        var exposure = scenario == "busy" ? TimeSpan.FromMilliseconds(cycleMs - 500)
            : longContinuous ? TimeSpan.FromSeconds(20) : CenteredTestSettings.Exposure;
        var settleBetweenShutters = longContinuous || scenario is "busy" or "idle-gap";
        config = config with
        {
            Rig = config.Rig with
            {
                ControlPolicy = new()
                {
                    ExposureControl = AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled
                },
                Pipeline = config.Rig.Pipeline with
                {
                    CadenceMode = cadence,
                    CaptureInterval = TimeSpan.FromMilliseconds(configuredMs),
                    DayExposure = exposure,
                    NightExposure = exposure,
                    DayGain = CenteredTestSettings.Gain,
                    NightGain = CenteredTestSettings.Gain
                }
            }
        };
        int[] expectedMs = scenario switch
        {
            "override" => [0, 2000, 12000, 22000, 32000],
            "override-faster" => [0, 2000, 2040, 2080, 2120],
            "override-pause" => [0, 2000, 2190, 2270, 2350],
            "override-two-misses" => [0, 2000, 2250, 2330, 2410],
            "mixed" or "mixed-late" => [0, 80, 330, 580, 830],
            "pause" => [0, 2000, 12000, 14000, 16000],
            "continuous-stopped" => [0, cycleMs, cycleMs * 2],
            _ => [0, cycleMs, cycleMs * 2, cycleMs * 3, cycleMs * 4]
        };
        var requests = new List<DateTimeOffset>();
        try
        {
            using var fixture = new CenteredFixture(root, timeProvider: clock, wakeup: new());
            var module = new CenteredRunnerModule(async (_, request, ct) =>
            {
                if (settleBetweenShutters && requests.Count > 0)
                    await SettleTraceLinearAsync(fixture, config, new ExposureTestTimeProvider(clock.GetUtcNow()), 1, ct)
                        .ConfigureAwait(false);
                requests.Add(request.RequestedStartUtc);
                var deviceStart = clock.GetUtcNow();
                var observed = scenario == "mixed-late" && requests.Count == 2
                    ? deviceStart.AddMilliseconds(30) : deviceStart;
                var frame = CenteredTraceFrame(100, request with { RequestedStartUtc = observed });
                clock.Advance(frame.AcquisitionTiming!.ReadoutCompletedUtc - clock.GetUtcNow());
                if (requests.Count == 2 && scenario is
                    "override" or "override-faster" or "override-pause" or "override-two-misses" or "mixed" or "mixed-late")
                {
                    var next = TimeSpan.FromMilliseconds(scenario switch
                    {
                        "override" => 10000,
                        "override-faster" => 40,
                        "override-pause" or "override-two-misses" => 80,
                        _ => 250
                    });
                    frame = frame with { NextSetpoint = frame.NextSetpoint with { NextIntervalOverride = next } };
                }
                return frame;
            });
            await using var moduleLifetime = module.ConfigureAwait(false);
            var distributor = new CenteredTraceNotifier
            {
                Committed = () =>
                {
                    if (requests.Count == expectedMs.Length)
                    {
                        stop.Cancel();
                        return;
                    }
                    if (cycleMs > 0 || (scenario is "pause" or "override-pause" or "override-two-misses" && requests.Count == 2))
                        clock.Advance(start.AddMilliseconds(expectedMs[requests.Count]) - clock.GetUtcNow());
                }
            };
            var runner = new CameraModuleRunner(module, new CaptureHostContext(config, fixture.Ingress, distributor),
                clock, NullLogger.Instance, new AstronomyEnginePlanetEphemeris());
            await runner.RunAsync(stop.Token).WaitAsync(token).ConfigureAwait(false);
            CollectionAssert.AreEqual(expectedMs.Select(ms => start.AddMilliseconds(ms)).ToArray(), requests.ToArray());
            // The runner clock advances when it waits. The processing deadline timer must not advance it.
            var processingClock = new ExposureTestTimeProvider(clock.GetUtcNow());
            await SettleTraceLinearAsync(fixture, config, processingClock, settleBetweenShutters ? 1 : expectedMs.Length, token)
                .ConfigureAwait(false);

            using var db = await OpenJournalAsync(root).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT calendar.slot, calendar.start_ticks, attempt.capture_id, window.deadline_utc_ticks
                FROM processing_centered_attempt_calendar calendar
                JOIN centered_exposure_attempts attempt ON attempt.attempt_id = calendar.attempt_id
                JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
                ORDER BY calendar.slot;
                """;
            var slots = new List<long>();
            var captureIds = new List<Guid>();
            var deadlines = new List<DateTimeOffset>();
            using (var rows = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                while (await rows.ReadAsync(token).ConfigureAwait(false))
                {
                    slots.Add(rows.GetInt64(0));
                    Assert.AreEqual(requests[captureIds.Count].UtcTicks, rows.GetInt64(1));
                    captureIds.Add(Guid.ParseExact(rows.GetString(2), "N"));
                    deadlines.Add(new(rows.GetInt64(3), TimeSpan.Zero));
                }
            }
            long[] expectedSlots = scenario switch
            {
                "pause" => [1, 2, 7, 8, 9],
                "override-pause" => [1, 2, 4, 5, 6],
                "override-two-misses" => [1, 2, 5, 6, 7],
                "idle-gap" => [1, 3, 5, 7, 9],
                "continuous-stopped" => [1, 2, 3],
                _ => [1, 2, 3, 4, 5]
            };
            CollectionAssert.AreEqual(expectedSlots, slots.ToArray());
            command.CommandText = "SELECT COUNT(*) FROM processing_centered_windows;";
            Assert.AreEqual(expectedSlots[^1], (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
            if (scenario == "override-two-misses")
                await AssertSkipVisibilityAsync(fixture, db, start, captureIds[1], token).ConfigureAwait(false);

            clock.Advance(deadlines.Max().AddMilliseconds(1) - clock.GetUtcNow());
            using var coordinator = new CenteredWindowCoordinator(fixture.Store, new CenteredWindowRuntimeJournal(), clock,
                NullLogger<CenteredWindowCoordinator>.Instance);
            await coordinator.CloseDueAsync(token).ConfigureAwait(false);
            var center = await fixture.Store.ReadCenteredWindowByReferenceAsync(captureIds[2], token).ConfigureAwait(false);
            Assert.IsNotNull(center);
            Assert.AreEqual(deadlines[2], center.Closure.EvidenceCutoffUtc);
            Assert.AreEqual(CenteredWindowSlotState.Eligible, center.Closure.Center.State);
            Assert.AreEqual(captureIds[2], center.Closure.Center.Input!.Key.CaptureId);
            Assert.AreEqual(exposure, center.Closure.Center.Input.Timing.ExposureEndedUtc - center.Closure.Center.Input.Timing.ExposureStartedUtc);
            Assert.HasCount(5, center.Closure.Slots);
            if (longContinuous)
            {
                var horizon = deadlines[2] - requests[2];
                Assert.IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(65), horizon);
                Assert.IsLessThan(TimeSpan.FromSeconds(76), horizon);
            }
            if (scenario is "override-two-misses" or "idle-gap" or "continuous-stopped")
            {
                if (scenario == "override-two-misses")
                {
                    await AssertTwoMissesAsync(fixture, root, clock, start, captureIds, deadlines, expectedSlots, token)
                        .ConfigureAwait(false);
                }
                else if (scenario == "idle-gap")
                {
                    CollectionAssert.AreEqual(new Guid?[] { captureIds[1], null, captureIds[2], null, captureIds[3] },
                        center.Closure.Slots.Select(slot => slot.Input?.Key.CaptureId).ToArray());
                    Assert.AreEqual("nominal-slot-missed", center.Closure.Slots[1].Reason);
                    Assert.AreEqual("nominal-slot-missed", center.Closure.Slots[3].Reason);
                }
                else
                {
                    CollectionAssert.AreEqual(new Guid?[] { captureIds[0], captureIds[1], captureIds[2], null, null },
                        center.Closure.Slots.Select(slot => slot.Input?.Key.CaptureId).ToArray());
                    foreach (var forecast in center.Closure.Slots.Skip(3))
                    {
                        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, forecast.State);
                        Assert.IsNull(forecast.Time.PlannedSlot);
                        Assert.IsNull(forecast.Reason);
                    }
                }
            }
            else if (scenario is "pause" or "override-pause")
            {
                Assert.AreEqual("nominal-slot-missed", center.Closure.Slots[1].Reason);
                if (scenario == "override-pause")
                {
                    var beforeGap = await fixture.Store.ReadCenteredWindowByReferenceAsync(captureIds[1], token).ConfigureAwait(false);
                    var afterGap = await fixture.Store.ReadCenteredWindowByReferenceAsync(captureIds[3], token).ConfigureAwait(false);
                    Assert.IsNotNull(beforeGap);
                    Assert.IsNotNull(afterGap);
                    Assert.AreEqual(CenteredWindowSlotState.Eligible, beforeGap.Closure.Center.State);
                    Assert.AreEqual(CenteredWindowSlotState.Eligible, afterGap.Closure.Center.State);
                    var expectedMissedStart = start.AddMilliseconds(2080);
                    foreach (var missed in new[] { beforeGap.Closure.Slots[3], center.Closure.Slots[1], afterGap.Closure.Slots[0] })
                    {
                        Assert.AreEqual("nominal-slot-missed", missed.Reason);
                        Assert.AreEqual(expectedMissedStart, missed.Time.ExpectedStartUtc);
                        Assert.AreEqual(TimeSpan.FromMilliseconds(16), missed.Time.Tolerance);
                    }
                }
            }
            else
            {
                CollectionAssert.AreEqual(captureIds.ToArray(), center.Closure.Slots.Select(slot => slot.Input!.Key.CaptureId).ToArray());
                if (scenario == "override-faster")
                    CollectionAssert.AreEqual(new TimeSpan?[]
                    {
                        TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(8),
                        TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(8)
                    }, center.Closure.Slots.Select(slot => slot.Time.Tolerance).ToArray());
                if (scenario is "mixed" or "mixed-late")
                {
                    Assert.AreEqual(TimeSpan.FromMilliseconds(16), center.Closure.Slots[1].Time.Tolerance);
                    Assert.AreEqual(TimeSpan.FromMilliseconds(50), center.Closure.Center.Time.Tolerance);
                    Assert.AreEqual(scenario == "mixed-late" ? CenteredWindowSlotState.OutsideTolerance :
                        CenteredWindowSlotState.Eligible, center.Closure.Slots[1].State);
                }
            }
        }
        finally { DeleteRoot(root); }
    }

    private static async Task AssertSkipVisibilityAsync(CenteredFixture fixture, SqliteConnection db, DateTimeOffset start,
        Guid captureId, CancellationToken token)
    {
        var window = await fixture.Store.ReadCenteredWindowStateAsync(captureId, token).ConfigureAwait(false);
        Assert.IsNotNull(window?.Checkpoint);
        var journal = new CenteredWindowRuntimeJournal();
        using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(token).ConfigureAwait(false);
        var before = await journal.ReadWindowEvidenceAsync(transaction, window.Checkpoint, start.AddMilliseconds(2249), token)
            .ConfigureAwait(false);
        var after = await journal.ReadWindowEvidenceAsync(transaction, window.Checkpoint, start.AddMilliseconds(2250), token)
            .ConfigureAwait(false);
        Assert.IsFalse(before.Observations.Any(observation => observation.EmptySlotReason == "nominal-slot-missed"));
        CollectionAssert.AreEqual(new long?[] { 3, 4 }, after.Observations
            .Where(observation => observation.EmptySlotReason == "nominal-slot-missed")
            .Select(observation => observation.Time.PlannedSlot).Order().ToArray());
        Assert.IsFalse(after.Observations.Any(observation => observation.Time.PlannedSlot == 5));
    }

    private static async Task AssertTwoMissesAsync(CenteredFixture fixture, string root, TimeProvider clock, DateTimeOffset start,
        List<Guid> captureIds, List<DateTimeOffset> deadlines, long[] expectedSlots, CancellationToken token)
    {
        using var db = await OpenJournalAsync(root).ConfigureAwait(false);
        var frozen = await ReadClosureBytesAsync(db, captureIds[1], token).ConfigureAwait(false);
        using var reopened = new SqliteCaptureProcessingStore(fixture.Options, clock);
        using var coordinator = new CenteredWindowCoordinator(reopened, new CenteredWindowRuntimeJournal(), clock,
            NullLogger<CenteredWindowCoordinator>.Instance);
        await coordinator.CloseDueAsync(token).ConfigureAwait(false);
        CollectionAssert.AreEqual(frozen, await ReadClosureBytesAsync(db, captureIds[1], token).ConfigureAwait(false));
        var expectedMisses = new Dictionary<long, DateTimeOffset>
        {
            [3] = start.AddMilliseconds(2080),
            [4] = start.AddMilliseconds(2160)
        };
        for (var index = 0; index < captureIds.Count; index++)
        {
            var original = await fixture.Store.ReadCenteredWindowByReferenceAsync(captureIds[index], token).ConfigureAwait(false);
            var recovered = await reopened.ReadCenteredWindowByReferenceAsync(captureIds[index], token).ConfigureAwait(false);
            Assert.IsNotNull(original);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(deadlines[index], recovered.Closure.EvidenceCutoffUtc);
            Assert.AreEqual(original.Closure.Identity, recovered.Closure.Identity);
            Assert.AreEqual(CenteredWindowSlotState.Eligible, recovered.Closure.Center.State);
            Assert.HasCount(5, recovered.Closure.Slots);
            foreach (var missed in recovered.Closure.Slots)
            {
                var expectedOrdinal = expectedSlots[index] + missed.Offset;
                if (!expectedMisses.TryGetValue(expectedOrdinal, out var expectedTime))
                    continue;
                Assert.AreEqual(expectedOrdinal, missed.Time.PlannedSlot);
                Assert.AreEqual(expectedTime, missed.Time.ExpectedStartUtc);
                Assert.AreEqual(TimeSpan.FromMilliseconds(16), missed.Time.Tolerance);
                Assert.AreEqual(CenteredWindowSlotState.MissingExposure, missed.State);
                Assert.AreEqual("nominal-slot-missed", missed.Reason);
                Assert.IsNull(missed.Input);
            }
            if (index == 1)
                Assert.IsFalse(recovered.Closure.Slots.Any(slot => slot.Input?.Key.CaptureId == captureIds[2]));
        }
    }

    private static async Task<byte[]> ReadClosureBytesAsync(SqliteConnection db, Guid captureId, CancellationToken token)
    {
        using var query = db.CreateCommand();
        query.CommandText = "SELECT closure_json FROM processing_centered_windows WHERE reference_capture_id = $capture;";
        query.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        return (byte[])(await query.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }
}
