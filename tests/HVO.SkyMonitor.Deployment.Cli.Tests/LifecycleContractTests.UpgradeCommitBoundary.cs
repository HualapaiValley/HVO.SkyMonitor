using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(0, "200", 200, true)]
    [DataRow(0, "503", 503, false)]
    [DataRow(0, "302", 302, false)]
    [DataRow(7, "000", null, null)]
    [DataRow(1, "200", null, null)]
    public async Task Status_ReportsApplicationReadinessSeparatelyFromAlive(
        int curlExit, string curlOutput, int? httpStatus, bool? ready)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.ApplicationHealth = new(curlExit, curlOutput, string.Empty);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(operation: null),
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("status", result.Outcome);
        Assert.IsTrue(result.Running);
        Assert.IsTrue(result.Alive);
        Assert.AreEqual(httpStatus, result.HealthStatusCode);
        Assert.AreEqual(ready, result.Healthy);
        Assert.AreEqual(1, fixture.Runner.ApplicationHealthProbes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, DeploymentJsonContext.Default.LifecycleResult));
        Assert.IsTrue(json.RootElement.GetProperty("alive").GetBoolean());
        Assert.AreEqual(ready, json.RootElement.GetProperty("healthy").ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null
        });
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Status_DriftedContainerCannotSupplyReadiness()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        fixture.Runner.Reboot(CandidateImageId, wasRunning: true);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(operation: null),
            fixture.Runner, null, null, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("drifted", result.Outcome);
        Assert.IsFalse(result.Healthy);
        Assert.IsNull(result.HealthStatusCode);
        Assert.AreEqual(0, fixture.Runner.ApplicationHealthProbes);
    }
}
