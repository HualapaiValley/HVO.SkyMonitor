using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record CameraAgentEventContextFrame(
    int Position,
    CameraAgentTransientOperatorSource? Source,
    CameraAgentCaptureDetailView? Capture,
    Guid? ExecutionId)
{
    internal CameraAgentPresentationSlot? Image => Source is null ? null : Capture?.Presentation.Stages
        .SingleOrDefault(slot => slot.ArtifactId == Source.ArtifactId &&
            slot.Availability == CameraAgentPresentationSlotAvailability.Available && slot.PreviewUrl is not null);
    internal string PositionLabel => Position == 0 ? "N · candidate endpoint" : FormattableString.Invariant($"N{Position:+0;-0}");
}

internal sealed record CameraAgentEventEvidenceView(
    CameraAgentTransientOperatorDetail Detail,
    IReadOnlyList<CameraAgentEventContextFrame> Frames)
{
    internal CameraAgentEventContextFrame? Reference => Frames.SingleOrDefault(static frame => frame.Position == 0);
    internal DateTimeOffset RecordedUtc => Reference?.Source?.ObservationStartedUtc ?? Detail.Candidate.CreatedUtc;
}

internal interface ICameraAgentEventEvidenceUiService
{
    ValueTask<OperatorUiResult<CameraAgentEventEvidenceView>> GetAsync(
        Guid candidateId, bool includeContext, CancellationToken cancellationToken);
}

// Compose existing protected reads, bounded to this candidate's five proven positions.
// A missing identity never triggers a sequence-neighbour or latest-image lookup.
internal sealed class CameraAgentEventEvidenceUiService(
    ICameraAgentTransientUiService transients,
    ICameraAgentOperatorUiService captures,
    ICameraAgentProcessingGraphUiService runs,
    ICameraAgentCapturePresentationProjector capturePresentation) : ICameraAgentEventEvidenceUiService
{
    public async ValueTask<OperatorUiResult<CameraAgentEventEvidenceView>> GetAsync(
        Guid candidateId, bool includeContext, CancellationToken cancellationToken)
    {
        var result = await transients.GetCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is not { } detail || detail.Candidate.CandidateId != candidateId)
        {
            return OperatorUiResult<CameraAgentEventEvidenceView>.Failure(
                result.Kind == OperatorUiResultKind.Unauthorized ? OperatorUiResultKind.Unauthorized :
                result.Kind == OperatorUiResultKind.NotFound || result.IsSuccess ? OperatorUiResultKind.NotFound : OperatorUiResultKind.Unavailable,
                "The requested local candidate evidence is unavailable.");
        }
        var sources = PositionSources(detail);
        var frames = new List<CameraAgentEventContextFrame>(5);
        for (var position = -2; position <= 2; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = sources.Where(source => source.Position == position).ToArray();
            var source = matches.Length == 1 ? matches[0].Source : null;
            CameraAgentCaptureDetailView? capture = null;
            Guid? executionId = null;
            if (source is not null && (includeContext || position == 0))
            {
                // Read retained metadata for this source without reconstructing unrelated products.
                // The protected preview endpoint validates the exact bytes on the browser request.
                var captureResult = await captures.GetGalleryCaptureAsync(source.CaptureId, cancellationToken).ConfigureAwait(false);
                if (captureResult.Kind == OperatorUiResultKind.Unauthorized)
                {
                    return Denied();
                }
                if (captureResult.IsSuccess && captureResult.Value is { } retained &&
                    retained.CaptureId == source.CaptureId &&
                    retained.Artifacts.SingleOrDefault(artifact => artifact.ArtifactId == source.ArtifactId && artifact.Role == source.Role) is { } artifact)
                {
                    var exactSource = retained with { Artifacts = [artifact] };
                    capture = new(retained, capturePresentation.Project(exactSource));
                }
                if (includeContext)
                {
                    var run = await runs.GetLiveExecutionIdAsync(source.CaptureId, cancellationToken).ConfigureAwait(false);
                    if (run.Kind == OperatorUiResultKind.Unauthorized)
                    {
                        return Denied();
                    }
                    executionId = run.IsSuccess && run.Value?.ExecutionId is { } id && id != Guid.Empty ? id : null;
                }
            }
            frames.Add(new(position, source, capture, executionId));
        }
        return OperatorUiResult<CameraAgentEventEvidenceView>.Success(new(detail, frames));
    }

    private static (int Position, CameraAgentTransientOperatorSource Source)[] PositionSources(CameraAgentTransientOperatorDetail detail)
    {
        if (detail.CenteredEvidence is { State: "Available", CenteredContextConverged: true } &&
            detail.CenteredSources is { Count: > 0 })
        {
            return detail.CenteredSources.Where(static source => source.RelativePosition is >= -2 and <= 2)
                .Select(static source => (source.RelativePosition!.Value, source)).ToArray();
        }
        var sources = detail.Sources ?? [];
        var centers = sources.Where(source => source.EvidenceId == detail.CandidateEvidence.CenterEvidenceId).ToArray();
        if (centers.Length != 1)
        {
            return [];
        }
        var centerOrdinal = centers[0].Ordinal;
        // The candidate journal is causal; future positions require the centered receipt.
        return sources.Where(source => source.Ordinal - centerOrdinal is >= -2 and <= 0)
            .Select(source => (source.Ordinal - centerOrdinal, source)).ToArray();
    }

    private static OperatorUiResult<CameraAgentEventEvidenceView> Denied() =>
        OperatorUiResult<CameraAgentEventEvidenceView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
}

internal static class CameraAgentEventFacts
{
    internal static bool HasRetainedAssessment(CameraAgentTransientAssessmentEvidence? assessment)
        => assessment is { State: "Available" };

    internal static string Classification(CameraAgentTransientAssessmentEvidence? assessment) => assessment is not { State: "Available" }
        ? "Not assessed"
        : assessment.Classification switch
        {
            "Meteor" => assessment.MeteorSeverity == "Fireball" ? "Fireball" : "Meteor",
            "Satellite" => "Satellite",
            "Aircraft" => "Aircraft",
            "SensorArtifact" => "Sensor artifact",
            "EnvironmentalArtifact" => "Environmental artifact",
            "Unknown" => "Unresolved",
            _ => "Not recorded"
        };

    internal static string ClassificationKey(CameraAgentTransientAssessmentEvidence? assessment)
        => Classification(assessment) switch
        {
            "Fireball" => "fireball",
            "Meteor" => "meteor",
            "Satellite" => "satellite",
            "Aircraft" => "aircraft",
            "Sensor artifact" => "sensor-artifact",
            "Environmental artifact" => "environmental-artifact",
            "Unresolved" => "unresolved",
            _ => "unassessed"
        };

    internal static string Title(CameraAgentTransientAssessmentEvidence? assessment)
        => HasRetainedAssessment(assessment) ? $"{Classification(assessment)} candidate" : "Candidate not assessed";

    internal static string Confidence(CameraAgentTransientAssessmentEvidence? assessment)
        => assessment is { State: "Available", ConfidenceMillionths: >= 0 and <= 1_000_000 } && assessment.ConfidenceMillionths is { } score
            ? (score / 10000d).ToString("0.##", CultureInfo.InvariantCulture) + "% · local score"
            : "Not recorded";
}
