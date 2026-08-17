using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
public sealed class TransientCandidateDeliveryHealthCheckTests
{
    [TestMethod]
    public async Task HealthReflectsDisabledCleanBlockedAndQuarantinedSnapshots()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch.AddHours(1));
        var state = new TransientCandidateDeliveryState(time);
        var health = new TransientCandidateDeliveryHealthCheck(state);

        state.SetDisabled();
        Assert.AreEqual(HealthStatus.Healthy, (await CheckAsync(health).ConfigureAwait(false)).Status);

        state.Set(
            TransientCandidateDeliveryAvailability.Healthy,
            "ready",
            new TransientCandidateDeliveryAggregate(0, 0, null),
            0,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
        Assert.AreEqual(HealthStatus.Healthy, (await CheckAsync(health).ConfigureAwait(false)).Status);

        state.Set(
            TransientCandidateDeliveryAvailability.Degraded,
            "authentication-blocked",
            new TransientCandidateDeliveryAggregate(2, 0, DateTimeOffset.UnixEpoch),
            0,
            2,
            null,
            time.GetUtcNow(),
            time.GetUtcNow());
        var degraded = await CheckAsync(health).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Degraded, degraded.Status);
        Assert.AreEqual(2, degraded.Data["authenticationBlockedCount"]);

        state.Set(
            TransientCandidateDeliveryAvailability.Unhealthy,
            "quarantined",
            new TransientCandidateDeliveryAggregate(1, 1, DateTimeOffset.UnixEpoch),
            0,
            0,
            null,
            time.GetUtcNow(),
            time.GetUtcNow());
        var unhealthy = await CheckAsync(health).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.AreEqual(1L, unhealthy.Data["quarantinedCount"]);
        Assert.IsFalse(unhealthy.Data.Keys.Any(key => key.Contains("candidate", StringComparison.OrdinalIgnoreCase)));
    }

    private static Task<HealthCheckResult> CheckAsync(TransientCandidateDeliveryHealthCheck health)
        => health.CheckHealthAsync(new HealthCheckContext());

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
