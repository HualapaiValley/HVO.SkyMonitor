using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Modules.RandomImage;
using Microsoft.Extensions.Time.Testing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Modules;

[TestClass]
public sealed class RandomImageCameraModuleTests
{
    [TestMethod]
    public async Task CaptureAsync_ReturnsFrameWithExpectedDimensions()
    {
        using var specific = JsonDocument.Parse("{\"pattern\":\"Gradient\"}");
        var moduleDescriptor = new CameraModuleDescriptor(
            typeof(RandomImageCameraModule).FullName ?? "RandomImage",
            specific.RootElement.Clone());
        var config = CreateConfig(moduleDescriptor, BuildRig(CreateEnvelope()));

        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        await using var module = new RandomImageCameraModule(timeProvider);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Frame);
        Assert.AreEqual(64, result.Frame.Width);
        Assert.AreEqual(48, result.Frame.Height);
        Assert.AreEqual(CaptureMode.Still, result.Mode);
    }

    [TestMethod]
    public async Task CaptureAsync_UsesEnvelopeDefaults()
    {
        using var specific = JsonDocument.Parse("{\"pattern\":\"Noise\"}");
        var moduleDescriptor = new CameraModuleDescriptor(
            typeof(RandomImageCameraModule).FullName ?? "RandomImage",
            specific.RootElement.Clone());
        var config = CreateConfig(moduleDescriptor, BuildRig(CreateEnvelope()));

        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        await using var module = new RandomImageCameraModule(timeProvider);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), result.NextSetpoint.Exposure);
        Assert.AreEqual(2.0d, result.NextSetpoint.Gain);
    }

    [TestMethod]
    public async Task CaptureAsync_FallsBackToPipelineDefaultsWhenEnvelopeMissing()
    {
        using var specific = JsonDocument.Parse("{\"pattern\":\"Noise\"}");
        var moduleDescriptor = new CameraModuleDescriptor(
            typeof(RandomImageCameraModule).FullName ?? "RandomImage",
            specific.RootElement.Clone());
        var rig = new CameraRigConfig(
            new SensorProfile("Sensor", 64, 48, 3.2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
            new OpticsProfile("Equidistant", 2.8, 180, 0),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2),
                5.0d,
                25.0d,
                Envelope: null));
        var config = CreateConfig(moduleDescriptor, rig);

        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        await using var module = new RandomImageCameraModule(timeProvider);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), result.NextSetpoint.Exposure);
        Assert.AreEqual(5.0d, result.NextSetpoint.Gain);
    }

    [TestMethod]
    public async Task CaptureAsync_HonorsControlPolicyTemperature()
    {
        using var specific = JsonDocument.Parse("{\"pattern\":\"Noise\"}");
        var moduleDescriptor = new CameraModuleDescriptor(
            typeof(RandomImageCameraModule).FullName ?? "RandomImage",
            specific.RootElement.Clone());
        var controlPolicy = new CameraControlPolicy
        {
            Temperature = new TemperatureControlDirective
            {
                Mode = TemperatureControlMode.Target,
                TargetC = -15.0
            }
        };
        var config = CreateConfig(moduleDescriptor, BuildRig(CreateEnvelope(), controlPolicy));

        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture));
        await using var module = new RandomImageCameraModule(timeProvider);
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var result = await module.CaptureAsync(
            new CaptureRequest(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CaptureMode.Still),
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsNotNull(result?.Frame);
        Assert.AreEqual(-15.0, result.Frame!.Metadata.TemperatureC, 1e-6);
    }

    private static CameraModuleConfig CreateConfig(CameraModuleDescriptor descriptor, CameraRigConfig rig) => new(
        new ObservatoryLocation(21.3, -157.8, 400, "Pacific/Honolulu"),
        descriptor,
        rig,
        Array.Empty<CaptureProcessingStepConfig>());

    private static CameraRigConfig BuildRig(ExposureEnvelope? envelope, CameraControlPolicy? controlPolicy = null) => new(
        new SensorProfile("Sensor", 64, 48, 3.2, SensorColorMode.Mono, CameraPixelFormat.Mono8),
        new OpticsProfile("Equidistant", 2.8, 180, 0),
        new RigOrientation(90, 0, 0),
        new PipelineExposureProfile(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), 1, 10, envelope),
        controlPolicy);

    private static ExposureEnvelope CreateEnvelope() => new(
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromSeconds(10),
        1.0d,
        200.0d,
        new ExposureDefaults(TimeSpan.FromMilliseconds(100), 2.0d),
        new ExposureDefaults(TimeSpan.FromSeconds(1), 20.0d),
        0.65d);
}
