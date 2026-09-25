using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

/// <summary>
/// Derives a card's product, lineage, integration and pipeline facts from the exact capture and the selected
/// presentation. Shared by the card and the page's client-side search so the label a user reads is the label
/// they can search. Nothing is inferred from the stage alone: a causal claim requires retained multi-source
/// lineage, and integration is never invented when that lineage is absent.
/// </summary>
public static class ArchiveCardFacts
{
    public static CameraAgentPresentationSlot? SelectedSlot(CameraAgentCapturePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return presentation.SelectedStage is { } selected
            ? presentation.Stages.SingleOrDefault(slot => slot.Stage == selected)
            : null;
    }

    public static CameraAgentGalleryArtifact? DisplayArtifact(
        CameraAgentGalleryCapture capture,
        CameraAgentCapturePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(presentation);
        var slot = SelectedSlot(presentation);
        var id = slot?.DisplayArtifactId ?? slot?.ArtifactId;
        return id is { } artifactId ? capture.Artifacts.FirstOrDefault(artifact => artifact.ArtifactId == artifactId) : null;
    }

    private static CameraAgentGalleryArtifact? CombinedArtifact(CameraAgentGalleryCapture capture) =>
        capture.Artifacts.FirstOrDefault(static artifact => artifact.Role == FrameArtifactRole.Combined);

    private static bool IsPresentationStage(CameraAgentPresentationStage? stage) =>
        stage is CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview;

    /// <summary>
    /// The multi-source count the displayed pixels can prove. A retained Combined artifact is the proof that a
    /// presentation derives from a causal mean; a display artifact that itself carries more than one source is
    /// proof on its own. A single-frame calibrated preview proves nothing and never yields a causal label.
    /// </summary>
    public static int ProvenSourceCount(CameraAgentGalleryCapture capture, CameraAgentCapturePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(presentation);
        var slot = SelectedSlot(presentation);
        var display = DisplayArtifact(capture, presentation);
        if (display is { Role: FrameArtifactRole.Combined, SourceArtifactIds.Count: > 0 } combined)
        {
            return combined.SourceArtifactIds.Count;
        }
        if (display is { SourceArtifactIds.Count: > 1 } multiSource)
        {
            return multiSource.SourceArtifactIds.Count;
        }
        if ((slot?.Stage == CameraAgentPresentationStage.Combined || IsPresentationStage(slot?.Stage)) &&
            CombinedArtifact(capture) is { SourceArtifactIds.Count: > 1 } retained)
        {
            return retained.SourceArtifactIds.Count;
        }
        return 0;
    }

    public static bool IsCausal(CameraAgentGalleryCapture capture, CameraAgentCapturePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(presentation);
        return SelectedSlot(presentation)?.Stage == CameraAgentPresentationStage.Combined ||
               ProvenSourceCount(capture, presentation) > 0;
    }

    public static string ProductLabel(CameraAgentGalleryCapture capture, CameraAgentCapturePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(presentation);
        var stage = SelectedSlot(presentation)?.Stage;
        return stage switch
        {
            CameraAgentPresentationStage.Calibrated => "Calibrated single frame",
            CameraAgentPresentationStage.Raw => "Raw evidence only",
            CameraAgentPresentationStage.Combined => "Unregistered live mean",
            CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview =>
                IsCausal(capture, presentation) ? "Processed presentation" : "Processed single frame",
            _ => "No display product"
        };
    }

    public static string ProductClass(CameraAgentGalleryCapture capture, CameraAgentCapturePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(presentation);
        var stage = SelectedSlot(presentation)?.Stage;
        return stage switch
        {
            CameraAgentPresentationStage.Calibrated => "single",
            CameraAgentPresentationStage.Raw => "raw",
            CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview =>
                IsCausal(capture, presentation) ? "causal" : "single",
            _ => "causal"
        };
    }
}

public sealed partial class ArchiveCard : ComponentBase
{
    private string? _failedSource;

    [Parameter, EditorRequired] public CameraAgentGalleryCapture Capture { get; set; } = default!;
    [Parameter, EditorRequired] public CameraAgentCapturePresentation Presentation { get; set; } = default!;
    [Parameter, EditorRequired] public Uri DetailUrl { get; set; } = default!;
    [Parameter] public Uri? RunUrl { get; set; }
    [Parameter] public string RunUnavailableReason { get; set; } = "The pipeline run identity is not resolved for this card.";

    private CameraAgentPresentationSlot? SelectedSlot => ArchiveCardFacts.SelectedSlot(Presentation);

    private bool PreviewFailed =>
        _failedSource is not null && SelectedSlot?.PreviewUrl?.OriginalString == _failedSource;

    private string ImageAlt => $"{SelectedSlot?.Label} sky capture from {GalleryPage.FormatCaptureTime(Capture.ExposureStartedUtc)}";

    private string ImageLinkLabel => PreviewFailed
        ? $"Open capture {Capture.CaptureSequence} detail. Image preview unavailable."
        : SelectedSlot?.PreviewUrl is null
            ? $"Open capture {Capture.CaptureSequence} detail. {UnavailableTitle}."
            : $"Open capture {Capture.CaptureSequence} detail.";

    private string CaptureTimeUtc => Capture.ExposureStartedUtc.UtcDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    private string ProductClass => ArchiveCardFacts.ProductClass(Capture, Presentation);

    private string ProductLabel => ArchiveCardFacts.ProductLabel(Capture, Presentation);

    private int ProvenSourceCount => ArchiveCardFacts.ProvenSourceCount(Capture, Presentation);

    private bool IsCausal => ArchiveCardFacts.IsCausal(Capture, Presentation);

    private string StageLabel => SelectedSlot?.Label ?? "No display stage";

    private string ArtifactCountLabel => Capture.ArtifactsTruncated
        ? $"{Capture.Artifacts.Count}+ artifacts"
        : $"{Capture.Artifacts.Count} artifact{(Capture.Artifacts.Count == 1 ? string.Empty : "s")}";

    // No event linkage exists in the capture projection yet; an event badge is only shown when one is retained.
    private string? EventLabel => null;

    private string Summary
    {
        get
        {
            if (PreviewFailed)
            {
                return "The preview bytes are unavailable; the capture facts below remain valid.";
            }
            if (SelectedSlot?.Stage == CameraAgentPresentationStage.Raw)
            {
                return "Raw evidence only; no displayable calibrated or combined derivative was retained.";
            }
            if (SelectedSlot?.Stage == CameraAgentPresentationStage.Calibrated)
            {
                return "Reference frame retained after calibration, before temporal combination.";
            }
            if (IsCausal)
            {
                var prefix = SelectedSlot?.Stage == CameraAgentPresentationStage.Combined
                    ? "Unregistered causal mean"
                    : "Processed presentation from an unregistered causal mean";
                return ProvenSourceCount > 0
                    ? $"{prefix} of {ProvenSourceCount} source frame{(ProvenSourceCount == 1 ? string.Empty : "s")}; not a registered stack."
                    : $"{prefix}; source lineage was not retained for this capture.";
            }
            if (SelectedSlot is null)
            {
                return "No displayable derivative is retained for this capture.";
            }
            return "Processed single-frame presentation retained for this capture.";
        }
    }

    private string SourceLabel => IsCausal
        ? ProvenSourceCount > 0
            ? $"{ProvenSourceCount} frames / endpoint #{Capture.CaptureSequence}"
            : "Source lineage not retained"
        : $"Reference #{Capture.CaptureSequence} only";

    private string IntegrationLabel
    {
        get
        {
            var exposure = Capture.Detail?.Controls?.EffectiveExposureMilliseconds;
            if (exposure is not { } milliseconds || milliseconds <= 0)
            {
                return "Not recorded";
            }
            // Integration is only summed across frames whose multi-source lineage is actually retained.
            var frames = IsCausal && ProvenSourceCount > 0 ? ProvenSourceCount : 1;
            var total = milliseconds * frames / 1000d;
            return FormattableString.Invariant($"{total:0.#} s");
        }
    }

    // DurableProcessingNodeStatus values are Pending, Running, Completed, Skipped, RetryableFailure, TerminalFailure.
    private string PipelineOutcome
    {
        get
        {
            if (Capture.ProcessingProjectionUnavailable)
            {
                return "warning";
            }
            if (Capture.ProcessingNodes.Count == 0)
            {
                return "pending";
            }
            if (Capture.ProcessingNodes.Any(static node => string.Equals(node.Status, "TerminalFailure", StringComparison.OrdinalIgnoreCase)))
            {
                return "failure";
            }
            if (Capture.ProcessingNodes.Any(static node => string.Equals(node.Status, "RetryableFailure", StringComparison.OrdinalIgnoreCase)))
            {
                return "warning";
            }
            if (Capture.ProcessingNodes.Any(static node => string.Equals(node.Status, "Running", StringComparison.OrdinalIgnoreCase)))
            {
                return "running";
            }
            if (Capture.ProcessingNodes.Any(static node => string.Equals(node.Status, "Pending", StringComparison.OrdinalIgnoreCase)))
            {
                return "pending";
            }
            if (Capture.ProcessingNodes.Any(static node => string.Equals(node.Status, "Skipped", StringComparison.OrdinalIgnoreCase)))
            {
                return "skipped";
            }
            return Capture.ProcessingNodes.All(static node => string.Equals(node.Status, "Completed", StringComparison.OrdinalIgnoreCase)) ? "success" : "pending";
        }
    }

    private string PipelineStatus => Capture.ProcessingProjectionUnavailable
        ? "Run evidence unavailable"
        : Capture.ProcessingNodes.Count == 0
            ? "No run recorded"
            : PipelineOutcome switch
            {
                "failure" => "Failed",
                "warning" => "Retrying",
                "running" => "In progress",
                "skipped" => "Skipped",
                "success" => "Succeeded",
                _ => "Not started"
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
}
