using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class SchedulePage : ComponentBase, IAsyncDisposable
{
    private CaptureScheduleOperatorState? _state;
    private CaptureSchedulePreview? _preview;
    private CaptureProcessingPlanPreview? _pipelinePlan;
    private string _editorJson = string.Empty;
    private string _overrideMode = "ForceClosed";
    private string _overrideStart = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private string _overrideEnd = DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
    private string _overrideProfile = string.Empty;
    private string? _confirmRevisionId;
    private string? _editorBasisRevisionId;
    private string? _message;
    private bool _messageIsError;
    private bool _overrideOneShot;
    private bool _loading = true;
    private bool _busy;
    private bool _pipelineCanToggle;
    private string? _stageKey;
    private string? _stagePayload;
    private long _stageExpectedVersion;
    private string? _activationKey;
    private string? _activationRevisionId;
    private long _activationExpectedVersion;
    private string? _overrideKey;
    private string? _overrideSignature;
    private CaptureScheduleOverride? _pendingOverride;
    private long _overrideExpectedVersion;
    private IJSObjectReference? _module;
    private ElementReference _confirmationPanel;
    private string? _activationTriggerId;
    private bool _focusConfirmation;
    private bool _restoreActivationFocus;
    private readonly Dictionary<string, (string Key, long ExpectedVersion)> _clearOverrideKeys = new(StringComparer.Ordinal);

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await RefreshAsync().ConfigureAwait(false);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConfirmation)
        {
            _focusConfirmation = false;
            await _confirmationPanel.FocusAsync().ConfigureAwait(false);
        }
        else if (_restoreActivationFocus)
        {
            _restoreActivationFocus = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/SchedulePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync(
                "focusById", _activationTriggerId, "schedule-heading").ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        try
        {
            var result = await ScheduleService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _state = result.Value;
                var editorRevision = _state.PendingRevision ?? _state.ActiveRevision;
                _editorJson = CameraAgentScheduleUiService.SerializeProfile(editorRevision.Profile);
                _editorBasisRevisionId = editorRevision.RevisionId;
                var pipelineResult = await ScheduleService.GetPipelineAsync(CancellationToken.None).ConfigureAwait(false);
                if (pipelineResult.IsSuccess && pipelineResult.Value is { } pipelineState)
                {
                    var selected = pipelineState.Pending is not null &&
                        string.Equals(pipelineState.Pending.RevisionId, editorRevision.RevisionId, StringComparison.Ordinal)
                            ? pipelineState.Pending
                            : pipelineState.Active;
                    _pipelinePlan = selected.Plan;
                    _pipelineCanToggle = selected.CanToggle;
                }
                else
                {
                    _pipelinePlan = null;
                    _pipelineCanToggle = false;
                }
                _preview = null;
                _overrideProfile = _state.Decision.SetpointProfileId ?? _state.ActiveRevision.Definition.SetpointProfiles[0].Id;
                _message = null;
            }
            else
            {
                SetMessage(result.Message ?? "Schedule state is unavailable.", error: true);
            }
        }
        finally
        {
            _loading = false;
            _busy = false;
        }
    }

    private async Task PreviewAsync()
    {
        if (_editorBasisRevisionId is null)
        {
            return;
        }
        var editorJson = _editorJson;
        var basisRevisionId = _editorBasisRevisionId;
        _busy = true;
        OperatorUiResult<CaptureSchedulePreview> result;
        try
        {
            result = await ScheduleService.PreviewAsync(
                editorJson, basisRevisionId, 7, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (!string.Equals(editorJson, _editorJson, StringComparison.Ordinal) ||
            !string.Equals(basisRevisionId, _editorBasisRevisionId, StringComparison.Ordinal))
        {
            return;
        }
        if (result.IsSuccess)
        {
            _preview = result.Value;
            var pipelineResult = await ScheduleService.PreviewPipelineAsync(
                editorJson, basisRevisionId, CancellationToken.None).ConfigureAwait(false);
            if (pipelineResult.IsSuccess && pipelineResult.Value is { } pipeline)
            {
                _pipelinePlan = pipeline.Plan;
                _pipelineCanToggle = string.Equals(
                    pipeline.Plan.SchemaVersion,
                    CapturePipelineSchemaVersions.ExplicitV2,
                    StringComparison.Ordinal);
                SetMessage("Schedule and desired graph previews are valid. No durable state changed.", error: false);
            }
            else
            {
                _pipelinePlan = null;
                SetMessage(pipelineResult.Message ?? "Graph preview validation failed.", error: true);
            }
        }
        else
        {
            _preview = null;
            SetMessage(result.Message ?? "Preview validation failed.", error: true);
        }
    }

    private async Task TogglePipelineAsync(string nodeId, bool enabled)
    {
        if (_editorBasisRevisionId is null)
        {
            return;
        }
        var basisRevisionId = _editorBasisRevisionId;
        var editorJson = _editorJson;
        _busy = true;
        OperatorUiResult<CameraAgentPipelineProfilePreview> result;
        try
        {
            result = await ScheduleService.TogglePipelineAsync(
                editorJson,
                basisRevisionId,
                nodeId,
                enabled,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (!string.Equals(editorJson, _editorJson, StringComparison.Ordinal) ||
            !string.Equals(basisRevisionId, _editorBasisRevisionId, StringComparison.Ordinal))
        {
            return;
        }
        if (result.IsSuccess && result.Value is { } preview)
        {
            _editorJson = preview.ProfileJson;
            _pipelinePlan = preview.Plan;
            _preview = null;
            SetMessage("Desired graph updated in the editor. Save the immutable draft to persist it.", error: false);
        }
        else
        {
            SetMessage(result.Message ?? "The desired graph toggle is invalid.", error: true);
        }
    }

    private async Task StageAsync()
    {
        if (_state is null || _editorBasisRevisionId is null)
        {
            return;
        }
        var stageSignature = string.Concat(_editorBasisRevisionId, "\n", _editorJson);
        if (!string.Equals(_stagePayload, stageSignature, StringComparison.Ordinal))
        {
            _stagePayload = stageSignature;
            _stageKey = NewKey();
            _stageExpectedVersion = _state.StateVersion;
        }
        _busy = true;
        var result = await ScheduleService.StageAsync(
            _editorJson,
            _editorBasisRevisionId,
            _stageExpectedVersion,
            _stageKey!,
            "operator draft",
            CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _stageKey = null;
            _stagePayload = null;
        }
        await CompleteMutationAsync(result, "Draft saved.").ConfigureAwait(false);
    }

    private void BeginActivation(string revisionId, string triggerId)
    {
        _confirmRevisionId = revisionId;
        _activationTriggerId = triggerId;
        _focusConfirmation = true;
    }

    private void CancelActivation()
    {
        _confirmRevisionId = null;
        _restoreActivationFocus = true;
    }

    private async Task ConfirmActivationAsync()
    {
        if (_state is null || _confirmRevisionId is null)
        {
            return;
        }
        var revisionId = _confirmRevisionId;
        if (!string.Equals(_activationRevisionId, revisionId, StringComparison.Ordinal))
        {
            _activationRevisionId = revisionId;
            _activationKey = NewKey();
            _activationExpectedVersion = _state.StateVersion;
        }
        _busy = true;
        var result = await ScheduleService.ActivateAsync(
            revisionId,
            _activationExpectedVersion,
            _activationKey!,
            "operator apply",
            CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _confirmRevisionId = null;
            _restoreActivationFocus = true;
            _activationKey = null;
            _activationRevisionId = null;
        }
        await CompleteMutationAsync(result, "Revision applied at the capture boundary.").ConfigureAwait(false);
    }

    private async Task AddOverrideAsync()
    {
        if (_state is null ||
            !DateTimeOffset.TryParse(_overrideStart, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start) ||
            !DateTimeOffset.TryParse(_overrideEnd, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
        {
            SetMessage("Override UTC boundaries are invalid.", error: true);
            return;
        }
        start = DateTimeOffset.FromUnixTimeMilliseconds(start.ToUnixTimeMilliseconds());
        end = DateTimeOffset.FromUnixTimeMilliseconds(end.ToUnixTimeMilliseconds());
        var mode = _overrideMode == "ForceOpen"
            ? CaptureScheduleOverrideMode.ForceOpen
            : CaptureScheduleOverrideMode.ForceClosed;
        var signature = string.Join('|',
            _state.ActiveRevision.RevisionId,
            mode,
            start.ToString("O", CultureInfo.InvariantCulture),
            end.ToString("O", CultureInfo.InvariantCulture),
            _overrideProfile,
            _overrideOneShot);
        if (!string.Equals(_overrideSignature, signature, StringComparison.Ordinal))
        {
            _overrideSignature = signature;
            _overrideKey = NewKey();
            _overrideExpectedVersion = _state.StateVersion;
            _pendingOverride = new CaptureScheduleOverride(
                $"override-{Guid.NewGuid():N}",
                _state.ActiveRevision.RevisionId,
                _state.ActiveRevision.ScheduleSha256,
                mode,
                start,
                end,
                mode == CaptureScheduleOverrideMode.ForceOpen ? _overrideProfile : null,
                mode == CaptureScheduleOverrideMode.ForceOpen && _overrideOneShot);
        }
        _busy = true;
        var result = await ScheduleService.AddOverrideAsync(
            _pendingOverride!,
            _overrideExpectedVersion,
            _overrideKey!,
            "operator override",
            CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _overrideKey = null;
            _overrideSignature = null;
            _pendingOverride = null;
        }
        await CompleteMutationAsync(result, "Override created.").ConfigureAwait(false);
    }

    private async Task ClearOverrideAsync(string overrideId)
    {
        if (_state is null)
        {
            return;
        }
        if (!_clearOverrideKeys.TryGetValue(overrideId, out var pendingCommand))
        {
            pendingCommand = (NewKey(), _state.StateVersion);
            _clearOverrideKeys.Add(overrideId, pendingCommand);
        }
        _busy = true;
        var result = await ScheduleService.ClearOverrideAsync(
            overrideId,
            pendingCommand.ExpectedVersion,
            pendingCommand.Key,
            "operator clear",
            CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _clearOverrideKeys.Remove(overrideId);
        }
        await CompleteMutationAsync(result, "Override cleared.").ConfigureAwait(false);
    }

    private async Task CompleteMutationAsync(
        OperatorUiResult<CaptureScheduleStoreSnapshot> result,
        string successMessage)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (!result.IsSuccess)
        {
            SetMessage(result.Message ?? "The command failed.", error: true);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        SetMessage(successMessage, error: false);
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private void EditorChanged()
    {
        _preview = null;
        _pipelinePlan = null;
        _message = null;
    }

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string ShortHash(string value) => value.Length <= 12 ? value : value[..12];

    private static string ActivationTriggerId(string revisionId, string origin)
        => $"schedule-activation-{origin}-{revisionId}";

    private static string PendingActivationTriggerId(string revisionId)
        => ActivationTriggerId(revisionId, "pending");

    private static string HistoryActivationTriggerId(CaptureScheduleRevisionSnapshot revision)
        => ActivationTriggerId(revision.RevisionId, $"history-{revision.RevisionNumber}");

    private static string FormatUtc(DateTimeOffset? value) => value?.ToString("u") ?? "No known transition";

    private static string FormatInterval(DateTimeOffset? start, DateTimeOffset? end)
        => start.HasValue && end.HasValue ? $"{start.Value:u} to {end.Value:u}" : "None";

    private static string Split(string value)
        => string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
