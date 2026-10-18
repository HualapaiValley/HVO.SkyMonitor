using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

public static class DeploymentSchemaVersions
{
    public const int InstanceManifest = 1;
    public const int InstallationState = 1;
    public const int InstallationResult = 1;
    public const int LifecycleOperation = 1;
    public const int StatePreflightReport = 1;
    public const int StateResetEvidence = 1;
    public const int LifecycleEvent = 1;
    /// <summary>
    /// Version 1 recorded the SBOM, provenance, and vulnerability-scan asset names; version 2 (issue #645) adds the
    /// optional per-platform component-inventory asset. Version-1 documents remain readable.
    /// </summary>
    public const int ImageReleaseEvidence = 2;
}

[JsonConverter(typeof(JsonStringEnumConverter<DeploymentComponent>))]
public enum DeploymentComponent
{
    CameraAgent
}

[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentReplayProfile>))]
public enum CameraAgentReplayProfile
{
    InProcess,
    LocalRunner
}

[JsonConverter(typeof(JsonStringEnumConverter<InstallationPhase>))]
public enum InstallationPhase
{
    Preflight,
    Prepare,
    Catalog,
    Image,
    Configuration,
    Compose,
    Startup,
    OwnerBootstrap,
    OwnerSeeded,
    Verification,
    Completed
}

[JsonConverter(typeof(JsonStringEnumConverter<InstallationStatus>))]
public enum InstallationStatus
{
    Pending,
    Running,
    Failed,
    Completed
}

[JsonConverter(typeof(JsonStringEnumConverter<InstallationOutcome>))]
public enum InstallationOutcome
{
    Planned,
    Installed
}

[JsonConverter(typeof(JsonStringEnumConverter<InstanceLifecycleCondition>))]
public enum InstanceLifecycleCondition
{
    Installed,
    Uninstalled
}

[JsonConverter(typeof(JsonStringEnumConverter<LifecycleOperationKind>))]
public enum LifecycleOperationKind
{
    Upgrade,
    Rollback,
    Reinstall,
    Uninstall,
    Purge,
    CatalogInstall,
    CatalogSelect,
    CatalogRollback,
    CatalogGarbageCollect,
    CatalogCheck
}

[JsonConverter(typeof(JsonStringEnumConverter<LifecycleOperationPhase>))]
public enum LifecycleOperationPhase
{
    Planned,
    Prepared,
    CandidateValidated,
    BackupRecorded,
    Drained,
    Mutating,
    CandidateStarted,
    CandidateVerified,
    Committed,
    Restoring,
    Completed,
    Restored,
    /// <summary>The operation's verified snapshot of the instance's small SQLite databases is recorded.</summary>
    StateSnapshotRecorded,
    /// <summary>A rollback has put back the database snapshot retained by the upgrade it undoes.</summary>
    RetainedStateApplied
}

public sealed record ApplicationIdentityBinding(
    int SchemaVersion,
    string State,
    Guid ConfiguredIdentity,
    Guid? BoundIdentity);

public sealed record CatalogInstallationIdentity(
    string CatalogId,
    string PackageVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    string DatabaseSha256,
    long DatabaseLength,
    long RowCount,
    string InstallRoot,
    string ManifestSha256,
    string Source,
    DistributionVerificationEvidence? Distribution = null);

public sealed record ImageInstallationIdentity(
    string Source,
    string ImmutableReference,
    string ImageId,
    string Architecture,
    string? ArchiveSha256,
    DistributionVerificationEvidence? Distribution = null,
    string? UpgradeCompatibility = null,
    string? SourceRevision = null,
    string? Component = null,
    string? ConfigurationContract = null,
    string? CatalogContract = null,
    string? ReplayRunnerContract = null,
    string? MinimumCompatibleRevision = null,
    string? IdentityMigration = null,
    string? RawIngressSchema = null,
    string? CatalogManifestVersion = null,
    string? RawIngressMigration = null);

public sealed record DockerDaemonIdentity(
    string Id,
    string Name,
    string Architecture,
    string ServerVersion);

/// <summary>
/// The CameraAgent container's Compose resource limits recorded with an instance. A limit the operator did not set
/// follows the installer default, so an upgrade moves it to the upgrading installer's default; a limit the operator
/// set is kept. A manifest written before this record existed carries none: that instance runs the original
/// 1 CPU, 1 GiB and 256 PID limits, which no operator could set.
/// </summary>
public sealed record ContainerResourceLimits(
    decimal Cpus,
    long MemoryBytes,
    int PidsLimit,
    bool CpusSetByOperator,
    bool MemorySetByOperator);

public sealed record InstanceManifest(
    int SchemaVersion,
    string Product,
    string ComponentSchemaVersion,
    DeploymentComponent Component,
    Guid InstanceId,
    string FriendlyName,
    Guid ApplicationIdentity,
    string DeploymentLocationId,
    long DeploymentLocationVersion,
    string DeploymentLocationSha256,
    string OwnerEmail,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    Guid InstallationId,
    uint RuntimeUid,
    uint RuntimeGid,
    string ProductRoot,
    string ConfigRoot,
    string StateRoot,
    string ComposeTemplateVersion,
    string ConfigurationSha256,
    string RigProfileSha256,
    string ScheduleSha256,
    string RigProfileName,
    string RigProfileVersion,
    string ScheduleSchemaVersion,
    string ScheduleState,
    string InstallationVerificationTokenSha256,
    string ComposeModelSha256,
    CatalogInstallationIdentity Catalog,
    ImageInstallationIdentity Image,
    ImageInstallationIdentity? PreviousImage,
    DockerDaemonIdentity DockerDaemon,
    string UpgradeCompatibility,
    DateTimeOffset CreatedUtc,
    CatalogInstallationIdentity? PreviousCatalog = null,
    InstanceLifecycleCondition LifecycleCondition = InstanceLifecycleCondition.Installed,
    string BindAddress = "127.0.0.1",
    int Port = 5130,
    Guid? LastLifecycleOperationId = null,
    DateTimeOffset? UpdatedUtc = null,
    string? LifecycleControlTokenSha256 = null,
    string? PreviousComposeModelSha256 = null,
    CameraAgentReplayProfile ReplayProfile = CameraAgentReplayProfile.InProcess,
    ContainerResourceLimits? Resources = null,
    LifecycleStateSnapshot? PreviousStateSnapshot = null);

public sealed record LifecycleOperationState(
    int SchemaVersion,
    Guid OperationId,
    LifecycleOperationKind Kind,
    Guid? InstanceId,
    string RequestSha256,
    LifecycleOperationPhase Phase,
    InstallationStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    ImageInstallationIdentity? OriginalImage = null,
    ImageInstallationIdentity? CandidateImage = null,
    CatalogInstallationIdentity? OriginalCatalog = null,
    CatalogInstallationIdentity? CandidateCatalog = null,
    bool MutationStarted = false,
    string? BackupManifestSha256 = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    LifecycleContinuityBoundary? PreMutationContinuity = null,
    LifecycleContinuityBoundary? PostMutationContinuity = null,
    [property: JsonPropertyName("originalOwnerBootstrapState")]
    string? ExpectedOwnerBootstrapState = null,
    Guid? RestoreResumeCommandId = null,
    LifecycleResumeReceipt? RestoreResumeReceipt = null,
    bool StateSnapshotRequired = false,
    string? StateMigration = null,
    LifecycleStateSnapshot? StateSnapshot = null,
    DateTimeOffset? StateSnapshotRestoredUtc = null,
    LifecycleStateSnapshot? RetainedStateSnapshot = null,
    IReadOnlyList<StateCounterFloor>? StateCounterFloors = null,
    StateRestorePlan? StateRestorePlan = null);

/// <summary>
/// What a snapshot restore covers, journaled before it replaces any store so that an interrupted retry still knows it:
/// every store location the instance's configured inventory listed when the restore began (and at any earlier attempt
/// of the same operation), and every location the target snapshot records. A store at a current location the snapshot
/// does not record was created after the snapshot, so the restore removes it, keeping every bulk file.
/// </summary>
public sealed record StateRestorePlan(
    Guid SnapshotId,
    IReadOnlyList<string> CurrentLocations,
    IReadOnlyList<string> TargetLocations);

/// <summary>
/// The highest value a CameraAgent counter or version token had reached when a snapshot restore began. Each one hands
/// out a sequence or version that leaves the instance or that a client sends back, so a restore never lets one be
/// handed out again for different state. <see cref="Carry"/> holds what the restore needs to continue a version line
/// whose row the snapshot does not have.
/// </summary>
public sealed record StateCounterFloor(string Store, string Counter, string Key, long Value, StateCounterCarry? Carry = null);

/// <summary>
/// The latest revision of an automation definition the restored snapshot does not have, recorded so the restore can
/// record that definition as removed at a version above every one it handed out.
/// </summary>
public sealed record StateCounterCarry(string RevisionSha256, string DefinitionJson);

/// <summary>
/// A verified snapshot of an instance's small SQLite databases, taken by one image lifecycle operation while the
/// instance was stopped. It is kept in that operation's directory,
/// <c>operations/lifecycle/&lt;operation&gt;/state-snapshots/&lt;snapshot&gt;</c>, and its manifest hash binds every file in
/// it. Bulk capture data is never part of it.
/// </summary>
public sealed record LifecycleStateSnapshot(
    Guid SnapshotId,
    Guid OperationId,
    string ManifestSha256,
    DateTimeOffset RecordedUtc);

/// <summary>The manifest of a <see cref="LifecycleStateSnapshot"/>: one entry per known CameraAgent SQLite store.</summary>
public sealed record StateSnapshotManifest(
    int SchemaVersion,
    Guid SnapshotId,
    Guid InstanceId,
    Guid OperationId,
    DateTimeOffset CreatedUtc,
    ImageInstallationIdentity Image,
    IReadOnlyList<StateSnapshotDatabase> Databases);

/// <summary>
/// One store in a <see cref="StateSnapshotManifest"/>. A store absent when the snapshot was taken is recorded as
/// absent, so restoring the snapshot removes one a candidate created.
/// </summary>
public sealed record StateSnapshotDatabase(
    string RelativePath,
    bool Present,
    string? SnapshotFile,
    long Length,
    string? Sha256,
    long UserVersion,
    string? SchemaSha256,
    IReadOnlyList<StateSnapshotTableCount> Tables);

public sealed record StateSnapshotTableCount(string Name, long Rows);

public sealed record LifecycleContinuityBoundary(
    string CaptureState,
    long CaptureVersion,
    long CaptureSequence,
    long RawPending,
    long RawLeased,
    long LanePending,
    long LaneLeased,
    long ProcessingPending,
    long ProcessingLeased,
    long OutboxPending,
    long OutboxLeased,
    DateTimeOffset RecordedUtc,
    bool CaptureInitialized = true,
    long ReplayPending = 0,
    long ReplayLeased = 0);

public sealed record BackupFileIdentity(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record InstanceBackupManifest(
    int SchemaVersion,
    Guid BackupId,
    Guid InstanceId,
    Guid OperationId,
    DateTimeOffset CreatedUtc,
    string InstanceManifestSha256,
    string ApplicationIdentitySha256,
    string ConfigurationSha256,
    string ComposeModelSha256,
    CatalogInstallationIdentity Catalog,
    ImageInstallationIdentity Image,
    IReadOnlyList<BackupFileIdentity> Files,
    string ArchiveSha256,
    long ArchiveLength);

public sealed record LifecycleResult(
    int SchemaVersion,
    LifecycleOperationKind? Operation,
    string Outcome,
    Guid? OperationId,
    Guid? InstanceId,
    InstanceLifecycleCondition? LifecycleCondition,
    string ProductRoot,
    string? InstanceRoot,
    ImageInstallationIdentity? Image,
    ImageInstallationIdentity? PreviousImage,
    CatalogInstallationIdentity? Catalog,
    CatalogInstallationIdentity? PreviousCatalog,
    DockerDaemonIdentity? DockerDaemon,
    bool? Running,
    bool? Healthy,
    IReadOnlyList<string> PreservedPaths,
    string? ResumeCommand,
    DateTimeOffset CompletedUtc)
{
    /// <summary>
    /// The upgrade notes an upgrade showed before it started, when its source provided a release.json. Omitted
    /// otherwise, so every other lifecycle result is unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LifecycleUpgradeNotes? UpgradeNotes { get; init; }

    /// <summary>
    /// What the operation did, or with <c>--plan</c> would do, item by item. Omitted by commands that report no plan.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeploymentPlan? Plan { get; init; }

    /// <summary>
    /// Advisories the command reported without refusing, such as a deprecated option it accepted and ignored. Omitted
    /// when there are none. Without <c>--json</c> they are written to standard error instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<InstallationWarning>? Warnings { get; init; }
}

/// <summary>One release's upgrade notes, from its <c>docs/upgrade-notes/X.Y.Z.md</c> as release.json carries them.</summary>
public sealed record UpgradeNote(string Version, string Text);

/// <summary>
/// The upgrade notes an upgrade from <see cref="InstalledVersion"/> to <see cref="TargetVersion"/> shows, oldest first.
/// <see cref="InstalledVersion"/> is null when the instance records no release version.
/// </summary>
public sealed record LifecycleUpgradeNotes(string? InstalledVersion, string TargetVersion, IReadOnlyList<UpgradeNote> Notes);

/// <summary>
/// What a <c>--json</c> upgrade writes to standard error, as one line, when it shows upgrade notes: the notes, before
/// the lifecycle journal, the backup, the pause or the image change. Standard output carries only the result, which
/// repeats them, so this is how a caller sees them before anything changes; a failed upgrade's error object follows
/// this line.
/// </summary>
public sealed record LifecycleUpgradeNotesEvent(int SchemaVersion, string Event, LifecycleUpgradeNotes UpgradeNotes)
{
    /// <summary>The <see cref="Event"/> this record carries.</summary>
    public const string Name = "upgrade-notes";
}

/// <summary>
/// What a <c>--json</c> upgrade or rollback writes to standard error, as one line, when it restores a database
/// snapshot: the warning that database changes made after the snapshot are lost. Without <c>--json</c> the same
/// warning is written to standard error as text.
/// </summary>
public sealed record LifecycleStateSnapshotRestoreEvent(int SchemaVersion, string Event, string Warning, LifecycleStateSnapshot Snapshot)
{
    /// <summary>The <see cref="Event"/> this record carries.</summary>
    public const string Name = "state-snapshot-restore";

    /// <summary>
    /// The <see cref="Event"/> a restore also writes when it could not read part of the state it replaces (a store a
    /// failed candidate damaged), so that part is carried only as far as configuration and the journal recorded it.
    /// </summary>
    public const string IncompleteName = "state-snapshot-restore-incomplete";
}

public sealed record InstallationState(
    int SchemaVersion,
    Guid InstallationId,
    Guid InstanceId,
    string RequestSha256,
    InstallationPhase Phase,
    InstallationStatus Status,
    DateTimeOffset UpdatedUtc,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>
/// An advisory condition the installer found that does not refuse the installation, such as a Docker host with less
/// memory than the CameraAgent memory limit.
/// </summary>
/// <param name="Code">The stable code of the finding, for example <c>docker-host-memory-below-limit</c>.</param>
/// <param name="Message">What was observed, what was expected, and the remedy.</param>
public sealed record InstallationWarning(string Code, string Message);

/// <summary>
/// The outcome of <c>cameraagent install</c> or its dry run. The installer retains it as
/// <c>state/deployment/installation-result.json</c> without its warnings: they describe the host one invocation
/// evaluated, so only the result that invocation reports carries them.
/// </summary>
public sealed record InstallationResult(
    int SchemaVersion,
    InstallationOutcome Outcome,
    Guid InstallationId,
    Guid InstanceId,
    Guid ApplicationIdentity,
    string FriendlyName,
    Uri Url,
    string OwnerEmail,
    string PasswordFile,
    string ProductRoot,
    string InstanceRoot,
    string ConfigRoot,
    string StateRoot,
    uint RuntimeUid,
    uint RuntimeGid,
    string ComposeTemplateVersion,
    double LatitudeDegrees,
    double LongitudeDegrees,
    double ElevationMeters,
    string TimeZoneId,
    string ConfigurationSha256,
    string RigProfileSha256,
    string ScheduleSha256,
    string RigProfileName,
    string RigProfileVersion,
    string ScheduleSchemaVersion,
    string ScheduleState,
    string ComposeModelSha256,
    CatalogInstallationIdentity Catalog,
    ImageInstallationIdentity Image,
    DockerDaemonIdentity DockerDaemon,
    bool Alive,
    bool Healthy,
    string OwnerBootstrapState,
    DateTimeOffset CompletedUtc,
    CameraAgentReplayProfile ReplayProfile = CameraAgentReplayProfile.InProcess,
    bool OwnerPasswordIsDefault = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<InstallationWarning>? Warnings = null)
{
    /// <summary>
    /// What this invocation created, changed or left unchanged, or with <c>--plan</c> would. Like
    /// <see cref="Warnings"/> it describes one invocation, so the retained result never carries it. A second run with the
    /// same answers reports every item unchanged and the summary <see cref="DeploymentPlan.NothingToChange"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeploymentPlan? Plan { get; init; }
}
