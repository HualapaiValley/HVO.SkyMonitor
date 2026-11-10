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
        "Availability", "LaneCount", "PendingCount", "PendingBytes", "LeasedCount", "QuarantineCount", "Reason",
        "RequiredQuarantinedLaneCount", "RequiredPressureLaneCount", "RequiredProcessingQuarantineCount", "OperationalFailureCount"
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
        state.Update([new CaptureLaneBacklog("secondary", false, 2, 8, DateTimeOffset.UtcNow, 0, 0, 0)]);

        var degraded = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Degraded, degraded.Status);
        state.Update([new CaptureLaneBacklog("standard", true, 1, 4, DateTimeOffset.UtcNow, 0, 0, 1)]);
        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.AreEqual("required-quarantine", unhealthy.Data["Reason"]);
        Assert.AreEqual(1, unhealthy.Data["RequiredQuarantinedLaneCount"]);
        Assert.AreEqual(0, unhealthy.Data["RequiredPressureLaneCount"]);
        Assert.AreEqual(1L, unhealthy.Data["RequiredProcessingQuarantineCount"]);
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequiredPressureIsReportedEvenWhenAQuarantineExists(bool sameLane)
    {
        var state = new CaptureLaneState(TimeProvider.System, Options.Create(new CameraAgentHostOptions()));
        state.Update([
            new CaptureLaneBacklog("standard", true, 1, 4, DateTimeOffset.UtcNow, 0, 0, sameLane ? 1 : 0, PressureLevel: 2),
            new CaptureLaneBacklog("optional", false, 1, 4, DateTimeOffset.UtcNow, 0, 0, 1)
        ]);

        var result = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        Assert.AreEqual(1, result.Data["RequiredPressureLaneCount"]);
        Assert.AreEqual(sameLane ? 1 : 0, result.Data["RequiredQuarantinedLaneCount"]);
        Assert.AreEqual(sameLane ? 1L : 0L, result.Data["RequiredProcessingQuarantineCount"]);
    }

    [TestMethod]
    [DataRow("lane-worker-failed")]
    [DataRow("lane-claim-failed")]
    [DataRow("lane-state-unavailable")]
    [DataRow("ephemeral-worker-failed")]
    public async Task WorkerOrStateFailurePreservesItsCauseAlongsideQuarantineCounts(string reason)
    {
        var state = new CaptureLaneState(TimeProvider.System, Options.Create(new CameraAgentHostOptions()));
        state.Update([new CaptureLaneBacklog("standard", true, 1, 4, DateTimeOffset.UtcNow, 0, 0, 1)]);
        var lane = reason == "lane-state-unavailable" ? null : "standard";
        state.SetUnhealthy(reason, lane);
        state.Update([new CaptureLaneBacklog("standard", true, 1, 4, DateTimeOffset.UtcNow, 0, 0, 1)]);
        state.ClearUnhealthy(reason, "unrelated");

        var result = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        Assert.AreEqual(reason, result.Data["Reason"]);
        Assert.AreEqual(1L, result.Data["RequiredProcessingQuarantineCount"]);
        Assert.AreEqual(1, result.Data["OperationalFailureCount"]);

        state.ClearUnhealthy(reason, lane);
        var recovered = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual("required-quarantine", recovered.Data["Reason"]);
        Assert.AreEqual(0, recovered.Data["OperationalFailureCount"]);
    }

    [TestMethod]
    public async Task WorkerRecoveryDoesNotClearAnotherLanesFailure()
    {
        var state = new CaptureLaneState(TimeProvider.System, Options.Create(new CameraAgentHostOptions()));
        state.Update([new CaptureLaneBacklog("standard", true, 1, 4, DateTimeOffset.UtcNow, 0, 0, 1)]);
        state.SetUnhealthy("lane-worker-failed", "standard");
        state.SetUnhealthy("lane-worker-failed", "secondary");
        state.ClearUnhealthy("lane-worker-failed", "standard");

        var result = await new CaptureLanesHealthCheck(state).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual("lane-worker-failed", result.Data["Reason"]);
        Assert.AreEqual(1, result.Data["OperationalFailureCount"]);
    }
}
