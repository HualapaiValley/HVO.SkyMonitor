using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public sealed partial class OperationsArchive : ComponentBase
{
    private string? ArchiveFocus => Guid.TryParse(Focus, out var id) ? $"archive-capture-{id:D}" : null;
    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
    [Parameter] public Guid? CaptureId { get; set; }
    [SupplyParameterFromQuery] public string? From { get; set; }
    [SupplyParameterFromQuery] public string? Until { get; set; }
    [SupplyParameterFromQuery] public string? Product { get; set; }
    [SupplyParameterFromQuery] public string? ObjectState { get; set; }
    [SupplyParameterFromQuery] public string? Reconstruction { get; set; }
    [SupplyParameterFromQuery] public string? Identity { get; set; }
    [SupplyParameterFromQuery] public string? Cursor { get; set; }
    [SupplyParameterFromQuery] public string? Focus { get; set; }
}
