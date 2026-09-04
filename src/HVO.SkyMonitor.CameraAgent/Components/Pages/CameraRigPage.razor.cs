using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CameraRigPage : ComponentBase
{
    private CaptureScheduleOperatorState? _state;
    private CaptureProfileFormModel? _model;
    private LocalCaptureProfileDefinition? _basisProfile;
    private string? _basisRevisionId;
    private string? _message;
    private bool _messageIsError;
    private bool _loading = true;
    private bool _busy;
    private bool _validated;
    private string? _validatedJson;
    private string? _stageKey;
    private string? _stagePayload;
    private long _stageExpectedVersion;

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await RefreshAsync().ConfigureAwait(false);

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
                var basis = _state.PendingRevision ?? _state.ActiveRevision;
                _basisProfile = basis.Profile;
                _basisRevisionId = basis.RevisionId;
                _model = CaptureProfileFormModel.FromProfile(basis.Profile);
                _validated = false;
                _validatedJson = null;
                _message = null;
            }
            else
            {
                _state = null;
                _model = null;
                SetMessage(result.Message ?? "The local profile is unavailable.", error: true);
            }
        }
        finally
        {
            _loading = false;
            _busy = false;
        }
    }

    private void FormChanged()
    {
        _validated = false;
        _validatedJson = null;
        _message = null;
    }

    private bool TryResolveJson(out string json)
    {
        json = string.Empty;
        if (_model is null || _basisProfile is null)
        {
            return false;
        }
        if (!_model.TryApply(_basisProfile, out var profile, out var errors))
        {
            SetMessage(string.Join(' ', errors), error: true);
            return false;
        }
        json = CameraAgentScheduleUiService.SerializeProfile(profile);
        return true;
    }

    /// <summary>
    /// Validates the draft the same way the server will stage it: the profile must parse,
    /// the schedule must expand, and the rig must remain compatible with the processing graph.
    /// </summary>
    private async Task PreviewAsync()
    {
        if (_basisRevisionId is null || !TryResolveJson(out var json))
        {
            return;
        }
        _busy = true;
        try
        {
            var schedule = await ScheduleService.PreviewAsync(json, _basisRevisionId, 1, CancellationToken.None).ConfigureAwait(false);
            if (schedule.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (!schedule.IsSuccess)
            {
                SetMessage(schedule.Message ?? "The draft profile is invalid.", error: true);
                return;
            }
            var pipeline = await ScheduleService.PreviewPipelineAsync(json, _basisRevisionId, CancellationToken.None).ConfigureAwait(false);
            if (!pipeline.IsSuccess)
            {
                SetMessage(pipeline.Message ?? "The draft rig is not compatible with the processing graph.", error: true);
                return;
            }
            _validated = true;
            _validatedJson = json;
            SetMessage("The draft is valid. No durable state changed.", error: false);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task StageAsync()
    {
        if (_state is null || _basisRevisionId is null || !_validated || _validatedJson is null)
        {
            return;
        }
        var signature = string.Concat(_basisRevisionId, "\n", _validatedJson);
        if (!string.Equals(_stagePayload, signature, StringComparison.Ordinal))
        {
            _stagePayload = signature;
            _stageKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            _stageExpectedVersion = _state.StateVersion;
        }
        _busy = true;
        OperatorUiResult<CaptureScheduleStoreSnapshot> result;
        try
        {
            result = await ScheduleService.StageAsync(
                _validatedJson,
                _basisRevisionId,
                _stageExpectedVersion,
                _stageKey!,
                "camera and rig draft",
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _stageKey = null;
            _stagePayload = null;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (!result.IsSuccess)
        {
            SetMessage(result.Message ?? "The draft could not be saved.", error: true);
            return;
        }
        await RefreshAsync().ConfigureAwait(false);
        SetMessage("Draft saved. Apply it from the capture schedule at a capture boundary.", error: false);
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private static string ShortHash(string value) => value.Length <= 12 ? value : value[..12];

    private static string Split(string value) => OperationsPage.SplitWords(value);
}
