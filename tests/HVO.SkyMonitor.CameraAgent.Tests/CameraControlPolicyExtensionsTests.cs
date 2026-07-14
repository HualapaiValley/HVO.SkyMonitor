using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraControlPolicyExtensionsTests
{
    [TestMethod]
    public void ResolveTemperatureSetpoint_Target_ReturnsConfiguredTemperature()
    {
        var policy = new CameraControlPolicy
        {
            Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Target, TargetC = -15 }
        };

        Assert.AreEqual(-15, policy.ResolveTemperatureSetpoint(-5));
    }

    [TestMethod]
    public void ResolveTemperatureSetpoint_Disabled_ReturnsNaN()
    {
        var policy = new CameraControlPolicy
        {
            Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Disabled }
        };

        Assert.IsTrue(double.IsNaN(policy.ResolveTemperatureSetpoint(-5)));
    }

    [TestMethod]
    public void ResolveTemperatureSetpoint_WithoutActionableDirective_ReturnsFallback()
    {
        Assert.AreEqual(-5, ((CameraControlPolicy?)null).ResolveTemperatureSetpoint(-5));
        Assert.AreEqual(-5, new CameraControlPolicy().ResolveTemperatureSetpoint(-5));
        Assert.AreEqual(-5, new CameraControlPolicy
        {
            Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Target }
        }.ResolveTemperatureSetpoint(-5));
    }

    [TestMethod]
    public void TemperatureControlRequested_RequiresTargetOrDisabledDirective()
    {
        Assert.IsFalse(((CameraControlPolicy?)null).TemperatureControlRequested());
        Assert.IsFalse(new CameraControlPolicy().TemperatureControlRequested());
        Assert.IsTrue(new CameraControlPolicy
        {
            Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Disabled }
        }.TemperatureControlRequested());
        Assert.IsTrue(new CameraControlPolicy
        {
            Temperature = new TemperatureControlDirective { Mode = TemperatureControlMode.Target, TargetC = -15 }
        }.TemperatureControlRequested());
    }
}
