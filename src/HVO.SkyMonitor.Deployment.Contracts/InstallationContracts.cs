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
    CatalogGarbageCollect
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
    Restored
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
    string? CatalogManifestVersion = null);

public sealed record DockerDaemonIdentity(
    string Id,
    string Name,
    string Architecture,
    string ServerVersion);

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
    CameraAgentReplayProfile ReplayProfile = CameraAgentReplayProfile.InProcess);

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
    Guid? RestoreResumeCommandId = null);

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
    DateTimeOffset RecordedUtc);

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
    DateTimeOffset CompletedUtc);

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
    CameraAgentReplayProfile ReplayProfile = CameraAgentReplayProfile.InProcess);
