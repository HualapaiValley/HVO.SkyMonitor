using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    [DataRow("continuous", 0, 600)]
    [DataRow("continuous", 2000, 600)]
    [DataRow("continuous", 30000, 5000)]
    [DataRow("overrun", 2000, 3000)]
    [DataRow("overrun", 2000, 3900)]
    [DataRow("override", 2000, 0)]
    [DataRow("override-pause", 2000, 0)]
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
        var cadence = scenario == "continuous" ? CaptureCadenceMode.Continuous : CaptureCadenceMode.MinimumStartInterval;
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
                    DayExposure = CenteredTestSettings.Exposure,
                    NightExposure = CenteredTestSettings.Exposure,
                    DayGain = CenteredTestSettings.Gain,
                    NightGain = CenteredTestSettings.Gain
                }
            }
        };
        int[] expectedMs = scenario switch
        {
            "override" => [0, 2000, 12000, 22000, 32000],
            "override-pause" => [0, 2000, 2190, 2270, 2350],
            "mixed" or "mixed-late" => [0, 80, 330, 580, 830],
            "pause" => [0, 2000, 12000, 14000, 16000],
            _ => [0, cycleMs, cycleMs * 2, cycleMs * 3, cycleMs * 4]
        };
        var requests = new List<DateTimeOffset>();
        try
        {
            using var fixture = new CenteredFixture(root, timeProvider: clock, wakeup: new());
            var module = new CenteredRunnerModule((_, request, _) =>
            {
                requests.Add(request.RequestedStartUtc);
                var deviceStart = clock.GetUtcNow();
                var observed = scenario == "mixed-late" && requests.Count == 2
                    ? deviceStart.AddMilliseconds(30) : deviceStart;
                var frame = CenteredTraceFrame(100, request with { RequestedStartUtc = observed });
                clock.Advance(frame.AcquisitionTiming!.ReadoutCompletedUtc - clock.GetUtcNow());
                if (requests.Count == 2 && scenario is "override" or "override-pause" or "mixed" or "mixed-late")
                {
                    var next = TimeSpan.FromMilliseconds(scenario switch
                    {
                        "override" => 10000,
                        "override-pause" => 80,
                        _ => 250
                    });
                    frame = frame with { NextSetpoint = frame.NextSetpoint with { NextIntervalOverride = next } };
                }
                return Task.FromResult(frame);
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
                    if (cycleMs > 0 || (scenario is "pause" or "override-pause" && requests.Count == 2))
                        clock.Advance(start.AddMilliseconds(expectedMs[requests.Count]) - clock.GetUtcNow());
                }
            };
            var runner = new CameraModuleRunner(module, new CaptureHostContext(config, fixture.Ingress, distributor),
                clock, NullLogger.Instance, new AstronomyEnginePlanetEphemeris());
            await runner.RunAsync(stop.Token).WaitAsync(token).ConfigureAwait(false);
            CollectionAssert.AreEqual(expectedMs.Select(ms => start.AddMilliseconds(ms)).ToArray(), requests.ToArray());
            // The runner clock advances when it waits. The processing deadline timer must not advance it.
            var processingClock = new ExposureTestTimeProvider(clock.GetUtcNow());
            await SettleTraceLinearAsync(fixture, config, processingClock, expectedMs.Length, token).ConfigureAwait(false);

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
                _ => [1, 2, 3, 4, 5]
            };
            CollectionAssert.AreEqual(expectedSlots, slots.ToArray());
            command.CommandText = "SELECT COUNT(*) FROM processing_centered_windows;";
            Assert.AreEqual(expectedSlots[^1], (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);

            clock.Advance(deadlines.Max().AddMilliseconds(1) - clock.GetUtcNow());
            using var coordinator = new CenteredWindowCoordinator(fixture.Store, new CenteredWindowRuntimeJournal(), clock,
                NullLogger<CenteredWindowCoordinator>.Instance);
            await coordinator.CloseDueAsync(token).ConfigureAwait(false);
            var center = await fixture.Store.ReadCenteredWindowByReferenceAsync(captureIds[2], token).ConfigureAwait(false);
            Assert.IsNotNull(center);
            Assert.AreEqual(deadlines[2], center.Closure.EvidenceCutoffUtc);
            Assert.AreEqual(CenteredWindowSlotState.Eligible, center.Closure.Center.State);
            Assert.AreEqual(captureIds[2], center.Closure.Center.Input!.Key.CaptureId);
            Assert.HasCount(5, center.Closure.Slots);
            if (scenario is "pause" or "override-pause")
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
}
