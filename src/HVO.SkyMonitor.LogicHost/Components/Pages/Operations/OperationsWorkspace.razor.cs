using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public sealed partial class OperationsWorkspace : ComponentBase
{
    [Parameter] public Guid? ObservatoryId { get; set; }
    [Parameter] public Guid? CameraId { get; set; }
    [SupplyParameterFromQuery] public string? ObservatoryPage { get; set; }
    [SupplyParameterFromQuery] public string? CameraPage { get; set; }
    [SupplyParameterFromQuery] public string? NavigationFocus { get; set; }

    public override Task SetParametersAsync(ParameterView parameters)
    {
        // The router reuses this page across all three routes. Omitted path parameters
        // must not retain the previously selected camera or observatory.
        ObservatoryId = null;
        CameraId = null;
        return base.SetParametersAsync(parameters);
    }
}
