using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public partial class AutomationScriptRunControls
{
    [Parameter] public LocalAutomationDefinitionState? Definition { get; set; }
    [Parameter] public LocalAutomationRun? Run { get; set; }
    [Parameter] public bool Available { get; set; }
    [Parameter] public bool Pending { get; set; }
    [Parameter] public EventCallback Changed { get; set; }
    [Inject] internal ICameraAgentAutomationUiService Service { get; set; } = default!;
    [Inject] internal NavigationManager Navigation { get; set; } = default!;

    private LocalAutomationRunNowRequest? _runNow;
    private LocalAutomationScriptStopRequest? _stop;
    private bool _busy;
    private bool _error;
    private string? _message;

    private void ReviewRunNow()
    {
        if (_busy || !Available || Pending || Definition is not { Definition.Enabled: true, Definition.TaskKind: LocalAutomationTaskKind.CustomScript }) return;
        _message = null;
        _runNow = new(Definition.Definition.DefinitionId, Definition.Version, Guid.NewGuid().ToString("N"), string.Empty, LocalAutomationScriptCommands.DefaultRunNowReason);
    }

    private void ReviewStop()
    {
        if (_busy || Run is not { Outcome: LocalAutomationRunOutcome.Queued or LocalAutomationRunOutcome.Running, Occurrence: { } occurrence } ||
            occurrence.Definition.TaskKind != LocalAutomationTaskKind.CustomScript) return;
        _message = null;
        _stop = new(Run.DefinitionId, Run.RunKey, occurrence.DefinitionVersion, string.Empty);
    }

    private void Cancel()
    {
        if (_busy) return;
        _runNow = null;
        _stop = null;
        _message = null;
    }

    private async Task ConfirmAsync()
    {
        if (_busy || _runNow is null && _stop is null) return;
        _busy = true;
        var stopping = _stop is not null;
        OperatorUiResult<LocalAutomationCommandResult> result;
        try
        {
            result = _stop is { } stop
                ? await Service.StopScriptAsync(stop, CancellationToken.None)
                : await Service.RunScriptNowAsync(_runNow!, CancellationToken.None);
        }
        finally { _busy = false; }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Navigation.NavigateTo("/Account/AccessDenied");
            return;
        }
        _error = !result.IsSuccess;
        _message = result.Message ?? (result.IsSuccess
            ? stopping ? "This run is settled. Refresh run history to see its recorded outcome." : "One script invocation is queued. Follow it in run history."
            : "The script command is unavailable. Refresh and retry.");
        // An unavailable response can follow a committed queue write. Keep the exact request/key.
        if (result.Kind == OperatorUiResultKind.Unavailable) return;
        _runNow = null;
        _stop = null;
        await Changed.InvokeAsync();
    }
}
