using System;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Telemetry;

[TestClass]
public sealed class CaptureTelemetrySinkTests
{
    [TestMethod]
    public void Report_RespectsCapacityAndMaintainsOrder()
    {
        var sink = new CaptureTelemetrySink(capacity: 2);
        var first = CreateSample(DateTimeOffset.UnixEpoch.AddSeconds(0));
        var second = CreateSample(DateTimeOffset.UnixEpoch.AddSeconds(10));
        var third = CreateSample(DateTimeOffset.UnixEpoch.AddSeconds(20));

        sink.Report(first);
        sink.Report(second);
        sink.Report(third);

        Assert.AreEqual(third, sink.Latest);

        var snapshot = sink.GetSnapshot();
        Assert.AreEqual(2, snapshot.Samples.Count);
        Assert.AreEqual(second.StartedUtc, snapshot.Samples[0].StartedUtc);
        Assert.AreEqual(third.StartedUtc, snapshot.Samples[1].StartedUtc);
    }

    [TestMethod]
    public void GetSnapshot_ComputesAggregates()
    {
        var sink = new CaptureTelemetrySink(capacity: 4);
        var start = DateTimeOffset.UnixEpoch;

        for (var i = 0; i < 4; i++)
        {
            var sample = CreateSample(
                start.AddMinutes(i),
                exposureMilliseconds: 100 + (i * 10),
                gain: 2 + i,
                frameStored: i % 2 == 0,
                requiresImmediateUpload: i == 3);
            sink.Report(sample);
        }

        var snapshot = sink.GetSnapshot();
        Assert.AreEqual(4, snapshot.Samples.Count);
        Assert.AreEqual(2, snapshot.Aggregate.FramesStored);
        Assert.AreEqual(1, snapshot.Aggregate.ImmediateUploadCount);
        Assert.IsTrue(snapshot.Aggregate.AverageExposureMilliseconds >= 100);
        Assert.IsTrue(snapshot.Aggregate.CapturesPerMinute > 0);
        Assert.IsTrue(snapshot.Aggregate.DutyCycle > 0);
    }

    private static CaptureTelemetrySample CreateSample(
        DateTimeOffset startedUtc,
        double exposureMilliseconds = 100,
        double gain = 2,
        bool frameStored = true,
        bool requiresImmediateUpload = false) => new(
            StartedUtc: startedUtc,
            Interval: TimeSpan.FromSeconds(10),
            Exposure: TimeSpan.FromMilliseconds(exposureMilliseconds),
            Gain: gain,
            TargetFps: null,
            Mode: CaptureMode.Still,
            RequiresImmediateUpload: requiresImmediateUpload,
            FrameStored: frameStored,
            ProcessingLatency: TimeSpan.FromMilliseconds(25),
            LoopDuration: TimeSpan.FromSeconds(2),
            ProcessingSteps: Array.Empty<CaptureProcessingStepTelemetry>());
}
