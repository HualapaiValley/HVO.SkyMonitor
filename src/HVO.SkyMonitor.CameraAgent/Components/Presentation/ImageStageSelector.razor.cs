using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class ImageStageSelector : ComponentBase
{
    [Parameter, EditorRequired] public IReadOnlyList<CameraAgentPresentationSlot> Stages { get; set; } = [];
    [Parameter] public CameraAgentPresentationStage? SelectedStage { get; set; }
    [Parameter] public EventCallback<CameraAgentPresentationStage> SelectedStageChanged { get; set; }

    private Task SelectAsync(CameraAgentPresentationSlot slot)
        => slot.Availability == CameraAgentPresentationSlotAvailability.Available
            ? SelectedStageChanged.InvokeAsync(slot.Stage)
            : Task.CompletedTask;

    private static string ReasonText(CameraAgentPresentationSlot slot)
        => slot.Availability == CameraAgentPresentationSlotAvailability.Available
            ? $"Show {slot.Label} image"
            : $"{slot.Label}: {PresentationReason(slot)}";

    private static string PresentationReason(CameraAgentPresentationSlot slot) => slot.Reason switch
    {
        "NoCapture" => "No capture is available.",
        "RawEvidenceUnavailable" => "Durable raw evidence is unavailable.",
        "ProcessingProjectionUnavailable" => "Processing evidence is unavailable.",
        "PreviewBoundsExceeded" => "The preview exceeds safe display limits.",
        "ArtifactInvalid" => "The produced artifact failed validation.",
        "UnsupportedMediaType" => "This artifact type cannot be previewed.",
        "ArtifactUnavailable" => "The produced artifact is unavailable.",
        "ProcessingFailed" => "This stage could not be created.",
        "ProcessingSkipped" => "This stage was skipped because required input was unavailable.",
        "ProjectionBoundReached" => "This stage was not found within the bounded projection.",
        "NotProduced" => "This stage was not produced.",
        _ => slot.Availability switch
        {
            CameraAgentPresentationSlotAvailability.Missing => "This stage was not produced.",
            CameraAgentPresentationSlotAvailability.Unsupported => "This stage cannot be previewed.",
            _ => "This stage is currently unavailable."
        }
    };
}
