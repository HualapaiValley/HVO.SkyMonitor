using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Optional retained capture fields absent from the reconstruction descriptor. Identity binds these fields to
/// the descriptor, not to a current submission or host configuration. ProcessingCompletedUtc projects the
/// selected retained GraphExecutionEvidenceV1.CompletedUtc, never artifact creation or wall-clock time.
/// </summary>
public sealed record FrameFactsRetainedCaptureExtrasV1(
    string DescriptorIdentitySha256,
    DateTimeOffset? SourceTimestamp = null,
    double? CadenceSeconds = null,
    DateTimeOffset? ProcessingCompletedUtc = null);

/// <summary>
/// Frozen local offset at one capture instant. RuleIdentity identifies retained zone rules or the retained
/// conversion evidence. A zone name alone is insufficient; construction/evaluation never resolves host zone rules.
/// </summary>
public sealed record FrameFactsRetainedLocalTimeV1(
    string DescriptorIdentitySha256, DateTimeOffset CaptureUtc, string ZoneId, TimeSpan Offset, string RuleIdentity);

/// <summary>Projection of retained stack product evidence, with no pixels or mutable processing configuration.</summary>
public sealed record FrameFactsRetainedStackV1(
    string DescriptorIdentitySha256, string Identity, IReadOnlyList<Guid> SourceArtifactIds, TimeSpan TotalIntegration);

/// <summary>
/// Projection of a completed retained association. Availability is the retained association's decision;
/// a supplied observation cannot override missing, stale or conflicting association evidence.
/// </summary>
public sealed record FrameFactsRetainedEnvironmentV1(
    Guid CaptureId, EnvironmentalObservationKind Kind, FrameFactsAvailability Availability,
    string AssociationIdentity, DateTimeOffset ExposureFromUtc, DateTimeOffset ExposureThroughUtc,
    EnvironmentalObservationFactV1? Observation);

/// <summary>
/// Retained inputs only. Null optional fields represent older records or evidence that was not retained.
/// The builder copies scalar values and provenance immediately; it keeps none of these input objects.
/// </summary>
public sealed record FrameFactsRetainedInputV1(
    ReconstructionDescriptor Descriptor,
    FrameFactsRetainedCaptureExtrasV1? CaptureExtras = null,
    FrameFactsRetainedStackV1? Stack = null,
    DeploymentLocationSnapshot? Location = null,
    FrameFactsRetainedLocalTimeV1? LocalTime = null,
    ProjectedSceneV1? Astronomy = null,
    IReadOnlyList<FrameFactsRetainedEnvironmentV1>? Environment = null);

/// <summary>Frozen source identity and semantic nature of one resolved fact, including unavailable facts.</summary>
public sealed record FrameFactsFactProvenance(string SourceContract, string SourceIdentity, string Nature);
