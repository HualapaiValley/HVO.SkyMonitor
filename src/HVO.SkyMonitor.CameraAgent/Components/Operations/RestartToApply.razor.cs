using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// Restart-to-apply control for changes staged at the restart boundary. It shows "Restart now"
/// only when CameraAgent is supervised and the viewer holds Operations change rights; otherwise
/// it explains what restart is needed. Once a restart is scheduled it shows a restarting state
/// and leaves reconnection to the Blazor reconnect modal, which reloads the page when the new
/// process rejects the old circuit.
/// </summary>
public sealed partial class RestartToApply : ComponentBase
{
    private CameraAgentRestartStatus? _status;
    private bool _restarting;
    private bool _busy;
    private string? _error;

    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;

    /// <summary>What the restart applies, phrased to follow "applies" (for example "the staged rig").</summary>
    [Parameter] public string Subject { get; set; } = "the staged change";

    protected override async Task OnInitializedAsync()
    {
        var result = await RigService.GetRestartStatusAsync(CancellationToken.None);
        if (result is { IsSuccess: true, Value: { } status })
        {
            _status = status;
            _restarting = status.Restarting;
        }
    }

    private async Task RequestAsync()
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        try
        {
            var result = await RigService.RequestRestartAsync(CancellationToken.None);
            if (result is { IsSuccess: true, Value: var disposition })
            {
                if (disposition is CameraAgentRestartDisposition.Scheduled or CameraAgentRestartDisposition.AlreadyRequested)
                    _restarting = true;
                else
                    _error = "This host is not supervised, so it cannot restart itself. Restart CameraAgent manually.";
            }
            else
            {
                _error = result?.Kind == OperatorUiResultKind.Unauthorized
                    ? "Operations change rights are required to restart CameraAgent."
                    : result?.Message ?? "The restart request failed.";
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
