using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// The full space-weather panel. It reads the shared projection on load, on an explicit Reload and after every
/// command (it never polls), and runs only the bounded Refresh, Resume and Acknowledge commands of
/// <see cref="SpaceWeatherOperations"/>. Every command uses one idempotency key per intent: an unavailable outcome,
/// whose durable result is unknown, pins the key and its payload so a retry sends the identical request; a revision
/// conflict wrote nothing and keeps the key; any other outcome is definite and the next attempt uses a new key.
/// </summary>
public sealed partial class SpaceWeatherPanel : ComponentBase, IAsyncDisposable
{
    internal const string DefinitionId = "space-weather-refresh";
    internal const string AutomationsDefinitionLink = "/operations/automations?definition=" + DefinitionId;
    internal const string AutomationsSetupLink = "/operations/automations?setup=space-weather";
    internal const string AutomationsRunsLink = "/operations/automations?view=runs";
    internal const string CloudText = "Cloud: not assessed";
    internal const string InconsistentText = "Data changed while reading; reload.";
    internal const string UnconfiguredHaltText = "Product not configured; this retained halt cannot be resumed here.";
    internal const string NoNoticesText = "No SWPC notices retained.";
    internal const string NoFirstSuccessText =
        "No successful fetch yet. Notices appear here after the first successful fetch of the alerts product.";
    internal const string StoreUnavailableText =
        "The space-weather store is unavailable, so nothing is claimed about current notices.";
    internal const string ReadFailedText =
        "The space-weather state could not be read, so nothing is claimed about current notices.";
    internal const string RevisionConflictReasonCode = "space-weather.revision-conflict";
    internal const string KeyReusedReasonCode = "space-weather.idempotency-key-reused";
    internal const string HaltChangedReasonCode = "space-weather.resume-halt-changed";
    private const string RefreshTriggerId = "space-weather-refresh";
    private const string FocusFallbackId = "space-weather-reload";
    private const string ModulePath = "./Components/Operations/SpaceWeatherPanel.razor.js";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, string> _acknowledgeKeys = new(StringComparer.Ordinal);
    private SpaceWeatherUiView? _view;
    private string? _error;
    private PanelMessage? _message;
    private string? _refreshKey;
    private SpaceWeatherUiHalt? _resumeHalt;
    private string? _resumeTriggerId;
    private string? _resumeKey;
    private string? _resumePayload;
    private string? _dialogMessage;
    private string? _focusTargetId;
    private string? _acknowledging;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;
    private int _historyPage;
    private bool _loading = true;
    private bool _reloading;
    private bool _refreshing;
    private bool _resuming;
    private bool _resumePinned;
    private bool _dialogMessageIsError;
    private bool _dialogOpen;
    private bool _showDialog;
    private bool _disposed;

    [Inject] internal ICameraAgentSpaceWeatherUiService SpaceWeatherService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private bool Busy => _reloading || _refreshing || _resuming || _acknowledging is not null;

    private bool CanMutate => _view?.CanMutate == true;

    /// <summary>The failed read's message, unless it only repeats the heading.</summary>
    private string? ErrorDetail => string.Equals(_error, SpaceWeatherSummary.UnavailableText, StringComparison.Ordinal) ? null : _error;

    /// <summary>Why Refresh is unavailable, shown beside the disabled button; null when it may run.</summary>
    internal string? RefreshUnavailableReason => RefreshBlockedReason(_view);

    internal static string? RefreshBlockedReason(SpaceWeatherUiView? view) => view switch
    {
        null => "The space-weather state could not be read.",
        { CanMutate: false } => "Operations change rights are required to refresh space weather.",
        { Projection.Enabled: false } => "Space weather is off.",
        { Projection.HasData: false } => "The space-weather store is unavailable.",
        { Projection.DefinitionExists: false } => "The space-weather-refresh automation does not exist.",
        { Projection.DefinitionEnabled: false } => "The space-weather-refresh automation is disabled.",
        { Projection.ProviderHalt: not null } => "SWPC fetching is halted for the provider. Resume it first.",
        _ => null,
    };

    internal static string? ResumeBlockedReason(SpaceWeatherUiView? view, SpaceWeatherUiHalt halt) => view switch
    {
        null => "The space-weather state could not be read.",
        { CanMutate: false } => "Operations change rights are required to resume fetching.",
        _ when !halt.CanResume => "This halt cannot be resumed here: its product is not enabled or its time is not a UTC time.",
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
            var result = await SpaceWeatherService.GetAsync(_lifetime.Token);
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
                _historyPage = view.Projection.ClampHistoryPage(_historyPage);
            }
            else
            {
                _view = null;
                _error = result.Message ?? SpaceWeatherSummary.UnavailableText;
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
        _refreshKey ??= NewKey();
        _refreshing = true;
        _message = null;
        try
        {
            var result = await SpaceWeatherService.RefreshAsync(_refreshKey, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            var pinned = result.Kind == OperatorUiResultKind.Unavailable;
            if (!KeepsKey(result))
            {
                _refreshKey = null;
            }
            _message = CommandMessage(result, pinned
                ? " The result is unknown; retry sends the identical request with the same key, so it is never queued twice."
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

    private async Task AcknowledgeAsync(SpaceWeatherUiNotice notice)
    {
        if (Busy || _disposed || !CanMutate || !notice.NeedsAcknowledgement)
        {
            return;
        }
        if (!_acknowledgeKeys.TryGetValue(notice.Group, out var key))
        {
            key = NewKey();
            _acknowledgeKeys[notice.Group] = key;
        }
        _acknowledging = notice.Group;
        _message = null;
        try
        {
            var result = await SpaceWeatherService.AcknowledgeAsync(notice.Group, key, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
                return;
            }
            if (!KeepsKey(result))
            {
                _acknowledgeKeys.Remove(notice.Group);
            }
            _message = CommandMessage(result, result.Kind == OperatorUiResultKind.Unavailable
                ? " The result is unknown; retry sends the identical request with the same key."
                : null);
            await LoadAsync();
        }
        finally
        {
            if (!_disposed)
            {
                _acknowledging = null;
            }
        }
    }

    /// <summary>Captures the halt as displayed; the resume binds to exactly this halt's unformatted start time.</summary>
    private void OpenResumeDialog(SpaceWeatherUiHalt halt, string triggerId)
    {
        if (Busy || ResumeBlockedReason(_view, halt) is not null || halt.SinceUtc is null)
        {
            return;
        }
        if (_resumePayload is not null && !string.Equals(Payload(halt), _resumePayload, StringComparison.Ordinal))
        {
            _resumeKey = null;
            _resumePayload = null;
            _resumePinned = false;
        }
        _resumeHalt = halt;
        _resumeTriggerId = triggerId;
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
        if (!_resumePinned)
        {
            _resumeKey = null;
            _resumePayload = null;
        }
    }

    private void CloseDialog()
    {
        if (_dialogOpen)
        {
            _focusTargetId = _resumeTriggerId;
        }
        _dialogOpen = false;
        _showDialog = false;
    }

    private async Task SubmitResumeAsync()
    {
        if (_resuming || _disposed || _resumeHalt is not { SinceUtc: { } since } halt)
        {
            return;
        }
        var payload = Payload(halt);
        if (!string.Equals(payload, _resumePayload, StringComparison.Ordinal) || _resumeKey is null)
        {
            _resumePayload = payload;
            _resumeKey = NewKey();
        }
        _resuming = true;
        _dialogMessageIsError = false;
        _dialogMessage = "Resuming SWPC fetching.";
        try
        {
            var result = await SpaceWeatherService.ResumeAsync(
                new SpaceWeatherResumeRequest(halt.Scope, halt.Product, since), _resumeKey, CancellationToken.None);
            if (_disposed)
            {
                return;
            }
            var reason = result.Value?.ReasonCode;
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
            }
            else if (result.Kind == OperatorUiResultKind.Unavailable)
            {
                _resumePinned = true;
                _dialogMessageIsError = true;
                _dialogMessage = string.Concat(CommandText(result),
                    " The result is unknown; retry sends the identical request with the same key.");
            }
            else if (result.Kind == OperatorUiResultKind.Invalid ||
                     string.Equals(reason, RevisionConflictReasonCode, StringComparison.Ordinal))
            {
                // Nothing was written, so the dialog stays open; only a revision conflict may retry with the same key.
                if (!KeepsKey(result))
                {
                    _resumeKey = null;
                    _resumePayload = null;
                }
                _dialogMessageIsError = true;
                _dialogMessage = CommandText(result);
            }
            else
            {
                // Success, a changed or cleared halt, a reused key or a missing notice: the dialog's halt is no longer
                // the one to act on, so close it and show the state read after the command.
                _resumeKey = null;
                _resumePayload = null;
                _resumePinned = false;
                _dialogMessage = null;
                CloseDialog();
                _message = CommandMessage(result, string.Equals(reason, HaltChangedReasonCode, StringComparison.Ordinal)
                    ? " The halt shown below is the current one."
                    : null);
                await LoadAsync();
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

    private void ShowHistoryPage(int index)
    {
        if (_view is { } view)
        {
            _historyPage = view.Projection.ClampHistoryPage(index);
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

    /// <summary>
    /// Only an unavailable outcome leaves the durable result unknown, so the same key must be retried; a revision
    /// conflict wrote no receipt, so the same key may be retried. A reused key, and every other outcome, starts over.
    /// </summary>
    internal static bool KeepsKey(OperatorUiResult<SpaceWeatherCommandView> result)
        => result.Kind == OperatorUiResultKind.Unavailable ||
            string.Equals(result.Value?.ReasonCode, RevisionConflictReasonCode, StringComparison.Ordinal);

    /// <summary>The resume payload a key is bound to: the halt's scope, product and exact start.</summary>
    internal static string Payload(SpaceWeatherUiHalt halt)
        => string.Join('\n', halt.Scope, halt.Product ?? string.Empty, halt.SinceUtcText);

    private static string NewKey() => $"ui-{Guid.NewGuid():N}";

    private static PanelMessage CommandMessage(OperatorUiResult<SpaceWeatherCommandView> result, string? suffix)
    {
        var text = string.Concat(CommandText(result), suffix);
        return result.Kind switch
        {
            OperatorUiResultKind.Success => PanelMessage.Info(text),
            OperatorUiResultKind.Conflict => PanelMessage.Failure(text, "warning"),
            _ => PanelMessage.Failure(text, "failure"),
        };
    }

    private static string CommandText(OperatorUiResult<SpaceWeatherCommandView> result)
        => result.Message ?? SpaceWeatherOperatorText.GenericText;

    private static string ScheduleText(SpaceWeatherUiProjection projection) => projection switch
    {
        { HasData: false } => "Unavailable",
        { DefinitionExists: false } => "No schedule",
        { DefinitionEnabled: false } => "Disabled",
        _ => "Enabled",
    };

    private static string SiteText(SpaceWeatherUiSite site)
        => site is { Configured: true, GeomagneticLatitude: { } latitude }
            ? string.Create(CultureInfo.InvariantCulture, $"Geomagnetic latitude {latitude:0.0}° (HVO SkyMonitor estimate)")
            : "Site location not configured; local relevance is unknown.";

    private static string Counters(SpaceWeatherUiSource source)
        => string.Create(CultureInfo.InvariantCulture,
            $"{source.ConsecutiveTransientFailures} transient / {source.ConsecutiveRateLimited} rate-limited / {source.ConsecutiveRejected} rejected");

    private static string TimeCell(SpaceWeatherUiTime? time, string none)
        => time is { } value ? string.Concat(value.UtcText, " (", value.LocalText, ")") : none;

    private static string AcknowledgementText(SpaceWeatherUiNotice notice)
        => notice.AcknowledgedAt is { } at
            ? string.Concat("Acknowledged ", at.UtcText, " (", at.LocalText, ")", AcknowledgementSuffix(notice.Escalation))
            : "Not acknowledged";

    /// <summary>What follows the acknowledgement time; a settled acknowledgement needs no suffix.</summary>
    private static string AcknowledgementSuffix(SpaceWeatherEscalation escalation) => escalation switch
    {
        SpaceWeatherEscalation.Escalated => "; escalated since acknowledged",
        SpaceWeatherEscalation.Unknown => "; escalation since then cannot be determined",
        _ => string.Empty
    };

    internal static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static NoticeList[] Lists(SpaceWeatherUiProjection projection, int historyPage) =>
    [
        new("space-weather-current", "Active and pending", "Active notices first, then pending; newest issued first.",
            projection.Current, projection.CurrentMore, "No pending or active SWPC notices retained.", false),
        new("space-weather-recent", "Recently ended", "Notices in the 24 hours after they ended; latest end first.",
            projection.Recent, projection.RecentMore, "No recently ended SWPC notices retained.", false),
        new("space-weather-history", "History", "Earlier retained notices; latest end first.",
            projection.HistoryPage(historyPage), 0, "No earlier SWPC notices retained.", true),
    ];

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

    private sealed record NoticeList(
        string Id,
        string Title,
        string Description,
        IReadOnlyList<SpaceWeatherUiNotice> Notices,
        int More,
        string EmptyText,
        bool Paged);

    private sealed record PanelMessage(string Text, string Tone, bool IsError)
    {
        public static PanelMessage Info(string text) => new(text, "success", false);

        public static PanelMessage Failure(string text, string tone) => new(text, tone, true);
    }
}
