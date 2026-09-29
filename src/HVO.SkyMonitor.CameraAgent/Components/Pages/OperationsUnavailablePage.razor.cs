using HVO.SkyMonitor.CameraAgent.Components.Operations;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class OperationsUnavailablePage : ComponentBase
{
    [Parameter] public string Section { get; set; } = string.Empty;

    private OperationsSection? Entry => OperationsSectionCatalog.Sections.FirstOrDefault(section =>
        section.UnavailableReason is not null &&
        string.Equals(section.Slug, Section, StringComparison.OrdinalIgnoreCase));
}
