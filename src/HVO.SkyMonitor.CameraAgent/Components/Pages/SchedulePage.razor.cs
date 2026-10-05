using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class SchedulePage : SiteTimeComponent, IAsyncDisposable
{
    private string? _blackoutInputZone;
    private readonly Dictionary<CaptureProfileFormModel.BlackoutRow, (string? Start, string? End)> _blackoutInputs = [];

    private const int CalendarNights = 7;
    private static readonly TimeSpan CalendarRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly EditorStep[] EditorSteps =
    [
        EditorStep.Policy, EditorStep.Setpoints, EditorStep.Windows,
        EditorStep.Blackouts, EditorStep.Processing, EditorStep.Review,
    ];

    private CaptureScheduleOperatorState? _state;
    private NamedRigSelection? _rigSelection;
    private bool _rigSelectionAvailable;
    private bool RigPendingRestart => _rigSelection?.PendingRevisionId is not null;
    private bool PendingRigSchedule => RigPendingRestart && _state?.PendingRevision is not null &&
        string.Equals(_rigSelection!.PendingScheduleRevisionId, _state.PendingRevision.RevisionId, StringComparison.Ordinal);
    private bool ScheduleActionsBlocked => !_rigSelectionAvailable || RigPendingRestart;
    private CameraAgentScheduleCalendar? _calendar;
    private string? _calendarMessage;
    private string _timeZoneId = "UTC";
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private bool _siteTimeZoneKnown;
    private ITimer? _calendarTimer;
    private readonly Lock _calendarGate = new();
    private long _calendarGeneration;
    private bool _disposed;
    private CaptureSchedulePreview? _preview;
    private CaptureProcessingPlanPreview? _pipelinePlan;
    private string _editorJson = string.Empty;
    private CaptureProfileFormModel? _model;
    private LocalCaptureProfileDefinition? _basisProfile;
    private bool _jsonIsTruth;
    private bool _editorDirty;
    private EditorStep _step = EditorStep.Policy;
    private string _overrideMode = "ForceClosed";
    private DateTime? _overrideStart;
    private DateTime? _overrideEnd;
    private string _overrideProfile = string.Empty;
    private string? _overrideTimeZoneId;
    private string? _confirmRevisionId;
    private long _confirmRevisionNumber;
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
    private ScheduleDialog _dialog;
    private ElementReference _dialogElement;
    private bool _showDialog;
    private string? _dialogTriggerId;
    private string? _focusTargetId;
    private bool _confirmRollback;
    private readonly Dictionary<string, (string Key, long ExpectedVersion)> _clearOverrideKeys = new(StringComparer.Ordinal);

    private enum ScheduleDialog
    {
        None,
        Editor,
        Override,
        Activation,
    }

    private enum EditorStep
    {
        Policy,
        Setpoints,
        Windows,
        Blackouts,
        Processing,
        Review,
    }

    private sealed record ExceptionRow(string Starts, string Rule, string Detail, string Ends);

    private sealed record PrecedenceStep(string State, string Title, string Detail);

    private sealed record ClockTick(double Fraction, string Label);

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal ICameraAgentNamedRigUiService NamedRigService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    /// <summary><c>override</c> opens the temporary override dialog once the page has loaded, when it is available.</summary>
    [Parameter, SupplyParameterFromQuery(Name = "action")] public string? Action { get; set; }

    private CameraAgentScheduleNight? Tonight => _calendar?.Nights.Count > 0 ? _calendar.Nights[0] : null;

    private string? OverrideUnavailableReason => _siteTimeZoneKnown
        ? null
        : "Temporary overrides are entered in site-local time, and the site timezone is unknown until the schedule calendar loads. Refresh to try again.";

    private string TimeZoneText => _siteTimeZoneKnown ? _timeZoneId : "UTC (site timezone unknown)";

    private string DecisionSummary
    {
        get
        {
            var decision = _state!.Decision;
            var reason = ReasonText(decision.Reason);
            var setpoint = decision.SetpointProfileId is null ? null : $" with setpoint {decision.SetpointProfileId}";
            var overrideText = decision.OverrideId is null ? "no temporary override" : "temporary override in effect";
            return $"{char.ToUpperInvariant(reason[0])}{reason[1..]}{setpoint} / {overrideText}";
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync(resetEditor: true).ConfigureAwait(false);
        if (string.Equals(Action, "override", StringComparison.Ordinal))
        {
            OpenOverrideDialog();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/SchedulePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/SchedulePage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, "schedule-refresh").ConfigureAwait(false);
        }
    }

    private Task RefreshAsync() => LoadAsync(resetEditor: false);

    private Task DiscardEditsAsync() => LoadAsync(resetEditor: true);

    /// <summary>
    /// Reloads durable state. Unsaved editor changes survive a refresh while the revision they were based on is
    /// still the editor basis; otherwise the editor reloads so a draft is never saved against a stale basis.
    /// </summary>
    private async Task LoadAsync(bool resetEditor)
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
                _rigSelectionAvailable = false;
                _rigSelection = null;
                var rigResult = await NamedRigService.GetAsync(CancellationToken.None).ConfigureAwait(false);
                if (rigResult.Kind == OperatorUiResultKind.Unauthorized)
                {
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                    return;
                }
                _rigSelectionAvailable = rigResult.IsSuccess && rigResult.Value is not null;
                _rigSelection = rigResult.Value?.Selection;
                _state = result.Value;
                var editorRevision = _state.PendingRevision ?? _state.ActiveRevision;
                var basisChanged = !string.Equals(_editorBasisRevisionId, editorRevision.RevisionId, StringComparison.Ordinal);
                var keepEdits = _editorDirty && !resetEditor && !basisChanged;
                if (!keepEdits)
                {
                    LoadEditor(editorRevision.Profile);
                    _editorBasisRevisionId = editorRevision.RevisionId;
                    _preview = null;
                    await LoadPipelineAsync(editorRevision.RevisionId).ConfigureAwait(false);
                }
                await LoadCalendarAsync().ConfigureAwait(false);
                var setpoints = _state.ActiveRevision.Definition.SetpointProfiles;
                if (!setpoints.Any(item => string.Equals(item.Id, _overrideProfile, StringComparison.Ordinal)))
                {
                    _overrideProfile = _state.Decision.SetpointProfileId ?? (setpoints.Count > 0 ? setpoints[0].Id : string.Empty);
                }
                if (!_rigSelectionAvailable)
                    SetMessage("Named rig selection is unavailable. Schedule changes are disabled until refresh succeeds.", error: true);
                else if (_editorDirty && !resetEditor && basisChanged)
                    SetMessage("The schedule changed while you were editing, so your unsaved edits were discarded. The editor now shows the latest revision.", error: true);
                else
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

    private async Task LoadPipelineAsync(string editorRevisionId)
    {
        var pipelineResult = await ScheduleService.GetPipelineAsync(CancellationToken.None).ConfigureAwait(false);
        if (pipelineResult.IsSuccess && pipelineResult.Value is { } pipelineState)
        {
            var selected = pipelineState.Pending is not null &&
                string.Equals(pipelineState.Pending.RevisionId, editorRevisionId, StringComparison.Ordinal)
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
    }

    /// <summary>
    /// Reads the calendar and applies it unless a later read started meanwhile, so a slow periodic read never
    /// replaces the result of a newer full load. Returns whether this read's result was applied.
    /// </summary>
    private async Task<bool> LoadCalendarAsync()
    {
        long generation;
        lock (_calendarGate)
        {
            generation = ++_calendarGeneration;
        }
        var calendarResult = await ScheduleService.GetCalendarAsync(CalendarNights, CancellationToken.None).ConfigureAwait(false);
        lock (_calendarGate)
        {
            if (generation != _calendarGeneration)
            {
                return false;
            }
            ApplyCalendar(calendarResult);
            return true;
        }
    }

    private void ApplyCalendar(OperatorUiResult<CameraAgentScheduleCalendar> calendarResult)
    {
        var message = calendarResult.Message;
        if (calendarResult.IsSuccess && calendarResult.Value is { } calendar)
        {
            try
            {
                _timeZone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
                _timeZoneId = calendar.TimeZoneId;
                _siteTimeZoneKnown = true;
                _calendar = calendar;
                _calendarMessage = null;
                ScheduleCalendarRefresh();
                return;
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                message = $"The site timezone '{calendar.TimeZoneId}' is not available on this host.";
            }
        }
        // Times still display, labelled UTC, but override entry stays closed until the site timezone is known again.
        _calendar = null;
        _calendarMessage = message;
        _timeZone = TimeZoneInfo.Utc;
        _timeZoneId = "UTC";
        _siteTimeZoneKnown = false;
        ScheduleCalendarRefresh();
    }

    /// <summary>
    /// Re-reads the calendar every few minutes, and at the night boundary, so a long-lived page moves its now
    /// marker and advances Tonight at site-local noon rather than showing the night it was opened on.
    /// </summary>
    private void ScheduleCalendarRefresh()
    {
        _calendarTimer?.Dispose();
        _calendarTimer = null;
        if (_disposed)
        {
            return;
        }
        var due = CalendarRefreshInterval;
        if (Tonight is { } night && night.EndUtc - TimeProvider.GetUtcNow() is var untilBoundary &&
            untilBoundary > TimeSpan.Zero && untilBoundary < due)
        {
            due = untilBoundary;
        }
        _calendarTimer = TimeProvider.CreateTimer(
            static state => ((SchedulePage)state!).OnCalendarRefreshDue(), this, due, Timeout.InfiniteTimeSpan);
    }

    private void OnCalendarRefreshDue() => _ = InvokeAsync(RefreshCalendarAsync);

    private async Task RefreshCalendarAsync()
    {
        if (_disposed)
        {
            return;
        }
        if (_busy)
        {
            // A full load in flight reads the calendar itself; check again on the next interval.
            ScheduleCalendarRefresh();
            return;
        }
        // Resume on the renderer's context: the read can complete asynchronously, and the render must not run off it.
        if (await LoadCalendarAsync().ConfigureAwait(true) && !_disposed)
        {
            StateHasChanged();
        }
    }

    private void OpenEditor() => OpenEditorAt(_step, "schedule-edit-open");

    private void OpenEditorAt(EditorStep step, string triggerId)
    {
        if (_state is null || _model is null)
        {
            return;
        }
        _step = step;
        OpenDialog(ScheduleDialog.Editor, triggerId);
    }

    private void OpenOverrideDialog()
    {
        if (_state is null || !_siteTimeZoneKnown)
        {
            return;
        }
        var now = LocalMinute(TimeProvider.GetUtcNow());
        _overrideStart = now;
        _overrideEnd = now.AddHours(1);
        _overrideOneShot = false;
        _overrideTimeZoneId = _timeZoneId;
        OpenDialog(ScheduleDialog.Override, "schedule-override-open");
    }

    private void OpenDialog(ScheduleDialog dialog, string triggerId)
    {
        _dialog = dialog;
        _dialogTriggerId = triggerId;
        _message = null;
        _showDialog = true;
    }

    private void CloseDialog()
    {
        if (_busy)
        {
            return;
        }
        FinishDialog();
    }

    private void FinishDialog()
    {
        _dialog = ScheduleDialog.None;
        _confirmRevisionId = null;
        _focusTargetId = _dialogTriggerId;
        _dialogTriggerId = null;
    }

    private void MoveStep(int delta)
    {
        var index = Math.Clamp(Array.IndexOf(EditorSteps, _step) + delta, 0, EditorSteps.Length - 1);
        _step = EditorSteps[index];
    }

    private async Task PreviewAsync()
    {
        if (_editorBasisRevisionId is null || !TryResolveEditorJson(out var editorJson))
        {
            return;
        }
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
                SetMessage("The draft is valid. Nothing has been saved yet.", error: false);
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
        if (_editorBasisRevisionId is null || !TryResolveEditorJson(out var editorJson))
        {
            return;
        }
        var basisRevisionId = _editorBasisRevisionId;
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
            LoadEditor(CameraAgentScheduleUiService.ParseProfile(preview.ProfileJson));
            _editorDirty = true;
            _pipelinePlan = preview.Plan;
            _preview = null;
            SetMessage("Processing step updated in the draft. Save the draft to keep it.", error: false);
        }
        else
        {
            SetMessage(result.Message ?? "The desired graph toggle is invalid.", error: true);
        }
    }

    private async Task StageAsync()
    {
        if (ScheduleActionsBlocked || _state is null || _editorBasisRevisionId is null || !TryResolveEditorJson(out var editorJson))
        {
            return;
        }
        var stageSignature = string.Concat(_editorBasisRevisionId, "\n", editorJson);
        if (!string.Equals(_stagePayload, stageSignature, StringComparison.Ordinal))
        {
            _stagePayload = stageSignature;
            _stageKey = NewKey();
            _stageExpectedVersion = _state.StateVersion;
        }
        _busy = true;
        var result = await ScheduleService.StageAsync(
            editorJson,
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
        if (result.IsSuccess)
        {
            _editorDirty = false;
        }
        if (await CompleteMutationAsync(result, "Draft saved.").ConfigureAwait(false))
        {
            FinishDialog();
            if (_state?.PendingRevision is { } pending)
            {
                SetMessage($"Draft revision {pending.RevisionNumber} saved. Review apply to activate it.", error: false);
                if (!PendingRigSchedule && !ScheduleActionsBlocked)
                {
                    _focusTargetId = PendingActivationTriggerId(pending.RevisionId);
                }
            }
        }
    }

    private void BeginActivation(string revisionId, string triggerId, bool rollback)
    {
        if (ScheduleActionsBlocked || _state is null) return;
        _confirmRevisionId = revisionId;
        _confirmRevisionNumber = _state.History
            .FirstOrDefault(item => string.Equals(item.RevisionId, revisionId, StringComparison.Ordinal))?.RevisionNumber
            ?? (_state.PendingRevision is { } pending && pending.RevisionId == revisionId ? pending.RevisionNumber : 0);
        _confirmRollback = rollback;
        OpenDialog(ScheduleDialog.Activation, triggerId);
    }

    private void CancelActivation() => CloseDialog();

    private async Task ConfirmActivationAsync()
    {
        if (ScheduleActionsBlocked || _state is null || _confirmRevisionId is null)
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
        var result = _confirmRollback
            ? await ScheduleService.RollbackAsync(
                revisionId,
                _activationExpectedVersion,
                _activationKey!,
                "operator rollback",
                CancellationToken.None).ConfigureAwait(false)
            : await ScheduleService.ActivateAsync(
                revisionId,
                _activationExpectedVersion,
                _activationKey!,
                "operator apply",
                CancellationToken.None).ConfigureAwait(false);
        _busy = false;
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _activationKey = null;
            _activationRevisionId = null;
        }
        var succeeded = await CompleteMutationAsync(result, "Revision applied at the capture boundary.").ConfigureAwait(false);
        if (result.Kind != OperatorUiResultKind.Unavailable &&
            result.Kind != OperatorUiResultKind.Unauthorized)
        {
            // A rejected command is final for this confirmation; show its reason on the page.
            FinishDialog();
            if (succeeded)
            {
                _focusTargetId = "schedule-refresh";
            }
        }
    }

    private async Task AddOverrideAsync()
    {
        if (_state is null)
        {
            return;
        }
        if (!_siteTimeZoneKnown)
        {
            SetMessage(OverrideUnavailableReason!, error: true);
            return;
        }
        if (!string.Equals(_overrideTimeZoneId, _timeZoneId, StringComparison.Ordinal))
        {
            // A calendar refresh changed the site timezone while the dialog was open, so the entered wall-clock
            // times would now convert to different instants. Ask once; the next submit reads them in the new zone.
            SetMessage($"The site timezone changed from {_overrideTimeZoneId} to {_timeZoneId} while this dialog was open. " +
                $"The times are now read as {_timeZoneId} times; check them and create the override again.", error: true);
            _overrideTimeZoneId = _timeZoneId;
            return;
        }
        if ((LocalTimeProblem(_overrideStart) ?? LocalTimeProblem(_overrideEnd)) is { } problem)
        {
            SetMessage(problem, error: true);
            return;
        }
        if (!TryParseLocal(_overrideStart, out var start) || !TryParseLocal(_overrideEnd, out var end))
        {
            SetMessage($"Enter both override times as local {_timeZoneId} times.", error: true);
            return;
        }
        if (end <= start)
        {
            SetMessage("The override must end after it starts.", error: true);
            return;
        }
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
        if (await CompleteMutationAsync(result, "Override created.").ConfigureAwait(false))
        {
            FinishDialog();
        }
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
        if (await CompleteMutationAsync(result, "Override cleared.").ConfigureAwait(false))
        {
            _focusTargetId = "schedule-override-open";
        }
    }

    /// <summary>Applies a command result and reports whether it succeeded and the page reloaded.</summary>
    private async Task<bool> CompleteMutationAsync(
        OperatorUiResult<CaptureScheduleStoreSnapshot> result,
        string successMessage)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return false;
        }
        if (!result.IsSuccess)
        {
            SetMessage(result.Message ?? "The command failed.", error: true);
            return false;
        }
        await RefreshAsync().ConfigureAwait(false);
        SetMessage(successMessage, error: false);
        return true;
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _messageIsError = error;
    }

    private void EditorChanged()
    {
        _editorDirty = true;
        _preview = null;
        _pipelinePlan = null;
        _message = null;
    }

    private void FormChanged()
    {
        _jsonIsTruth = false;
        EditorChanged();
        // Keep the advanced view showing exactly what a command would send.
        if (_model is not null && _basisProfile is not null && _model.TryApply(_basisProfile, out var profile, out _))
        {
            _editorJson = CameraAgentScheduleUiService.SerializeProfile(profile);
        }
    }

    private void JsonChanged()
    {
        _jsonIsTruth = true;
        EditorChanged();
    }

    private void AddSetpoint() { _model?.AddSetpoint(); FormChanged(); }

    private void AddWindow() { _model?.AddWeeklyWindow(); FormChanged(); }

    private void SetWindowDay(CaptureProfileFormModel.WeeklyWindowRow row, DayOfWeek day, bool selected)
    {
        _model?.SetWeeklyWindowDay(row, day, selected);
        FormChanged();
    }

    private void SplitWindow(CaptureProfileFormModel.WeeklyWindowRow row, DayOfWeek day)
    {
        _model?.SplitWeeklyWindow(row, day);
        FormChanged();
    }

    private string? BlackoutInput(CaptureProfileFormModel.BlackoutRow row, bool start)
    {
        if (_blackoutInputs.TryGetValue(row, out var draft)) return start ? draft.Start : draft.End;
        var value = start ? row.StartUtc : row.EndUtc;
        return DateTimeOffset.TryParse(value, Invariant, DateTimeStyles.AssumeUniversal, out var utc)
            ? SiteTime.Input(utc) : value;
    }

    private void SetBlackoutInput(CaptureProfileFormModel.BlackoutRow row, bool start, ChangeEventArgs args)
    {
        var text = args.Value?.ToString();
        var draft = (Start: BlackoutInput(row, true), End: BlackoutInput(row, false));
        _blackoutInputs[row] = start ? (text, draft.End) : (draft.Start, text);
        _jsonIsTruth = false;
        EditorChanged();
    }

    private bool ResolveBlackoutInputs()
    {
        if (_blackoutInputs.Count > 0 && !string.Equals(_blackoutInputZone, SiteTime.Label, StringComparison.Ordinal))
        {
            SetMessage("The site time zone changed while the blackout editor was open. Reopen it before entering times.", error: true);
            return false;
        }
        foreach (var row in (_model?.Blackouts ?? []).Where(_blackoutInputs.ContainsKey))
        {
            var draft = _blackoutInputs[row];
            if (!ResolveBlackoutBoundary(row.StartUtc, draft.Start, out var start) ||
                !ResolveBlackoutBoundary(row.EndUtc, draft.End, out var end)) return false;
            row.StartUtc = start;
            row.EndUtc = end;
        }
        return true;
    }

    private bool ResolveBlackoutBoundary(string recordedUtc, string? entered, out string resolvedUtc)
    {
        resolvedUtc = recordedUtc;
        // The existing instant already disambiguates a fold and may contain subsecond precision.
        if (DateTimeOffset.TryParse(recordedUtc, Invariant, DateTimeStyles.AssumeUniversal, out var recorded) &&
            string.Equals(entered, SiteTime.Input(recorded), StringComparison.Ordinal)) return true;
        if (!SiteTime.TryInput(entered, out var instant, out var error) || instant is null)
        {
            SetMessage(error ?? "Both blackout times are required.", error: true);
            return false;
        }
        resolvedUtc = instant.Value.ToString("O", Invariant);
        return true;
    }

    private void AddBlackout() { _model?.AddBlackout(); FormChanged(); }

    private void RemoveSetpoint(CaptureProfileFormModel.SetpointProfileRow row) { _model?.Setpoints.Remove(row); FormChanged(); }

    private void RemoveWindow(CaptureProfileFormModel.WeeklyWindowRow row) { _model?.WeeklyWindows.Remove(row); FormChanged(); }

    private void RemoveBlackout(CaptureProfileFormModel.BlackoutRow row) { _model?.Blackouts.Remove(row); FormChanged(); }

    /// <summary>Loads a sanitized profile into both the typed form and the canonical JSON view.</summary>
    private void LoadEditor(LocalCaptureProfileDefinition profile)
    {
        _blackoutInputZone = SiteTime.Label;
        _blackoutInputs.Clear();
        _basisProfile = profile;
        _model = CaptureProfileFormModel.FromProfile(profile);
        _editorJson = CameraAgentScheduleUiService.SerializeProfile(profile);
        _jsonIsTruth = false;
        _editorDirty = false;
    }

    /// <summary>
    /// Produces the JSON the service receives: the advanced editor text when it was edited last,
    /// otherwise the typed fields rewritten onto the basis profile. Parse errors stop the command.
    /// </summary>
    private bool TryResolveEditorJson(out string editorJson)
    {
        if (_jsonIsTruth || _model is null || _basisProfile is null)
        {
            editorJson = _editorJson;
            return true;
        }
        if (!ResolveBlackoutInputs())
        {
            editorJson = string.Empty;
            return false;
        }
        if (!_model.TryApply(_basisProfile, out var profile, out var errors))
        {
            SetMessage(string.Join(' ', errors), error: true);
            editorJson = string.Empty;
            return false;
        }
        editorJson = CameraAgentScheduleUiService.SerializeProfile(profile);
        _editorJson = editorJson;
        return true;
    }

    /// <summary>
    /// Names a site-local time a daylight-saving change makes unusable: one the clocks skip, or one they repeat,
    /// which a datetime-local field cannot say which occurrence of is meant.
    /// </summary>
    private string? LocalTimeProblem(DateTime? value)
    {
        if (value is not { } entered)
        {
            return null;
        }
        var local = DateTime.SpecifyKind(entered, DateTimeKind.Unspecified);
        var text = local.ToString("d MMM HH:mm", Invariant);
        if (_timeZone.IsInvalidTime(local))
        {
            return $"{text} does not exist in {_timeZoneId}; the clocks skip it for daylight saving. Choose a time outside that hour.";
        }
        return _timeZone.IsAmbiguousTime(local)
            ? $"{text} occurs twice in {_timeZoneId} when daylight saving ends, so it cannot name one moment. Choose a time outside that hour."
            : null;
    }

    /// <summary>Parses a datetime-local value in the site timezone, rejecting times a daylight-saving change skips or repeats.</summary>
    private bool TryParseLocal(DateTime? value, out DateTimeOffset utc)
    {
        utc = default;
        if (value is not { } entered)
        {
            return false;
        }
        var local = DateTime.SpecifyKind(entered, DateTimeKind.Unspecified);
        if (_timeZone.IsInvalidTime(local) || _timeZone.IsAmbiguousTime(local))
        {
            return false;
        }
        var converted = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _timeZone), TimeSpan.Zero);
        utc = DateTimeOffset.FromUnixTimeMilliseconds(converted.ToUnixTimeMilliseconds());
        return true;
    }

    private DateTime LocalMinute(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, _timeZone).DateTime;
        return new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified);
    }

    private string Clock(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _timeZone).ToString("HH:mm zzz", Invariant);

    private string DayClock(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _timeZone).ToString("ddd d MMM HH:mm zzz", Invariant);

    private static string? SegmentClass(CameraAgentScheduleSegment segment) => segment switch
    {
        { Admitted: true, OverrideId: not null } => "override-open",
        { Admitted: true } => "open",
        { Reason: CaptureScheduleAdmissionReason.Blackout } => "blackout",
        { Reason: CaptureScheduleAdmissionReason.ForceClosedOverride } => "override-closed",
        _ => null,
    };

    private static double Fraction(CameraAgentScheduleNight night, DateTimeOffset value)
    {
        var total = (night.EndUtc - night.StartUtc).TotalMinutes;
        return total <= 0 ? 0 : Math.Clamp((value - night.StartUtc).TotalMinutes / total, 0, 1);
    }

    private static string Percent(double fraction) => (fraction * 100).ToString("0.###", Invariant) + "%";

    private static string SegmentStyle(CameraAgentScheduleNight night, CameraAgentScheduleSegment segment)
    {
        var left = Fraction(night, segment.StartUtc);
        var width = Fraction(night, segment.EndUtc) - left;
        return $"left: {Percent(left)}; width: {Percent(width)}";
    }

    private double? NowFraction(CameraAgentScheduleNight night)
    {
        var now = _calendar!.GeneratedUtc;
        return now >= night.StartUtc && now < night.EndUtc ? Fraction(night, now) : null;
    }

    private List<ClockTick> Ticks(CameraAgentScheduleNight night)
    {
        var ticks = new List<ClockTick>(5);
        for (var hours = 0; hours <= 24; hours += 6)
        {
            var at = hours == 24 ? night.EndUtc : night.StartUtc.AddHours(hours);
            ticks.Add(new ClockTick(Fraction(night, at), Clock(at)));
        }
        return ticks;
    }

    private string TonightLabel(CameraAgentScheduleNight night)
    {
        var now = _calendar!.GeneratedUtc;
        var open = night.Segments.Where(static item => item.Admitted).ToArray();
        if (open.FirstOrDefault(item => item.StartUtc <= now && now < item.EndUtc) is { } current)
        {
            return $"Planned open until {Clock(current.EndUtc)}";
        }
        if (open.FirstOrDefault(item => item.StartUtc > now) is { } next)
        {
            return $"Planned closed / opens {Clock(next.StartUtc)}";
        }
        return open.Length > 0 ? $"Tonight's window ended {Clock(open[^1].EndUtc)}" : "No capture window tonight";
    }

    private static string ClosedReason(CameraAgentScheduleNight night)
    {
        var reasons = night.Segments.Select(static item => item.Reason).ToHashSet();
        if (reasons.Contains(CaptureScheduleAdmissionReason.Blackout)) return "A blackout covers tonight.";
        if (reasons.Contains(CaptureScheduleAdmissionReason.ForceClosedOverride)) return "A temporary override closes capture.";
        if (reasons.Contains(CaptureScheduleAdmissionReason.DateException)) return "A date exception closes tonight.";
        if (reasons.Contains(CaptureScheduleAdmissionReason.NoSolarEvent)) return "The solar event a window needs does not occur tonight.";
        return "No weekly window starts tonight.";
    }

    private static string SegmentDetail(CameraAgentScheduleSegment segment)
        => $"{segment.SetpointProfileId ?? "No setpoint"} / {ReasonText(segment.Reason)}";

    private string NightSummary(CameraAgentScheduleNight night)
    {
        var open = night.Segments.Where(static item => item.Admitted).ToArray();
        if (open.Length == 0)
        {
            return "Closed";
        }
        var span = $"{Clock(open[0].StartUtc)} to {Clock(open[^1].EndUtc)}";
        return open.Length == 1 ? span : $"{span} / {open.Length} spans";
    }

    private static string ReasonText(CaptureScheduleAdmissionReason reason) => reason switch
    {
        CaptureScheduleAdmissionReason.SafetyUnavailable => "capture safety unavailable",
        CaptureScheduleAdmissionReason.ManualPause => "capture paused",
        CaptureScheduleAdmissionReason.Blackout => "blackout",
        CaptureScheduleAdmissionReason.ForceClosedOverride => "temporary override closed",
        CaptureScheduleAdmissionReason.ForceOpenOverride => "temporary override open",
        CaptureScheduleAdmissionReason.DateException => "date exception",
        CaptureScheduleAdmissionReason.WeeklyWindow => "weekly window",
        CaptureScheduleAdmissionReason.LegacyCompatibility => "always open (legacy profile)",
        CaptureScheduleAdmissionReason.NoSolarEvent => "no solar event",
        CaptureScheduleAdmissionReason.DefaultClosed => "outside every window",
        _ => Split(reason.ToString()),
    };

    private static string BoundaryKindText(CaptureScheduleBoundaryKind kind) => kind switch
    {
        CaptureScheduleBoundaryKind.Sunrise => "sunrise",
        CaptureScheduleBoundaryKind.Sunset => "sunset",
        CaptureScheduleBoundaryKind.CivilDawn => "civil dawn",
        CaptureScheduleBoundaryKind.CivilDusk => "civil dusk",
        CaptureScheduleBoundaryKind.NauticalDawn => "nautical dawn",
        CaptureScheduleBoundaryKind.NauticalDusk => "nautical dusk",
        CaptureScheduleBoundaryKind.AstronomicalDawn => "astronomical dawn",
        CaptureScheduleBoundaryKind.AstronomicalDusk => "astronomical dusk",
        _ => Split(kind.ToString()),
    };

    private static string SafetyText(CaptureScheduleSafetyState state) => state switch
    {
        CaptureScheduleSafetyState.Available => "Ingress, lanes and storage available",
        CaptureScheduleSafetyState.IngressUnavailable => "Blocked: durable ingress unavailable",
        CaptureScheduleSafetyState.RequiredLaneUnavailable => "Blocked: a required lane is unavailable",
        CaptureScheduleSafetyState.StorageUnavailable => "Blocked: local storage unavailable",
        _ => "Blocked: safety state unknown",
    };

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", Invariant) + " s";

    private static string Number(double value) => value.ToString("0.###", Invariant);

    private static string CadenceText(CaptureScheduleSetpointProfile setpoint)
        => setpoint.CadenceMode == CaptureCadenceMode.Continuous
            ? setpoint.TargetFps is { } fps ? $"Continuous / {Number(fps)} fps" : "Continuous"
            : $"Every {Seconds(setpoint.CaptureInterval)} (minimum start)";

    private string WindowSummary(string setpointId)
    {
        var windows = _state!.ActiveRevision.Definition.WeeklyWindows
            .Where(item => string.Equals(item.SetpointProfileId, setpointId, StringComparison.Ordinal))
            .GroupBy(item => (BoundaryText(item.Start), BoundaryText(item.End)))
            .Select(group => $"{DaysText(group.Select(static item => item.Day))} {group.Key.Item1} to {group.Key.Item2}")
            .ToArray();
        return windows.Length == 0 ? "No weekly window" : string.Join("; ", windows);
    }

    private static string DaysText(IEnumerable<DayOfWeek> days)
    {
        var set = days.Distinct().Order().ToArray();
        return set.Length == 7 ? "Every night" : string.Join(", ", set.Select(static day => day.ToString()[..3]));
    }

    private static string BoundaryText(CaptureScheduleBoundary boundary)
    {
        var text = boundary.Kind == CaptureScheduleBoundaryKind.FixedLocalTime
            ? boundary.LocalTime?.ToString("HH:mm", Invariant) ?? "unset"
            : BoundaryKindText(boundary.Kind);
        if (boundary.Kind != CaptureScheduleBoundaryKind.FixedLocalTime && boundary.Offset != TimeSpan.Zero)
        {
            var minutes = boundary.Offset.TotalMinutes;
            text += $" {(minutes < 0 ? "-" : "+")} {Math.Abs(minutes).ToString("0", Invariant)} min";
        }
        return boundary.DayOffset == 0 ? text : $"{text} (day {boundary.DayOffset:+0;-0})";
    }

    private List<ExceptionRow> ExceptionRows()
    {
        var definition = _state!.ActiveRevision.Definition;
        var rows = new List<ExceptionRow>();
        foreach (var rule in (definition.DateExceptions ?? []).OrderBy(static item => item.Date))
        {
            var detail = rule.ForceClosed
                ? "Closed all night"
                : $"{rule.Windows?.Count ?? 0} replacement window{(rule.Windows?.Count == 1 ? "" : "s")}";
            rows.Add(new ExceptionRow(rule.Date.ToString("ddd d MMM yyyy", Invariant), "Date exception", $"{detail} / {rule.Id}", "End of that night"));
        }
        foreach (var blackout in (definition.Blackouts ?? []).OrderBy(static item => item.StartUtc))
        {
            rows.Add(new ExceptionRow(DayClock(blackout.StartUtc), "Blackout", blackout.Id, DayClock(blackout.EndUtc)));
        }
        return rows;
    }

    private string OverrideLabel(CaptureScheduleOverride item)
        => $"{(item.Mode == CaptureScheduleOverrideMode.ForceOpen ? "force open" : "force closed")} from {DayClock(item.StartUtc)}";

    private List<PrecedenceStep> PrecedenceSteps()
    {
        var decision = _state!.Decision;
        var safetyBlocked = decision.Reason == CaptureScheduleAdmissionReason.SafetyUnavailable;
        var paused = decision.Reason == CaptureScheduleAdmissionReason.ManualPause;
        var scheduleClosed = !decision.Admitted && !safetyBlocked && !paused;
        var setpoint = decision.SetpointProfileId is null
            ? null
            : _state.ActiveRevision.Definition.SetpointProfiles.FirstOrDefault(item => string.Equals(item.Id, decision.SetpointProfileId, StringComparison.Ordinal));
        return
        [
            new(safetyBlocked ? "blocking" : "passed", "Safety",
                SafetyText(decision.SafetyState)),
            new(paused ? "blocking" : safetyBlocked ? "not-reached" : "passed", "Pause",
                paused ? "Capture is paused by an operator" : safetyBlocked ? "Not reached" : "Not paused"),
            new(scheduleClosed ? "blocking" : decision.Admitted ? "passed" : "not-reached", "Schedule",
                decision.Admitted || scheduleClosed ? $"{(decision.Admitted ? "Open" : "Closed")}: {ReasonText(decision.Reason)}" : "Not reached"),
            new(decision.Admitted ? "info" : "not-reached", "Cadence",
                decision.Admitted && setpoint is not null ? CadenceText(setpoint) : decision.Admitted ? "Setpoint default" : "Not reached"),
        ];
    }

    private static string StepTitle(EditorStep step) => step switch
    {
        EditorStep.Policy => "Capture policy",
        EditorStep.Setpoints => "Setpoints",
        EditorStep.Windows => "Weekly windows",
        EditorStep.Blackouts => "Blackouts",
        EditorStep.Processing => "Processing",
        _ => "Review",
    };

    private static string NewKey() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string ShortHash(string value) => value.Length <= 12 ? value : value[..12];

    private static string ActivationTriggerId(string revisionId, string origin)
        => $"schedule-activation-{origin}-{revisionId}";

    private static string PendingActivationTriggerId(string revisionId)
        => ActivationTriggerId(revisionId, "pending");

    private static string HistoryActivationTriggerId(CaptureScheduleRevisionSnapshot revision)
        => ActivationTriggerId(revision.RevisionId, $"history-{revision.RevisionNumber}");

    private static string Split(string value)
        => string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _calendarTimer?.Dispose();
        _calendarTimer = null;
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
