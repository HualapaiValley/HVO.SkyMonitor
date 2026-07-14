using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureLanesHealthCheckTests
{
    private static readonly string[] ExpectedDataKeys =
    [
        "Availability", "LaneCount", "PendingCount", "PendingBytes", "LeasedCount", "QuarantineCount"
    ];

    [TestMethod]
    public async Task RequiredQuarantineIsUnhealthyAndOptionalPressureIsDegraded()
    {
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = "raw",
            CaptureDistribution = new CaptureDistributionOptions
            {
                OptionalMaximumPendingCount = 2
            }
        });
        var state = new CaptureLaneState(TimeProvider.System, options);
        var check = new CaptureLanesHealthCheck(state);
        state.Update([new CaptureLaneBacklog("secondary", false, 2, 8, DateTimeOffset.UtcNow, 0, 0)]);

        var degraded = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Degraded, degraded.Status);
        state.Update([new CaptureLaneBacklog("standard", true, 1, 4, DateTimeOffset.UtcNow, 0, 1)]);
        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Unhealthy, unhealthy.Status);
        CollectionAssert.AreEquivalent(
            ExpectedDataKeys,
            unhealthy.Data.Keys.ToArray());
        Assert.IsFalse(CaptureLanePressureMath.IsAtOrAbovePercentage(
            long.MaxValue / 2, long.MaxValue, 80));
        Assert.IsTrue(CaptureLanePressureMath.IsAtOrAbovePercentage(
            long.MaxValue - 1, long.MaxValue, 80));
        Assert.IsTrue(CaptureLanePressureMath.ExceedsAfterAdding(
            long.MaxValue - 1, 2, long.MaxValue));
    }
}
