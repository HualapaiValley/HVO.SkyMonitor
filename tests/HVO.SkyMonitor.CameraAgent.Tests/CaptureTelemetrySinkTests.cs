using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureTelemetrySinkTests
{
    [TestMethod]
    public void GetSnapshot_AfterCapacityWrap_ReturnsRetainedSamplesInChronologicalOrder()
    {
        var sink = new CaptureTelemetrySink(capacity: 2);
        var first = Sample(0, 1, frameStored: false);
        var second = Sample(1, 2, frameStored: true);
        var third = Sample(2, 3, frameStored: true);

        sink.Report(first);
        sink.Report(second);
        sink.Report(third);
        var snapshot = sink.GetSnapshot();

        CollectionAssert.AreEqual(new[] { second, third }, snapshot.Samples.ToArray());
        Assert.AreSame(third, sink.Latest);
        Assert.AreEqual(2.5, snapshot.Aggregate.AverageGain);
        Assert.AreEqual(2, snapshot.Aggregate.FramesStored);
    }

    [TestMethod]
    public void Reset_StartsFreshMeasurementWindow()
    {
        var sink = new CaptureTelemetrySink();
        sink.Report(Sample(0, 1, frameStored: true));

        sink.Reset();

        Assert.IsNull(sink.Latest);
        Assert.IsEmpty(sink.GetSnapshot().Samples);
    }

    private static CaptureTelemetrySample Sample(int minute, double gain, bool frameStored)
        => new(
            DateTimeOffset.UnixEpoch.AddMinutes(minute),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(1),
            gain,
            null,
            CaptureMode.Still,
            RequiresImmediateUpload: false,
            frameStored,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            []);
}
