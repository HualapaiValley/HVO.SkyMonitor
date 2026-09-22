using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingExecutionDetailPage : ComponentBase
{
    private CameraAgentProcessingExecutionDetailView? _view;
    private string? _message;
    private bool _loading = true;
    private bool _notFound;
    private string? _selectedNodeId;
    private string _tab = "stage";
    private TransientCaptureRunState? _transient;
    private CameraAgentProcessingExecutionsView? _recent;
    private bool _transientUnavailable;

    [Parameter] public Guid ExecutionId { get; set; }

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal ITransientRuntimeManagement TransientRuntime { get; set; } = default!;

    [Inject] internal IOptions<CameraAgentHostOptions> HostOptions { get; set; } = default!;

    private bool TransientEnabled => HostOptions.Value.TransientDetection.Mode is TransientOperatingMode.Edge or TransientOperatingMode.Hybrid;

    private CameraAgentProcessingNodeView? SelectedNode => _view?.Nodes.FirstOrDefault(node => node.NodeId == _selectedNodeId);

    private string TransientLabel => !TransientEnabled ? "Disabled"
        : _transientUnavailable ? "State unavailable"
        : _transient is null ? "No transient work recorded for this capture"
        : $"{_transient.WorkState} / {_transient.FrameState ?? "frame not recorded"}";

    private void SelectStageTab() => _tab = "stage";
    private void SelectArtifactsTab() => _tab = "artifacts";
    private void SelectAttemptsTab() => _tab = "attempts";

    protected override async Task OnParametersSetAsync() => await RefreshAsync().ConfigureAwait(false);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The transient band is optional; a failed read is shown as unavailable without concealing the execution journal.")]
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
                if (_selectedNodeId is null || _view.Nodes.All(node => node.NodeId != _selectedNodeId))
                {
                    _selectedNodeId = _view.Nodes.Count > 0 ? _view.Nodes[0].NodeId : null;
                }
                _transient = null;
                _transientUnavailable = false;
                if (TransientEnabled)
                {
                    try
                    {
                        _transient = await TransientRuntime.ReadCaptureRunAsync(_view.Execution.CaptureId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        _transientUnavailable = true;
                    }
                }
                var recent = await GraphService.GetExecutionsAsync(10, CancellationToken.None).ConfigureAwait(false);
                _recent = recent.IsSuccess ? recent.Value : null;
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
        "Pending" or "RetryableFailure" => "state-chip--pending",
        "Skipped" => "state-chip--pending",
        _ => "state-chip--failed"
    };
}
