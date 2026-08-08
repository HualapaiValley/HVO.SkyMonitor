using HVO.SkyMonitor.CameraAgent.Common.Fleet;

namespace HVO.SkyMonitor.CameraAgent.Tests.Fleet;

[TestClass]
[TestCategory("Unit")]
public sealed class FleetRuntimeStateTests
{
    [TestMethod]
    public void ResetTimings_PreservesCaptureStateAndStartsEmptyMeasurementWindow()
    {
        var state = new FleetRuntimeState(TimeProvider.System);
        state.ModuleAvailable();
        state.ProcessingCompleted(TimeSpan.FromMilliseconds(4));
        Assert.HasCount(1, state.Snapshot.Timings);

        state.ResetTimings();

        Assert.IsEmpty(state.Snapshot.Timings);
        Assert.AreEqual("Available", state.Snapshot.Capture.Availability.ToString());
    }
}
