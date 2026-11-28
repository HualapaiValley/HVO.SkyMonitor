using System;
using System.IO;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Configures where the agent stores local device identity and operator notification state.
/// </summary>
public sealed class DeviceProvisioningOptions
{
    private static readonly string DefaultStateDirectory = Path.Combine(AppContext.BaseDirectory, "data", "provisioning");

    /// <summary>
    /// Root directory for local device identity and operator notification records.
    /// </summary>
    public string StateDirectory { get; set; } = DefaultStateDirectory;

    /// <summary>
    /// File name (without directory) used to persist the device identity document.
    /// </summary>
    public string IdentityFileName { get; set; } = "device-identity.json";

    internal string GetIdentityPath()
        => Path.Combine(StateDirectory, IdentityFileName);

}
