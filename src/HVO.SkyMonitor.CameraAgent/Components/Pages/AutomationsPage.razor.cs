using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class AutomationsPage : ComponentBase, IAsyncDisposable
{
    private const int MaximumIntervals = 12;

    /// <summary>The identifier the save confirmation returns focus to.</summary>
    internal const string SaveTriggerId = "automation-save";

    private enum PendingCommandKind
    {
        Save,
        Toggle,
        Remove
    }

    private CaptureScheduleOperatorState? _schedule;
    private EnvironmentalUiStatus? _environment;
    private LocalAutomationOperatorState? _automation;
    private string? _message;
    private bool _loading = true;

    private string _idInput = string.Empty;
    private string _nameInput = string.Empty;
    private string _targetInput = string.Empty;
    private string _intervalInput = "3600";
    private string _reasonInput = string.Empty;
    private LocalAutomationTaskKind _taskKindInput = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition;
    private LocalAutomationTriggerKind _triggerKindInput = LocalAutomationTriggerKind.Periodic;
    private bool _enabledInput = true;
    private long _editingVersion;
    private bool _formDirty;

    private bool _confirming;
    private bool _busy;
    private PendingCommandKind _pendingKind;
    private LocalAutomationDefinitionState? _pendingDefinition;
    private string? _commandMessage;
    private bool _commandMessageIsError;
    private string? _commandKey;
    private string? _commandPayload;
    private long _commandExpectedVersion;
    private string _restoreFocusId = SaveTriggerId;

    private IJSObjectReference? _module;
    private ElementReference _confirmationDialog;
    private bool _focusConfirmation;
    private bool _restoreTriggerFocus;

    [Inject] internal ICameraAgentScheduleUiService ScheduleService { get; set; } = default!;

    [Inject] internal ICameraAgentEnvironmentalUiService EnvironmentalService { get; set; } = default!;

    [Inject] internal ICameraAgentAutomationUiService AutomationService { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    protected override async Task OnInitializedAsync() => await LoadAsync().ConfigureAwait(false);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusConfirmation)
        {
            _focusConfirmation = false;
            await InvokeModuleAsync("showModal", _confirmationDialog).ConfigureAwait(false);
        }
        else if (_restoreTriggerFocus)
        {
            _restoreTriggerFocus = false;
            await InvokeModuleAsync("focusById", _restoreFocusId, "automation-definitions").ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var schedule = await ScheduleService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var environment = await EnvironmentalService.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            var automation = await AutomationService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (schedule.Kind == OperatorUiResultKind.Unauthorized ||
                environment.Kind == OperatorUiResultKind.Unauthorized ||
                automation.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
                return;
            }
            _schedule = schedule.IsSuccess ? schedule.Value : null;
            _environment = environment.IsSuccess ? environment.Value : null;
            _automation = automation.IsSuccess ? automation.Value : null;
            var failures = new List<string>(3);
            if (!schedule.IsSuccess)
            {
                failures.Add(schedule.Message ?? "The capture schedule could not be read.");
            }
            if (!environment.IsSuccess)
            {
                failures.Add(environment.Message ?? "The environmental sources could not be read.");
            }
            if (!automation.IsSuccess)
            {
                failures.Add(automation.Message ?? "The local automations could not be read.");
            }
            _message = failures.Count == 0 ? null : string.Join(' ', failures);
            SeedForm();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Seeds the editor from the registry while the operator has not edited it.</summary>
    private void SeedForm()
    {
        if (_formDirty || _confirming || _automation is null)
        {
            return;
        }
        if (_automation.Registry.Count == 0)
        {
            return;
        }
        var descriptor = _automation.Registry[0];
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

    private void ResetForm()
    {
        _editingVersion = 0;
        _formDirty = false;
        _idInput = string.Empty;
        _nameInput = string.Empty;
        _intervalInput = "3600";
        _reasonInput = string.Empty;
        _enabledInput = true;
        _commandMessage = null;
        SeedForm();
    }

    private void BeginEdit(LocalAutomationDefinitionState definition)
    {
        _commandMessage = null;
        _editingVersion = definition.Version;
        _idInput = definition.Definition.DefinitionId;
        _nameInput = definition.Definition.Name;
        _taskKindInput = definition.Definition.TaskKind;
        _targetInput = definition.Definition.TaskTarget;
        _triggerKindInput = definition.Definition.TriggerKind;
        _intervalInput = definition.Definition.TriggerInterval.ToString(CultureInfo.InvariantCulture);
        _enabledInput = definition.Definition.Enabled;
        _reasonInput = string.Empty;
        _formDirty = true;
        _restoreFocusId = "automation-editor";
        _restoreTriggerFocus = true;
    }

    private void BeginSave()
    {
        _commandMessage = null;
        if (!TryParseInterval(out _))
        {
            return;
        }
        _pendingKind = PendingCommandKind.Save;
        _pendingDefinition = null;
        _restoreFocusId = SaveTriggerId;
        _confirming = true;
        _focusConfirmation = true;
    }

    private void BeginToggle(LocalAutomationDefinitionState definition)
    {
        _commandMessage = null;
        _pendingKind = PendingCommandKind.Toggle;
        _pendingDefinition = definition;
        _restoreFocusId = ToggleTriggerId(definition);
        _confirming = true;
        _focusConfirmation = true;
    }

    private void BeginRemove(LocalAutomationDefinitionState definition)
    {
        _commandMessage = null;
        _pendingKind = PendingCommandKind.Remove;
        _pendingDefinition = definition;
        _restoreFocusId = RemoveTriggerId(definition);
        _confirming = true;
        _focusConfirmation = true;
    }

    private void CancelCommand()
    {
        if (_busy)
        {
            return;
        }
        _confirming = false;
        _restoreTriggerFocus = true;
    }

    private async Task ConfirmCommandAsync()
    {
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
            _commandKey = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            _commandExpectedVersion = signature.Value.ExpectedVersion;
        }
        var reason = string.IsNullOrWhiteSpace(_reasonInput) ? null : _reasonInput.Trim();
        _busy = true;
        OperatorUiResult<LocalAutomationCommandResult> result;
        try
        {
            result = _pendingKind == PendingCommandKind.Remove
                ? await AutomationService.RemoveAsync(
                    new LocalAutomationRemoveRequest(
                        signature.Value.DefinitionId,
                        _commandExpectedVersion,
                        _commandKey!,
                        string.Empty,
                        reason),
                    CancellationToken.None).ConfigureAwait(false)
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
                    CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _commandKey = null;
            _commandPayload = null;
            _confirming = false;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (result.IsSuccess && result.Value is { } applied)
        {
            _formDirty = false;
            _reasonInput = string.Empty;
            if (_pendingKind != PendingCommandKind.Toggle)
            {
                _editingVersion = 0;
            }
            await LoadAsync().ConfigureAwait(false);
            if (_pendingKind == PendingCommandKind.Save)
            {
                ResetForm();
            }
            SetCommandMessage(Describe(applied), error: false);
        }
        else
        {
            if (result.Kind is OperatorUiResultKind.Conflict or OperatorUiResultKind.NotFound)
            {
                // Re-read durable state so the next attempt carries the current expected version.
                await LoadAsync().ConfigureAwait(false);
            }
            SetCommandMessage(result.Message ?? "The automation command failed.", error: true);
        }
        if (result.Kind != OperatorUiResultKind.Unavailable)
        {
            _restoreTriggerFocus = true;
        }
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
                    interval.ToString(CultureInfo.InvariantCulture),
                    _editingVersion.ToString(CultureInfo.InvariantCulture)));
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
                    pending.Version.ToString(CultureInfo.InvariantCulture)));
        }
        return (definition.DefinitionId, definition.Name, !definition.Enabled, definition.TaskKind,
            definition.TaskTarget, definition.TriggerKind, definition.TriggerInterval, pending.Version,
            string.Join('|', "toggle", definition.DefinitionId, !definition.Enabled,
                pending.Version.ToString(CultureInfo.InvariantCulture)));
    }

    private bool TryParseInterval(out int interval)
    {
        if (!int.TryParse(_intervalInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval))
        {
            SetCommandMessage("The interval must be a whole number.", error: true);
            return false;
        }
        var (minimum, maximum) = _triggerKindInput == LocalAutomationTriggerKind.Periodic
            ? (LocalAutomationContract.MinimumPeriodicIntervalSeconds,
               LocalAutomationContract.MaximumPeriodicIntervalSeconds)
            : (LocalAutomationContract.MinimumCaptureInterval, LocalAutomationContract.MaximumCaptureInterval);
        if (interval < minimum || interval > maximum)
        {
            SetCommandMessage(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The interval must be between {minimum} and {maximum}."),
                error: true);
            return false;
        }
        return true;
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

    private string ConfirmationHeading() => _pendingKind switch
    {
        PendingCommandKind.Remove => $"Remove {_pendingDefinition?.Definition.DefinitionId}?",
        PendingCommandKind.Toggle => _pendingDefinition?.Definition.Enabled == true
            ? $"Disable {_pendingDefinition?.Definition.DefinitionId}?"
            : $"Enable {_pendingDefinition?.Definition.DefinitionId}?",
        _ => _editingVersion == 0
            ? $"Record automation {_idInput}?"
            : $"Record revision {_editingVersion + 1} of {_idInput}?"
    };

    private string ConfirmationDescription() => _pendingKind switch
    {
        PendingCommandKind.Remove =>
            "The definition stops running immediately. Its recorded revisions and run history are retained.",
        PendingCommandKind.Toggle => _pendingDefinition?.Definition.Enabled == true
            ? "The definition stops running. Its recorded revisions and run history are retained."
            : "The definition starts running at its next occurrence. Occurrences that elapsed while it was "
              + "disabled are not replayed.",
        _ => "A new immutable revision is recorded. Only a registered task kind and a registered trigger kind "
             + "are stored; the runner issues the same operation an operator can issue by hand."
    };

    private IReadOnlyList<string> Targets()
        => _automation?.Registry.FirstOrDefault(descriptor => descriptor.TaskKind == _taskKindInput)?.Targets ?? [];

    private IReadOnlyList<LocalAutomationTriggerKind> CompatibleTriggers()
        => _automation?.Registry
            .FirstOrDefault(descriptor => descriptor.TaskKind == _taskKindInput)?.CompatibleTriggers ?? [];

    private static string DescribeNextRun(LocalAutomationDefinitionState definition)
    {
        if (!definition.Definition.Enabled)
        {
            return "Disabled";
        }
        if (definition.NextRunUtc is { } due)
        {
            return due.ToString("u", CultureInfo.InvariantCulture);
        }
        return definition.NextRunCaptureSequence is { } sequence
            ? string.Create(CultureInfo.InvariantCulture, $"At capture sequence {sequence}")
            : "After the next capture establishes a baseline";
    }

    internal static string EditTriggerId(LocalAutomationDefinitionState definition)
        => $"automation-edit-{definition.Definition.DefinitionId}";

    internal static string ToggleTriggerId(LocalAutomationDefinitionState definition)
        => $"automation-toggle-{definition.Definition.DefinitionId}";

    internal static string RemoveTriggerId(LocalAutomationDefinitionState definition)
        => $"automation-remove-{definition.Definition.DefinitionId}";

    private async ValueTask InvokeModuleAsync(string identifier, params object?[] arguments)
    {
        _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./Components/Pages/AutomationsPage.razor.js").ConfigureAwait(false);
        await _module.InvokeVoidAsync(identifier, arguments).ConfigureAwait(false);
    }

    private static string FormatUtc(DateTimeOffset? value) => value?.ToString("u") ?? "Not scheduled";

    private static string Split(string value) => OperationsPage.SplitWords(value);

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
