using HVO.SkyMonitor.AgentCore;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task CenteredVirtualSkyOrdinaryBoundCaptureRetainsQualifiedFullFrameLayout(CameraPixelFormat format)
    {
        var baseline = format == CameraPixelFormat.Mono16
            ? CreateAsi174Config(64, 64)
            : CreateConfig(format, 64, 64) with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    asi178Sensor = new { enabled = true, blackLevelContainerAdu = 63 }
                }))
            };
        var config = baseline with
        {
            AgentId = "virtual-fixture",
            CenteredExposure = new("virtual-one", CenteredExposureProfile.CurrentExecutionVersion)
        };
        var module = CreateModule(FixtureUtc, new ExposureTestTimeProvider(FixtureUtc, autoAdvance: true));
        await using var captureModule = module.ConfigureAwait(false);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var attempt = new ExposureAttemptKey(new(config.AgentId!, "virtual-one"), Guid.NewGuid(), 101, Guid.NewGuid());
        var request = new CaptureRequest(FixtureUtc.AddMilliseconds(-1), TimeSpan.FromSeconds(1), CaptureMode.Still,
            new(TimeSpan.FromMilliseconds(20), 10, null, null));
        var result = await module.CaptureBoundAsync(attempt, request, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        Assert.IsNotNull(result.Frame.Layout);
        Assert.IsNotNull(result.AcquisitionTiming);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), result.AcquisitionTiming.ExposureStartedUtc - request.RequestedStartUtc);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20),
            result.AcquisitionTiming.ExposureEndedUtc - result.AcquisitionTiming.ExposureStartedUtc);
        var virtualExposure = result.Frame.Metadata.Scene!.VirtualExposure!;
        Assert.AreEqual(request.RequestedStartUtc, virtualExposure.RequestedStartUtc);
        Assert.AreEqual(request.RequestedStartUtc, virtualExposure.ScenarioStartUtc);
        Assert.AreEqual(virtualExposure.CelestialMidpointUtc, result.Frame.Metadata.Scene.SceneUtc);
        Assert.AreEqual(format, result.Frame.Layout.PixelFormat);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), result.Frame.Metadata.Exposure);
        Assert.AreEqual(10d, result.Frame.Metadata.Gain);
        Assert.IsTrue(CenteredLinearQualification.TryResolveLevels(result.Frame.Layout, out _, out _, out var reason), reason);
        Assert.AreEqual(64, result.Frame.Layout.Width);
        Assert.AreEqual(128, result.Frame.Layout.StrideBytes);
        Assert.AreEqual(format == CameraPixelFormat.BayerRggb16 ? 0 : (int?)null, result.Frame.Layout.Readout!.CfaOriginX);
        Assert.AreEqual(format == CameraPixelFormat.BayerRggb16 ? 0 : (int?)null, result.Frame.Layout.Readout.CfaOriginY);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            module.CaptureBoundAsync(attempt, request, CancellationToken.None)).ConfigureAwait(false);
    }
}
