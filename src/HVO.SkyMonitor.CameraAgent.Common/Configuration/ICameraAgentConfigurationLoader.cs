using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed record InstallationConfigurationSnapshot(CameraModuleConfig Configuration, string? ConfigurationSha256);

public interface ICameraAgentConfigurationLoader
{
    Task<CameraModuleConfig> LoadAsync(CancellationToken cancellationToken);

    Task<CameraModuleConfig> LoadForRetainedIntegrityScrubAsync(CancellationToken cancellationToken)
        => LoadAsync(cancellationToken);

    // Strict retained-evidence checks belong to the protected read, not startup/recovery admission.
    Task<CameraModuleConfig> LoadForInstallationVerificationAsync(CancellationToken cancellationToken)
        => LoadAsync(cancellationToken);

    async Task<InstallationConfigurationSnapshot> LoadInstallationSnapshotAsync(CancellationToken cancellationToken)
        => new(await LoadForInstallationVerificationAsync(cancellationToken).ConfigureAwait(false), null);
}
