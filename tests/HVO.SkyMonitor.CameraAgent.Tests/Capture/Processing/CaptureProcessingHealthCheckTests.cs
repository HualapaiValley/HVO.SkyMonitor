using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureProcessingHealthCheckTests
{
    [TestMethod]
    [DataRow("completed", HealthStatus.Healthy)]
    [DataRow("retry", HealthStatus.Degraded)]
    [DataRow("terminal", HealthStatus.Unhealthy)]
    public async Task ReportsDurableGraphOutcome(string outcome, HealthStatus expected)
    {
        var state = new CaptureProcessingState();
        state.GraphStarted();
        state.GraphCompleted(outcome);
        var check = new CaptureProcessingHealthCheck(state);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(expected, result.Status);
        Assert.AreEqual(outcome == "completed" ? "Healthy" : outcome == "retry" ? "Degraded" : "Unhealthy",
            result.Data["Availability"]);
    }

    [TestMethod]
    public async Task DurableTerminalState_IsNotClearedByLaterSuccessfulGraph()
    {
        var state = new CaptureProcessingState();
        state.SetDurable(2, 1, 1, DateTimeOffset.UtcNow.AddMinutes(-1));
        state.GraphStarted();
        state.GraphCompleted("completed");
        var check = new CaptureProcessingHealthCheck(state);

        var result = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        Assert.AreEqual(1L, result.Data["TerminalCount"]);
        Assert.IsGreaterThan(0d, Convert.ToDouble(
            result.Data["OldestPendingAgeSeconds"], System.Globalization.CultureInfo.InvariantCulture));
    }
}
