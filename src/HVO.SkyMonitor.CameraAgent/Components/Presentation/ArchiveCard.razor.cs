using HVO.SkyMonitor.AgentCore;
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
    [Parameter] public Uri? RunUrl { get; set; }
    [Parameter] public string RunUnavailableReason { get; set; } = "The pipeline run identity is not resolved for this card.";

    private CameraAgentPresentationSlot? SelectedSlot => Presentation.SelectedStage is { } selected
        ? Presentation.Stages.Single(slot => slot.Stage == selected)
        : null;

    private bool PreviewFailed =>
        _failedSource is not null && SelectedSlot?.PreviewUrl?.OriginalString == _failedSource;

    private string ImageAlt => $"{SelectedSlot?.Label} sky capture from {GalleryPage.FormatCaptureTime(Capture.ExposureStartedUtc)}";

    private string ImageLinkLabel => PreviewFailed
        ? $"Open capture {Capture.CaptureSequence} detail. Image preview unavailable."
        : SelectedSlot?.PreviewUrl is null
            ? $"Open capture {Capture.CaptureSequence} detail. {UnavailableTitle}."
            : $"Open capture {Capture.CaptureSequence} detail.";

    private string CaptureTimeUtc => Capture.ExposureStartedUtc.UtcDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // Product identity is the selected display stage; a registered/centered derivative is never claimed because
    // no retained lineage proves it. The prototype's "registered stack" fixture is deliberately not reproduced.
    private string ProductClass => SelectedSlot?.Stage switch
    {
        CameraAgentPresentationStage.Calibrated => "single",
        CameraAgentPresentationStage.Raw => "raw",
        _ => "causal"
    };

    private string ProductLabel => SelectedSlot?.Stage switch
    {
        CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview => "Processed presentation",
        CameraAgentPresentationStage.Combined => "Unregistered live mean",
        CameraAgentPresentationStage.Calibrated => "Calibrated single frame",
        CameraAgentPresentationStage.Raw => "Raw evidence only",
        _ => "No display product"
    };

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
            if (IsCausalStage)
            {
                var frames = CombinedSourceCount > 0
                    ? $"of {CombinedSourceCount} source frame{(CombinedSourceCount == 1 ? string.Empty : "s")}"
                    : string.Empty;
                var prefix = SelectedSlot?.Stage == CameraAgentPresentationStage.Combined
                    ? "Unregistered causal mean"
                    : "Processed presentation from an unregistered causal mean";
                return frames.Length > 0
                    ? $"{prefix} {frames}; not a registered stack."
                    : $"{prefix}; source lineage was not retained for this capture.";
            }
            return SelectedSlot?.Stage switch
            {
                CameraAgentPresentationStage.Calibrated =>
                    "Reference frame retained after calibration, before temporal combination.",
                CameraAgentPresentationStage.Raw =>
                    "Raw evidence only; no displayable calibrated or combined derivative was retained.",
                _ => "No displayable derivative is retained for this capture."
            };
        }
    }

    // Processed, live-mean, and any presentation stage are all built on the unregistered causal mean.
    private bool IsCausalStage => SelectedSlot?.Stage is CameraAgentPresentationStage.Annotated or
        CameraAgentPresentationStage.Preview or CameraAgentPresentationStage.Combined;

    private string SourceLabel
    {
        get
        {
            if (IsCausalStage && CombinedSourceCount > 0)
            {
                return $"{CombinedSourceCount} frames / endpoint #{Capture.CaptureSequence}";
            }
            if (IsCausalStage)
            {
                return "Source lineage not retained";
            }
            return $"Reference #{Capture.CaptureSequence} only";
        }
    }

    private int CombinedSourceCount
    {
        get
        {
            var combined = Capture.Artifacts.FirstOrDefault(static artifact => artifact.Role == FrameArtifactRole.Combined);
            return combined?.SourceArtifactIds.Count ?? 0;
        }
    }

    private string IntegrationLabel
    {
        get
        {
            var exposure = Capture.Detail?.Controls?.EffectiveExposureMilliseconds;
            if (exposure is not { } milliseconds || milliseconds <= 0)
            {
                return "Not recorded";
            }
            var frames = IsCausalStage && CombinedSourceCount > 0
                ? CombinedSourceCount
                : 1;
            var total = milliseconds * frames / 1000d;
            return FormattableString.Invariant($"{total:0.#} s");
        }
    }

    private string PipelineOutcome => Capture.ProcessingProjectionUnavailable || Capture.ProcessingNodes.Count == 0
        ? "pending"
        : Capture.ProcessingNodes.Any(static node => node.Status is "Failed" or "Failure")
            ? "failure"
            : Capture.ProcessingNodes.All(static node => node.Status is "Completed" or "Succeeded")
                ? "success"
                : "running";

    private string PipelineStatus => Capture.ProcessingProjectionUnavailable
        ? "Run evidence unavailable"
        : Capture.ProcessingNodes.Count == 0
            ? "No run recorded"
            : PipelineOutcome switch
            {
                "failure" => "Failed",
                "success" => "Succeeded",
                "running" => "In progress",
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
