using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class RetentionBackgroundServiceTests
{
    [TestMethod]
    public async Task ApplyRetentionAsync_RemovesOnlyExpiredArtifactDates()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-retention", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frames", "2020", "01", "01", "Raw"));
            Directory.CreateDirectory(Path.Combine(root, "frames", "2026", "07", "11", "Raw"));
            await File.WriteAllTextAsync(Path.Combine(root, "frames", "2020", "01", "01", "Raw", "expired.bin"), "expired").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "frames", "2026", "07", "11", "Raw", "current.bin"), "current").ConfigureAwait(false);
            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero));
            var service = new RetentionBackgroundService(
                new StubConfigurationAccessor(),
                Options.Create(new CameraAgentHostOptions()), timeProvider, NullLogger<RetentionBackgroundService>.Instance);

            await service.ApplyRetentionAsync(CreateConfig(root), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(Path.Combine(root, "frames", "2020", "01", "01", "Raw", "expired.bin")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "frames", "2026", "07", "11", "Raw", "current.bin")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CameraModuleConfig CreateConfig(string root)
    {
        using var options = System.Text.Json.JsonDocument.Parse($"{{\"storageRoot\":\"{root.Replace("\\", "\\\\", StringComparison.Ordinal)}\",\"retentionDays\":7}}");
        return new CameraModuleConfig(new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(new SensorProfile("Virtual", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            [new CaptureProcessingStepConfig("NoOpFileStorageProcessingStep", Options: options.RootElement.Clone())]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => false;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<CameraModuleConfig>(new NotSupportedException());
    }
}
