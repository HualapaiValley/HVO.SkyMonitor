using HVO.SkyMonitor.Common.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Shared;

public partial class ErrorDisplay : ComponentBase
{
    private string? CorrelationId => CorrelationIdAccessor.GetCorrelationId();

    [Parameter]
    public EventCallback OnDismiss { get; set; }

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ICorrelationIdAccessor CorrelationIdAccessor { get; set; } = default!;

    private async Task OnDismissClicked()
    {
        if (OnDismiss.HasDelegate)
        {
            await OnDismiss.InvokeAsync().ConfigureAwait(false);
        }
    }
}
