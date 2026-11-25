using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Services;

public enum CenteredLiveMode
{
    Centered,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Single denotes one exposure.")]
    Single
}

public enum CenteredArchiveRevisionRole
{
    Initial,
    Earlier,
    LastPublishedLive,
    LaterArchiveEnrichment
}

public sealed record CameraAgentCenteredArchiveRequest(
    string? BaseId = null,
    string? RevisionId = null,
    Guid? SelectionId = null)
{
    public bool HasExplicitTarget =>
        !string.IsNullOrWhiteSpace(BaseId) ||
        !string.IsNullOrWhiteSpace(RevisionId) ||
        SelectionId is not null;
}

public sealed record CameraAgentCenteredArchiveRevisionEntry(
    string RevisionId,
    long Order,
    DateTimeOffset PublishedUtc,
    bool SelectedLive,
    bool IsInitial,
    bool IsLastLive,
    bool IsLaterEnrichment,
    CenteredArchiveRevisionRole Role,
    string Label,
    int ReadyLayerCount,
    int PartialLayerCount,
    int WaitingLayerCount);

public sealed record CameraAgentCenteredArchiveBaseSummary(
    CenteredViewArchiveBase ArchiveBase,
    IReadOnlyList<CameraAgentCenteredArchiveRevisionEntry> Revisions);

public sealed record CameraAgentCenteredArchiveView(
    Guid ReferenceCaptureId,
    IReadOnlyList<CameraAgentCenteredArchiveBaseSummary> Bases,
    CenteredView? SelectedView,
    CameraAgentLayeredPresentation? Presentation,
    CameraAgentLayeredPresentationStatus PresentationStatus,
    string? PresentationReason,
    CenteredViewSavedSelection? SavedSelection,
    string? SelectionError,
    Uri? PreviewUrl);

internal sealed record CameraAgentCenteredPresentationRead(
    CameraAgentLayeredPresentationStatus Status,
    CameraAgentLayeredPresentation? Presentation,
    string? Reason);

internal sealed record CameraAgentCenteredLiveView(
    CenteredViewLiveContext Context,
    CenteredLiveMode Mode,
    CenteredView? View,
    CenteredViewLiveInterval? Interval,
    CameraAgentLayeredPresentation? Presentation,
    CameraAgentLayeredPresentationStatus PresentationStatus,
    string? PresentationReason,
    bool RetainedFromPriorRun,
    Uri? PreviewUrl)
{
    public bool PresentationDeferred { get; init; }
    public TimeSpan? ExpectedCaptureInterval { get; init; }
}

internal static class CameraAgentCenteredViewProjection
{
    internal static bool IsPresentationCompatible(CenteredView view, CameraAgentLayeredPresentation? presentation) =>
        presentation is not null &&
        presentation.CaptureId == view.Base.Definition.Reference.Capture.CaptureId &&
        presentation.BaseArtifactId == view.Base.Definition.Preview.Product.ArtifactId &&
        presentation.ManifestIdentitySha256 == view.Revision.Manifest.ManifestIdentitySha256 &&
        presentation.WidthPixels == view.Base.Definition.Preview.Transform.PreviewWidth &&
        presentation.HeightPixels == view.Base.Definition.Preview.Transform.PreviewHeight;

    internal static bool IsVisibilityCarryOverCompatible(CenteredViewBase previous, CenteredViewBase next) =>
        previous.Definition.Stream.Camera == next.Definition.Stream.Camera &&
        previous.Definition.Preview.Product.Compatibility == next.Definition.Preview.Product.Compatibility;

    internal static Uri BoundPreviewUrl(CenteredView view) =>
        new(
            CenteredViewLinks.Base(
                view.Base.Definition.Reference.Capture.CaptureId,
                view.Base.BaseId) + "/preview?inline=true",
            UriKind.Relative);

    internal static string BoundPreviewDownloadUrl(CenteredView view) =>
        CenteredViewLinks.Base(
            view.Base.Definition.Reference.Capture.CaptureId,
            view.Base.BaseId) + "/preview";

    internal static IReadOnlyList<CameraAgentCenteredArchiveRevisionEntry> ClassifyRevisions(
        CenteredViewArchiveBase archiveBase,
        IReadOnlyList<CenteredViewRevision> revisions,
        IReadOnlyList<CenteredViewLiveInterval> liveHistory)
    {
        var liveRevisionIds = liveHistory
            .Select(static interval => interval.RevisionId)
            .ToHashSet(StringComparer.Ordinal);
        if (archiveBase.LastLiveRevisionId is { } lastLiveId)
        {
            liveRevisionIds.Add(lastLiveId);
        }

        var ordered = revisions
            .OrderBy(static revision => revision.Order)
            .ToArray();
        var lastLiveOrder = archiveBase.LastLiveRevisionId is null
            ? (long?)null
            : ordered.FirstOrDefault(revision => string.Equals(revision.RevisionId, archiveBase.LastLiveRevisionId, StringComparison.Ordinal))?.Order;

        var entries = new List<CameraAgentCenteredArchiveRevisionEntry>(ordered.Length);
        foreach (var revision in ordered)
        {
            var isInitial = string.Equals(revision.RevisionId, archiveBase.InitialRevisionId, StringComparison.Ordinal);
            var isLastLive = archiveBase.LastLiveRevisionId is not null &&
                string.Equals(revision.RevisionId, archiveBase.LastLiveRevisionId, StringComparison.Ordinal);
            var selectedLive = liveRevisionIds.Contains(revision.RevisionId);
            var isLaterEnrichment = !selectedLive &&
                lastLiveOrder is { } cutoff &&
                revision.Order > cutoff;
            var role = isLaterEnrichment
                ? CenteredArchiveRevisionRole.LaterArchiveEnrichment
                : isLastLive
                    ? CenteredArchiveRevisionRole.LastPublishedLive
                    : isInitial
                        ? CenteredArchiveRevisionRole.Initial
                        : CenteredArchiveRevisionRole.Earlier;

            var label = role switch
            {
                CenteredArchiveRevisionRole.LastPublishedLive =>
                    FormattableString.Invariant($"Last published live (rev #{revision.Order})"),
                CenteredArchiveRevisionRole.LaterArchiveEnrichment =>
                    FormattableString.Invariant($"Later archive enrichment (rev #{revision.Order}, not shown live)"),
                CenteredArchiveRevisionRole.Initial =>
                    FormattableString.Invariant($"Initial published base (rev #{revision.Order})"),
                _ when archiveBase.LastLiveRevisionId is null =>
                    FormattableString.Invariant($"Archive revision (rev #{revision.Order}, not shown live)"),
                _ => FormattableString.Invariant($"Earlier revision (rev #{revision.Order})")
            };

            var readyCount = revision.Layers.Count(static layer => layer.Readiness == CenteredViewReadiness.Ready && layer.Drawable is not null);
            var partialCount = revision.Layers.Count(static layer => layer.Readiness == CenteredViewReadiness.Partial && layer.Drawable is not null);
            var waitingCount = revision.Layers.Count(static layer => layer.Readiness == CenteredViewReadiness.Waiting);

            entries.Add(new CameraAgentCenteredArchiveRevisionEntry(
                revision.RevisionId,
                revision.Order,
                revision.PublishedUtc,
                selectedLive,
                isInitial,
                isLastLive,
                isLaterEnrichment,
                role,
                label,
                readyCount,
                partialCount,
                waitingCount));
        }

        return entries.AsReadOnly();
    }
}
