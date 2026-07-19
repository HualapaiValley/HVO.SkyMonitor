using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public enum TransientEventState
{
    Pending,
    Provisional,
    Validated,
    Rejected,
    NeedsReview
}

public enum TransientCandidateState
{
    PendingContext,
    Provisional,
    Complete,
    Rejected
}

public enum TransientAssessmentAuthority
{
    Provisional,
    Authoritative
}

public enum TransientClassification
{
    Unknown,
    Meteor,
    Satellite,
    Aircraft,
    SensorArtifact,
    EnvironmentalArtifact
}

public enum TransientMeteorSeverity
{
    Meteor,
    Fireball
}

public enum TransientSourceLocatorKind
{
    WholeArtifact
}

public enum TransientTimingQuality
{
    Reported,
    Estimated,
    Unknown
}

public enum TransientAssessmentProducerKind
{
    DeterministicAlgorithm
}

/// <summary>Identifies supported V1 observation extraction producer families.</summary>
public enum TransientExtractionProducerKind
{
    DeterministicAlgorithm
}

public enum TransientReasonKind
{
    Supporting,
    Contradicting,
    Limitation
}

public enum TransientReviewDisposition
{
    NeedsReview,
    Confirmed,
    Overridden,
    Rejected
}

public enum TransientNotificationState
{
    Pending,
    Sent,
    Failed,
    Suppressed,
    Superseded
}

public enum TransientDerivativeKind
{
    Crop,
    Preview,
    Mask,
    Overlay,
    Reconstruction
}

public enum TransientDerivativeLimitation
{
    IntraExposureTimingUnavailable,
    SaturatedPhotometryUnrecoverable
}

/// <summary>Identifies one immutable artifact without exposing storage location or credentials.</summary>
public sealed record TransientArtifactReferenceV1(
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] FrameArtifactRole Role,
    [property: JsonRequired] string Variant,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] string ChecksumSha256);

/// <summary>V1 locator for an entire immutable still-image artifact.</summary>
public sealed record TransientWholeArtifactLocatorV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] TransientSourceLocatorKind Kind,
    [property: JsonRequired] TransientArtifactReferenceV1 Artifact)
{
    public const string CurrentSchemaVersion = "transient-source-locator-v1";
}

/// <summary>Identifies the source and version that supplied observation timing.</summary>
public sealed record TransientTimingProvenanceV1(
    [property: JsonRequired] string Source,
    [property: JsonRequired] string Version);

/// <summary>
/// Representation-neutral evidence identity and half-open UTC observation interval. V1 locates a whole artifact;
/// later locator schemas may address ranges within temporal artifacts.
/// </summary>
public sealed record TransientSourceEvidenceReferenceV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid EvidenceId,
    [property: JsonRequired] TransientWholeArtifactLocatorV1 Locator,
    [property: JsonRequired] DateTimeOffset ObservationStartedUtc,
    [property: JsonRequired] DateTimeOffset ObservationEndedUtc,
    [property: JsonRequired] TransientTimingQuality TimingQuality,
    [property: JsonRequired] TransientTimingProvenanceV1 TimingProvenance)
{
    public const string CurrentSchemaVersion = "transient-source-evidence-v1";
}

/// <summary>A point in continuous detector pixel-edge coordinates.</summary>
public readonly record struct TransientPointV1(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y);

/// <summary>A positive-size region in continuous detector pixel-edge coordinates.</summary>
public sealed record TransientBoundingRegionV1(
    [property: JsonRequired] double X,
    [property: JsonRequired] double Y,
    [property: JsonRequired] double Width,
    [property: JsonRequired] double Height);

/// <summary>A measured profile sample at a normalized position from zero through one million.</summary>
public sealed record TransientProfileSampleV1(
    [property: JsonRequired] int PositionMillionths,
    [property: JsonRequired] double Value);

/// <summary>Authoritative structured geometry bound to one source-evidence representation.</summary>
public sealed record TransientGeometryV1(
    [property: JsonRequired] Guid SourceEvidenceId,
    [property: JsonRequired] int CoordinateWidth,
    [property: JsonRequired] int CoordinateHeight,
    [property: JsonRequired] TransientBoundingRegionV1 Bounds,
    [property: JsonRequired] IReadOnlyList<TransientPointV1> Polyline);

/// <summary>Measured linear-signal features bound to one source-evidence representation.</summary>
public sealed record TransientFeaturesV1(
    [property: JsonRequired] Guid SourceEvidenceId,
    [property: JsonRequired] double LengthPixels,
    [property: JsonRequired] double MeanWidthPixels,
    [property: JsonRequired] double MaximumWidthPixels,
    [property: JsonRequired] long IntegratedSignalAdu,
    [property: JsonRequired] ushort PeakSignalAdu,
    [property: JsonRequired] int SaturatedSampleCount,
    [property: JsonRequired] int FragmentCount,
    [property: JsonRequired] IReadOnlyList<TransientProfileSampleV1> WidthProfile,
    [property: JsonRequired] IReadOnlyList<TransientProfileSampleV1> BrightnessProfile);

/// <summary>One event-bearing observation and its authoritative structured evidence.</summary>
public sealed record TransientObservationV1(
    [property: JsonRequired] Guid ObservationId,
    [property: JsonRequired] int Ordinal,
    [property: JsonRequired] TransientSourceEvidenceReferenceV1 Source,
    [property: JsonRequired] IReadOnlyList<TransientArtifactReferenceV1> BackgroundArtifacts,
    [property: JsonRequired] TransientObservationProvenanceV1 Provenance,
    [property: JsonRequired] TransientObservationExtractionV1 Extraction,
    [property: JsonRequired] TransientGeometryV1 Geometry,
    [property: JsonRequired] TransientFeaturesV1 Features);

/// <summary>Detector-input and profile identities used to measure one observation or candidate.</summary>
public sealed record TransientObservationProvenanceV1(
    [property: JsonRequired] string DetectorInputIdentitySha256,
    [property: JsonRequired] string CalibrationIdentity,
    [property: JsonRequired] string MaskIdentity,
    [property: JsonRequired] string ProcessingProfileIdentity);

/// <summary>Versioned deterministic producer that extracted observation geometry and features.</summary>
public sealed record TransientExtractionProducerV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] TransientExtractionProducerKind Kind,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Version)
{
    public const string CurrentSchemaVersion = "transient-extraction-producer-v1";
}

/// <summary>
/// Append-only extraction lineage for observation geometry and features. Candidate identity is optional for
/// retrospective extraction that did not originate from a persisted candidate.
/// </summary>
public sealed record TransientObservationExtractionV1(
    [property: JsonRequired] Guid? OriginatingCandidateId,
    [property: JsonRequired] TransientExtractionProducerV1 Producer,
    [property: JsonRequired] string RecipeIdentitySha256);

/// <summary>Versioned deterministic producer identity. Future model or service producers use a later schema.</summary>
public sealed record TransientAssessmentProducerV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] TransientAssessmentProducerKind Kind,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Version)
{
    public const string CurrentSchemaVersion = "transient-assessment-producer-v1";
}

/// <summary>A stable machine-readable reason bound to zero or more event observations.</summary>
public sealed record TransientReasonV1(
    [property: JsonRequired] string Code,
    [property: JsonRequired] TransientReasonKind Kind,
    [property: JsonRequired] IReadOnlyList<Guid> ObservationIds);

/// <summary>An append-only assessment; supersession never removes the prior producer version.</summary>
public sealed record TransientAssessmentV1(
    [property: JsonRequired] Guid AssessmentId,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] TransientAssessmentAuthority Authority,
    [property: JsonRequired] TransientClassification Classification,
    [property: JsonRequired] TransientMeteorSeverity? MeteorSeverity,
    [property: JsonRequired] int ConfidenceMillionths,
    [property: JsonRequired] IReadOnlyList<TransientReasonV1> Reasons,
    [property: JsonRequired] TransientAssessmentProducerV1 Producer,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] IReadOnlyList<Guid> EvidenceObservationIds,
    [property: JsonRequired] Guid? SupersedesAssessmentId);

/// <summary>A reviewer-authored classification override that does not mutate an algorithm assessment.</summary>
public sealed record TransientReviewOverrideV1(
    [property: JsonRequired] TransientClassification Classification,
    [property: JsonRequired] TransientMeteorSeverity? MeteorSeverity,
    [property: JsonRequired] int ConfidenceMillionths);

/// <summary>An append-only review decision over one retained assessment.</summary>
public sealed record TransientReviewV1(
    [property: JsonRequired] Guid ReviewId,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] string ReviewerIdentity,
    [property: JsonRequired] TransientReviewDisposition Disposition,
    [property: JsonRequired] Guid AssessmentId,
    [property: JsonRequired] TransientReviewOverrideV1? Override,
    [property: JsonRequired] IReadOnlyList<string> ReasonCodes,
    [property: JsonRequired] Guid? SupersedesReviewId);

/// <summary>An append-only notification status record; transport and delivery policy remain host-owned.</summary>
public sealed record TransientNotificationV1(
    [property: JsonRequired] Guid NotificationId,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] string Channel,
    [property: JsonRequired] TransientNotificationState State,
    [property: JsonRequired] Guid AssessmentId,
    [property: JsonRequired] string? ReasonCode,
    [property: JsonRequired] Guid? SupersedesNotificationId);

/// <summary>A traceable derivative reference; overlays are views and never replace structured geometry.</summary>
public sealed record TransientDerivativeV1(
    [property: JsonRequired] Guid DerivativeId,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] TransientDerivativeKind Kind,
    [property: JsonRequired] TransientArtifactReferenceV1 Artifact,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] IReadOnlyList<Guid> OrderedSourceEvidenceIds,
    [property: JsonRequired] IReadOnlyList<TransientDerivativeLimitation> Limitations);

/// <summary>A versioned detector candidate that remains separate from one-capture artifact sets.</summary>
public sealed record TransientCandidateV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid CandidateId,
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] TransientCandidateState State,
    [property: JsonRequired] DateTimeOffset CreatedUtc,
    [property: JsonRequired] Guid CenterEvidenceId,
    [property: JsonRequired] IReadOnlyList<TransientSourceEvidenceReferenceV1> ContextSources,
    [property: JsonRequired] TransientObservationProvenanceV1 Provenance,
    [property: JsonRequired] TransientObservationExtractionV1 Extraction,
    [property: JsonRequired] TransientGeometryV1? Geometry,
    [property: JsonRequired] TransientFeaturesV1? Features,
    [property: JsonRequired] IReadOnlyList<TransientReasonV1> Reasons)
{
    public const string CurrentSchemaVersion = "transient-candidate-v1";
}

/// <summary>
/// One immutable event version. Event identity is caller-owned and deliberately independent of exact artifact lists,
/// chunk boundaries, or derivative packaging.
/// </summary>
public sealed record TransientEventV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] Guid EventVersionId,
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid? PreviousEventVersionId,
    [property: JsonRequired] DateTimeOffset? PreviousVersionCreatedUtc,
    [property: JsonRequired] string AgentId,
    [property: JsonRequired] TransientEventState State,
    [property: JsonRequired] DateTimeOffset EventCreatedUtc,
    [property: JsonRequired] DateTimeOffset VersionCreatedUtc,
    [property: JsonRequired] DateTimeOffset FirstObservedUtc,
    [property: JsonRequired] DateTimeOffset LastObservedUtc,
    [property: JsonRequired] IReadOnlyList<TransientObservationV1> Observations,
    [property: JsonRequired] IReadOnlyList<TransientAssessmentV1> Assessments,
    [property: JsonRequired] IReadOnlyList<TransientReviewV1> Reviews,
    [property: JsonRequired] IReadOnlyList<TransientNotificationV1> Notifications,
    [property: JsonRequired] IReadOnlyList<TransientDerivativeV1> Derivatives)
{
    public const string CurrentSchemaVersion = "transient-event-v1";
}
