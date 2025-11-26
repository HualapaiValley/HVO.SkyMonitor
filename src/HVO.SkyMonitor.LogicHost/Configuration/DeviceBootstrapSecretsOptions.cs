using HVO.SkyMonitor.Common.Identity;

namespace HVO.SkyMonitor.LogicHost.Configuration;

/// <summary>
/// Overrides used when generating device bootstrap secrets.
/// Allows LogicHost to ship camera-agent specific identity wiring without mutating its own credentials.
/// </summary>
internal sealed class DeviceBootstrapSecretsOptions
{
    /// <summary>
    /// Optional Central Identity snapshot to embed in device bootstrap payloads.
    /// When omitted, LogicHost falls back to its own <see cref="CentralIdentityOptions"/> configuration.
    /// </summary>
    public CentralIdentityOptions? CentralIdentity { get; set; }
}
