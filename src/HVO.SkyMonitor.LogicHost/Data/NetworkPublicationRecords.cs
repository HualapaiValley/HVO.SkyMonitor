namespace HVO.SkyMonitor.LogicHost.Data;

internal enum ObservatoryProfileVisibility
{
    Private,
    Public
}

internal enum ObservatoryLocationDisclosureLevel
{
    Hidden,
    Region,
    Approximate,
    Exact
}

internal enum PublicRecordSubjectKind
{
    LogicalCamera,
    Artifact,
    TransientEvent,
    TransientDerivative
}

internal enum PublicationDecisionState
{
    Released,
    Withdrawn
}

internal sealed class ObservatoryPublicationProfileVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public int Version { get; set; }
    public string PublicSlug { get; set; } = string.Empty;
    public string PublicDisplayName { get; set; } = string.Empty;
    public string PublicDescription { get; set; } = string.Empty;
    public ObservatoryProfileVisibility ProfileVisibility { get; set; }
    public bool PublishEnvironmentalSummary { get; set; }
    public bool AllowAutomaticVerifiedEventInclusion { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string CanonicalSha256 { get; set; } = string.Empty;
}

internal sealed class ObservatoryLocationDisclosureVersion
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Observatory? Observatory { get; set; }
    public int Version { get; set; }
    public ObservatoryLocationDisclosureLevel DisclosureLevel { get; set; }
    public string? RegionCode { get; set; }
    public string? RegionLabel { get; set; }
    public double? PublicLatitudeDegrees { get; set; }
    public double? PublicLongitudeDegrees { get; set; }
    public double? PublicPrecisionMeters { get; set; }
    public Guid? SourceObservatoryLocationVersionId { get; set; }
    public ObservatoryLocationVersion? SourceObservatoryLocationVersion { get; set; }
    public DateTimeOffset EffectiveFromUtc { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string CanonicalSha256 { get; set; } = string.Empty;
}

internal sealed class PublicRecordPublicationDecision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid PublicId { get; init; } = Guid.NewGuid();
    public Guid AuthorityObservatoryId { get; set; }
    public Observatory? AuthorityObservatory { get; set; }
    public PublicRecordSubjectKind SubjectKind { get; set; }
    public PublicationDecisionState State { get; set; }
    public Guid? LogicalCameraId { get; set; }
    public LogicalCamera? LogicalCamera { get; set; }
    public Guid? CentralArtifactId { get; set; }
    public CentralArtifact? CentralArtifact { get; set; }
    public Guid? CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? CentralTransientEvent { get; set; }
    public Guid? SourceEventVersionId { get; set; }
    public CentralTransientEventVersionRecord? SourceEventVersion { get; set; }
    public Guid? CentralTransientDerivativeId { get; set; }
    public CentralTransientDerivativeRecord? CentralTransientDerivative { get; set; }
    public string ProjectionSchemaVersion { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset? EffectiveUntilUtc { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public Guid? SupersedesDecisionId { get; set; }
    public PublicRecordPublicationDecision? SupersedesDecision { get; set; }
}
