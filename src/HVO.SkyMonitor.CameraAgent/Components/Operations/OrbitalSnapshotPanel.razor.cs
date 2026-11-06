using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// Software &amp; Catalog's orbital-element snapshot panel. It reads one composed status on load and on an explicit
/// Reload (it never polls), and runs only the bounded Refresh and Resume commands of
/// <see cref="OrbitalElementsOperations"/>. Every command uses one idempotency key per intent; only an unavailable
/// outcome, whose durable result is unknown, keeps the key so a retry sends the identical request.
/// </summary>
public sealed partial class OrbitalSnapshotPanel : SiteTimeComponent, IAsyncDisposable
{
    /// <summary>The fixed reason recorded with a refresh requested from this panel.</summary>
    internal const string RefreshReason = "Requested from the Software & Catalog page.";
    internal const string AutomationsDefinitionLink = "/operations/automations?definition=" + OrbitalScheduleDefinitionId;
    internal const string AutomationsRunsLink = "/operations/automations?view=runs";
    internal const int MaximumResumeReasonLength = 256;
    internal const string ResumeReasonRequiredText = "Enter a reason of 1 to 256 characters on a single line.";
    private const string OrbitalScheduleDefinitionId = OrbitalElementsAutomationAdapter.DefinitionId;
    private const string RefreshTriggerId = "orbital-refresh";
    private const string ResumeTriggerId = "orbital-resume";
    private const string ResumeReasonId = "orbital-resume-reason";
    private const string FocusFallbackId = "orbital-reload";
    private const string ModulePath = "./Components/Operations/OrbitalSnapshotPanel.razor.js";
    private readonly CancellationTokenSource _lifetime = new();
    private OrbitalDataStatusView? _view;
    private string? _error;
    private PanelMessage? _message;
    private string? _refreshKey;
    private OrbitalProviderHalt? _resumeHalt;
    private string _resumeReason = string.Empty;
    private string? _resumeKey;
    private string? _resumePayload;
    private string? _dialogMessage;
    private string? _focusTargetId;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;
    private bool _loading = true;
    private bool _reloading;
    private bool _refreshing;
    private bool _resuming;
    private bool _resumePinned;
    private bool _dialogMessageIsError;
    private bool _dialogOpen;
    private bool _showDialog;
    private bool _disposed;

    [Inject] internal ICameraAgentOrbitalUiService OrbitalService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    /// <summary>Raised after every status read with the view, or null when it could not be read, so the page summary matches.</summary>
    [Parameter] public EventCallback<OrbitalDataStatusView?> ViewChanged { get; set; }

    private bool Busy => _reloading || _refreshing || _resuming;

    private string? RefreshUnavailableReason => _view switch
    {
        null => "The orbital data status could not be read.",
        { CanMutate: false } => "Operations change rights are required to refresh orbital data.",
        { RefreshBlockedReasonCode: { } code } view => Describe(code, view.Status.NextPermittedAttemptUtc),
        _ => null,
    };

    private string? ResumeUnavailableReason => _view switch
    {
        null => "The orbital data status could not be read.",
        { CanMutate: false } => "Operations change rights are required to resume the provider.",
        { CanResume: false } => "There is no provider halt to resume.",
        _ => null,
    };

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath).ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    /// <returns>False when access was denied and the page navigated away.</returns>
    private async Task<bool> LoadAsync()
    {
        try
        {
            var result = await OrbitalService.GetAsync(_lifetime.Token);
            if (_disposed)
            {
                return false;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return false;
            }
            if (result is { IsSuccess: true, Value: { } view })
            {
                _view = view;
                _error = null;
            }
            else
            {
                _view = null;
                _error = result.Message ?? "Orbital data status is unavailable.";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                _loading = false;
            }
        }
        await ViewChanged.InvokeAsync(_view);
        return true;
    }

    private async Task ReloadAsync()
    {
        if (Busy || _disposed)
        {
            return;
        }
        _reloading = true;
        _message = null;
        try
        {
            await LoadAsync();
        }
        finally
        {
            if (!_disposed)
            {
                _reloading = false;
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (Busy || _disposed || RefreshUnavailableReason is not null)
        {
            return;
        }
        _refreshKey ??= $"ui-{Guid.NewGuid():N}";
        _refreshing = true;
        _message = null;
        try
        {
            var result = await OrbitalService.RefreshAsync(_refreshKey, RefreshReason, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            // Only an unavailable outcome leaves the durable result unknown; the same key must then be retried. Any
            // other outcome is definite, so the next Refresh is a new intent with a new key.
            var pinned = result.Kind == OperatorUiResultKind.Unavailable;
            if (!pinned)
            {
                _refreshKey = null;
            }
            _message = CommandMessage(result, pinned
                ? " Retry sends the identical request with the same key, so it is never queued twice."
                : null);
            await LoadAsync();
        }
        finally
        {
            if (!_disposed)
            {
                _refreshing = false;
            }
        }
    }

    /// <summary>Captures the halt as displayed; the resume binds to exactly this halt's start time.</summary>
    private void OpenResumeDialog()
    {
        if (Busy || ResumeUnavailableReason is not null || _view?.Status.ProviderHalt is not { } halt)
        {
            return;
        }
        _resumeHalt = halt;
        _dialogMessage = null;
        _focusTargetId = null;
        _dialogOpen = true;
        _showDialog = true;
    }

    /// <summary>Ignored while the request is in flight so its outcome is always observed.</summary>
    private void CancelDialog()
    {
        if (_resuming)
        {
            return;
        }
        CloseDialog();
        _dialogMessage = null;
        _resumeKey = null;
        _resumePayload = null;
        _resumePinned = false;
    }

    private void CloseDialog()
    {
        if (_dialogOpen)
        {
            _focusTargetId = ResumeTriggerId;
        }
        _dialogOpen = false;
        _showDialog = false;
    }

    private async Task SubmitResumeAsync()
    {
        if (_resuming || _disposed || _resumeHalt is not { } halt)
        {
            return;
        }
        var reason = _resumeReason.Trim();
        if (reason.Length is < 1 or > MaximumResumeReasonLength || reason.Any(char.IsControl))
        {
            _dialogMessageIsError = true;
            _dialogMessage = ResumeReasonRequiredText;
            return;
        }
        var payload = string.Concat(halt.SinceUtc.UtcTicks.ToString(CultureInfo.InvariantCulture), "\n", reason);
        if (!string.Equals(payload, _resumePayload, StringComparison.Ordinal) || _resumeKey is null)
        {
            _resumePayload = payload;
            _resumeKey = $"ui-{Guid.NewGuid():N}";
        }
        _resuming = true;
        _dialogMessageIsError = false;
        _dialogMessage = "Resuming the provider.";
        try
        {
            var result = await OrbitalService.ResumeAsync(halt.SinceUtc, _resumeKey, reason, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            switch (result.Kind)
            {
                case OperatorUiResultKind.Unauthorized:
                    DenyAccess();
                    break;
                case OperatorUiResultKind.Unavailable:
                    _resumePinned = true;
                    _dialogMessageIsError = true;
                    _dialogMessage = string.Concat(CommandText(result),
                        " The result is unknown; retry sends the identical request with the same key.");
                    break;
                case OperatorUiResultKind.Invalid:
                    _resumeKey = null;
                    _resumePayload = null;
                    _dialogMessageIsError = true;
                    _dialogMessage = CommandText(result);
                    break;
                default:
                    // Success, or a conflict such as a changed or cleared halt: the dialog's halt is no longer the
                    // current one either way, so close it and show the state read after the command.
                    _resumeKey = null;
                    _resumePayload = null;
                    _resumePinned = false;
                    _resumeReason = string.Empty;
                    _dialogMessage = null;
                    CloseDialog();
                    _message = CommandMessage(result, null);
                    await LoadAsync();
                    break;
            }
        }
        finally
        {
            if (!_disposed)
            {
                _resuming = false;
            }
        }
    }

    private void DenyAccess()
    {
        _view = null;
        _message = null;
        _dialogOpen = false;
        _showDialog = false;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private PanelMessage CommandMessage(OperatorUiResult<OrbitalCommandView> result, string? suffix)
    {
        var text = string.Concat(CommandText(result), suffix);
        return result.Kind switch
        {
            OperatorUiResultKind.Success => PanelMessage.Info(text),
            OperatorUiResultKind.Conflict => PanelMessage.Failure(text, "warning"),
            _ => PanelMessage.Failure(text, "failure"),
        };
    }

    /// <summary>
    /// The command's reason in operator text, with a not-yet-permitted time in site time and a halt pointing at Resume.
    /// A failure without a command result keeps the service's fixed sanitized message.
    /// </summary>
    private string CommandText(OperatorUiResult<OrbitalCommandView> result)
    {
        if (result.Value is not { } command)
        {
            return result.Message ?? "The orbital command could not be completed.";
        }
        var text = Describe(command.ReasonCode, command.NextPermittedAttemptUtc);
        return string.Equals(command.ReasonCode, OrbitalElementsOperations.ProviderHaltedReasonCode, StringComparison.Ordinal)
            ? string.Concat(text, " Review the halt below, then resume the provider.")
            : text;
    }

    private string Describe(string? reasonCode, DateTimeOffset? nextPermitted)
        => OrbitalOperatorText.Describe(reasonCode, nextPermitted is { } next ? SiteTime.Format(next) : null);

    /// <summary>Unavailable storage always calls for repair, whatever the read model last recorded.</summary>
    private static OrbitalRefreshRecoveryAction RecoveryAction(OrbitalDataStatusView view)
        => view.StoreUnavailable ? OrbitalRefreshRecoveryAction.RepairStorage : view.Status.RecoveryAction;

    private string ReasonHeadline(OrbitalDataStatusView view)
    {
        var reason = view.StoreUnavailable ? view.HealthReason : view.Status.Reason;
        return OrbitalOperatorText.LeadingReasonCode(reason) is { } code
            ? Describe(code, view.Status.NextPermittedAttemptUtc)
            : OrbitalOperatorText.ReasonText(reason);
    }

    /// <summary>Health's own degrading reason, when it adds something the refresh reason does not already say.</summary>
    private static string? HealthNote(OrbitalDataStatusView view)
        => view.StoreUnavailable || view.HealthState == OrbitalElementsHealthState.Healthy ||
            string.Equals(view.HealthReason, view.Status.Reason, StringComparison.Ordinal)
            ? null
            : string.Concat("Health: ", OrbitalOperatorText.ReasonText(view.HealthReason));

    private static string ReasonTone(OrbitalDataStatusView view) => RecoveryAction(view) switch
    {
        OrbitalRefreshRecoveryAction.None => view.HasData ? "success" : "pending",
        OrbitalRefreshRecoveryAction.WaitForPermittedAttempt or OrbitalRefreshRecoveryAction.Configure => "pending",
        OrbitalRefreshRecoveryAction.InspectQuarantine => "warning",
        _ => "failure",
    };

    private static string FreshnessDetail(OrbitalDataStatusView view)
    {
        var limit = string.Create(CultureInfo.InvariantCulture, $"{OrbitalElementsHealthEvaluator.ElementStaleAge.TotalHours:0} h");
        return view.OldestElementAge is { } oldest
            ? string.Concat("Newest element epoch; the oldest is ", HealthPage.FormatDuration(oldest),
                " old. Elements read as stale after ", limit, ", however recently they were fetched.")
            : string.Concat("Newest element epoch. Elements read as stale after ", limit, ", however recently they were fetched.");
    }

    /// <summary>The marker's position on a track that spans one and a half stale limits; the stale limit sits at two thirds.</summary>
    internal static string MarkerPercent(TimeSpan newestAge)
    {
        var span = OrbitalElementsHealthEvaluator.ElementStaleAge.TotalSeconds * 1.5;
        var percent = Math.Clamp(newestAge.TotalSeconds / span * 100, 0, 100);
        return percent.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private string NextAttemptText(OrbitalDataStatusView view)
    {
        var status = view.Status;
        if (view.IsHalted)
        {
            return "Halted until an operator resumes the provider";
        }
        if (status.NextPermittedAttemptUtc is { } next && view.ObservedUtc < next)
        {
            return string.Concat("Next permitted ", SiteTime.Format(next, "d MMM HH:mm"));
        }
        if (!status.Enabled)
        {
            return "Refresh is off";
        }
        return status.NextDueUtc is { } due
            ? string.Concat("Next due ", SiteTime.Format(due, "d MMM HH:mm"))
            : "No refresh is scheduled";
    }

    private string NextPermittedText(OrbitalDataStatusView view)
        => view.Status.NextPermittedAttemptUtc is { } next
            ? view.ObservedUtc < next
                ? SiteTime.Format(next)
                : string.Concat(SiteTime.Format(next), " (permitted now)")
            : "Permitted now";

    private string TimeWithAge(DateTimeOffset? instant, TimeSpan? age, string none)
        => instant is { } value
            ? age is { } elapsed
                ? string.Concat(SiteTime.Format(value), " (", HealthPage.FormatDuration(elapsed), " ago)")
                : SiteTime.Format(value)
            : none;

    private static string EpochRange(OrbitalSnapshotManifestV1 manifest)
        => manifest is { MinimumEpochUtc: { } oldest, MaximumEpochUtc: { } newest }
            ? string.Concat(Utc(oldest), " to ", Utc(newest))
            : "Not recorded";

    private static string EpochAges(OrbitalDataStatusView view)
        => view is { OldestElementAge: { } oldest, NewestElementAge: { } newest }
            ? string.Concat("Oldest ", HealthPage.FormatDuration(oldest), " old / newest ", HealthPage.FormatDuration(newest), " old")
            : "Ages unavailable";

    private static string ExclusionsText(IReadOnlyList<string> exclusions)
        => exclusions.Count == 0 ? "no known exclusions listed" : string.Concat("excludes ", string.Join(", ", exclusions));

    private static string ScheduleText(OrbitalScheduleSummary? schedule) => schedule switch
    {
        null => "Unavailable",
        { Exists: false } => "No schedule",
        { Enabled: false } => "Disabled",
        { IntervalSeconds: { } interval } => string.Concat("Enabled, every ", Interval(interval)),
        _ => "Enabled",
    };

    private string ScheduleDetail(OrbitalScheduleSummary? schedule) => schedule switch
    {
        null => "The automation store could not be read.",
        { Exists: false } => "Create the orbital-elements-refresh automation to schedule refreshes.",
        { ActiveRun: true } => "A run is queued or running.",
        { Enabled: true, NextRunUtc: { } next } => string.Concat("Next run ", SiteTime.Format(next), "."),
        _ => string.Empty,
    };

    private static string Interval(int seconds)
    {
        var interval = TimeSpan.FromSeconds(seconds);
        return seconds % 3600 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)interval.TotalHours} h")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds:N0} s");
    }

    /// <summary>Only an absolute HTTPS terms URI becomes a link; anything else is shown as its reference text alone.</summary>
    private static string? TermsLink(Uri? terms)
        => terms is { IsAbsoluteUri: true } && string.Equals(terms.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            ? terms.AbsoluteUri
            : null;

    /// <summary>Header values stay out of the page: only whether one was recorded.</summary>
    internal static string RecordedText(string? value) => string.IsNullOrEmpty(value) ? "Not recorded" : "Recorded";

    private static string CodeText(string? recorded) => OrbitalOperatorText.LeadingReasonCode(recorded) ?? "Not a reason code";

    private static string Utc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

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

    private sealed record PanelMessage(string Text, string Tone, bool IsError)
    {
        public static PanelMessage Info(string text) => new(text, "success", false);

        public static PanelMessage Failure(string text, string tone) => new(text, tone, true);
    }
}
