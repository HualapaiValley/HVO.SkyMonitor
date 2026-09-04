using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class PipelineSummaryPage : ComponentBase
{
    private CameraAgentPipelineOperatorState? _state;
    private string? _message;
    private bool _loading = true;

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var result = await ScheduleService.GetPipelineAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _state = result.Value;
                _message = null;
            }
            else
            {
                _state = null;
                _message = result.Message ?? "The processing graph could not be read.";
            }
        }
        finally
        {
            _loading = false;
        }
    }
}
