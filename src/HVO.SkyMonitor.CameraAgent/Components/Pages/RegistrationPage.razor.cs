using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Fleet.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// Registration: the prototype <c>renderRegistration()</c> composition bound to the local device identity, the
/// protected registration record and the live heartbeat and delivery state. Registering is a two-step dialog:
/// share the device ID and verification code with LogicHost, then import the envelope it issues. Clearing
/// credentials is not offered, because a later import replaces them once LogicHost has revoked the device.
/// </summary>
public sealed partial class RegistrationPage : ComponentBase, IAsyncDisposable
{
    internal const string MaskedCode = "••••••••••";
    private const string FocusFallbackId = "registration-heading";
    private const string RegisterTriggerId = "registration-register";
    private const string ClearUnavailableReason =
        "Clearing is not offered. After LogicHost revokes this device, registering again with a new envelope replaces the stored credentials.";
    private readonly CancellationTokenSource _lifetime = new();
    private RegistrationView? _view;
    private StatusMessage? _message;
    private WizardStep _wizardStep;
    private string _envelopeInput = string.Empty;
    private string? _error;
    private string? _importError;
    private string? _copyStatus;
    private string? _focusTargetId;
    private string? _dialogFocusId;
    private bool _loading = true;
    private bool _submitting;
    private bool _wizardOpen;
    private bool _showDialog;
    private bool _revealCode;
    private bool _disposed;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;

    [Inject] internal ICameraAgentRegistrationUiService RegistrationService { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    private string? RegisterUnavailableReason => _view switch
    {
        null => "The registration state could not be read. Refresh to try again.",
        { State: RegistrationState.Standalone } =>
            "Central integration is off. Turn it on in the CameraAgent settings and restart to register with LogicHost.",
        { CanImport: false } => "Operations change rights are required to register this CameraAgent.",
        { Identity: null } => "The device identity could not be read. Refresh to try again.",
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
                "import", "./Components/Pages/RegistrationPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
            if (focus is not null)
            {
                await _module.InvokeVoidAsync("focusById", focus, "registration-dialog-cancel").ConfigureAwait(false);
            }
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/RegistrationPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var result = await RegistrationService.GetRegistrationAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            if (result.IsSuccess && result.Value is not null)
            {
                _view = result.Value;
                _error = null;
            }
            else
            {
                // Keep the last read state visible and say why it is not current.
                _error = result.Message ?? "The registration state could not be read.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private void OpenWizard()
    {
        if (_submitting || RegisterUnavailableReason is not null)
        {
            return;
        }
        _wizardStep = WizardStep.Share;
        _envelopeInput = string.Empty;
        _importError = null;
        _copyStatus = null;
        _message = null;
        _focusTargetId = null;
        _dialogFocusId = "registration-dialog-next";
        _wizardOpen = true;
        _showDialog = true;
    }

    private void ShowImportStep()
    {
        _wizardStep = WizardStep.Import;
        _copyStatus = null;
        _focusTargetId = "registration-envelope";
    }

    private void ShowShareStep()
    {
        if (_submitting)
        {
            return;
        }
        _wizardStep = WizardStep.Share;
        _importError = null;
        _focusTargetId = "registration-dialog-next";
    }

    private void CancelWizard()
    {
        if (_submitting)
        {
            return;
        }
        CloseWizard();
    }

    private void CloseWizard()
    {
        _wizardOpen = false;
        _showDialog = false;
        _envelopeInput = string.Empty;
        _importError = null;
        _copyStatus = null;
        _focusTargetId = RegisterTriggerId;
    }

    private async Task ImportAsync()
    {
        if (_submitting || string.IsNullOrWhiteSpace(_envelopeInput))
        {
            return;
        }
        _submitting = true;
        _importError = null;
        try
        {
            var result = await RegistrationService.ImportEnvelopeAsync(_envelopeInput, _lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            if (result.IsSuccess && result.Value is { } receipt)
            {
                _submitting = false;
                CloseWizard();
                var registered = receipt.FriendlyName is { } name ? $"Registered as {name}" : "Registered with LogicHost";
                _message = receipt.SetupIncomplete
                    ? new StatusMessage(
                        "warning",
                        $"{registered}, but setup did not finish.",
                        "The registration is stored, then a follow-up step failed; the host log has the detail. LogicHost has used this envelope, so do not paste it again.")
                    : new StatusMessage("success", $"{registered}.", ImportDetail(receipt.LocationReview));
                await LoadAsync();
                return;
            }
            // A retry needs a deliberate new paste, so the envelope never outlives the attempt that used it.
            _envelopeInput = string.Empty;
            _importError = result.Message ?? "The envelope could not be imported. Nothing was stored.";
            _focusTargetId = "registration-envelope";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _submitting = false;
        }
    }

    private void ToggleReveal() => _revealCode = !_revealCode;

    private async Task CopyAsync(string value, CopiedValue copied)
    {
        var (label, noun) = copied == CopiedValue.DeviceId ? ("Device ID", "device ID") : ("Verification code", "verification code");
        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", value);
            _copyStatus = $"{label} copied.";
        }
        catch (JSException)
        {
            // The browser blocked clipboard access; show the value so it can be selected and copied by hand.
            if (copied == CopiedValue.VerificationCode)
            {
                _revealCode = true;
            }
            _copyStatus = $"The browser blocked copying. Select the {noun} and copy it by hand.";
        }
    }

    /// <summary>Drops everything read so far, including the verification code, before leaving the page.</summary>
    private void DenyAccess()
    {
        _view = null;
        _message = null;
        _envelopeInput = string.Empty;
        _revealCode = false;
        _wizardOpen = false;
        _showDialog = false;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    internal static string StateText(RegistrationState state) => state switch
    {
        RegistrationState.Standalone => "Standalone",
        RegistrationState.NotRegistered => "Not registered",
        RegistrationState.Waiting => "Waiting for LogicHost",
        RegistrationState.Active => "Active",
        RegistrationState.Rejected => "Credentials refused",
        _ => "Unreadable",
    };

    private static Banner Profile(RegistrationView view)
    {
        var name = view.Record?.FriendlyName ?? "This CameraAgent";
        return view.State switch
        {
            RegistrationState.Standalone => new Banner(
                "pending", "This CameraAgent runs standalone", "Central integration is off; nothing is sent to LogicHost"),
            RegistrationState.NotRegistered => new Banner(
                "pending", "This CameraAgent is not registered", "Identity created / waiting for an envelope from LogicHost"),
            RegistrationState.Waiting => new Banner(
                "running", $"{name} is waiting for LogicHost", "Envelope imported / waiting for the first heartbeat acknowledgement"),
            RegistrationState.Active => new Banner(
                "success", $"{name} is active", $"Envelope imported / heartbeat acknowledged / delivery {DeliveryShort(view.Delivery)}"),
            RegistrationState.Rejected => new Banner(
                "failure", $"LogicHost refuses {name}'s credentials", "Heartbeats are rejected, as they are once LogicHost revokes a device"),
            _ => new Banner(
                "failure", "The registration record could not be read", "Captures continue locally; nothing reaches LogicHost until this is resolved"),
        };
    }

    private static Banner Guidance(RegistrationView view) => view.State switch
    {
        RegistrationState.Standalone => new Banner(
            "pending", "Registration needs central integration.",
            "Turn central integration on in the CameraAgent settings and restart, then register here. Standalone capture is unaffected."),
        RegistrationState.NotRegistered => new Banner(
            "running", "Registration takes one envelope.",
            "Choose Register with LogicHost, share the device ID and verification code with an observatory owner in LogicHost, then paste the envelope LogicHost issues."),
        RegistrationState.Rejected => new Banner(
            "failure", "LogicHost no longer accepts these credentials.",
            "If the device was revoked on purpose, nothing more is needed. To bring it back, issue a new envelope in LogicHost and choose Register again."),
        RegistrationState.Unavailable => new Banner(
            "failure", "The protected registration could not be read.",
            "This usually follows a lost data-protection key. Register again with a new envelope to replace it; the host log has the detail."),
        _ => new Banner(
            "running", "Revocation is central-first.",
            $"Revoke {view.Record?.FriendlyName ?? "this device"} in LogicHost, wait until this page reports Credentials refused, then register again if the device should return. Clearing an active registration is not offered."),
    };

    private static string LifecycleSummary(RegistrationView view) => view.State switch
    {
        RegistrationState.Active => view.Record is { } record ? $"Completed {record.IssuedUtc.UtcDateTime.ToString("d MMM", CultureInfo.InvariantCulture)}" : "Completed",
        RegistrationState.Rejected => "Refused",
        RegistrationState.Waiting => "Activating",
        _ => "Not started",
    };

    private List<Step> LifecycleSteps(RegistrationView view)
    {
        var hasIdentity = view.Identity is not null;
        var imported = view.Record is not null;
        var steps = new List<Step>
        {
            hasIdentity
                ? new Step("Identity created", $"Device ID and verification code, {FormatDate(view.Identity!.CreatedUtc)}", string.Empty, false)
                : new Step("Identity created", "The device identity could not be read", "blocking", true),
            imported
                ? new Step("Owner registered", "Observatory assignment chosen in LogicHost", string.Empty, false)
                : new Step("Owner registered", "An observatory owner enters the device ID and code in LogicHost", hasIdentity ? "info" : "not-reached", hasIdentity),
            imported
                ? new Step("Envelope imported", $"Protected credentials issued {FormatDate(view.Record!.IssuedUtc)}", string.Empty, false)
                : new Step("Envelope imported", "Paste the envelope LogicHost issues", "not-reached", false),
        };
        steps.Add(view.State switch
        {
            RegistrationState.Active => new Step("Activated", "Heartbeat acknowledged by LogicHost", string.Empty, false),
            RegistrationState.Rejected => new Step("Activated", "LogicHost refuses the credentials", "blocking", true),
            RegistrationState.Waiting => new Step("Activated", "Waiting for the first heartbeat acknowledgement", "info", true),
            _ => new Step("Activated", "Heartbeat and delivery authorized", "not-reached", false),
        });
        return steps;
    }

    private static Chip CentralChip(RegistrationView view) => view.State switch
    {
        RegistrationState.Active => new Chip("Acknowledged", "success"),
        RegistrationState.Waiting => new Chip("Waiting", "pending"),
        RegistrationState.Rejected => new Chip("Refused", "failure"),
        RegistrationState.Unavailable => new Chip("Unreadable", "failure"),
        RegistrationState.Standalone => new Chip("Not used", "neutral"),
        _ => new Chip("Not registered", "warning"),
    };

    private string LastHeartbeat(RegistrationView view)
    {
        if (view.State == RegistrationState.Standalone)
        {
            return "Not used";
        }
        return view.Record is not null && view.Heartbeat.LastAcknowledgedUtc is { } acknowledged && acknowledged >= view.Record.IssuedUtc
            ? $"{HealthPage.FormatAge(TimeProvider.GetUtcNow() - acknowledged)} ago"
            : "None yet";
    }

    private static string HeartbeatText(RegistrationView view) => view.State switch
    {
        RegistrationState.Active => "Healthy",
        RegistrationState.Rejected => "Refused by LogicHost",
        _ => view.Heartbeat.Availability switch
        {
            FleetAvailability.Initializing => "Starting",
            FleetAvailability.Available => "Waiting for acknowledgement",
            FleetAvailability.Degraded => "Retrying",
            _ => "Unavailable",
        },
    };

    internal static string DeliveryText(RegistrationDelivery delivery)
    {
        if (!delivery.UploadEnabled)
        {
            return "Off in settings";
        }
        return delivery.Availability switch
        {
            ArtifactOutboxAvailability.Healthy => delivery.PendingCount == 0
                ? "Healthy, nothing pending"
                : string.Create(CultureInfo.InvariantCulture, $"Healthy, {delivery.PendingCount:N0} pending"),
            ArtifactOutboxAvailability.Initializing => "Starting",
            ArtifactOutboxAvailability.Degraded => string.Create(
                CultureInfo.InvariantCulture, $"Degraded, {delivery.RetryCount:N0} retrying, {delivery.QuarantineCount:N0} quarantined"),
            _ => "Unavailable",
        };
    }

    private static string DeliveryShort(RegistrationDelivery delivery) => !delivery.UploadEnabled
        ? "off in settings"
        : delivery.Ready ? "healthy" : "not yet healthy";

    private static string ImportDetail(DeploymentLocationResolutionStatus review) => review switch
    {
        DeploymentLocationResolutionStatus.Acknowledged =>
            "LogicHost acknowledged this camera's location. The page shows Active once the first heartbeat is acknowledged.",
        DeploymentLocationResolutionStatus.Rejected =>
            "LogicHost rejected the reported location; local geometry still governs capture. Review it on Observatory & location.",
        _ => "LogicHost will review the reported location. The page shows Active once the first heartbeat is acknowledged.",
    };

    private static string FormatDate(DateTimeOffset value)
        => value.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _envelopeInput = string.Empty;
        _revealCode = false;
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

    private enum WizardStep
    {
        Share,
        Import,
    }

    private enum CopiedValue
    {
        DeviceId,
        VerificationCode,
    }

    private sealed record Banner(string Tone, string Title, string Detail);

    private sealed record StatusMessage(string Tone, string Title, string Detail);

    private sealed record Chip(string Text, string Css);

    private sealed record Step(string Title, string Detail, string State, bool Current);
}
