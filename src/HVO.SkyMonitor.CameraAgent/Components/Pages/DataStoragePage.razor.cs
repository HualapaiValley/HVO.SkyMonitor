using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class DataStoragePage : ComponentBase
{
    private CameraAgentOperationsView? _view;
    private string? _message;
    private bool _loading = true;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    private bool HasPressure => _view is not null && (
        _view.Summary.Storage.Value.Any(static storage => storage.IsUnderPressure) ||
        _view.Summary.CaptureLanes.Value.Lanes.Any(static lane => lane.PressureLevel > 0));

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var result = await OperatorService.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _view = result.Value;
                _message = null;
            }
            else
            {
                // Keep the last valid snapshot visible and say why it is stale.
                _message = result.Message ?? "Current data and storage facts are unavailable.";
            }
        }
        finally
        {
            _loading = false;
        }
    }
}
