using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// System control: the prototype <c>renderControl()</c> composition bound to the durable capture-control
/// and schedule-override records. Pause and resume are confirmed in a dialog with an optional reason
/// recorded on the receipt; restart is offered only where a supervisor brings CameraAgent back. Drain,
/// upgrade and purge have no bounded in-app command, so they stay disabled with the reason.
/// </summary>
public sealed partial class ControlPage : SiteTimeComponent, IAsyncDisposable
{
    internal const int ReasonMaxLength = 512;
    private const string FocusFallbackId = "control-heading";
    private const string TransferReasonPrefix = "transferred from revision ";
    private const string CaptureTriggerId = "control-capture";
    private const string RestartTriggerId = "control-restart";
    private const string DrainUnavailableReason =
        "No drain command exists. Pausing acquisition stops new captures while accepted processing and delivery continue.";
    private const string UpgradeUnavailableReason =
        "Upgrade, rollback and reinstall run through the installer: hvo-skymonitor cameraagent upgrade, rollback or reinstall.";
    private const string PurgeUnavailableReason =
        "Retention removes local evidence. Purge runs only through the installer (hvo-skymonitor cameraagent purge) with its own confirmation.";
    private readonly CancellationTokenSource _lifetime = new();
    private CameraAgentOperationsView? _view;
    private IReadOnlyList<SystemControlReceipt>? _receipts;
    private CameraAgentRestartStatus? _restart;
    private PendingControl? _pending;
    private StatusMessage? _message;
    private string _reasonInput = string.Empty;
    private string? _error;
    private string? _receiptsError;
    private string? _commandError;
    private string? _focusTargetId;
    private string? _dialogFocusId;
    private int _timeGeneration;
    private bool _loading = true;
    private bool _submitting;
    private bool _restarting;
    private bool _showDialog;
    private bool _disposed;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal ICameraAgentSystemUiService SystemService { get; set; } = default!;
    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    private string CaptureState => _view?.Summary.CaptureControl.Value.State ?? "Unknown";

    private bool CanPause => CaptureState == "Running";

    private bool CanResume => CaptureState == "Paused";

    private string? CaptureUnavailableReason => _view is null
        ? "The capture state could not be read. Refresh to try again."
        : CaptureState switch
        {
            "Running" or "Paused" => null,
            "PauseRequested" => "A pause is in progress: the current exposure finishes before capture stops.",
            "Initializing" => "Capture control is still starting.",
            _ => "Capture control is unavailable on this host.",
        };

    private Banner StateBanner => CaptureState switch
    {
        "Running" => new Banner("success", "Capture is not paused.", "The schedule and the other admission checks decide when each exposure starts."),
        "Paused" => new Banner("warning", "Capture is paused.", "No new exposure starts until capture is resumed. Processing and delivery of accepted work continue."),
        "PauseRequested" => new Banner("running", "Capture is pausing.", "The current exposure finishes through durable ingress before capture stops."),
        "Initializing" => new Banner("running", "Capture control is starting.", "Pause and resume become available once the durable state is read."),
        _ => new Banner("failure", "Capture control is unavailable.", "Pause and resume are disabled; see Health & diagnostics."),
    };

    private string RestartDetail => _restart switch
    {
        { Supervised: true } => "Stop this host so its supervisor starts it again and applies staged changes.",
        { Supervised: false } => "This host is not supervised, so it cannot restart itself.",
        _ => "Restart availability could not be read.",
    };

    private string? RestartUnavailableReason => _restarting
        ? "A restart is already in progress."
        : _restart switch
        {
            null => "Restart availability could not be read. Refresh to try again.",
            { Supervised: false } => "This host is not supervised, so it cannot restart itself. Restart it from wherever it was started.",
            { CanRequest: false } => "Operations change rights are required to restart CameraAgent.",
            _ => null,
        };

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            var focus = _dialogFocusId;
            _dialogFocusId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/ControlPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
            if (focus is not null)
            {
                await _module.InvokeVoidAsync("focusById", focus, "control-dialog-cancel").ConfigureAwait(false);
            }
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/ControlPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    /// <summary>The page's Refresh: reads the capture state and receipts, and has the Time panel read the clock again.</summary>
    private Task RefreshAsync()
    {
        _timeGeneration++;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            if (!await ReadStateAsync())
            {
                return;
            }
            var receipts = await SystemService.GetControlReceiptsAsync(CameraAgentSystemUiService.MaxReceipts, _lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (receipts.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            _receipts = receipts.IsSuccess ? receipts.Value : null;
            _receiptsError = receipts.IsSuccess ? null : receipts.Message ?? "Control receipts could not be read.";

            var restart = await RigService.GetRestartStatusAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            _restart = restart.IsSuccess ? restart.Value : null;
            _restarting |= _restart?.Restarting == true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Reads the capture state; false when the viewer was sent to the access-denied page.</summary>
    private async Task<bool> ReadStateAsync()
    {
        var operations = await OperatorService.GetOperationsAsync(_lifetime.Token);
        if (_disposed)
        {
            return false;
        }
        if (operations.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
            return false;
        }
        // A stale expected version would only produce a conflict, so a failed read disables pause and resume.
        _view = operations.IsSuccess ? operations.Value : null;
        _error = operations.IsSuccess ? null : operations.Message ?? "The operations summary is unavailable.";
        return true;
    }

    /// <summary>Drops the state and receipts read so far, and any open dialog, before leaving the page.</summary>
    private void DenyAccess()
    {
        _view = null;
        _receipts = null;
        _restart = null;
        _pending = null;
        _message = null;
        _showDialog = false;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private void BeginCapture(bool paused)
    {
        if (_view is null || _submitting || (paused ? !CanPause : !CanResume))
        {
            return;
        }
        _pending = new PendingControl(
            paused ? ControlKind.Pause : ControlKind.Resume,
            paused ? "Pause acquisition?" : "Resume acquisition?",
            paused
                ? "The current exposure finishes through durable ingress before another exposure is blocked. Processing and delivery of accepted work continue."
                : "Capture admission reopens using the current startup-validated configuration. The schedule still decides when each exposure starts.",
            paused ? "Pause" : "Resume",
            _view.Summary.CaptureControl.Value.Version,
            CreateIdempotencyKey(),
            null,
            CaptureTriggerId);
        OpenDialog("control-reason");
    }

    private void BeginRestart()
    {
        if (_submitting || RestartUnavailableReason is not null)
        {
            return;
        }
        _pending = new PendingControl(
            ControlKind.Restart,
            "Restart CameraAgent?",
            "Captures pause while CameraAgent stops and its supervisor starts it again. Staged restart-bound changes apply when it is back.",
            "Restart",
            0,
            string.Empty,
            null,
            RestartTriggerId);
        OpenDialog("control-dialog-cancel");
    }

    private void OpenDialog(string focusId)
    {
        _reasonInput = string.Empty;
        _commandError = null;
        _message = null;
        _focusTargetId = null;
        _dialogFocusId = focusId;
        _showDialog = true;
    }

    private void CancelDialog()
    {
        if (_submitting)
        {
            return;
        }
        CloseDialog();
    }

    private void CloseDialog()
    {
        _focusTargetId = _pending?.TriggerId;
        _pending = null;
        _commandError = null;
        _showDialog = false;
    }

    private async Task ConfirmAsync()
    {
        if (_pending is null || _submitting)
        {
            return;
        }
        _submitting = true;
        _commandError = null;
        try
        {
            if (_pending.Kind == ControlKind.Restart)
            {
                await RestartAsync();
            }
            else
            {
                await SetCaptureAsync(_pending);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _submitting = false;
        }
    }

    /// <summary>
    /// Submits the pending pause or resume. A failed attempt keeps the idempotency key, so confirming
    /// again cannot apply the change twice; a changed reason is a different request and mints a new key.
    /// A version conflict re-reads the state and either re-arms the dialog or closes it.
    /// </summary>
    private async Task SetCaptureAsync(PendingControl pending)
    {
        var reason = TrimmedReason();
        if (reason?.Length > ReasonMaxLength)
        {
            _commandError = $"The reason must be {ReasonMaxLength} characters or fewer.";
            return;
        }
        if (pending.SentReason is not null && !string.Equals(pending.SentReason.Value, reason, StringComparison.Ordinal))
        {
            pending = pending with { IdempotencyKey = CreateIdempotencyKey() };
        }
        _pending = pending = pending with { SentReason = new SentReason(reason) };
        var paused = pending.Kind == ControlKind.Pause;
        var result = await OperatorService.SetCapturePausedAsync(
            paused, pending.ExpectedVersion, pending.IdempotencyKey, reason, _lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (result.IsSuccess && result.Value is { } receipt)
        {
            CloseDialog();
            _message = new StatusMessage("success", $"{receipt.Action}: {receipt.Disposition}.", $"Current state: {OperationsPage.SplitWords(receipt.State)}.");
            await LoadAsync();
        }
        else if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
        }
        else if (result.Kind == OperatorUiResultKind.Conflict)
        {
            if (!await ReadStateAsync())
            {
                return;
            }
            if (_view is not null && (paused ? CanPause : CanResume))
            {
                _pending = pending with
                {
                    ExpectedVersion = _view.Summary.CaptureControl.Value.Version,
                    IdempotencyKey = CreateIdempotencyKey(),
                    SentReason = null,
                };
                _commandError = $"Capture state changed while this was open. Capture is still {(paused ? "running" : "paused")}; review the new expected state version and confirm again.";
            }
            else
            {
                CloseDialog();
                _message = new StatusMessage("warning", "Capture state changed.",
                    _view is null
                        ? "The current capture state could not be read, so the command was not sent again."
                        : $"Current state: {OperationsPage.SplitWords(CaptureState)}. The command was not sent again.");
                await LoadAsync();
            }
        }
        else
        {
            _commandError = $"{result.Message ?? "The command could not be completed."} Confirming again retries the same request.";
        }
    }

    private async Task RestartAsync()
    {
        var result = await RigService.RequestRestartAsync(_lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (result.IsSuccess && result.Value is CameraAgentRestartDisposition.Scheduled or CameraAgentRestartDisposition.AlreadyRequested)
        {
            CloseDialog();
            _focusTargetId = null;
            _restarting = true;
        }
        else if (result.IsSuccess)
        {
            _commandError = "This host is not supervised, so it cannot restart itself. Restart it from wherever it was started.";
        }
        else
        {
            _commandError = result.Kind == OperatorUiResultKind.Unauthorized
                ? "Operations change rights are required to restart CameraAgent."
                : result.Message ?? "The restart request failed.";
        }
    }

    private string? TrimmedReason() => string.IsNullOrWhiteSpace(_reasonInput) ? null : _reasonInput.Trim();

    internal static string ActionText(SystemControlReceipt receipt) => receipt.Kind switch
    {
        SystemControlReceiptKind.PauseCapture => "Pause acquisition",
        SystemControlReceiptKind.ResumeCapture => "Resume acquisition",
        SystemControlReceiptKind.OverrideCreated => $"{OverrideModeText(receipt.OverrideMode)} override created",
        SystemControlReceiptKind.OverrideConsumed => $"{OverrideModeText(receipt.OverrideMode)} override used",
        _ => $"{OverrideModeText(receipt.OverrideMode)} override cleared",
    };

    private static string OverrideModeText(CaptureScheduleOverrideMode? mode) => mode switch
    {
        CaptureScheduleOverrideMode.ForceOpen => "Force open",
        CaptureScheduleOverrideMode.ForceClosed => "Force closed",
        _ => "Schedule",
    };

    private string? OverrideWindow(SystemControlReceipt receipt)
        => receipt is { OverrideStartUtc: { } start, OverrideEndUtc: { } end }
            ? $"{FormatReceiptTime(start)} to {FormatReceiptTime(end)}"
            : null;

    internal static string ActorText(SystemControlActor actor) => actor switch
    {
        SystemControlActor.Installer => "Installer",
        SystemControlActor.System => "System",
        _ => "Local owner",
    };

    /// <summary>
    /// The recorded reason. The fixed codes recorded when no note was given read as words, and an override carried
    /// to a new camera rig reads as that rather than as the internal schedule revision identifiers it records.
    /// </summary>
    internal static string ReasonText(string? reason) => reason switch
    {
        null or "" => "None given",
        CameraAgentOperatorUiService.PauseReasonCode => "Maintenance (no note given)",
        CameraAgentOperatorUiService.ResumeReasonCode => "Resume (no note given)",
        "capture admission" => "Used by capture admission",
        _ when reason.StartsWith(TransferReasonPrefix, StringComparison.Ordinal) => "Carried over when the camera rig changed",
        _ => reason,
    };

    private static Chip OutcomeChip(SystemControlOutcome outcome) => outcome switch
    {
        SystemControlOutcome.Applied => new Chip("Completed", "success"),
        SystemControlOutcome.NoChange => new Chip("Already current", "neutral"),
        _ => new Chip("Pending", "pending"),
    };

    private string FormatReceiptTime(DateTimeOffset value) => SiteTime.Format(value);

    private static string CreateIdempotencyKey() =>
        $"ui-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
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

    private enum ControlKind
    {
        Pause,
        Resume,
        Restart,
    }

    /// <summary>The reason a submitted attempt carried; distinguishes "no reason" from "never sent".</summary>
    private sealed record SentReason(string? Value);

    private sealed record PendingControl(
        ControlKind Kind,
        string Heading,
        string Description,
        string ConfirmLabel,
        long ExpectedVersion,
        string IdempotencyKey,
        SentReason? SentReason,
        string TriggerId);

    private sealed record Banner(string Tone, string Title, string Detail);

    private sealed record StatusMessage(string Tone, string Title, string Detail);

    private sealed record Chip(string Text, string Css);
}
