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

    /// <summary>
    /// The operator settings file CameraAgent loads over its image settings from the Identity mount. The operator
    /// UI and hand edits both change it, so it is the one persisted place for operator-editable settings.
    /// </summary>
    public const string OperatorSettingsFileName = "appsettings.local.json";

    /// <summary>The largest operator settings file deployment tooling carries or seeds.</summary>
    public const int MaximumOperatorSettingsBytes = 1024 * 1024;

    public static string OperatorSettingsPath(string stateRoot)
        => Path.Combine(stateRoot, IdentityDirectoryName, OperatorSettingsFileName);

    /// <summary>
    /// The copy of an instance's operator settings file a state reset keeps in the product's shared operations
    /// directory while the Identity mount is deleted; the instance id keeps one camera's copy from another's.
    /// </summary>
    public static string OperatorSettingsResetCopyPath(string operationsRoot, Guid instanceId, Guid operationId)
        => Path.Combine(
            operationsRoot,
            $"{OperatorSettingsResetCopyPrefix(instanceId)}{operationId:D}.{OperatorSettingsFileName}");

    public static string OperatorSettingsResetCopyPattern(Guid instanceId)
        => $"{OperatorSettingsResetCopyPrefix(instanceId)}*.{OperatorSettingsFileName}";

    private static string OperatorSettingsResetCopyPrefix(Guid instanceId) => $"cameraagent-{instanceId:D}.state-reset-";

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

    /// <summary>The local automation store's location under the state root.</summary>
    public const string AutomationStore = RawDirectoryName + "/.automation/local-automations.db";

    /// <summary>The orbital elements store's location under the state root.</summary>
    public const string OrbitalElementsStore = RawDirectoryName + "/.orbital-elements/orbital-elements.db";

    /// <summary>
    /// Every SQLite store CameraAgent keeps, relative to the state root, with <c>/</c> separators, except the orbital
    /// elements store. These are the small databases a migrating upgrade snapshots. None lies inside a bulk frame,
    /// archive or derived tree, so the snapshot never reads bulk data. The artifact outbox lives in each storage root
    /// the generated pipeline writes to. <see cref="OrbitalElementsStore"/> is deliberately not listed: it holds the
    /// provider's attempt floor and any halt, and a restore must never rewind them, or the release rolled back to could
    /// request inside the floor or after the provider refused.
    /// </summary>
    public static IReadOnlyList<string> SqliteStores { get; } =
    [
        $"{IdentityDirectoryName}/{IdentityDatabaseFileName}",
        $"{RawDirectoryName}/journal/raw-ingress.db",
        $"{RawDirectoryName}/outbox/artifact-outbox.db",
        $"{ArchiveDirectoryName}/outbox/artifact-outbox.db",
        $"{RawDirectoryName}/.fleet/fleet-status.db",
        $"{RawDirectoryName}/.environment/environmental-observation-outbox.db",
        $"{RawDirectoryName}/evidence/execution-evidence-outbox.db",
        $"{RawDirectoryName}/.time-lapses/time-lapses.db",
        $"{RawDirectoryName}/.nightly-products/nightly-products.db",
        $"{RawDirectoryName}/.automation/local-automations.db"
    ];

    /// <summary>The files SQLite keeps beside a database while it has uncheckpointed or uncommitted content.</summary>
    public static IReadOnlyList<string> SqliteSidecarSuffixes { get; } = ["-wal", "-shm", "-journal"];

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
