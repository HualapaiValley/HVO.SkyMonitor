using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Capture")]
public sealed class CaptureRuntimeStateTests
{
    [TestMethod]
    public void ResetTimings_PreservesCaptureStateAndStartsEmptyMeasurementWindow()
    {
        var state = new CaptureRuntimeState(TimeProvider.System);
        state.ModuleAvailable();
        state.ProcessingCompleted(TimeSpan.FromMilliseconds(4));
        Assert.HasCount(1, state.Snapshot.Timings);

        state.ResetTimings();

        Assert.IsEmpty(state.Snapshot.Timings);
        Assert.AreEqual("Available", state.Snapshot.Capture.Availability.ToString());
    }
}
