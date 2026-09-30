using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class AutomationsPage : ComponentBase, IAsyncDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The day track covers the current observing night and the next one, so two nights always span it.</summary>
    private const int CalendarNights = 2;

    /// <summary>A run marker card is about a fifth of the minimum track width; closer markers share a row only as ticks.</summary>
    private const double MarkerSpacingPercent = 21;
    private const double MarkerMinimumPercent = 11;
    private const double MarkerMaximumPercent = 89;
    private const int MarkerRows = 2;

    /// <summary>The identifier the create action returns focus to.</summary>
    internal const string CreateTriggerId = "automation-create";

    /// <summary>The editor action that validates the definition and asks for confirmation.</summary>
    internal const string SaveTriggerId = "automation-save";

    internal const string ConfirmId = "automation-confirm";
    internal const string CancelId = "automation-cancel";
    private const string FocusFallbackId = "automation-refresh";
    private const string DefaultPeriodicInterval = "3600";
    private const string DefaultCaptureInterval = "10";

    private enum PendingCommandKind
    {
        Save,
        Toggle,
        Remove
    }

    private enum DialogMode
    {
        None,
        Editor,
        Confirm
    }

    internal enum AutomationView
    {
        Definitions,
        Schedule,
        Runs
    }

    private readonly CancellationTokenSource _lifetime = new();
    private LocalAutomationOperatorState? _automation;
    private CameraAgentScheduleCalendar? _calendar;
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;
    private string _timeZoneId = "UTC";
    private string? _calendarMessage;
    private string? _error;
    private bool _loading = true;
    private AutomationView _view;
    private string? _appliedView;

    private string _idInput = string.Empty;
    private string _nameInput = string.Empty;
    private string _targetInput = string.Empty;
    private string _intervalInput = DefaultPeriodicInterval;
    private string _reasonInput = string.Empty;
    private string _commandReasonInput = string.Empty;
    private LocalAutomationTaskKind _taskKindInput = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition;
    private LocalAutomationTriggerKind _triggerKindInput = LocalAutomationTriggerKind.Periodic;
    private bool _enabledInput = true;
    private long _editingVersion;
    private bool _formDirty;

    private DialogMode _dialog;
    private bool _busy;
    private PendingCommandKind _pendingKind;
    private LocalAutomationDefinitionState? _pendingDefinition;
    private string? _commandMessage;
    private bool _commandMessageIsError;
    private string? _commandKey;
    private string? _commandPayload;
    private long _commandExpectedVersion;
    private string _restoreFocusId = CreateTriggerId;

    private IJSObjectReference? _module;
    private ElementReference _dialogElement;
    private bool _showDialog;
    private string? _focusTargetId;

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal ICameraAgentAutomationUiService AutomationService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "view")]
    public string? View { get; set; }

    private bool Editing => _editingVersion != 0;

    /// <summary>Why a new definition cannot be created right now, or null when it can.</summary>
    private string? CreateUnavailableReason
    {
        get
        {
            if (_automation is null)
            {
                return "The local automation store has not been read.";
            }
            if (_automation.Definitions.Count >= LocalAutomationContract.MaximumDefinitions)
            {
                return string.Create(
                    Invariant,
                    $"This CameraAgent already holds the maximum of {LocalAutomationContract.MaximumDefinitions} automation definitions. Remove one before creating another.");
            }
            return AvailableTask is null ? RegistryUnavailableReason : null;
        }
    }

    /// <summary>The first registered task that can actually be scheduled here.</summary>
    private LocalAutomationTaskDescriptor? AvailableTask => _automation?.Registry
        .FirstOrDefault(static descriptor => descriptor.Available && descriptor.Targets.Count > 0);

    private string RegistryUnavailableReason => _automation?.Registry
        .Select(static descriptor => descriptor.UnavailableReason)
        .FirstOrDefault(static reason => !string.IsNullOrWhiteSpace(reason))
        ?? "No registered automation task is available on this CameraAgent.";

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override void OnParametersSet()
    {
        if (string.Equals(_appliedView, View, StringComparison.Ordinal))
        {
            return;
        }
        _appliedView = View;
        _view = ParseView(View);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            await InvokeModuleAsync("showModal", _dialogElement);
        }
        if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            await InvokeModuleAsync("focusById", target, FocusFallbackId);
        }
    }

    internal static AutomationView ParseView(string? value) => value switch
    {
        _ when string.Equals(value, "schedule", StringComparison.OrdinalIgnoreCase) => AutomationView.Schedule,
        _ when string.Equals(value, "runs", StringComparison.OrdinalIgnoreCase) => AutomationView.Runs,
        _ => AutomationView.Definitions
    };

    private void SelectView(AutomationView view)
    {
        _view = view;
        var value = view switch
        {
            AutomationView.Schedule => "schedule",
            AutomationView.Runs => "runs",
            _ => null
        };
        _appliedView = value;
        NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("view", value), replace: true);
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var automation = await AutomationService.GetAsync(_lifetime.Token);
            var calendar = await ScheduleService.GetCalendarAsync(CalendarNights, _lifetime.Token);
            if (automation.Kind == OperatorUiResultKind.Unauthorized ||
                calendar.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            if (automation.IsSuccess && automation.Value is { } state)
            {
                _automation = state;
                _error = null;
                // A conflict re-read is only useful if the next attempt carries the version it just read.
                // A failed read tells us nothing about the version, so it leaves the edit alone rather than
                // silently demoting it to a create that can never succeed.
                if (Editing)
                {
                    _editingVersion = state.Definitions
                        .FirstOrDefault(candidate => string.Equals(
                            candidate.Definition.DefinitionId, _idInput.Trim(), StringComparison.Ordinal))
                        ?.Version ?? 0;
                }
            }
            else
            {
                // The last valid snapshot stays on screen; the banner says it is no longer current.
                _error = automation.Message ?? "The local automations could not be read.";
            }
            ApplyCalendar(calendar);
            SeedForm();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// The schedule calendar supplies the site timezone and the capture windows. Without it every time is shown in
    /// UTC and labelled so, and the day track has no capture-window band.
    /// </summary>
    private void ApplyCalendar(OperatorUiResult<CameraAgentScheduleCalendar> result)
    {
        var message = result.Message;
        if (result.IsSuccess && result.Value is { } calendar)
        {
            try
            {
                _timeZone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
                _timeZoneId = calendar.TimeZoneId;
                _calendar = calendar;
                _calendarMessage = null;
                return;
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                message = $"The site timezone '{calendar.TimeZoneId}' is not available on this host.";
            }
        }
        _calendar = null;
        _timeZone = TimeZoneInfo.Utc;
        _timeZoneId = "UTC";
        _calendarMessage = message ?? "The capture schedule calendar could not be read.";
    }

    /// <summary>Seeds the editor from the registry while the operator has not edited it.</summary>
    private void SeedForm()
    {
        if (_formDirty || _dialog != DialogMode.None || _automation is null)
        {
            return;
        }
        var descriptor = AvailableTask ?? (_automation.Registry.Count == 0 ? null : _automation.Registry[0]);
        if (descriptor is null)
        {
            return;
        }
        _taskKindInput = descriptor.TaskKind;
        _triggerKindInput = descriptor.CompatibleTriggers.Count == 0
            ? LocalAutomationTriggerKind.Periodic
            : descriptor.CompatibleTriggers[0];
        _targetInput = descriptor.Targets.Count == 0 ? string.Empty : descriptor.Targets[0];
    }

    private void FormChanged()
    {
        _formDirty = true;
        _commandMessage = null;
    }

    private void TaskChanged()
    {
        FormChanged();
        // A target or trigger of another task kind is never valid for this one, so both follow the task.
        var targets = Targets();
        if (!targets.Contains(_targetInput, StringComparer.Ordinal))
        {
            _targetInput = targets.Count == 0 ? string.Empty : targets[0];
        }
        var triggers = CompatibleTriggers();
        if (!triggers.Contains(_triggerKindInput) && triggers.Count > 0)
        {
            _triggerKindInput = triggers[0];
        }
    }

    private void TriggerChanged()
    {
        FormChanged();
        // Seconds and captures are different units, so an untouched default follows the trigger it belongs to.
        _intervalInput = (_triggerKindInput, _intervalInput) switch
        {
            (LocalAutomationTriggerKind.CaptureRelative, DefaultPeriodicInterval) => DefaultCaptureInterval,
            (LocalAutomationTriggerKind.Periodic, DefaultCaptureInterval) => DefaultPeriodicInterval,
            _ => _intervalInput
        };
    }

    private void ResetForm()
    {
        _editingVersion = 0;
        _formDirty = false;
        _idInput = string.Empty;
        _nameInput = string.Empty;
        _intervalInput = DefaultPeriodicInterval;
        _reasonInput = string.Empty;
        _enabledInput = true;
        SeedForm();
    }

    private void OpenCreate()
    {
        if (CreateUnavailableReason is not null || _busy)
        {
            return;
        }
        _commandMessage = null;
        ResetForm();
        _restoreFocusId = CreateTriggerId;
        OpenDialog(DialogMode.Editor, "automation-id");
    }

    private void BeginEdit(LocalAutomationDefinitionState definition)
    {
        if (_busy)
        {
            return;
        }
        _commandMessage = null;
        _editingVersion = definition.Version;
        _idInput = definition.Definition.DefinitionId;
        _nameInput = definition.Definition.Name;
        _taskKindInput = definition.Definition.TaskKind;
        _targetInput = definition.Definition.TaskTarget;
        _triggerKindInput = definition.Definition.TriggerKind;
        _intervalInput = definition.Definition.TriggerInterval.ToString(Invariant);
        _enabledInput = definition.Definition.Enabled;
        _reasonInput = string.Empty;
        _formDirty = true;
        _restoreFocusId = EditTriggerId(definition);
        OpenDialog(DialogMode.Editor, "automation-name");
    }

    private void BeginSave()
    {
        _commandMessage = null;
        if (!TryValidateForm())
        {
            return;
        }
        _pendingKind = PendingCommandKind.Save;
        _pendingDefinition = null;
        _dialog = DialogMode.Confirm;
        _focusTargetId = CancelId;
    }

    private void BeginToggle(LocalAutomationDefinitionState definition)
        => BeginRowCommand(PendingCommandKind.Toggle, definition, ToggleTriggerId(definition));

    private void BeginRemove(LocalAutomationDefinitionState definition)
        => BeginRowCommand(PendingCommandKind.Remove, definition, RemoveTriggerId(definition));

    private void BeginRowCommand(PendingCommandKind kind, LocalAutomationDefinitionState definition, string triggerId)
    {
        if (_busy)
        {
            return;
        }
        _commandMessage = null;
        _commandReasonInput = string.Empty;
        _pendingKind = kind;
        _pendingDefinition = definition;
        _restoreFocusId = triggerId;
        OpenDialog(DialogMode.Confirm, CancelId);
    }

    private void OpenDialog(DialogMode mode, string focusId)
    {
        var opening = _dialog == DialogMode.None;
        _dialog = mode;
        _showDialog = opening;
        _focusTargetId = focusId;
    }

    /// <summary>Cancel steps back one level: a save confirmation returns to its editor, anything else closes.</summary>
    private void CancelCommand()
    {
        if (_busy)
        {
            return;
        }
        if (_dialog == DialogMode.Confirm && _pendingKind == PendingCommandKind.Save)
        {
            _dialog = DialogMode.Editor;
            _focusTargetId = SaveTriggerId;
            return;
        }
        CloseDialog();
    }

    /// <summary>Escape behaves like the dialog's own cancel button and is ignored while a command is in flight.</summary>
    private void DismissDialog()
    {
        if (_dialog == DialogMode.Confirm)
        {
            CancelCommand();
            return;
        }
        CloseDialog();
    }

    private void CloseDialog()
    {
        if (_busy)
        {
            return;
        }
        var wasEditing = _dialog == DialogMode.Editor || _pendingKind == PendingCommandKind.Save;
        _dialog = DialogMode.None;
        _pendingDefinition = null;
        if (wasEditing)
        {
            // Closing the editor discards it; a definition is only ever changed through a confirmed save.
            ResetForm();
        }
        _focusTargetId = _restoreFocusId;
    }

    private async Task ConfirmCommandAsync()
    {
        if (_busy)
        {
            return;
        }
        var signature = PendingSignature();
        if (signature is null)
        {
            return;
        }
        // A retry after an unavailable command must reuse the same key and expected version so the
        // store recognizes the replay instead of recording a second revision.
        if (!string.Equals(_commandPayload, signature.Value.Signature, StringComparison.Ordinal))
        {
            _commandPayload = signature.Value.Signature;
            _commandKey = Guid.NewGuid().ToString("N", Invariant);
            _commandExpectedVersion = signature.Value.ExpectedVersion;
        }
        var reason = TrimmedReason();
        var kind = _pendingKind;
        _busy = true;
        _commandMessage = null;
        OperatorUiResult<LocalAutomationCommandResult> result;
        try
        {
            result = kind == PendingCommandKind.Remove
                ? await AutomationService.RemoveAsync(
                    new LocalAutomationRemoveRequest(
                        signature.Value.DefinitionId,
                        _commandExpectedVersion,
                        _commandKey!,
                        string.Empty,
                        reason),
                    CancellationToken.None)
                : await AutomationService.SaveAsync(
                    new LocalAutomationSaveRequest(
                        signature.Value.DefinitionId,
                        signature.Value.Name,
                        signature.Value.Enabled,
                        signature.Value.TaskKind,
                        signature.Value.TaskTarget,
                        signature.Value.TriggerKind,
                        signature.Value.TriggerInterval,
                        _commandExpectedVersion,
                        _commandKey!,
                        string.Empty,
                        reason),
                    CancellationToken.None);
        }
        finally
        {
            _busy = false;
        }
        // A command is never abandoned half-way by navigation, but a page that has gone has nothing to update.
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unavailable)
        {
            // The command may or may not have committed, so the dialog stays open on the same key for a retry.
            SetCommandMessage(result.Message ?? "The automation store is unavailable. Try again.", error: true);
            return;
        }
        _commandKey = null;
        _commandPayload = null;
        if (result.IsSuccess && result.Value is { } applied)
        {
            if (kind == PendingCommandKind.Save)
            {
                _formDirty = false;
                _reasonInput = string.Empty;
                _editingVersion = 0;
            }
            _dialog = DialogMode.None;
            _pendingDefinition = null;
            await LoadAsync();
            ResetForm();
            SetCommandMessage(Describe(applied), error: false);
            _focusTargetId = _restoreFocusId;
            return;
        }
        if (result.Kind is OperatorUiResultKind.Conflict or OperatorUiResultKind.NotFound)
        {
            // Re-read durable state so the next attempt carries the current expected version.
            await LoadAsync();
        }
        SetCommandMessage(result.Message ?? "The automation command failed.", error: true);
        if (kind == PendingCommandKind.Save)
        {
            // A rejected save goes back to the editor with the reason, so the operator can correct it in place.
            _dialog = DialogMode.Editor;
            _focusTargetId = SaveTriggerId;
            return;
        }
        _dialog = DialogMode.None;
        _pendingDefinition = null;
        _focusTargetId = _restoreFocusId;
    }

    private (string DefinitionId, string Name, bool Enabled, LocalAutomationTaskKind TaskKind, string TaskTarget,
        LocalAutomationTriggerKind TriggerKind, int TriggerInterval, long ExpectedVersion, string Signature)?
        PendingSignature()
    {
        if (_pendingKind == PendingCommandKind.Save)
        {
            if (!TryParseInterval(out var interval))
            {
                return null;
            }
            var id = _idInput.Trim();
            var name = _nameInput.Trim();
            var target = _targetInput.Trim();
            return (id, name, _enabledInput, _taskKindInput, target, _triggerKindInput, interval, _editingVersion,
                string.Join('|', "save", id, name, _enabledInput, _taskKindInput, target, _triggerKindInput,
                    interval.ToString(Invariant),
                    _editingVersion.ToString(Invariant),
                    TrimmedReason() ?? string.Empty));
        }
        if (_pendingDefinition is not { } pending)
        {
            return null;
        }
        var definition = pending.Definition;
        if (_pendingKind == PendingCommandKind.Remove)
        {
            return (definition.DefinitionId, definition.Name, definition.Enabled, definition.TaskKind,
                definition.TaskTarget, definition.TriggerKind, definition.TriggerInterval, pending.Version,
                string.Join('|', "remove", definition.DefinitionId,
                    pending.Version.ToString(Invariant),
                    TrimmedReason() ?? string.Empty));
        }
        return (definition.DefinitionId, definition.Name, !definition.Enabled, definition.TaskKind,
            definition.TaskTarget, definition.TriggerKind, definition.TriggerInterval, pending.Version,
            string.Join('|', "toggle", definition.DefinitionId, !definition.Enabled,
                pending.Version.ToString(Invariant),
                TrimmedReason() ?? string.Empty));
    }

    /// <summary>
    /// Rejects an unusable definition before a confirmation is raised, so the operator never confirms a
    /// command the store will reject. The bounds match the ones the contract enforces.
    /// </summary>
    private bool TryValidateForm()
    {
        if (!LocalAutomationDefinitionValidator.IsIdentifier(
                _idInput?.Trim(), LocalAutomationContract.MaximumDefinitionIdLength))
        {
            SetCommandMessage(
                "The identifier must start with a lower-case letter and use only lower-case letters, digits, "
                + "hyphens, and dots.",
                error: true);
            return false;
        }
        if (!LocalAutomationDefinitionValidator.IsText(
                _nameInput?.Trim(), LocalAutomationContract.MaximumNameLength))
        {
            SetCommandMessage(
                string.Create(
                    Invariant,
                    $"The name must be a single line of at most {LocalAutomationContract.MaximumNameLength} characters."),
                error: true);
            return false;
        }
        if (!LocalAutomationDefinitionValidator.IsText(
                _targetInput?.Trim(), LocalAutomationContract.MaximumTargetLength))
        {
            SetCommandMessage("Select a registered task target.", error: true);
            return false;
        }
        return TryParseInterval(out _);
    }

    /// <summary>The reason exactly as the store will hash it, so the retained key stays payload-accurate.</summary>
    private string? TrimmedReason()
    {
        var reason = _pendingKind == PendingCommandKind.Save ? _reasonInput : _commandReasonInput;
        return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private bool TryParseInterval(out int interval)
    {
        if (!int.TryParse(_intervalInput, NumberStyles.Integer, Invariant, out interval))
        {
            SetCommandMessage("The interval must be a whole number.", error: true);
            return false;
        }
        var (minimum, maximum) = IntervalBounds(_triggerKindInput);
        if (interval < minimum || interval > maximum)
        {
            SetCommandMessage(
                string.Create(Invariant, $"The interval must be between {minimum} and {maximum}."),
                error: true);
            return false;
        }
        return true;
    }

    private static (int Minimum, int Maximum) IntervalBounds(LocalAutomationTriggerKind trigger)
        => trigger == LocalAutomationTriggerKind.Periodic
            ? (LocalAutomationContract.MinimumPeriodicIntervalSeconds,
               LocalAutomationContract.MaximumPeriodicIntervalSeconds)
            : (LocalAutomationContract.MinimumCaptureInterval, LocalAutomationContract.MaximumCaptureInterval);

    private string IntervalHint()
    {
        var (minimum, maximum) = IntervalBounds(_triggerKindInput);
        var bounds = _triggerKindInput == LocalAutomationTriggerKind.Periodic
            ? string.Create(Invariant, $"{minimum} to {maximum} seconds")
            : string.Create(Invariant, $"{minimum} to {maximum} durable captures");
        return int.TryParse(_intervalInput, NumberStyles.Integer, Invariant, out var interval) &&
               interval >= minimum && interval <= maximum
            ? $"{DescribeTrigger(_triggerKindInput, interval)}. Accepted range: {bounds}."
            : $"Accepted range: {bounds}.";
    }

    private void SetCommandMessage(string message, bool error)
    {
        _commandMessage = message;
        _commandMessageIsError = error;
    }

    private static string Describe(LocalAutomationCommandResult result) => result.Status switch
    {
        LocalAutomationCommandStatus.Applied => "Recorded a new immutable automation revision.",
        LocalAutomationCommandStatus.Replayed =>
            "This command was already recorded. No additional revision was created.",
        _ => "The stored definition already matches this request, so no revision was created."
    };

    private string ConfirmationEyebrow() => _pendingKind switch
    {
        PendingCommandKind.Remove => "Remove automation",
        PendingCommandKind.Toggle => _pendingDefinition?.Definition.Enabled == true
            ? "Disable automation"
            : "Enable automation",
        _ => Editing ? "Record a new revision" : "Create automation"
    };

    private string ConfirmationHeading() => _pendingKind switch
    {
        PendingCommandKind.Remove => $"Remove {_pendingDefinition?.Definition.DefinitionId}?",
        PendingCommandKind.Toggle => _pendingDefinition?.Definition.Enabled == true
            ? $"Disable {_pendingDefinition?.Definition.DefinitionId}?"
            : $"Enable {_pendingDefinition?.Definition.DefinitionId}?",
        _ => Editing
            ? string.Create(Invariant, $"Record revision {_editingVersion + 1} of {_idInput.Trim()}?")
            : $"Record automation {_idInput.Trim()}?"
    };

    private string ConfirmationDescription() => _pendingKind switch
    {
        PendingCommandKind.Remove =>
            "The definition stops running immediately. Its recorded runs stay in the run history, and its "
            + "revisions are retained durably under a bounded limit but are no longer listed on this page.",
        PendingCommandKind.Toggle => _pendingDefinition?.Definition.Enabled == true
            ? "The definition stops running. Its recorded revisions and run history are retained."
            : "The definition starts running at its next occurrence. Occurrences that elapsed while it was "
              + "disabled are not replayed.",
        _ => "A new immutable revision is recorded. Only a registered task kind and a registered trigger kind "
             + "are stored; the runner issues the same operation an operator can issue by hand."
    };

    private string ConfirmLabel() => _pendingKind switch
    {
        PendingCommandKind.Remove => "Remove automation",
        PendingCommandKind.Toggle => _pendingDefinition?.Definition.Enabled == true
            ? "Disable automation"
            : "Enable automation",
        _ => "Record revision"
    };

    private LocalAutomationTaskDescriptor? Descriptor(LocalAutomationTaskKind kind)
        => _automation?.Registry.FirstOrDefault(descriptor => descriptor.TaskKind == kind);

    private IReadOnlyList<string> Targets() => Descriptor(_taskKindInput)?.Targets ?? [];

    private IReadOnlyList<LocalAutomationTriggerKind> CompatibleTriggers()
        => Descriptor(_taskKindInput)?.CompatibleTriggers ?? [];

    private string TaskDescription(LocalAutomationDefinition definition)
        => Descriptor(definition.TaskKind)?.Description ?? Split(definition.TaskKind.ToString());

    internal static string DescribeTrigger(LocalAutomationTriggerKind trigger, int interval)
    {
        if (trigger == LocalAutomationTriggerKind.CaptureRelative)
        {
            return interval == 1
                ? "Every capture"
                : string.Create(Invariant, $"Every {interval} captures");
        }
        return interval switch
        {
            _ when interval % 3600 == 0 => string.Create(Invariant, $"Every {interval / 3600} h"),
            _ when interval % 60 == 0 => string.Create(Invariant, $"Every {interval / 60} min"),
            _ => string.Create(Invariant, $"Every {interval} s")
        };
    }

    private string DescribeNextRun(LocalAutomationDefinitionState definition)
    {
        if (!definition.Definition.Enabled)
        {
            return "Disabled";
        }
        if (definition.NextRunUtc is { } due)
        {
            return DayClock(due);
        }
        return definition.NextRunCaptureSequence is { } sequence
            ? string.Create(Invariant, $"At capture sequence {sequence}")
            : "After the next capture establishes a baseline";
    }

    private string DescribeLastRun(LocalAutomationRun? run) => run is null
        ? "None yet"
        : $"{Split(run.Outcome.ToString())} / {DayClock(run.StartedAtUtc)}";

    private string DefinitionName(string definitionId)
        => _automation?.Definitions.FirstOrDefault(candidate => string.Equals(
               candidate.Definition.DefinitionId, definitionId, StringComparison.Ordinal))?.Definition.Name
           ?? definitionId;

    private int EnabledCount => _automation?.Definitions.Count(static item => item.Definition.Enabled) ?? 0;

    private int RunningCount => _automation?.Runs.Count(static run => run.Outcome == LocalAutomationRunOutcome.Running) ?? 0;

    private LocalAutomationCalendarEntry? NextEntry => _automation?.Calendar.MinBy(static entry => entry.DueUtc);

    private LocalAutomationDefinitionState? NextCaptureRelative => _automation?.Definitions
        .Where(static item => item.Definition.Enabled && item.NextRunCaptureSequence is not null)
        .MinBy(static item => item.NextRunCaptureSequence);

    /// <summary>Finished runs in the 24 hours before the read, and how many of them succeeded.</summary>
    private (int Succeeded, int Finished, bool Truncated) LastDay
    {
        get
        {
            if (_automation is null)
            {
                return (0, 0, false);
            }
            var since = _automation.ReadAtUtc.AddHours(-24);
            var finished = _automation.Runs
                .Where(run => run.Outcome != LocalAutomationRunOutcome.Running && run.StartedAtUtc >= since)
                .ToList();
            // The projection holds only the newest runs. When all of them fall inside the day, older ones may not.
            var truncated = _automation.Runs.Count >= LocalAutomationContract.MaximumProjectedRuns &&
                            _automation.Runs.Min(static run => run.StartedAtUtc) >= since;
            return (finished.Count(static run => run.Outcome == LocalAutomationRunOutcome.Succeeded),
                finished.Count, truncated);
        }
    }

    internal static string OutcomeClass(LocalAutomationRunOutcome outcome) => outcome switch
    {
        LocalAutomationRunOutcome.Running => "running",
        LocalAutomationRunOutcome.Succeeded => "success",
        LocalAutomationRunOutcome.Skipped => "skipped",
        LocalAutomationRunOutcome.Failed => "failure",
        _ => "warning"
    };

    /// <summary>The 24-hour window the day track draws, starting at the current local hour.</summary>
    private (DateTimeOffset Start, DateTimeOffset End) TrackWindow()
    {
        var anchor = _automation?.ReadAtUtc ?? _calendar?.GeneratedUtc ?? DateTimeOffset.UnixEpoch;
        var local = TimeZoneInfo.ConvertTime(anchor, _timeZone);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset)
            .ToUniversalTime();
        return (start, start.AddHours(24));
    }

    private static double Percent(DateTimeOffset value, DateTimeOffset start, DateTimeOffset end)
        => Math.Clamp((value - start).TotalMinutes / (end - start).TotalMinutes * 100, 0, 100);

    private static string Css(double value) => value.ToString("0.##", Invariant);

    /// <summary>Contiguous admitted spans inside the window; adjacent segments with different setpoints merge.</summary>
    private List<TrackBand> CaptureWindows(DateTimeOffset start, DateTimeOffset end)
    {
        var bands = new List<TrackBand>();
        if (_calendar is null)
        {
            return bands;
        }
        DateTimeOffset? openStart = null;
        DateTimeOffset openEnd = default;
        foreach (var segment in _calendar.Nights.SelectMany(static night => night.Segments).OrderBy(static s => s.StartUtc))
        {
            if (!segment.Admitted)
            {
                continue;
            }
            if (openStart is not null && segment.StartUtc <= openEnd)
            {
                openEnd = segment.EndUtc > openEnd ? segment.EndUtc : openEnd;
                continue;
            }
            Add();
            openStart = segment.StartUtc;
            openEnd = segment.EndUtc;
        }
        Add();
        return bands;

        void Add()
        {
            if (openStart is not { } bandStart || openEnd <= start || bandStart >= end)
            {
                return;
            }
            var left = Percent(bandStart, start, end);
            var right = Percent(openEnd, start, end);
            // A window of a day or more would otherwise read as "12:00 – 12:00".
            var label = bandStart <= start && openEnd >= end
                ? "Capture window / open all 24 hours"
                : openEnd - bandStart >= TimeSpan.FromDays(1)
                    ? $"Capture window / {DayClock(bandStart)} – {DayClock(openEnd)}"
                    : $"Capture window / {Clock(bandStart)} – {Clock(openEnd)}";
            bands.Add(new TrackBand(left, right - left, label));
        }
    }

    /// <summary>
    /// Due runs inside the window. Each gets a tick at its exact time; a labelled card goes on the first row with room,
    /// clamped so it never overflows the track. A run with no room keeps only its tick and still appears in the list.
    /// </summary>
    private List<TrackMarker> RunMarkers(DateTimeOffset start, DateTimeOffset end)
    {
        var markers = new List<TrackMarker>();
        if (_automation is null)
        {
            return markers;
        }
        var lastCard = Enumerable.Repeat(double.NegativeInfinity, MarkerRows).ToArray();
        foreach (var entry in _automation.Calendar.Where(item => item.DueUtc >= start && item.DueUtc < end).OrderBy(static item => item.DueUtc))
        {
            var exact = Percent(entry.DueUtc, start, end);
            var card = Math.Clamp(exact, MarkerMinimumPercent, MarkerMaximumPercent);
            var row = Array.FindIndex(lastCard, previous => card - previous >= MarkerSpacingPercent);
            if (row >= 0)
            {
                lastCard[row] = card;
            }
            markers.Add(new TrackMarker(exact, row >= 0 ? card : null, row, Clock(entry.DueUtc), entry.Name));
        }
        return markers;
    }

    private string Clock(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _timeZone).ToString("HH:mm", Invariant);

    private string DayClock(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, _timeZone).ToString("ddd d MMM HH:mm", Invariant);

    internal static string EditTriggerId(LocalAutomationDefinitionState definition)
        => $"automation-edit-{definition.Definition.DefinitionId}";

    internal static string ToggleTriggerId(LocalAutomationDefinitionState definition)
        => $"automation-toggle-{definition.Definition.DefinitionId}";

    internal static string RemoveTriggerId(LocalAutomationDefinitionState definition)
        => $"automation-remove-{definition.Definition.DefinitionId}";

    private static string Split(string value) => OperationsPage.SplitWords(value);

    private static string Plural(int count, string singular, string plural)
        => string.Create(Invariant, $"{count} {(count == 1 ? singular : plural)}");

    private async ValueTask InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        try
        {
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/AutomationsPage.razor.js");
            await _module.InvokeVoidAsync(identifier, arguments);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private sealed record TrackBand(double Left, double Width, string Label);

    private sealed record TrackMarker(double Tick, double? Card, int Row, string Time, string Name);
}
