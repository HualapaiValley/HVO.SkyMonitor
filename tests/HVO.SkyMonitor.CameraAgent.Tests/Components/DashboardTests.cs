using System;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class DashboardTests
{
    [TestMethod]
    public void Dashboard_WithNoSamples_ShowsEmptyState()
    {
        using var ctx = new Bunit.TestContext();
        var snapshot = CreateSnapshot();
        ConfigureServices(ctx, snapshot, DateTimeOffset.Parse("2025-11-26T05:00:00Z", CultureInfo.InvariantCulture));

        var cut = ctx.RenderComponent<Dashboard>();

        var statusText = cut.Find(".status-pill").TextContent.Trim();
        Assert.AreEqual("Waiting for frames", statusText);

        StringAssert.Contains(cut.Markup, "Waiting for telemetry…", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "No telemetry buffered yet.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Dashboard_WithLatestSample_ShowsLiveStatusAndPreview()
    {
        var sampleTime = new DateTimeOffset(2025, 11, 26, 5, 0, 0, TimeSpan.Zero);
        var sample = CreateSample(sampleTime);
        var snapshot = CreateSnapshot(sample);

        using var ctx = new Bunit.TestContext();
        ConfigureServices(ctx, snapshot, sampleTime.AddSeconds(1));

        var cut = ctx.RenderComponent<Dashboard>();

        var statusText = cut.Find(".status-pill").TextContent.Trim();
        Assert.AreEqual("Live", statusText);

        var previews = cut.FindAll(".frame-preview-card img");
        Assert.AreEqual(2, previews.Count);
        Assert.AreEqual(FormattableString.Invariant($"/api/v1.0/frames/raw?ts={sampleTime.ToUnixTimeMilliseconds()}"), previews[0].GetAttribute("src"));
        Assert.AreEqual(FormattableString.Invariant($"/api/v1.0/frames/processed?ts={sampleTime.ToUnixTimeMilliseconds()}"), previews[1].GetAttribute("src"));
        StringAssert.Contains(cut.Markup, "Virtual Sensor", StringComparison.Ordinal);

        var historyRow = cut.Find("table tbody tr");
        StringAssert.Contains(historyRow.TextContent, sampleTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static void ConfigureServices(Bunit.TestContext ctx, CaptureTelemetrySnapshot snapshot, DateTimeOffset utcNow)
    {
        ctx.Services.AddSingleton<ICaptureTelemetryProvider>(new TestTelemetryProvider(snapshot));
        ctx.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(utcNow));
        ctx.Services.AddSingleton<IOptions<CapturePreviewOptions>>(Options.Create(new CapturePreviewOptions { PollingIntervalSeconds = 60 }));
        ctx.Services.AddSingleton<ICameraAgentConfigurationAccessor>(new TestConfigurationAccessor());
        ctx.Services.AddSingleton<ILatestFrameAccessor, LatestFrameAccessor>();
        ctx.Services.AddSingleton<ILogger<Dashboard>>(_ => NullLogger<Dashboard>.Instance);
    }

    private static CaptureTelemetrySample CreateSample(DateTimeOffset startedUtc)
    {
        return new CaptureTelemetrySample(
            startedUtc,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2),
            1.5,
            2.0,
            CaptureMode.Still,
            RequiresImmediateUpload: false,
            FrameStored: true,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(400),
            Array.Empty<CaptureProcessingStepTelemetry>());
    }

    private static CaptureTelemetrySnapshot CreateSnapshot(params CaptureTelemetrySample[] samples)
    {
        var aggregate = CaptureTelemetryAggregate.FromSamples(samples);
        return new CaptureTelemetrySnapshot(samples, aggregate);
    }

    private sealed class TestTelemetryProvider : ICaptureTelemetryProvider
    {
        private readonly CaptureTelemetrySnapshot _snapshot;
        private readonly CaptureTelemetrySample? _latest;

        public TestTelemetryProvider(CaptureTelemetrySnapshot snapshot)
        {
            _snapshot = snapshot;
            _latest = snapshot.Samples.Count > 0 ? snapshot.Samples[^1] : null;
        }

        public CaptureTelemetrySnapshot GetSnapshot() => _snapshot;

        public CaptureTelemetrySample? Latest => _latest;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class TestConfigurationAccessor : ICameraAgentConfigurationAccessor
    {
        private static readonly CameraModuleConfig Config = new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("Virtual Sensor", 640, 480, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 3, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1, 1)));

        public bool IsConfigured => true;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Config);
    }
}
