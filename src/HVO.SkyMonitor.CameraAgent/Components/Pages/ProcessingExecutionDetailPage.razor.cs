using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingExecutionDetailPage : ComponentBase
{
    private CameraAgentProcessingExecutionDetailView? _view;
    private string? _message;
    private bool _loading = true;
    private bool _notFound;

    [Parameter] public Guid ExecutionId { get; set; }

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnParametersSetAsync() => await RefreshAsync().ConfigureAwait(false);

    private async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var result = await GraphService.GetExecutionDetailAsync(ExecutionId, CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            _notFound = result.Kind == OperatorUiResultKind.NotFound;
            if (result.IsSuccess && result.Value is not null)
            {
                _view = result.Value;
                _message = null;
            }
            else
            {
                _view = null;
                _message = result.Message ?? "The execution could not be read.";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    internal static string NodeStatusClass(string status) => status switch
    {
        "Completed" => "state-chip--completed",
        "Running" => "state-chip--running",
        "Pending" or "Waiting" => "state-chip--pending",
        _ => "state-chip--failed"
    };
}
