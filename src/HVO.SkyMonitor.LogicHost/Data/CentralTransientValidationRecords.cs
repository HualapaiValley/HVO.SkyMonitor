using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralTransientEventRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string AgentId { get; set; } = string.Empty;
    public Guid EventId { get; set; }
    public DateTimeOffset EventCreatedUtc { get; set; }
    public ICollection<CentralTransientEventVersionRecord> Versions { get; } = [];
    public ICollection<CentralTransientObservationRecord> Observations { get; } = [];
    public ICollection<CentralTransientAssessmentRecord> Assessments { get; } = [];
}

internal sealed class CentralTransientEventVersionRecord
{
    public Guid EventVersionId { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public int Version { get; set; }
    public int? PreviousVersionNumber { get; set; }
    public Guid? PreviousEventVersionId { get; set; }
    public CentralTransientEventVersionRecord? PreviousVersion { get; set; }
    public DateTimeOffset? PreviousVersionCreatedUtc { get; set; }
    public TransientEventState State { get; set; }
    public DateTimeOffset VersionCreatedUtc { get; set; }
    public DateTimeOffset FirstObservedUtc { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string CanonicalEventJson { get; set; } = string.Empty;
    public string CanonicalEventSha256 { get; set; } = string.Empty;
    public int CanonicalEventByteLength { get; set; }
    public ICollection<CentralTransientEventVersionObservation> Observations { get; } = [];
    public ICollection<CentralTransientEventVersionAssessment> Assessments { get; } = [];
}

internal sealed class CentralTransientObservationRecord
{
    public Guid ObservationId { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public string DetectorInputIdentitySha256 { get; set; } = string.Empty;
    public string CalibrationIdentity { get; set; } = string.Empty;
    public string MaskIdentity { get; set; } = string.Empty;
    public string ProcessingProfileIdentity { get; set; } = string.Empty;
    public Guid? OriginatingCandidateId { get; set; }
    public string ExtractionProducerSchemaVersion { get; set; } = string.Empty;
    public TransientExtractionProducerKind ExtractionProducerKind { get; set; }
    public string ExtractionProducerName { get; set; } = string.Empty;
    public string ExtractionProducerVersion { get; set; } = string.Empty;
    public string ExtractionRecipeIdentitySha256 { get; set; } = string.Empty;
    public string ExtractionReceiptIdentitySha256 { get; set; } = string.Empty;
    public string GeometryJson { get; set; } = string.Empty;
    public string FeaturesJson { get; set; } = string.Empty;
    public Guid SourceReferenceId { get; set; }
    public CentralTransientObservationSourceReference? Source { get; set; }
    public ICollection<CentralTransientObservationBackgroundReference> Backgrounds { get; } = [];
}

internal sealed class CentralTransientObservationSourceReference
{
    public Guid ObservationId { get; set; }
    public CentralTransientObservationRecord? Observation { get; set; }
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public string EvidenceSchemaVersion { get; set; } = string.Empty;
    public Guid EvidenceId { get; set; }
    public string LocatorSchemaVersion { get; set; } = string.Empty;
    public TransientSourceLocatorKind LocatorKind { get; set; }
    public Guid ArtifactId { get; set; }
    public FrameArtifactRole ArtifactRole { get; set; }
    public string ArtifactVariant { get; set; } = string.Empty;
    public string ArtifactRecipeIdentitySha256 { get; set; } = string.Empty;
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
    public DateTimeOffset ObservationStartedUtc { get; set; }
    public DateTimeOffset ObservationEndedUtc { get; set; }
    public TransientTimingQuality TimingQuality { get; set; }
    public string TimingProvenanceSource { get; set; } = string.Empty;
    public string TimingProvenanceVersion { get; set; } = string.Empty;
}

internal sealed class CentralTransientObservationBackgroundReference
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ObservationId { get; set; }
    public CentralTransientObservationRecord? Observation { get; set; }
    public int Ordinal { get; set; }
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public Guid ArtifactId { get; set; }
    public FrameArtifactRole ArtifactRole { get; set; }
    public string ArtifactVariant { get; set; } = string.Empty;
    public string ArtifactRecipeIdentitySha256 { get; set; } = string.Empty;
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
}

internal sealed class CentralTransientAssessmentRecord
{
    public Guid AssessmentId { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public TransientAssessmentAuthority Authority { get; set; }
    public TransientClassification Classification { get; set; }
    public TransientMeteorSeverity? MeteorSeverity { get; set; }
    public int ConfidenceMillionths { get; set; }
    public Guid? SupersedesAssessmentId { get; set; }
    public DateTimeOffset? SupersedesAssessmentCreatedUtc { get; set; }
    public CentralTransientAssessmentRecord? SupersedesAssessment { get; set; }
    public string ProducerSchemaVersion { get; set; } = string.Empty;
    public TransientAssessmentProducerKind ProducerKind { get; set; }
    public string ProducerName { get; set; } = string.Empty;
    public string ProducerVersion { get; set; } = string.Empty;
    public string RecipeIdentitySha256 { get; set; } = string.Empty;
    public string ReceiptSchemaVersion { get; set; } = string.Empty;
    public string ExecutionIdentitySha256 { get; set; } = string.Empty;
    public string OptionsIdentitySha256 { get; set; } = string.Empty;
    public string CanonicalReceiptJson { get; set; } = string.Empty;
    public string CanonicalReceiptSha256 { get; set; } = string.Empty;
    public int CanonicalReceiptByteLength { get; set; }
    public ICollection<CentralTransientAssessmentObservation> EvidenceObservations { get; } = [];
}

internal sealed class CentralTransientEventVersionObservation
{
    public Guid CentralTransientEventId { get; set; }
    public Guid EventVersionId { get; set; }
    public CentralTransientEventVersionRecord? EventVersion { get; set; }
    public int Ordinal { get; set; }
    public Guid ObservationId { get; set; }
    public CentralTransientObservationRecord? Observation { get; set; }
}

internal sealed class CentralTransientEventVersionAssessment
{
    public Guid CentralTransientEventId { get; set; }
    public Guid EventVersionId { get; set; }
    public CentralTransientEventVersionRecord? EventVersion { get; set; }
    public int Ordinal { get; set; }
    public Guid AssessmentId { get; set; }
    public CentralTransientAssessmentRecord? Assessment { get; set; }
}

internal sealed class CentralTransientAssessmentObservation
{
    public Guid CentralTransientEventId { get; set; }
    public Guid AssessmentId { get; set; }
    public CentralTransientAssessmentRecord? Assessment { get; set; }
    public int Ordinal { get; set; }
    public Guid ObservationId { get; set; }
    public CentralTransientObservationRecord? Observation { get; set; }
}

internal sealed class CentralTransientValidationJob
{
    public Guid CentralDerivativeJobId { get; set; }
    public CentralDerivativeJob? Job { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string SubmissionSchemaVersion { get; set; } = string.Empty;
    public string SubmissionIdentitySha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public CentralTransientExtractionReceipt? ExtractionReceipt { get; set; }
    public ICollection<CentralTransientValidationIdentitySlot> IdentitySlots { get; } = [];
}

internal sealed class CentralTransientExtractionReceipt
{
    public Guid CentralDerivativeJobId { get; set; }
    public CentralTransientValidationJob? ValidationJob { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string ExtractionIdentitySha256 { get; set; } = string.Empty;
    public string OptionsIdentitySha256 { get; set; } = string.Empty;
    public string CanonicalReceiptJson { get; set; } = string.Empty;
    public string CanonicalReceiptSha256 { get; set; } = string.Empty;
    public int CanonicalReceiptByteLength { get; set; }
    public ICollection<CentralTransientExtractionSourceReference> Sources { get; } = [];
}

internal sealed class CentralTransientExtractionSourceReference
{
    public Guid CentralDerivativeJobId { get; set; }
    public CentralTransientExtractionReceipt? ExtractionReceipt { get; set; }
    public int Ordinal { get; set; }
    public TransientTemporalPosition Position { get; set; }
    public string DetectorInputIdentitySha256 { get; set; } = string.Empty;
    public Guid CentralArtifactId { get; set; }
    public CentralArtifact? Artifact { get; set; }
    public string EvidenceSchemaVersion { get; set; } = string.Empty;
    public Guid EvidenceId { get; set; }
    public string LocatorSchemaVersion { get; set; } = string.Empty;
    public TransientSourceLocatorKind LocatorKind { get; set; }
    public Guid ArtifactId { get; set; }
    public FrameArtifactRole ArtifactRole { get; set; }
    public string ArtifactVariant { get; set; } = string.Empty;
    public string ArtifactRecipeIdentitySha256 { get; set; } = string.Empty;
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
    public DateTimeOffset ObservationStartedUtc { get; set; }
    public DateTimeOffset ObservationEndedUtc { get; set; }
    public TransientTimingQuality TimingQuality { get; set; }
    public string TimingProvenanceSource { get; set; } = string.Empty;
    public string TimingProvenanceVersion { get; set; } = string.Empty;
}

internal sealed class CentralTransientValidationIdentitySlot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralDerivativeJobId { get; set; }
    public CentralTransientValidationJob? ValidationJob { get; set; }
    public int Ordinal { get; set; }
    public CentralTransientValidationIdentitySlotState State { get; set; }
    public Guid EventId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid ObservationId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid? CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid? PersistedEventVersionId { get; set; }
    public CentralTransientEventVersionRecord? PersistedEventVersion { get; set; }
    public Guid? PersistedObservationId { get; set; }
    public CentralTransientObservationRecord? PersistedObservation { get; set; }
    public Guid? PersistedAssessmentId { get; set; }
    public CentralTransientAssessmentRecord? PersistedAssessment { get; set; }
}

internal enum CentralTransientValidationIdentitySlotState
{
    Reserved,
    Committed,
    Unused
}
