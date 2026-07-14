using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressHealthCheckTests
{
    private static readonly string[] ExpectedDataKeys =
    [
        "Availability", "PendingCount", "PendingBytes", "QuarantineCount", "QuarantineBytes"
    ];

    [TestMethod]
    public async Task CheckHealthAsync_ReportsBoundedDurableState()
    {
        var state = new RawIngressState(TimeProvider.System);
        var healthCheck = new RawIngressHealthCheck(state);

        state.Set(RawIngressAvailability.Accepting, "accepting", 2, 100);
        var healthy = await healthCheck.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        state.Set(RawIngressAvailability.Degraded, "reconciliation-findings", 2, 100, 1, 20);
        var degraded = await healthCheck.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        state.Set(RawIngressAvailability.Unhealthy, "integrity", 2, 100, 1, 20);
        var unhealthy = await healthCheck.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Healthy, healthy.Status);
        Assert.AreEqual(HealthStatus.Degraded, degraded.Status);
        Assert.AreEqual(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.AreEqual(2L, unhealthy.Data["PendingCount"]);
        Assert.AreEqual(1L, unhealthy.Data["QuarantineCount"]);
        CollectionAssert.AreEquivalent(
            ExpectedDataKeys,
            unhealthy.Data.Keys.ToArray());
    }
}
