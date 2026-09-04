using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingGraphDetailPage : ComponentBase
{
    private CameraAgentProcessingGraphRevisionDetail? _detail;
    private string? _message;
    private bool _loading = true;
    private bool _notFound;

    [Parameter] public string RevisionId { get; set; } = string.Empty;

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnParametersSetAsync() => await LoadAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var result = await GraphService.GetRevisionDetailAsync(RevisionId, CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            _notFound = result.Kind == OperatorUiResultKind.NotFound;
            if (result.IsSuccess && result.Value is not null)
            {
                _detail = result.Value;
                _message = null;
            }
            else
            {
                _detail = null;
                _message = result.Message ?? "The graph revision could not be read.";
            }
        }
        finally
        {
            _loading = false;
        }
    }
}
