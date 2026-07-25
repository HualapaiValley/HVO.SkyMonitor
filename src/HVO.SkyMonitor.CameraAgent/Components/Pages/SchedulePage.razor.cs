using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class SchedulePage : ComponentBase
{
    private CaptureScheduleOperatorState? _state;
    private CaptureSchedulePreview? _preview;
    private string _editorJson = string.Empty;
    private string _overrideMode = "ForceClosed";
    private string _overrideStart = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    private string _overrideEnd = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    private string _overrideProfile = string.Empty;
    private string? _confirmRevisionId;
    private string? _message;
    private bool _messageIsError;
    private bool _overrideOneShot;
    private bool _loading = true;
    private bool _busy;

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
                _editorJson = CameraAgentScheduleUiService.SerializeProfile(_state.PendingRevision?.Profile ?? _state.ActiveRevision.Profile);
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
        _busy = true;
        var result = await ScheduleService.PreviewAsync(_editorJson, 7, CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.IsSuccess)
        {
            _preview = result.Value;
            SetMessage("Preview is valid. No durable state changed.", error: false);
        }
        else
        {
            SetMessage(result.Message ?? "Preview validation failed.", error: true);
        }
    }

    private async Task StageAsync()
    {
        if (_state is null)
        {
            return;
        }
        _busy = true;
        var result = await ScheduleService.StageAsync(
            _editorJson, _state.StateVersion, NewKey(), "operator draft", CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        await CompleteMutationAsync(result, "Draft saved.").ConfigureAwait(false);
    }

    private void BeginActivation(string revisionId) => _confirmRevisionId = revisionId;

    private void CancelActivation() => _confirmRevisionId = null;

    private async Task ConfirmActivationAsync()
    {
        if (_state is null || _confirmRevisionId is null)
        {
            return;
        }
        var revisionId = _confirmRevisionId;
        _busy = true;
        var result = await ScheduleService.ActivateAsync(
            revisionId, _state.StateVersion, NewKey(), "operator apply", CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        _confirmRevisionId = null;
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
        var scheduleOverride = new CaptureScheduleOverride(
            $"override-{Guid.NewGuid():N}",
            _state.ActiveRevision.RevisionId,
            _state.ActiveRevision.ScheduleSha256,
            mode,
            start,
            end,
            mode == CaptureScheduleOverrideMode.ForceOpen ? _overrideProfile : null,
            mode == CaptureScheduleOverrideMode.ForceOpen && _overrideOneShot);
        _busy = true;
        var result = await ScheduleService.AddOverrideAsync(
            scheduleOverride, _state.StateVersion, NewKey(), "operator override", CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        await CompleteMutationAsync(result, "Override created.").ConfigureAwait(false);
    }

    private async Task ClearOverrideAsync(string overrideId)
    {
        if (_state is null)
        {
            return;
        }
        _busy = true;
        var result = await ScheduleService.ClearOverrideAsync(
            overrideId, _state.StateVersion, NewKey(), "operator clear", CancellationToken.None).ConfigureAwait(false);
        _busy = false;
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

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string ShortHash(string value) => value.Length <= 12 ? value : value[..12];

    private static string FormatUtc(DateTimeOffset? value) => value?.ToString("u") ?? "No known transition";

    private static string FormatInterval(DateTimeOffset? start, DateTimeOffset? end)
        => start.HasValue && end.HasValue ? $"{start.Value:u} to {end.Value:u}" : "None";

    private static string Split(string value)
        => string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
