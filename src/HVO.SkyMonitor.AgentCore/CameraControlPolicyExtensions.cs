namespace HVO.SkyMonitor.AgentCore;

public static class CameraControlPolicyExtensions
{
    public static double ResolveTemperatureSetpoint(this CameraControlPolicy? policy, double fallback)
    {
        if (policy?.Temperature is { Mode: TemperatureControlMode.Target, TargetC: { } target })
        {
            return target;
        }

        if (policy?.Temperature?.Mode == TemperatureControlMode.Disabled)
        {
            return double.NaN;
        }

        return fallback;
    }

    public static bool TemperatureControlRequested(this CameraControlPolicy? policy)
        => policy?.Temperature?.Mode is TemperatureControlMode.Target or TemperatureControlMode.Disabled;
}
