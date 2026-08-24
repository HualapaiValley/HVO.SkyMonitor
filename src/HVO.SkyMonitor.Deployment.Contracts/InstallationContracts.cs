using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

public static class DeploymentSchemaVersions
{
    public const int InstanceManifest = 1;
    public const int InstallationState = 1;
    public const int InstallationResult = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<DeploymentComponent>))]
public enum DeploymentComponent
{
    CameraAgent
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
    string Source);

public sealed record ImageInstallationIdentity(
    string Source,
    string ImmutableReference,
    string ImageId,
    string Architecture,
    string? ArchiveSha256);

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
    DateTimeOffset CreatedUtc);

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
    DateTimeOffset CompletedUtc);
