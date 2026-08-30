using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class ArchiveCard : ComponentBase
{
    private string? _failedSource;

    [Parameter, EditorRequired] public CameraAgentGalleryCapture Capture { get; set; } = default!;
    [Parameter, EditorRequired] public CameraAgentCapturePresentation Presentation { get; set; } = default!;
    [Parameter, EditorRequired] public Uri DetailUrl { get; set; } = default!;
    [Parameter, EditorRequired] public string Age { get; set; } = string.Empty;
    [Parameter] public EventCallback<ArchiveLargeImageRequest> ViewLargeRequested { get; set; }

    private CameraAgentPresentationSlot? SelectedSlot => Presentation.SelectedStage is { } selected
        ? Presentation.Stages.Single(slot => slot.Stage == selected)
        : null;

    private bool PreviewFailed =>
        _failedSource is not null && SelectedSlot?.PreviewUrl?.OriginalString == _failedSource;

    private string ViewerTriggerId => FormattableString.Invariant($"archive-view-large-{Capture.CaptureId:N}");

    private string ImageAlt => $"{SelectedSlot?.Label} sky capture from {GalleryPage.FormatCaptureTime(Capture.ExposureStartedUtc)}";

    private string ImageLinkLabel => PreviewFailed
        ? $"Open capture {Capture.CaptureSequence} detail. Image preview unavailable."
        : SelectedSlot?.PreviewUrl is null
            ? $"Open capture {Capture.CaptureSequence} detail. {UnavailableTitle}."
            : $"Open capture {Capture.CaptureSequence} detail.";

    private string CaptureTime => GalleryPage.FormatCaptureTime(Capture.ExposureStartedUtc);

    private string SummaryTitle => PreviewFailed
        ? "Image preview unavailable"
        : SelectedSlot?.Label ?? "Preview unavailable";

    private string EvidenceLabel => Capture.EvidenceOrigin switch
    {
        GalleryEvidenceOrigin.Simulated => "Simulated evidence",
        GalleryEvidenceOrigin.DeveloperFixture => "Developer fixture",
        _ => "Origin unknown / not physical"
    };

    private string EvidenceClass => Capture.EvidenceOrigin switch
    {
        GalleryEvidenceOrigin.Simulated => "evidence--simulated",
        GalleryEvidenceOrigin.DeveloperFixture => "evidence--fixture",
        _ => "evidence--unknown"
    };

    private string UnavailableTitle
    {
        get
        {
            var reasons = Presentation.Stages.Select(static slot => slot.Reason).ToArray();
            if (reasons.Contains("ProcessingFailed", StringComparer.Ordinal))
            {
                return "Processing could not create a displayable image";
            }
            if (reasons.Contains("ProcessingSkipped", StringComparer.Ordinal))
            {
                return "Image processing was skipped";
            }
            if (Presentation.Stages.Any(static slot => slot.Availability == CameraAgentPresentationSlotAvailability.Unsupported))
            {
                return "This capture cannot be displayed in the browser";
            }
            return "Preview unavailable";
        }
    }

    protected override void OnParametersSet()
    {
        if (_failedSource is not null && _failedSource != SelectedSlot?.PreviewUrl?.OriginalString)
        {
            _failedSource = null;
        }
    }

    private void MarkPreviewFailed() => _failedSource = SelectedSlot?.PreviewUrl?.OriginalString;

    private void RetryPreview() => _failedSource = null;

    private Task ViewLargeAsync()
    {
        var slot = SelectedSlot;
        return slot?.PreviewUrl is { } source
            ? ViewLargeRequested.InvokeAsync(new ArchiveLargeImageRequest(
                source,
                $"{slot.Label} capture from {GalleryPage.FormatCaptureTime(Capture.ExposureStartedUtc)}",
                ImageAlt,
                ViewerTriggerId))
            : Task.CompletedTask;
    }
}

public sealed record ArchiveLargeImageRequest(Uri Source, string Title, string Alt, string TriggerId);
