namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

public sealed record CameraAgentTransientOperatorQuery(
    int? PageSize = null,
    string? Cursor = null);

public sealed record CameraAgentTransientOperatorPage(
    IReadOnlyList<CameraAgentTransientOperatorCandidate> Items,
    string? NextCursor);

public sealed record CameraAgentTransientOperatorCandidate(
    Guid CandidateId,
    Guid EventId,
    string CandidateState,
    string EventState,
    string WorkflowPhase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string CausalEvidenceState,
    string CenteredEvidenceState,
    string AssessmentEvidenceState,
    string FinalEvidenceState);

public sealed record CameraAgentTransientOperatorDetail(
    CameraAgentTransientOperatorCandidate Candidate,
    CameraAgentTransientCandidateEvidence CandidateEvidence,
    CameraAgentTransientExtractionEvidence CausalEvidence,
    CameraAgentTransientExtractionEvidence CenteredEvidence,
    CameraAgentTransientAssessmentEvidence AssessmentEvidence,
    CameraAgentTransientFinalEvidence FinalEvidence,
    IReadOnlyList<CameraAgentTransientOperatorSource>? Sources = null);

// A durable source frame the candidate was extracted from, resolved to the
// retained raw capture so the operator can open the exact evidence.
public sealed record CameraAgentTransientOperatorSource(
    int Ordinal,
    Guid EvidenceId,
    Guid ArtifactId,
    HVO.SkyMonitor.AgentCore.FrameArtifactRole? Role,
    Guid CaptureId,
    long CaptureSequence,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ObservationStartedUtc,
    DateTimeOffset ObservationEndedUtc);

public sealed record CameraAgentTransientCandidateEvidence(
    string State,
    DateTimeOffset? CreatedUtc,
    Guid? CenterEvidenceId,
    int ContextSourceCount,
    CameraAgentTransientGeometrySummary? Geometry,
    CameraAgentTransientFeatureSummary? Features,
    IReadOnlyList<string> ReasonCodes);

public sealed record CameraAgentTransientGeometrySummary(
    int CoordinateWidth,
    int CoordinateHeight,
    double X,
    double Y,
    double Width,
    double Height,
    int PolylinePointCount);

public sealed record CameraAgentTransientFeatureSummary(
    double LengthPixels,
    double MeanWidthPixels,
    double MaximumWidthPixels,
    long IntegratedSignalAdu,
    ushort PeakSignalAdu,
    int SaturatedSampleCount,
    int FragmentCount);

public sealed record CameraAgentTransientExtractionEvidence(
    string State,
    bool? CenteredContextConverged = null,
    int? SourceCount = null,
    int? CandidateCount = null,
    string? IdentitySha256 = null);

public sealed record CameraAgentTransientAssessmentEvidence(
    string State,
    Guid? AssessmentId = null,
    string? Authority = null,
    string? Classification = null,
    string? MeteorSeverity = null,
    int? ConfidenceMillionths = null,
    int? EvidenceObservationCount = null,
    IReadOnlyList<string>? ReasonCodes = null,
    string? IdentitySha256 = null);

public sealed record CameraAgentTransientFinalEvidence(
    string State,
    string? EventState = null,
    int? EventVersion = null,
    DateTimeOffset? FirstObservedUtc = null,
    DateTimeOffset? LastObservedUtc = null,
    int? ObservationCount = null,
    int? AssessmentCount = null,
    string? ReceiptIdentitySha256 = null);

public interface ICameraAgentTransientOperatorProjection
{
    ValueTask<CameraAgentTransientOperatorPage> GetPageAsync(
        CameraAgentTransientOperatorQuery query,
        CancellationToken cancellationToken);

    ValueTask<CameraAgentTransientOperatorDetail?> GetCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken);
}

public sealed class CameraAgentTransientOperatorQueryException : Exception
{
    public CameraAgentTransientOperatorQueryException()
    {
    }

    public CameraAgentTransientOperatorQueryException(string message)
        : base(message)
    {
    }

    public CameraAgentTransientOperatorQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
