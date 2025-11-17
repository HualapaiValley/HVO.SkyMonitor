using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public partial class Error : ComponentBase
{
    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    private string? RequestId { get; set; }
    private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    private string? CorrelationId { get; set; }
    private bool ShowCorrelationId => !string.IsNullOrWhiteSpace(CorrelationId);

    protected override void OnInitialized()
    {
        RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
        CorrelationId = HttpContext?.Request.Query.TryGetValue("correlationId", out var values) == true
            ? values.ToString()
            : null;
    }

    private void Reload()
    {
        NavigationManager.NavigateTo(NavigationManager.Uri, forceLoad: true);
    }
}
