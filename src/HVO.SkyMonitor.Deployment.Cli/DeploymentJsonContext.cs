using System.Text.Json.Serialization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstanceManifest))]
[JsonSerializable(typeof(InstallationState))]
[JsonSerializable(typeof(InstallationResult))]
[JsonSerializable(typeof(ApplicationIdentityBinding))]
[JsonSerializable(typeof(LifecycleOperationState))]
[JsonSerializable(typeof(LifecycleResult))]
[JsonSerializable(typeof(LifecycleUpgradeNotesEvent))]
[JsonSerializable(typeof(InstanceBackupManifest))]
[JsonSerializable(typeof(StateSnapshotManifest))]
[JsonSerializable(typeof(LifecycleStateSnapshotRestoreEvent))]
[JsonSerializable(typeof(CatalogInstallationIdentity))]
[JsonSerializable(typeof(PurgeDeletionEvidence))]
[JsonSerializable(typeof(CatalogGarbageCollectionEvidence))]
[JsonSerializable(typeof(CatalogInstallOperationEvidence))]
[JsonSerializable(typeof(OwnerRecoveryOperationState))]
[JsonSerializable(typeof(OwnerRecoveryResult))]
[JsonSerializable(typeof(CameraAgentStatePreflightReport))]
[JsonSerializable(typeof(CameraAgentStateResetEvidence))]
[JsonSerializable(typeof(CameraAgentStateResetResult))]
[JsonSerializable(typeof(CameraAgentImageReleaseEvidence))]
[JsonSerializable(typeof(HostPrerequisiteReport))]
[JsonSerializable(typeof(HostPolicyResult))]
internal sealed partial class DeploymentJsonContext : JsonSerializerContext;
