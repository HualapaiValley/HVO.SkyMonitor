namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>Artifacts created by prepare, recorded before recovery can remove them.</summary>
public sealed record PrepareCreation(bool Root, bool ControlDirectory, bool Marker);

/// <summary>The immutable host binding used by split-host lifecycle recovery.</summary>
public sealed record DeploymentTarget(
    string Name, string SshHost, string DockerContext, string Architecture, string HostName,
    string HostIdentity, string DockerDaemonIdentity, string RuntimeRoot, string RuntimeOwner,
    string? Component, string? InstanceId);

/// <summary>A retained prepare result; an older persistent installation has no run-created artifacts.</summary>
public sealed record PreparedTarget(
    string Target, string RuntimeRoot, string Disposition, PrepareCreation NewlyCreated,
    string MarkerDigest, string CreatingRunId);
