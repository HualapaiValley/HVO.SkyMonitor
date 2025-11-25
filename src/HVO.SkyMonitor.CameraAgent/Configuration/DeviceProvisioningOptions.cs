using System;
using System.IO;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Configures where the agent stores device identity and secure bootstrap state.
/// </summary>
public sealed class DeviceProvisioningOptions
{
    private static readonly string DefaultStateDirectory = Path.Combine(AppContext.BaseDirectory, "data", "provisioning");

    /// <summary>
    /// Root directory for provisioning artifacts (device identity, secrets, logs).
    /// </summary>
    public string StateDirectory { get; set; } = DefaultStateDirectory;

    /// <summary>
    /// File name (without directory) used to persist the device identity document.
    /// </summary>
    public string IdentityFileName { get; set; } = "device-identity.json";

    /// <summary>
    /// File name (without directory) used to persist encrypted device secrets.
    /// </summary>
    public string SecretsFileName { get; set; } = "device-secrets.dat";

    internal string GetIdentityPath()
        => Path.Combine(StateDirectory, IdentityFileName);

    internal string GetSecretsPath()
        => Path.Combine(StateDirectory, SecretsFileName);
}
