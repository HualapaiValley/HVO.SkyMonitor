using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

// Facts about the capture that Current Sky displays, projected from the
// durable gallery record so the page never infers them from in-memory state.
public sealed record CameraAgentCurrentSkyFacts(
    Guid CaptureId,
    long CaptureSequence,
    string? RigId,
    DateTimeOffset ExposureStartedUtc,
    ObservingDay ObservingDay,
    double? ExposureMilliseconds,
    double? Gain,
    double? SensorTemperatureC,
    int? Width,
    int? Height,
    string EvidenceAvailability,
    CameraAgentCurrentSkyCloudFacts Cloud,
    CameraAgentCombinedLineage? CombinedLineage,
    ProfileIdentityDescriptor? ProcessingProfile);

public sealed record CameraAgentCurrentSkyCloudFacts(
    string Availability,
    string? Status,
    string? Quality,
    int? CoverageMillionths,
    int? ConfidenceMillionths);

// The combined stage is a linear arithmetic mean of unregistered causal
// frames (COMB-003); the lineage names the ordered sources so the page can
// say exactly what was averaged without implying registration or stacking.
public sealed record CameraAgentCombinedLineage(
    Guid ArtifactId,
    int SourceCount,
    IReadOnlyList<Guid> SourceArtifactIds,
    string? RecipeName,
    string? RecipeIdentitySha256,
    bool SourcesTruncated);

public static class CameraAgentCurrentSkyFactsProjector
{
    public const string CombinedMethodLabel = "Unregistered causal arithmetic mean";

    public static CameraAgentCurrentSkyFacts Project(CameraAgentGalleryCapture capture, ObservingDayCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(calendar);
        var detail = capture.Detail;
        var cloud = detail?.CloudAssessment;
        var combined = capture.Artifacts
            .Where(static artifact => artifact.Role == FrameArtifactRole.Combined)
            .OrderByDescending(static artifact => artifact.CreatedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        return new CameraAgentCurrentSkyFacts(
            capture.CaptureId,
            capture.CaptureSequence,
            capture.RigId,
            capture.ExposureStartedUtc,
            calendar.Resolve(capture.ExposureStartedUtc),
            detail?.Controls?.EffectiveExposureMilliseconds,
            detail?.Controls?.EffectiveGain,
            detail?.Controls?.EffectiveTemperatureC,
            detail?.Layout?.Width,
            detail?.Layout?.Height,
            detail?.EvidenceAvailability ?? "Unavailable",
            new CameraAgentCurrentSkyCloudFacts(
                cloud?.Availability ?? "Unavailable",
                cloud?.Status,
                cloud?.Quality,
                cloud?.CoverageMillionths,
                cloud?.ConfidenceMillionths),
            combined is null
                ? null
                : new CameraAgentCombinedLineage(
                    combined.ArtifactId,
                    combined.SourceArtifactIds.Count,
                    combined.SourceArtifactIds,
                    combined.Recipe?.Name,
                    combined.Recipe?.IdentitySha256,
                    capture.ArtifactsTruncated),
            detail?.ProcessingProfile);
    }
}
