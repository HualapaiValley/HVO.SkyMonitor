using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

/// <summary>
/// Manual focus sessions through the real capture-owner boundary: the durable admission coordinator, the module ownership
/// registry, the production preview source, and a real VirtualSky module rendering the simulated defocus model.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ManualFocusSessionCoordinatorTests
{
    private static readonly ManualFocusPreviewSettings Gain20 = new(TimeSpan.FromSeconds(1), 20);

    private static ManualFocusSessionLimits Limits(
        int historyCapacity = 100,
        TimeSpan? observerTimeout = null,
        TimeSpan? sampleDeadlineGrace = null,
        TimeSpan? minimumSamplePeriod = null)
        => ManualFocusSessionLimits.Default with
        {
            HistoryCapacity = historyCapacity,
            MinimumSafetyTimeout = TimeSpan.FromMilliseconds(50),
            ObserverTimeout = observerTimeout ?? TimeSpan.FromMinutes(5),
            SampleDeadlineGrace = sampleDeadlineGrace ?? TimeSpan.FromSeconds(30),
            MinimumSamplePeriod = minimumSamplePeriod ?? TimeSpan.Zero
        };

    [TestMethod]
    public async Task ContinuousLoop_MeasuresTheVirtualStarThroughTheOwnerUntilStopped()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var started = await coordinator.StartAsync(new(Gain20, 400), "alice", CancellationToken.None).ConfigureAwait(false);
        var running = await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 3, "three samples").ConfigureAwait(false);
        var stopped = await coordinator.StopAsync(started.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        var samplesAtStop = stopped.TotalSamples;
        await Task.Delay(200).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Running, started.State);
        Assert.AreEqual(400d, started.SimulatedFocusPosition);
        Assert.AreEqual(ManualFocusRetentionState.InMemoryOnly, started.Retention);
        Assert.AreEqual(ManualFocusSessionState.Running, running.State);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, running.History.Count).Select(static value => (long)value).ToArray(),
            running.History.Select(static sample => sample.Sequence).ToArray(), "Every exposure is one consecutive sample.");
        var latest = running.Latest!;
        Assert.AreEqual(FocusStarStatus.Valid, latest.Measurement.Status, latest.Measurement.ReasonCode);
        Assert.AreEqual(FocusTestModules.ZenithStar.X, latest.Measurement.Centroid!.Value.X, 0.15);
        Assert.AreEqual(FocusTestModules.ZenithStar.Y, latest.Measurement.Centroid.Value.Y, 0.15);
        Assert.AreEqual(ManualFocusTargetSource.Automatic, latest.TargetSource);
        Assert.AreEqual(400d, latest.SimulatedFocusPosition);
        var provenance = latest.Provenance;
        Assert.AreEqual("VirtualSky", provenance.ModuleType);
        Assert.AreEqual(1, provenance.ModuleGeneration);
        Assert.AreEqual("virtual-simulated-defocus", provenance.FidelityKind);
        Assert.IsFalse(provenance.QualifiesPhysicalFocus);
        Assert.AreEqual(VirtualSimulatedFocusOptions.ModelId, provenance.SimulatedFocusModelId);
        Assert.AreEqual(module.SimulatedFocus!.ParametersSha256, provenance.SimulatedFocusParametersSha256);
        Assert.AreEqual(CameraPixelFormat.Mono16, provenance.PixelFormat);
        Assert.AreEqual(484, provenance.FrameWidth);
        Assert.AreEqual(64, provenance.FrameSha256.Length);
        Assert.AreEqual(2 * ManualFocusPreviewMeasurement.MeasurementHalfWindow, provenance.WindowWidth);
        Assert.AreEqual(ManualFocusFrameSampler.AlgorithmVersion, provenance.SamplerAlgorithmVersion);
        Assert.AreEqual(FocusStarMeasurement.MetricDefinition, provenance.MetricDefinition);
        Assert.AreEqual(FocusStarMeasurer.AlgorithmVersion, provenance.MetricAlgorithmVersion);
        Assert.AreEqual(ManualFocusPreviewMeasurement.PreviewSettingsIdentity(Gain20, 400),
            provenance.PreviewSettingsIdentitySha256);
        var images = coordinator.LatestImages!;
        Assert.IsGreaterThan(0, images.OverviewJpeg.Length);
        Assert.IsNotNull(images.StarJpeg);
        Assert.AreEqual(484, images.FrameWidth);

        Assert.AreEqual(ManualFocusSessionState.Stopped, stopped.State);
        Assert.AreEqual(ManualFocusReasonCodes.StoppedByOperator, stopped.EndReason);
        Assert.IsNotNull(stopped.EndedUtc);
        Assert.AreEqual(samplesAtStop, coordinator.Snapshot.TotalSamples, "No exposure may be taken after Stop returns.");
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Admission.Snapshot.State);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false),
            "The admission gate must reopen after the last preview.");
        Assert.IsTrue(await fixture.Ownership.RevokeAsync(TimeSpan.Zero).ConfigureAwait(false),
            "No module lease may outlive the session.");
    }

    [TestMethod]
    public async Task Adjust_AppliesFromTheNextExposureAndTracksTheBestValidSample()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());
        var session = await coordinator.StartAsync(new(Gain20, 200), "alice", CancellationToken.None).ConfigureAwait(false);

        await FocusWait.UntilAsync(coordinator, static s => s.History.Any(static x => x.SimulatedFocusPosition == 200),
            "a sample at 200").ConfigureAwait(false);
        coordinator.Adjust(session.SessionId, "alice", new(SimulatedFocusPosition: 560));
        await FocusWait.UntilAsync(coordinator, static s => s.History.Any(static x => x.SimulatedFocusPosition == 560),
            "a sample at 560").ConfigureAwait(false);
        var brighter = new ManualFocusPreviewSettings(TimeSpan.FromSeconds(2), 10);
        coordinator.Adjust(session.SessionId, "alice", new(Settings: brighter, SimulatedFocusPosition: 1000));
        var final = await FocusWait.UntilAsync(coordinator,
            static s => s.History.Any(static x => x.SimulatedFocusPosition == 1000), "a sample at 1000").ConfigureAwait(false);
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        double Diameter(double position) => final.History.First(sample => sample.SimulatedFocusPosition == position)
            .Measurement.HalfFluxDiameterPixels!.Value;
        Assert.IsTrue(Diameter(200) > 2 * Diameter(560), $"HFD at 200 {Diameter(200)} vs 560 {Diameter(560)}");
        Assert.IsTrue(Diameter(1000) > 2 * Diameter(560), $"HFD at 1000 {Diameter(1000)} vs 560 {Diameter(560)}");
        Assert.AreEqual(560d, final.Best!.SimulatedFocusPosition, "The best sample is the narrowest valid one.");
        foreach (var sample in final.History)
        {
            Assert.AreEqual(ManualFocusPreviewMeasurement.PreviewSettingsIdentity(sample.Settings, sample.SimulatedFocusPosition),
                sample.Provenance.PreviewSettingsIdentitySha256, "Each sample records the settings it was taken with.");
        }
        Assert.AreEqual(brighter, final.History.First(static x => x.SimulatedFocusPosition == 1000).Settings);
    }

    [TestMethod]
    public async Task HistoryCap_KeepsTheNewestSamplesAndNeverStopsTheLoop()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits(historyCapacity: 3));
        var session = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);

        var snapshot = await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 7, "seven samples").ConfigureAwait(false);
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Running, snapshot.State);
        Assert.AreEqual(3, snapshot.HistoryCapacity);
        CollectionAssert.AreEqual(
            new[] { snapshot.TotalSamples - 2, snapshot.TotalSamples - 1, snapshot.TotalSamples },
            snapshot.History.Select(static sample => sample.Sequence).ToArray());
        Assert.IsNotNull(snapshot.Best);
    }

    [TestMethod]
    public async Task MinimumSamplePeriod_PacesAnInstantCameraAndStopInterruptsTheWait()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System,
            Limits(minimumSamplePeriod: TimeSpan.FromMilliseconds(400)));
        var sinceStart = System.Diagnostics.Stopwatch.StartNew();
        var session = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);

        await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 3, "three samples").ConfigureAwait(false);
        var toThirdSample = sinceStart.Elapsed;
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        using var slow = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System,
            Limits(minimumSamplePeriod: TimeSpan.FromMinutes(10)));
        var waiting = await slow.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        await FocusWait.UntilAsync(slow, static s => s.TotalSamples >= 1, "the first sample").ConfigureAwait(false);
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        var stopped = await slow.StopAsync(waiting.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        stopWatch.Stop();

        // The third exposure cannot begin before two periods have passed since the first; unpaced VirtualSky takes ~0.15 s.
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800), toThirdSample);
        Assert.AreEqual(ManualFocusReasonCodes.StoppedByOperator, stopped.EndReason);
        Assert.AreEqual(1, stopped.TotalSamples, "No exposure is taken while the loop waits out its period.");
        Assert.IsLessThan(TimeSpan.FromSeconds(5), stopWatch.Elapsed, "Stop must not wait out the sample period.");
        Assert.IsTrue(await fixture.Ownership.RevokeAsync(TimeSpan.Zero).ConfigureAwait(false),
            "No module lease outlives a session stopped during its wait.");
    }

    [TestMethod]
    public async Task NoStarAndSaturation_AreExplicitSamplesThatNeverBecomeTheBest()
    {
        var empty = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var emptyScope = empty.ConfigureAwait(false);
        await empty.PublishVirtualSkyAsync(magnitude: 30).ConfigureAwait(false);
        using var starless = new ManualFocusSessionCoordinator(empty.Source, TimeProvider.System, Limits());
        var first = await starless.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        var noStar = await FocusWait.UntilAsync(starless, static s => s.TotalSamples >= 2, "two samples").ConfigureAwait(false);
        await starless.StopAsync(first.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        var bright = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var brightScope = bright.ConfigureAwait(false);
        await bright.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var clipped = new ManualFocusSessionCoordinator(bright.Source, TimeProvider.System, Limits());
        var second = await clipped.StartAsync(new(new(TimeSpan.FromSeconds(4), 200), 560), "alice", CancellationToken.None)
            .ConfigureAwait(false);
        var saturated = await FocusWait.UntilAsync(clipped, static s => s.TotalSamples >= 2, "two samples").ConfigureAwait(false);
        await clipped.StopAsync(second.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Running, noStar.State);
        Assert.AreEqual(FocusStarStatus.NoStar, noStar.Latest!.Measurement.Status);
        Assert.AreEqual(FocusStarReasonCodes.NoCandidate, noStar.Latest.Measurement.ReasonCode);
        Assert.IsNull(noStar.Latest.Measurement.HalfFluxDiameterPixels);
        Assert.IsNull(noStar.Best);
        Assert.IsNull(noStar.LastFailure, "A starless frame is a measured sample, not an acquisition failure.");

        Assert.AreEqual(FocusStarStatus.Saturated, saturated.Latest!.Measurement.Status);
        Assert.IsNull(saturated.Latest.Measurement.HalfFluxDiameterPixels, "A clipped star never reports a width.");
        Assert.IsNotNull(saturated.Latest.Measurement.Centroid, "The clipped star is still located for the operator.");
        Assert.IsNull(saturated.Best);
    }

    [TestMethod]
    public async Task OperatorTarget_IsSearchedNearThePickAndAutomaticSelectionCanBeRestored()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var session = await coordinator.StartAsync(new(Gain20, 500, new PixelPoint(250, 158)), "alice", CancellationToken.None)
            .ConfigureAwait(false);
        var picked = await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 2, "two samples").ConfigureAwait(false);
        coordinator.Adjust(session.SessionId, "alice", new(Target: new PixelPoint(150, 150)));
        var empty = await FocusWait.UntilAsync(coordinator,
            s => s.History.Any(sample => sample.Sequence > picked.TotalSamples &&
                sample.Measurement.ReasonCode == FocusStarReasonCodes.NoCandidateNearSelection),
            "a sample at the empty pick").ConfigureAwait(false);
        coordinator.Adjust(session.SessionId, "alice", new(ResetToAutomaticTarget: true));
        var restored = await FocusWait.UntilAsync(coordinator,
            s => s.Latest is { TargetSource: ManualFocusTargetSource.Automatic } latest && latest.Sequence > empty.TotalSamples,
            "an automatic sample").ConfigureAwait(false);
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusTargetSource.Operator, picked.Latest!.TargetSource);
        Assert.AreEqual(FocusStarStatus.Valid, picked.Latest.Measurement.Status);
        Assert.AreEqual(FocusTestModules.ZenithStar.X, picked.Latest.Measurement.Centroid!.Value.X, 0.15,
            "The pick only seeds the search; the centroid is measured from pixels.");
        Assert.AreEqual(FocusStarStatus.NoStar, empty.History.Last(static sample =>
            sample.Measurement.ReasonCode == FocusStarReasonCodes.NoCandidateNearSelection).Measurement.Status);
        Assert.AreEqual(new PixelPoint(150, 150), empty.Target, "An empty pick is kept so the operator sees why.");
        Assert.AreEqual(FocusStarStatus.Valid, restored.Latest!.Measurement.Status);
    }

    [TestMethod]
    public async Task OnlyTheOwnerCanAdjustStopSaveOrObserve()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits(),
            new ManualFocusSessionStore(fixture.Root));
        var session = await coordinator.StartAsync(new(Gain20), "alice", CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() =>
            coordinator.StartAsync(new(Gain20), "bob", CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() =>
            coordinator.StopAsync(session.SessionId, "bob", CancellationToken.None)).ConfigureAwait(false);
        Assert.ThrowsExactly<ManualFocusSessionConflictException>(() =>
            coordinator.Adjust(session.SessionId, "bob", new(SimulatedFocusPosition: 560)));
        Assert.ThrowsExactly<ManualFocusSessionConflictException>(() => coordinator.Observe(session.SessionId, "bob"));
        Assert.ThrowsExactly<ManualFocusSessionStateException>(() => coordinator.Observe(Guid.NewGuid().ToString("N"), "alice"));
        await Assert.ThrowsExactlyAsync<ManualFocusSessionStateException>(() =>
            coordinator.SaveAsync(session.SessionId, "alice", CancellationToken.None)).ConfigureAwait(false);
        Assert.ThrowsExactly<ManualFocusSessionStateException>(() => coordinator.Discard(session.SessionId, "alice"));
        Assert.AreEqual(ManualFocusSessionState.Running, coordinator.Snapshot.State);
        Assert.AreEqual("alice", coordinator.Snapshot.OwnerId);

        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() =>
            coordinator.SaveAsync(session.SessionId, "bob", CancellationToken.None)).ConfigureAwait(false);
        Assert.ThrowsExactly<ManualFocusSessionConflictException>(() => coordinator.Discard(session.SessionId, "bob"));
        Assert.ThrowsExactly<ManualFocusSessionStateException>(() =>
            coordinator.Adjust(session.SessionId, "alice", new(SimulatedFocusPosition: 560)));
    }

    [TestMethod]
    public async Task Validation_RejectsSettingsPositionsTargetsAndTimeoutsOutsideTheDeclaredBounds()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());
        var invalid = new ManualFocusSessionRequest[]
        {
            new(new(TimeSpan.Zero, 20)),
            new(new(TimeSpan.FromSeconds(61), 20)),
            new(new(TimeSpan.FromSeconds(1), -1)),
            new(new(TimeSpan.FromSeconds(1), 1001)),
            new(new(TimeSpan.FromSeconds(1), double.NaN)),
            new(Gain20, -1),
            new(Gain20, 1000.5),
            new(Gain20, double.PositiveInfinity),
            new(Gain20, Target: new PixelPoint(double.NaN, 1)),
            new(Gain20, Target: new PixelPoint(-1, 1)),
            new(Gain20, SafetyTimeout: TimeSpan.FromMilliseconds(10)),
            new(Gain20, SafetyTimeout: TimeSpan.FromHours(2))
        };

        foreach (var request in invalid)
        {
            await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
                coordinator.StartAsync(request, "alice", CancellationToken.None), $"{request}").ConfigureAwait(false);
        }
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(new(Gain20), " ", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ManualFocusSessionState.Idle, coordinator.Snapshot.State);

        var session = await coordinator.StartAsync(new(Gain20), "alice", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(200d, session.SimulatedFocusPosition, "A session starts at the declared default position.");
        Assert.ThrowsExactly<ManualFocusSessionValidationException>(() =>
            coordinator.Adjust(session.SessionId, "alice", new(SimulatedFocusPosition: 1001)));
        Assert.ThrowsExactly<ManualFocusSessionValidationException>(() =>
            coordinator.Adjust(session.SessionId, "alice", new(Settings: new(TimeSpan.FromSeconds(1), 5000))));
        Assert.ThrowsExactly<ManualFocusSessionValidationException>(() =>
            coordinator.Adjust(session.SessionId, "alice", new(Target: new PixelPoint(1, 1), ResetToAutomaticTarget: true)));
        Assert.AreEqual(200d, coordinator.Snapshot.SimulatedFocusPosition, "A rejected change leaves the session unchanged.");
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DisabledSimulatedFocus_RefusesAPositionAndStillMeasuresTheFixedPsf()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync(focus: new VirtualSimulatedFocusOptions { Enabled = false }).ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None)).ConfigureAwait(false);
        var session = await coordinator.StartAsync(new(Gain20), "alice", CancellationToken.None).ConfigureAwait(false);
        var sampled = await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 1, "a sample").ConfigureAwait(false);
        Assert.ThrowsExactly<ManualFocusSessionValidationException>(() =>
            coordinator.Adjust(session.SessionId, "alice", new(SimulatedFocusPosition: 560)));
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        Assert.IsNull(session.SimulatedFocusPosition);
        Assert.IsNull(session.SimulatedFocus);
        Assert.AreEqual("virtual-fixed-psf", sampled.Latest!.Provenance.FidelityKind);
        Assert.IsNull(sampled.Latest.Provenance.SimulatedFocusModelId);
        Assert.AreEqual(FocusStarStatus.Valid, sampled.Latest.Measurement.Status);
    }

    [TestMethod]
    public async Task ObserverTimeout_EndsAnUnwatchedSessionWhileObservationKeepsItRunning()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System,
            Limits(observerTimeout: TimeSpan.FromMilliseconds(400)));
        var session = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);

        for (var beat = 0; beat < 12; beat++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            coordinator.Observe(session.SessionId, "alice");
        }
        var watched = coordinator.Snapshot;
        var ended = await FocusWait.EndedAsync(coordinator).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Running, watched.State, "Observing must keep the session alive.");
        Assert.AreEqual(ManualFocusSessionState.TimedOut, ended.State);
        Assert.AreEqual(ManualFocusReasonCodes.ObserverLost, ended.EndReason);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SafetyTimeout_EndsTheSessionWithItsLabelledReason()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var session = await coordinator.StartAsync(new(Gain20, 560, SafetyTimeout: TimeSpan.FromMilliseconds(500)), "alice",
            CancellationToken.None).ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(coordinator).ConfigureAwait(false);

        Assert.AreEqual(session.StartedUtc + TimeSpan.FromMilliseconds(500), session.SafetyDeadlineUtc);
        Assert.AreEqual(ManualFocusSessionState.TimedOut, ended.State);
        Assert.AreEqual(ManualFocusReasonCodes.SafetyTimeout, ended.EndReason);
        // Timer expiry and the wall clock are read separately; allow the timer's coarse resolution.
        Assert.IsTrue(ended.EndedUtc >= session.SafetyDeadlineUtc - TimeSpan.FromMilliseconds(50),
            $"ended {ended.EndedUtc:O}, deadline {session.SafetyDeadlineUtc:O}");
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task SampleDeadline_CancelsAStuckPreviewAndRepeatedFailuresFaultTheSession()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        module.BeforePreview = static (_, token) => Task.Delay(Timeout.Infinite, token);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System,
            Limits(sampleDeadlineGrace: TimeSpan.FromMilliseconds(150)));

        await coordinator.StartAsync(new(new(TimeSpan.FromMilliseconds(10), 20)), "alice", CancellationToken.None)
            .ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(coordinator).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Faulted, ended.State);
        Assert.AreEqual(ManualFocusReasonCodes.RepeatedFailures, ended.EndReason);
        Assert.AreEqual(ManualFocusReasonCodes.SampleDeadlineExceeded, ended.LastFailure);
        Assert.AreEqual(0, ended.TotalSamples, "A timed-out exposure never becomes a sample.");
        Assert.AreEqual(ManualFocusSessionLimits.Default.MaximumConsecutiveFailures, module.CancelledPreviews);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        Assert.IsTrue(await fixture.Ownership.RevokeAsync(TimeSpan.Zero).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task TransientPreviewFailure_IsReportedAndTheLoopRecovers()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        module.BeforePreview = async (number, _) =>
        {
            if (number == 2)
            {
                failed.TrySetResult();
                throw new IOException("sensor readout glitch");
            }
            if (number == 3)
            {
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
        };
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var session = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var reported = await FocusWait.UntilAsync(coordinator, static s => s.LastFailure is not null, "the failure").ConfigureAwait(false);
        var recovered = await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 2, "recovery").ConfigureAwait(false);
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("IOException: sensor readout glitch", reported.LastFailure);
        Assert.AreEqual(ManualFocusSessionState.Running, recovered.State);
        Assert.IsNull(recovered.LastFailure, "A good sample clears the reported failure.");
    }

    [TestMethod]
    public async Task Stop_CancelsTheInFlightExposureImmediatelyAndReleasesTheCamera()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        module.BeforePreview = static (_, token) => Task.Delay(Timeout.Infinite, token);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var session = await coordinator.StartAsync(new(new(TimeSpan.FromSeconds(60), 20)), "alice", CancellationToken.None)
            .ConfigureAwait(false);
        await module.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false),
            "A normal capture must not be admitted while a preview holds the camera.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stopped = await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        watch.Stop();

        Assert.AreEqual(ManualFocusSessionState.Stopped, stopped.State);
        Assert.AreEqual(ManualFocusReasonCodes.StoppedByOperator, stopped.EndReason);
        Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed, "Stop must not wait for the 60 s exposure.");
        Assert.AreEqual(1, module.CancelledPreviews);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        Assert.IsTrue(await fixture.Ownership.RevokeAsync(TimeSpan.Zero).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Revocation_EndsTheSessionAsCameraWithdrawnAndDrainsItsLease()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        module.BeforePreview = static (_, token) => Task.Delay(Timeout.Infinite, token);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        await coordinator.StartAsync(new(new(TimeSpan.FromSeconds(60), 20)), "alice", CancellationToken.None).ConfigureAwait(false);
        await module.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var drained = await fixture.Ownership.RevokeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var ended = await FocusWait.EndedAsync(coordinator).ConfigureAwait(false);

        Assert.IsTrue(drained, "Revocation must cancel the preview and see its lease released before the module closes.");
        Assert.AreEqual(ManualFocusSessionState.Faulted, ended.State);
        Assert.AreEqual(ManualFocusReasonCodes.CameraWithdrawn, ended.EndReason);
        Assert.AreEqual(ManualFocusReasonCodes.CameraWithdrawnMessage, ended.LastFailure);
        Assert.IsFalse(coordinator.Availability.Available);
        Assert.AreEqual(ManualFocusReasonCodes.NoModule, coordinator.Availability.Reason);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task PausedAdmission_StaysPausedAcrossPreviewsAndAfterStop()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        await fixture.Admission.PauseAsync("focus-pause", null, "alice", "focusing", CancellationToken.None).ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var session = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        var sampled = await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 2, "two samples").ConfigureAwait(false);
        var pausedDuring = fixture.Admission.Snapshot.State;
        await coordinator.StopAsync(session.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(FocusStarStatus.Valid, sampled.Latest!.Measurement.Status, "Focusing works while capture is paused.");
        Assert.AreEqual(CaptureAdmissionState.Paused, pausedDuring);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Admission.Snapshot.State);
        Assert.IsFalse(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false),
            "A preview must never reopen a paused admission gate.");
        await fixture.Admission.ResumeAsync("focus-resume", null, "alice", "done", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Preview_WaitsForAnAdmittedCaptureAndACaptureWaitsForThePreview()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        module.BeforePreview = (_, token) => release.Task.WaitAsync(token);

        var capture = await fixture.EnterCaptureAsync(CancellationToken.None).ConfigureAwait(false);
        var preview = fixture.Source.AcquireAsync(Gain20, 560, CancellationToken.None);
        await Task.Delay(200).ConfigureAwait(false);
        var startedWhileCaptureHeld = module.Previews;
        capture.MarkNoPublicationRequired();
        capture.Dispose();
        await module.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var admittedDuringPreview = await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        release.TrySetResult();
        var result = await preview.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        Assert.AreEqual(0, startedWhileCaptureHeld, "A preview must wait for the in-flight capture to drain.");
        Assert.IsFalse(admittedDuringPreview, "A capture must not be admitted while the preview runs.");
        Assert.IsTrue(await fixture.CaptureAdmittedWithinAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false));
        Assert.AreEqual("true", result.Frame.Metadata.Extra!["focusPreview"]);
        Assert.AreEqual(1, result.ModuleGeneration);
        Assert.IsNotNull(result.ImageCircle, "The owner's optics bound the measurable sky.");
    }

    [TestMethod]
    public async Task FailClosedAdmission_RefusesNewSessionsAndFaultsARunningOne()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());
        await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 1, "a sample").ConfigureAwait(false);

        fixture.Admission.FailClosed();
        var ended = await FocusWait.EndedAsync(coordinator).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Faulted, ended.State);
        Assert.AreEqual(ManualFocusReasonCodes.AdmissionUnavailable, ended.EndReason);
        Assert.AreEqual(ManualFocusReasonCodes.AdmissionFailClosed, ended.LastFailure);
        Assert.AreEqual(ManualFocusReasonCodes.AdmissionFailClosed, coordinator.Availability.Reason);
        var refused = await Assert.ThrowsExactlyAsync<ManualFocusSessionUnavailableException>(() =>
            coordinator.StartAsync(new(Gain20), "alice", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ManualFocusReasonCodes.AdmissionFailClosed, refused.Message);
    }

    [TestMethod]
    public async Task NoModuleOrAModuleWithoutPreview_IsUnavailableWithAnHonestReason()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());

        var none = coordinator.Availability;
        var noneStart = await Assert.ThrowsExactlyAsync<ManualFocusSessionUnavailableException>(() =>
            coordinator.StartAsync(new(Gain20), "alice", CancellationToken.None)).ConfigureAwait(false);
        fixture.Publish(new CaptureOnlyModule(), null);
        var captureOnly = coordinator.Availability;

        Assert.IsFalse(none.Available);
        Assert.AreEqual(ManualFocusReasonCodes.NoModule, none.Reason);
        Assert.AreEqual(ManualFocusReasonCodes.NoModule, noneStart.Message);
        Assert.IsFalse(captureOnly.Available);
        Assert.AreEqual(ManualFocusReasonCodes.ModuleWithoutPreview("CaptureOnly"), captureOnly.Reason);
        Assert.AreEqual(ManualFocusSessionState.Idle, coordinator.Snapshot.State);
    }

    [TestMethod]
    public async Task HostStop_EndsTheRunningSessionBeforeTheOwnerRevokesTheModule()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        var module = await fixture.PublishScriptedAsync().ConfigureAwait(false);
        module.BeforePreview = static (_, token) => Task.Delay(Timeout.Infinite, token);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());
        await coordinator.StartAsync(new(new(TimeSpan.FromSeconds(60), 20)), "alice", CancellationToken.None).ConfigureAwait(false);
        await module.PreviewStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await ((IHostedService)coordinator).StopAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Stopped, coordinator.Snapshot.State);
        Assert.AreEqual(ManualFocusReasonCodes.HostStopping, coordinator.Snapshot.EndReason);
        Assert.IsTrue(await fixture.Ownership.RevokeAsync(TimeSpan.Zero).ConfigureAwait(false),
            "The preview lease must already be released when the capture owner revokes the module.");
    }

    [TestMethod]
    public async Task SaveAndDiscard_RetainOnlyWhatTheOwnerChoosesAndARestartResumesNothing()
    {
        var fixture = await FocusOwnerFixture.CreateAsync().ConfigureAwait(false);
        await using var fixtureScope = fixture.ConfigureAwait(false);
        await fixture.PublishVirtualSkyAsync().ConfigureAwait(false);
        using var store = new ManualFocusSessionStore(fixture.Root);
        using var coordinator = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits(), store);

        var kept = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 2, "two samples").ConfigureAwait(false);
        var ended = await coordinator.StopAsync(kept.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        var saved = await coordinator.SaveAsync(kept.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionStateException>(() =>
            coordinator.SaveAsync(kept.SessionId, "alice", CancellationToken.None)).ConfigureAwait(false);
        var afterDiscardAttempt = coordinator.Discard(kept.SessionId, "alice");
        var content = await store.ReadAsync(saved.SavedRecordId!, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusRetentionState.InMemoryOnly, ended.Retention);
        Assert.AreEqual(ManualFocusRetentionState.Saved, saved.Retention);
        Assert.AreEqual(ManualFocusRetentionState.Saved, afterDiscardAttempt.Retention, "A saved record is not discarded.");
        Assert.IsTrue(content!.Summary.Verified);
        Assert.AreEqual(ended.TotalSamples, content.Record.Session.TotalSamples);
        CollectionAssert.AreEqual(ended.History.Select(static s => s.Provenance.FrameSha256).ToArray(),
            content.Record.Session.History.Select(static s => s.Provenance.FrameSha256).ToArray());
        Assert.AreEqual("alice", content.Record.SavedBy);
        Assert.IsFalse(content.Summary.QualifiesPhysicalFocus);

        var dropped = await coordinator.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        await FocusWait.UntilAsync(coordinator, static s => s.TotalSamples >= 1, "a sample").ConfigureAwait(false);
        await coordinator.StopAsync(dropped.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        var discarded = coordinator.Discard(dropped.SessionId, "alice");
        await Assert.ThrowsExactlyAsync<ManualFocusSessionStateException>(() =>
            coordinator.SaveAsync(dropped.SessionId, "alice", CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusRetentionState.Discarded, discarded.Retention);
        Assert.IsEmpty(discarded.History);
        Assert.IsNull(discarded.Best);
        Assert.IsNull(coordinator.LatestImages);
        Assert.HasCount(1, await store.ListAsync(10, CancellationToken.None).ConfigureAwait(false));

        using var restarted = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits(), store);
        Assert.AreEqual(ManualFocusSessionState.Idle, restarted.Snapshot.State, "No session resumes after a restart.");

        using var unretained = new ManualFocusSessionCoordinator(fixture.Source, TimeProvider.System, Limits());
        var transient = await unretained.StartAsync(new(Gain20, 560), "alice", CancellationToken.None).ConfigureAwait(false);
        await unretained.StopAsync(transient.SessionId, "alice", CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(unretained.RetentionAvailable);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionUnavailableException>(() =>
            unretained.SaveAsync(transient.SessionId, "alice", CancellationToken.None)).ConfigureAwait(false);
    }
}
