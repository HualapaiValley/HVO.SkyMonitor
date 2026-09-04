using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Names the durable CameraAgent state contract an image reads and writes.
/// </summary>
/// <remarks>
/// The <c>io.hvo.skymonitor.state-compatibility</c> label previously claimed unbounded
/// <c>backward-compatible</c> migration. That claim was untrue for CameraAgent state produced before the
/// image's declared minimum compatible revision, so the label now names a contract identity instead of a
/// promise, and the persisted-state boundaries decide whether a specific instance may be upgraded in place.
/// </remarks>
internal static class CameraAgentStateContract
{
    /// <summary>The durable state contract every supported CameraAgent image reads and writes.</summary>
    public const string Current = "cameraagent-state-v2";

    /// <summary>The superseded unbounded claim retained only so already installed images stay readable.</summary>
    public const string LegacyUnbounded = "backward-compatible";

    public static bool IsCurrent(string? declaration)
        => string.Equals(declaration, Current, StringComparison.Ordinal);

    /// <summary>
    /// A declaration this deployment understands. The legacy value carries no boundary, so an instance that
    /// declares it is admitted only after the persisted-state preflight proves the actual boundaries match.
    /// </summary>
    public static bool IsKnown(string? declaration)
        => IsCurrent(declaration) || string.Equals(declaration, LegacyUnbounded, StringComparison.Ordinal);

    public static string Describe(string? declaration)
        => string.IsNullOrWhiteSpace(declaration) ? "none" : declaration;
}

/// <summary>
/// A writable host directory that Compose binds into the CameraAgent container. Docker creates a missing bind
/// source as <c>root:root</c> mode <c>0755</c>, which a capability-dropped non-root container cannot restrict,
/// so deployment tooling must create every one of these before Compose starts.
/// </summary>
internal sealed record CameraAgentBindSource(string HostPath, string ContainerPath);

internal static class CameraAgentStateLayout
{
    /// <summary>Container path of the Identity state mount.</summary>
    public const string IdentityDirectoryName = "identity";
    public const string DataProtectionDirectoryName = "data-protection";
    public const string ProvisioningDirectoryName = "provisioning";
    public const string RawDirectoryName = "raw";
    public const string ArchiveDirectoryName = "archive";
    public const string ReplayRunnerDirectoryName = "replay-runner";

    public const string IdentityDatabaseFileName = "cameraagent_identity.db";

    /// <summary>Every writable bind source the generated Compose model mounts, nested sources included.</summary>
    public static IReadOnlyList<CameraAgentBindSource> WritableBindSources(
        string stateRoot,
        ContractReplayProfile replayProfile)
    {
        var sources = new List<CameraAgentBindSource>
        {
            new(Path.Combine(stateRoot, IdentityDirectoryName), "/app/App_Data"),
            new(Path.Combine(stateRoot, DataProtectionDirectoryName), "/app/DataProtection-Keys"),
            new(Path.Combine(stateRoot, ProvisioningDirectoryName), "/app/data/provisioning"),
            new(Path.Combine(stateRoot, RawDirectoryName), "/app/data/raw"),
            new(Path.Combine(stateRoot, ArchiveDirectoryName), "/app/data/archive")
        };
        if (replayProfile == ContractReplayProfile.LocalRunner)
        {
            sources.Add(new(Path.Combine(stateRoot, ReplayRunnerDirectoryName), "/run/hvo-replay"));
        }
        return sources;
    }

    public static string IdentityDatabasePath(string stateRoot)
        => Path.Combine(stateRoot, IdentityDirectoryName, IdentityDatabaseFileName);

    /// <summary>The shared raw-ingress journal that owns the capture, lane, processing, and transient schema.</summary>
    public static string RawIngressDatabasePath(string stateRoot)
        => Path.Combine(stateRoot, RawDirectoryName, "journal", "raw-ingress.db");

    /// <summary>Every runtime state tree a CameraAgent-only reset deletes.</summary>
    public static IReadOnlyList<string> ResettableStateDirectories(string stateRoot) =>
    [
        Path.Combine(stateRoot, IdentityDirectoryName),
        Path.Combine(stateRoot, DataProtectionDirectoryName),
        Path.Combine(stateRoot, ProvisioningDirectoryName),
        Path.Combine(stateRoot, RawDirectoryName),
        Path.Combine(stateRoot, ArchiveDirectoryName),
        Path.Combine(stateRoot, ReplayRunnerDirectoryName)
    ];
}
