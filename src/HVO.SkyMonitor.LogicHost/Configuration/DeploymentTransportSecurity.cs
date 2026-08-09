namespace HVO.SkyMonitor.LogicHost.Configuration;

internal static class DeploymentTransportSecurity
{
    public static bool AllowsInsecureOpenIddictTransport(bool isProduction, string? deploymentMode)
        => !isProduction || string.Equals(deploymentMode, "isolated", StringComparison.Ordinal);
}
