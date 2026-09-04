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
    ProfileIdentityDescriptor? ProcessingProfile,
    bool CombinedLineageUnavailable = false);

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
    string? RecipeIdentitySha256);

public static class CameraAgentCurrentSkyFactsProjector
{
    public const string CombinedMethodLabel = "Unregistered causal arithmetic mean";

    // The lineage describes the combined artifact the presentation displays.
    // Without that identity, the newest combined artifact is described only
    // when the artifact list is complete; a bounded list could hide it.
    public static CameraAgentCurrentSkyFacts Project(
        CameraAgentGalleryCapture capture,
        ObservingDayCalendar calendar,
        Guid? combinedArtifactId = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(calendar);
        var detail = capture.Detail;
        var cloud = detail?.CloudAssessment;
        var combinedArtifacts = capture.Artifacts.Where(static artifact => artifact.Role == FrameArtifactRole.Combined).ToArray();
        var combined = combinedArtifactId is { } displayed
            ? combinedArtifacts.FirstOrDefault(artifact => artifact.ArtifactId == displayed)
            : capture.ArtifactsTruncated
                ? null
                : combinedArtifacts.OrderByDescending(static artifact => artifact.CreatedUtc ?? DateTimeOffset.MinValue).FirstOrDefault();
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
                    combined.Recipe?.IdentitySha256),
            detail?.ProcessingProfile,
            // Combined lineage could not be described: the bounded artifact list
            // either hid the displayed artifact or leaves the newest one unknowable.
            combined is null && capture.ArtifactsTruncated && (combinedArtifactId is not null || combinedArtifacts.Length > 0));
    }
}
