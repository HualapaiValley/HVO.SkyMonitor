using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class VirtualSkyCameraModuleTests
{
    [TestMethod]
    public async Task CaptureAsync_WithMono16Profile_ProducesDeterministicFrame()
    {
        var module = new VirtualSkyCameraModule(TimeProvider.System);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still);

        var first = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var second = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraPixelFormat.Mono16, first.Frame!.PixelFormat);
        Assert.AreEqual(8, first.Frame.PixelData.Length);
        CollectionAssert.AreEqual(first.Frame.PixelData.ToArray(), second.Frame!.PixelData.ToArray());
    }

    [TestMethod]
    public async Task CaptureAsync_WithRgb24Profile_ProducesRgbFrame()
    {
        var module = new VirtualSkyCameraModule(TimeProvider.System);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Rgb24), CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(CameraPixelFormat.Rgb24, result.Frame!.PixelFormat);
        Assert.AreEqual(12, result.Frame.PixelData.Length);
    }

    [TestMethod]
    public async Task CaptureAsync_WhenTimeAdvances_MovesStarGeometry()
    {
        var module = new VirtualSkyCameraModule(TimeProvider.System);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);

        var first = await module.CaptureAsync(new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
        var later = await module.CaptureAsync(new CaptureRequest(DateTimeOffset.UnixEpoch.AddMinutes(1), TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreNotEqual(first.Frame!.PixelData.ToArray(), later.Frame!.PixelData.ToArray());
    }

    [TestMethod]
    public async Task CaptureAsync_WithFullAsi174Profile_ProducesCanonicalMono16Frame()
    {
        var module = new VirtualSkyCameraModule(TimeProvider.System);
        await module.InitializeAsync(CreateConfig(CameraPixelFormat.Mono16, 1936, 1216), CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1936, result.Frame!.Width);
        Assert.AreEqual(1216, result.Frame.Height);
        Assert.AreEqual(1936 * 1216 * 2, result.Frame.PixelData.Length);
    }

    [TestMethod]
    public async Task CaptureAsync_WithRequestedSetpoint_ReportsAndAppliesSetpoint()
    {
        var module = new VirtualSkyCameraModule(TimeProvider.System);
        await module.InitializeAsync(CreateConfig(), CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(2), 3, null, null);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(setpoint, result.NextSetpoint);
        Assert.AreEqual(setpoint.Exposure, result.Frame!.Metadata.Exposure);
        Assert.AreEqual(setpoint.Gain, result.Frame.Metadata.Gain);
    }

    private static CameraModuleConfig CreateConfig(CameraPixelFormat format = CameraPixelFormat.Mono16, int width = 2, int height = 2) => new(
        new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
        new CameraRigConfig(new SensorProfile("Virtual", width, height, 5.86, format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color, format),
            new OpticsProfile("EquidistantFisheye", 3, 180, 0), new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)));
}
