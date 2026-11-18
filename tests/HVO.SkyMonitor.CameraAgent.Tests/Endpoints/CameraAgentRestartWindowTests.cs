using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Endpoints;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentRestartWindowTests
{
    [TestMethod]
    [DataRow(false, CaptureScheduleAdmissionReason.DefaultClosed, 241, true)]
    [DataRow(false, CaptureScheduleAdmissionReason.Blackout, 300, true)]
    [DataRow(false, CaptureScheduleAdmissionReason.ForceClosedOverride, 300, true)]
    [DataRow(false, CaptureScheduleAdmissionReason.DefaultClosed, 239, false)]
    [DataRow(false, CaptureScheduleAdmissionReason.ManualPause, 300, false)]
    [DataRow(false, CaptureScheduleAdmissionReason.NoSolarEvent, 300, false)]
    [DataRow(false, CaptureScheduleAdmissionReason.SafetyUnavailable, 300, false)]
    [DataRow(true, CaptureScheduleAdmissionReason.WeeklyWindow, 300, false)]
    [DataRow(true, CaptureScheduleAdmissionReason.ForceOpenOverride, 300, false)]
    public void RestartRequiresKnownClosedWindowForEntireBudget(bool admitted, CaptureScheduleAdmissionReason reason,
        int seconds, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var decision = new CaptureScheduleDecision(admitted, reason, CaptureScheduleSafetyState.Available, now,
            null, null, null, false, now.AddSeconds(seconds));
        Assert.AreEqual(expected, CameraAgentControlLifecycleEndpoints.IsRestartWindowSafe(decision, now));
    }
}
