using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(200_000_000)]
    [DataRow(0)]
    [DataRow(10_000)]
    [DataRow(1)]
    public async Task CaptureAsync_WaitsForEffectiveExposureAndReportsOperationalTiming(int exposureTicks)
    {
        var clock = new ExposureTestTimeProvider(FixtureUtc);
        var staging = new RecordingProjectedSceneStagingStore();
        var module = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]),
            new ProjectedSceneStore(), stagingStore: staging);
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Mono16, 64, 48) with
        {
            Pipeline = new([new("ProjectedScene", DependsOn: ["$raw"])])
        }, CancellationToken.None).ConfigureAwait(false);
        var exposure = TimeSpan.FromTicks(exposureTicks);
        // Requested/celestial time may differ from the operational clock.
        var request = new CaptureRequest(FixtureUtc.AddMinutes(-1), TimeSpan.FromSeconds(25), CaptureMode.Still,
            new(exposure, 1, null, null));
        var capture = module.CaptureAsync(request, CancellationToken.None);
        if (exposureTicks > 0)
        {
            Assert.IsFalse(capture.IsCompleted);
            Assert.IsEmpty(staging.Stages);
            var waitDuration = TimeSpan.FromMilliseconds(Math.Ceiling(exposure.TotalMilliseconds));
            clock.Advance(waitDuration - TimeSpan.FromTicks(1));
            Assert.IsFalse(capture.IsCompleted, "An exposure cannot publish before its complete duration.");
            clock.Advance(TimeSpan.FromTicks(1));
        }
        var result = await capture.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var timing = result.AcquisitionTiming!;
        Assert.AreEqual(FixtureUtc, timing.ExposureStartedUtc);
        Assert.AreEqual(exposure, timing.ExposureEndedUtc - timing.ExposureStartedUtc);
        Assert.IsTrue(timing.ReadoutCompletedUtc >= timing.ExposureEndedUtc);
        Assert.IsTrue(timing.ReadoutCompletedUtc <= clock.GetUtcNow());
        Assert.AreEqual(exposure, result.Frame!.Metadata.Exposure);
        Assert.AreEqual(request.RequestedStartUtc, result.Frame.Metadata.Scene!.VirtualExposure!.CelestialStartUtc);
        Assert.AreEqual(exposure, result.Frame.Metadata.Scene.VirtualExposure.CelestialEndUtc -
            result.Frame.Metadata.Scene.VirtualExposure.CelestialStartUtc);
        Assert.HasCount(1, staging.Stages);
    }

    [TestMethod]
    public async Task CaptureAsync_CancelledExposureProducesNoFrameOrStagedScene()
    {
        var clock = new ExposureTestTimeProvider(FixtureUtc);
        var staging = new RecordingProjectedSceneStagingStore();
        var module = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]),
            new ProjectedSceneStore(), stagingStore: staging);
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Mono16, 64, 48) with
        {
            Pipeline = new([new("ProjectedScene", DependsOn: ["$raw"])])
        }, CancellationToken.None).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var capture = module.CaptureAsync(new(FixtureUtc, TimeSpan.FromSeconds(20), CaptureMode.Still,
            new(TimeSpan.FromSeconds(20), 1, null, null)), cancellation.Token);
        clock.Advance(TimeSpan.FromSeconds(7));
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await capture.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual(0, clock.ActiveTimers);
        Assert.IsEmpty(staging.Stages);
        Assert.IsEmpty(staging.DeletedKeys);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsTrue(capture.IsCanceled);
    }

    [TestMethod]
    public async Task FocusPreview_ModelsExposureWithoutCreatingAnAcquisitionTimer()
    {
        var clock = new ExposureTestTimeProvider(FixtureUtc);
        var module = new VirtualSkyCameraModule(clock, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Mono16, 64, 48), CancellationToken.None).ConfigureAwait(false);
        var result = await module.CaptureFocusPreviewAsync(new(new(FixtureUtc, TimeSpan.FromSeconds(20), CaptureMode.Still,
            new(TimeSpan.FromSeconds(20), 1, null, null)), null), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(clock.TimerCreated.Task.IsCompleted);
        Assert.AreEqual(TimeSpan.FromSeconds(20), result.Frame!.Metadata.Exposure);
        Assert.AreEqual(FixtureUtc, result.AcquisitionTiming!.ReadoutCompletedUtc);
    }
}
