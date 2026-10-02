using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
public sealed class ManualFocusSessionCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 4, 0, 0, TimeSpan.Zero);
    private static readonly ManualFocusPreviewSettings Settings = new(TimeSpan.FromSeconds(1), 110);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [TestMethod]
    public async Task UnavailableSource_StartThrowsUnavailableWithReason()
    {
        var frameSource = new FakePreviewFrameSource { Available = false, Reason = "no-live-preview" };
        using var coordinator = Create(frameSource);

        var exception = await Assert.ThrowsExactlyAsync<ManualFocusSessionUnavailableException>(() =>
            coordinator.StartAsync(Request(), "operator", CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual("no-live-preview", exception.Message);
        Assert.AreEqual(ManualFocusSessionState.Idle, coordinator.GetSnapshot().State);
    }

    [TestMethod]
    public async Task Start_WhenSessionActive_ThrowsConflictWithoutTouchingAcquisition()
    {
        var acquisition = new FakeExclusiveAcquisition();
        using var coordinator = Create(new FakePreviewFrameSource(), acquisition);
        var first = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() =>
            coordinator.StartAsync(Request(), "operator", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ManualFocusSessionState.Active, coordinator.GetSnapshot().State);
        Assert.AreEqual(first.SessionId, coordinator.GetSnapshot().SessionId);
        Assert.AreEqual(0, acquisition.Invocations);
    }

    [TestMethod]
    public async Task Sample_MeasuresFrameWithProvenanceAndHoldsExclusiveAcquisition()
    {
        var acquisition = new FakeExclusiveAcquisition();
        var frameSource = new FakePreviewFrameSource();
        using var coordinator = Create(frameSource, acquisition);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);

        var sample = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(FocusSharpnessStatus.Valid, sample.Measurement.Status);
        Assert.IsTrue(sample.Measurement.MedianFwhmPixels > 0);
        Assert.AreEqual(1, acquisition.Invocations, "One preview must hold the exclusive acquisition exactly once.");
        Assert.AreEqual(1, frameSource.AcquireCount);
        Assert.AreEqual("frame-1", sample.Provenance.FrameId);
        Assert.AreEqual(FocusSharpnessMeasurement.MetricDefinition, sample.Provenance.MetricDefinition);
        Assert.AreEqual(FocusSharpnessMeasurement.Units, sample.Provenance.MetricUnits);
        Assert.AreEqual(FocusSharpnessAnalyzer.AlgorithmVersion, sample.Provenance.MetricAlgorithmVersion);
        Assert.AreEqual(64, sample.Provenance.MetricSettingsIdentitySha256.Length);
        Assert.AreEqual(64, sample.Provenance.PreviewSettingsIdentitySha256.Length);
        Assert.AreEqual(0, sample.Index);
        Assert.AreEqual(ManualFocusSessionState.Active, coordinator.GetSnapshot().State);
        Assert.HasCount(1, coordinator.GetSnapshot().Samples);
    }

    [TestMethod]
    public async Task Sample_NoStarFrame_ReturnsExplicitNoStarsWithoutAValue()
    {
        var frameSource = new FakePreviewFrameSource { FrameFactory = () => FlatFrame(160, 96) };
        using var coordinator = Create(frameSource);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);

        var sample = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(FocusSharpnessStatus.NoStars, sample.Measurement.Status);
        Assert.IsNull(sample.Measurement.MedianFwhmPixels);
        Assert.AreEqual(ManualFocusSessionState.Active, coordinator.GetSnapshot().State);
    }

    [TestMethod]
    public async Task Sample_SaturatedFrame_ReturnsExplicitSaturatedWithoutAValue()
    {
        var frameSource = new FakePreviewFrameSource
        {
            FrameFactory = () =>
            {
                var frame = StarFrame(160, 96, 1.0);
                return frame with { SaturatedMask = Enumerable.Repeat(true, frame.Pixels.Length).ToArray() };
            }
        };
        using var coordinator = Create(frameSource);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);

        var sample = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(FocusSharpnessStatus.Saturated, sample.Measurement.Status);
        Assert.IsNull(sample.Measurement.MedianFwhmPixels);
    }

    [TestMethod]
    public async Task Timeout_ReclaimsSessionAndClearsSamples()
    {
        var time = new MutableTimeProvider(Start);
        var frameSource = new FakePreviewFrameSource();
        using var coordinator = Create(frameSource, timeProvider: time);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);
        _ = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        time.Now = Start + Timeout + TimeSpan.FromSeconds(1);
        var snapshot = coordinator.GetSnapshot();

        Assert.AreEqual(ManualFocusSessionState.TimedOut, snapshot.State);
        Assert.IsEmpty(snapshot.Samples);
        Assert.AreEqual(ManualFocusReasonCodes.TimedOut, snapshot.EndReason);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionStateException>(() =>
            coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Cancel_EndsSessionWithoutRetainingSamples()
    {
        var frameSource = new FakePreviewFrameSource();
        using var coordinator = Create(frameSource);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);
        _ = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        var snapshot = await coordinator.CancelAsync(
            session.SessionId, "operator", "operator cancelled", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Cancelled, snapshot.State);
        Assert.IsEmpty(snapshot.Samples);
        Assert.AreEqual("operator cancelled", snapshot.EndReason);
    }

    [TestMethod]
    public async Task End_RetainedKeepsSamplesAndDiscardedClearsThem()
    {
        using var retained = Create(new FakePreviewFrameSource());
        var retainedSession = await retained.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);
        _ = await retained.SampleAsync(retainedSession.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);
        var completed = await retained.EndAsync(
            retainedSession.SessionId, "operator", ManualFocusSessionDisposition.Retained, null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Completed, completed.State);
        Assert.HasCount(1, completed.Samples);
        Assert.AreEqual(ManualFocusReasonCodes.Retained, completed.EndReason);

        using var discarded = Create(new FakePreviewFrameSource());
        var discardedSession = await discarded.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);
        _ = await discarded.SampleAsync(discardedSession.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);
        var cancelled = await discarded.EndAsync(
            discardedSession.SessionId, "operator", ManualFocusSessionDisposition.Discarded, null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ManualFocusSessionState.Cancelled, cancelled.State);
        Assert.IsEmpty(cancelled.Samples);
        Assert.AreEqual(ManualFocusReasonCodes.Discarded, cancelled.EndReason);
    }

    [TestMethod]
    public void Restart_HasNoRetainedSession()
    {
        using var restarted = Create(new FakePreviewFrameSource());

        var snapshot = restarted.GetSnapshot();

        Assert.AreEqual(ManualFocusSessionState.Idle, snapshot.State);
        Assert.IsEmpty(snapshot.Samples);
        Assert.AreEqual(string.Empty, snapshot.SessionId);
    }

    [TestMethod]
    public async Task CameraBusy_OnExclusiveConflict_RecordsReasonAndKeepsSessionActive()
    {
        var acquisition = new FakeExclusiveAcquisition { ThrowConflict = true };
        using var coordinator = Create(new FakePreviewFrameSource(), acquisition);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ManualFocusSessionConflictException>(() =>
            coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None)).ConfigureAwait(false);
        var snapshot = coordinator.GetSnapshot();

        Assert.AreEqual(ManualFocusSessionState.Active, snapshot.State);
        Assert.AreEqual(ManualFocusReasonCodes.CameraBusy, snapshot.FailureReason);
        Assert.IsEmpty(snapshot.Samples);
    }

    [TestMethod]
    public async Task Region_IsCroppedAndRecordedInProvenance()
    {
        var frameSource = new FakePreviewFrameSource();
        using var coordinator = Create(frameSource);
        var region = new ManualFocusRegion(40, 20, 80, 64);
        var session = await coordinator.StartAsync(Request(region), "operator", CancellationToken.None).ConfigureAwait(false);

        var sample = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(region, sample.Provenance.Region);
        Assert.AreEqual(80, sample.Provenance.FrameWidth);
        Assert.AreEqual(64, sample.Provenance.FrameHeight);
    }

    [TestMethod]
    public async Task SampleLimit_IsEnforced()
    {
        var limits = ManualFocusSessionLimits.Default with { MaximumSamples = 1 };
        using var coordinator = Create(new FakePreviewFrameSource(), limits: limits);
        var session = await coordinator.StartAsync(Request(), "operator", CancellationToken.None).ConfigureAwait(false);
        _ = await coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None).ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.SampleAsync(session.SessionId, "operator", CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Validation_RejectsOutOfRangeRequestsAndActors()
    {
        using var coordinator = Create(new FakePreviewFrameSource());

        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(
                Request() with { Settings = new(TimeSpan.FromHours(1), 110) },
                "operator",
                CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(
                Request() with { Settings = new(TimeSpan.FromSeconds(1), -1) },
                "operator",
                CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(
                Request() with { SessionTimeout = TimeSpan.FromSeconds(1) },
                "operator",
                CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(Request(), " ", CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ManualFocusSessionValidationException>(() =>
            coordinator.StartAsync(
                Request(new ManualFocusRegion(0, 0, 4, 4)),
                "operator",
                CancellationToken.None)).ConfigureAwait(false);
    }

    private static ManualFocusSessionRequest Request(ManualFocusRegion? region = null)
        => new(Settings, region, Timeout);

    private static ManualFocusSessionCoordinator Create(
        IManualFocusPreviewFrameSource frameSource,
        FakeExclusiveAcquisition? acquisition = null,
        TimeProvider? timeProvider = null,
        ManualFocusSessionLimits? limits = null)
        => new(
            acquisition ?? new FakeExclusiveAcquisition(),
            frameSource,
            timeProvider ?? new MutableTimeProvider(Start),
            limits);

    private static ManualFocusPreviewFrame StarFrame(int width, int height, double sigma)
    {
        var pixels = Enumerable.Repeat(100.0, width * height).ToArray();
        AddGaussian(pixels, width, 30.5, 30.5, sigma, sigma, 1000);
        AddGaussian(pixels, width, 80.5, 30.5, sigma, sigma, 1000);
        AddGaussian(pixels, width, 130.5, 30.5, sigma, sigma, 1000);
        return new ManualFocusPreviewFrame(
            "frame-1", width, height, pixels, Enumerable.Repeat(true, pixels.Length).ToArray(), []);
    }

    private static ManualFocusPreviewFrame FlatFrame(int width, int height)
    {
        var pixels = Enumerable.Repeat(100.0, width * height).ToArray();
        return new ManualFocusPreviewFrame(
            "frame-1", width, height, pixels, Enumerable.Repeat(true, pixels.Length).ToArray(), []);
    }

    private static void AddGaussian(double[] pixels, int width, double centerX, double centerY, double sigmaX, double sigmaY, double peak)
    {
        for (var index = 0; index < pixels.Length; index++)
        {
            var dx = (index % width + 0.5 - centerX) / sigmaX;
            var dy = (index / width + 0.5 - centerY) / sigmaY;
            pixels[index] += peak * Math.Exp(-0.5 * (dx * dx + dy * dy));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeExclusiveAcquisition : IManualFocusExclusiveAcquisition
    {
        private int _inUse;

        public int Invocations;
        public bool ThrowConflict;

        public async Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken)
        {
            if (ThrowConflict || Interlocked.Exchange(ref _inUse, 1) == 1)
            {
                throw new ManualFocusSessionConflictException("The camera is busy with another exclusive operation.");
            }
            Interlocked.Increment(ref Invocations);
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _inUse, 0);
            }
        }
    }

    private sealed class FakePreviewFrameSource : IManualFocusPreviewFrameSource
    {
        public bool Available { get; set; } = true;
        public string Reason { get; set; } = "unavailable";
        public Func<ManualFocusPreviewFrame> FrameFactory { get; set; } = () => StarFrame(160, 96, 1.0);
        public int AcquireCount { get; private set; }

        public ManualFocusSessionAvailability GetAvailability()
            => Available ? new(true, string.Empty) : ManualFocusSessionAvailability.Unavailable(Reason);

        public ValueTask<ManualFocusPreviewFrame> AcquireAsync(
            ManualFocusPreviewSettings settings,
            ManualFocusRegion? region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            return ValueTask.FromResult(FrameFactory());
        }
    }
}
