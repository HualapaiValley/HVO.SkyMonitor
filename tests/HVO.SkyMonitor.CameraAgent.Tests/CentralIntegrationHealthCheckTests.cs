using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralIntegrationHealthCheckTests
{
    private static readonly IOptions<CameraAgentHostOptions> DisabledOptions = Options.Create(
        new CameraAgentHostOptions
        {
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
        });

    [TestMethod]
    public async Task DisabledCentralCapabilitiesAreHealthyWithoutInitializingOutboxes()
    {
        var context = new HealthCheckContext();

        var artifact = await new ArtifactOutboxHealthCheck(
            new ArtifactOutboxState(), DisabledOptions, TimeProvider.System)
            .CheckHealthAsync(context).ConfigureAwait(false);
        var fleet = await new FleetHeartbeatHealthCheck(
            new FleetHeartbeatState(), DisabledOptions, TimeProvider.System)
            .CheckHealthAsync(context).ConfigureAwait(false);
        var environmental = await new EnvironmentalObservationDeliveryHealthCheck(
            new EnvironmentalObservationDeliveryState(), DisabledOptions, TimeProvider.System)
            .CheckHealthAsync(context).ConfigureAwait(false);

        foreach (var result in new[] { artifact, fleet, environmental })
        {
            Assert.AreEqual(HealthStatus.Healthy, result.Status);
            Assert.AreEqual("Disabled", result.Data["Availability"]);
            Assert.AreEqual(0L, result.Data["PendingCount"]);
        }
    }
}
