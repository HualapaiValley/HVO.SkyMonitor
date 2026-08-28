using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class ObservatoryLocationVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public long Version { get; set; }
    public string CanonicalSha256 { get; set; } = string.Empty;
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
    public double LatitudeDegrees { get; set; }
    public double LongitudeDegrees { get; set; }
    public double ElevationMeters { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public double? AllowedDeploymentRadiusMeters { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
}

internal sealed class DeviceDeploymentLocationVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RegistrationId { get; set; }
    public DeviceRegistration? Registration { get; set; }
    public Guid? DevicePublicId { get; set; }
    public Guid ObservatoryId { get; set; }
    public Guid ObservatoryLocationVersionId { get; set; }
    public ObservatoryLocationVersion? ObservatoryLocationVersion { get; set; }
    public long ObservatoryLocationVersionNumber { get; set; }
    public string ObservatoryLocationCanonicalSha256 { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public long Version { get; set; }
    public string CanonicalSha256 { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DeploymentLocationSourceKind SourceKind { get; set; }
    public double? HorizontalAccuracyMeters { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveUntilUtc { get; set; }
    public double LatitudeDegrees { get; set; }
    public double LongitudeDegrees { get; set; }
    public double ElevationMeters { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public DeploymentLocationResolutionStatus Status { get; set; }
    public string? ReasonCode { get; set; }
    public DateTimeOffset ProposedAtUtc { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    public string? ResolvedByUserId { get; set; }
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
    public ICollection<DeploymentLocationResolutionAudit> ResolutionAudits { get; } = [];
    public DeploymentLocationReconciliationWork? ReconciliationWork { get; set; }
}

internal static class DeploymentLocationReconciliationStatuses
{
    internal const string Pending = "Pending";
    internal const string Processing = "Processing";
    internal const string Retry = "Retry";
    internal const string Completed = "Completed";
}

internal sealed class DeploymentLocationReconciliationWork
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceDeploymentLocationVersionId { get; set; }
    public DeviceDeploymentLocationVersion? DeploymentLocation { get; set; }
    public Guid AuthorityConcurrencyToken { get; set; }
    public string Status { get; set; } = DeploymentLocationReconciliationStatuses.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? LastErrorCode { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long? CaptureCount { get; set; }
    public DateTimeOffset? DiscoveryCutoffUtc { get; set; }
    public long DiscoveredCaptureCount { get; set; }
    public DateTimeOffset? DiscoveryCursorFirstReceivedAtUtc { get; set; }
    public Guid? DiscoveryCursorCentralFrameId { get; set; }
    public long CompletedCaptureCount { get; set; }
    public long ScheduledArtifactCount { get; set; }
    public DateTimeOffset? LastCompletedFirstReceivedAtUtc { get; set; }
    public Guid? LastCompletedCentralFrameId { get; set; }
    public DateTimeOffset? ActiveBatchUpperFirstReceivedAtUtc { get; set; }
    public Guid? ActiveBatchUpperCentralFrameId { get; set; }
    public int ActiveBatchCaptureCount { get; set; }
    public Guid? SchedulingCentralFrameId { get; set; }
    public Guid? SchedulingCentralArtifactId { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<DeploymentLocationReconciliationCapture> Captures { get; } = [];
}

internal sealed class DeploymentLocationReconciliationCapture
{
    public Guid DeploymentLocationReconciliationWorkId { get; set; }
    public DeploymentLocationReconciliationWork? Work { get; set; }
    public Guid AuthorityConcurrencyToken { get; set; }
    public Guid CentralFrameId { get; set; }
    public CentralFrame? CentralFrame { get; set; }
    public DateTimeOffset FirstReceivedAtUtc { get; set; }
}

internal sealed class DeploymentLocationResolutionAudit
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DeviceDeploymentLocationVersionId { get; set; }
    public DeviceDeploymentLocationVersion? DeploymentLocation { get; set; }
    public Guid RegistrationId { get; set; }
    public DeploymentLocationResolutionStatus? PreviousStatus { get; set; }
    public DeploymentLocationResolutionStatus NewStatus { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

internal sealed class CentralCaptureLocation
{
    public Guid CentralFrameId { get; set; }
    public CentralFrame? CentralFrame { get; set; }
    public Guid? DeviceDeploymentLocationVersionId { get; set; }
    public DeviceDeploymentLocationVersion? DeploymentLocation { get; set; }
    public string LocationId { get; set; } = string.Empty;
    public long Version { get; set; }
    public string Source { get; set; } = string.Empty;
    public double? HorizontalAccuracyMeters { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveUntilUtc { get; set; }
}

internal enum CentralCaptureLocationEvidenceState
{
    ReportedUnresolved,
    ReportedResolved,
    Mismatch
}
