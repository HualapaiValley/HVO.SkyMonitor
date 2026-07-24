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
    LegacyIncomplete = 0,
    ReportedUnresolved = 1,
    ReportedResolved = 2,
    Mismatch = 3
}
