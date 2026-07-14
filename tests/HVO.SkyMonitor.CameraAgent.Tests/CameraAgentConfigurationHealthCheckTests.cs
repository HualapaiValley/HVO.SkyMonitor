using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentConfigurationHealthCheckTests
{
    [TestMethod]
    public async Task CheckHealthAsync_WhenConfigured_ReturnsHealthy()
    {
        var check = new CameraAgentConfigurationHealthCheck(new StubConfigurationAccessor(true));
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Healthy, result.Status);
    }

    [TestMethod]
    public async Task CheckHealthAsync_WhenConfigurationIsPending_ReturnsDegraded()
    {
        var check = new CameraAgentConfigurationHealthCheck(new StubConfigurationAccessor(false));
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Degraded, result.Status);
    }

    private sealed class StubConfigurationAccessor(bool isConfigured) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => isConfigured;
        public void SetConfiguration(HVO.SkyMonitor.AgentCore.CameraModuleConfig config) => throw new NotSupportedException();
        public ValueTask<HVO.SkyMonitor.AgentCore.CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<HVO.SkyMonitor.AgentCore.CameraModuleConfig>(new NotSupportedException());
    }
}
