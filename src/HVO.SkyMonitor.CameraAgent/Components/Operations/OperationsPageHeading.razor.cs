using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The prototype Operations page heading: eyebrow, page title and description, with the local
/// authority marker and any page actions beside it. Styled by the Operations layout.
/// </summary>
public sealed partial class OperationsPageHeading : ComponentBase
{
    [Parameter, EditorRequired] public string Eyebrow { get; set; } = string.Empty;

    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;

    [Parameter] public string? Description { get; set; }

    /// <summary>The id of the page h1, for a landmark's aria-labelledby.</summary>
    [Parameter] public string? HeadingId { get; set; }

    [Parameter] public RenderFragment? Actions { get; set; }
}
