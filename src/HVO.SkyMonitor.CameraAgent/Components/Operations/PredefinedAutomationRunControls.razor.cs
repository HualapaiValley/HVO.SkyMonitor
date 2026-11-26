using System.Globalization;
using System.Runtime.CompilerServices;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class PredefinedAutomationRunControls : IDisposable
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly ConditionalWeakTable<TimeZoneInfo, string> SiteTimeZoneRules = new();
    private readonly CancellationTokenSource _lifetime = new();

    [Parameter, EditorRequired] public LocalAutomationDefinitionState Definition { get; set; } = default!;
    [Parameter] public LocalAutomationRun? Run { get; set; }
    [Parameter] public bool Pending { get; set; }
    [Parameter] public EventCallback Changed { get; set; }
    [Inject] internal ICameraAgentAutomationUiService Service { get; set; } = default!;
    [Inject] internal NavigationManager Navigation { get; set; } = default!;

    private string _dateInput = string.Empty;
    private string _windowInput = string.Empty;
    private string _reason = string.Empty;
    private string? _message;
    private bool _error;
    private bool _busy;
    private bool _queued;
    private IReadOnlyList<LocalAutomationSourceWindow> _windows = [];
    private (string DefinitionId, long Version, string? RunKey, string? WindowIdentity, string Reason)? _lastCommand;
    private string _commandKey = string.Empty;

    private bool Backfill => Run is null && Definition.Definition.SourceWindow is not null;
    private string ActionLabel => Run is not null ? "Retry" : Backfill ? "Backfill" : "Run now";
    private LocalAutomationSourceWindow? SelectedWindow => _windows.FirstOrDefault(
        window => window.StartUtc.UtcTicks.ToString(Invariant) == _windowInput);
    private bool CanQueue => !_busy && !_queued && !Pending && (!Backfill || SelectedWindow is not null) &&
        LocalAutomationDefinitionValidator.IsText(_reason.Trim(), LocalAutomationContract.MaximumReasonLength);

    private void DateChanged(ChangeEventArgs args)
    {
        _dateInput = args.Value as string ?? string.Empty;
        _windows = [];
        _windowInput = string.Empty;
        WindowChanged();
    }

    private void WindowChanged()
    {
        _message = null;
        _queued = false;
    }

    private async Task PreviewAsync()
    {
        if (_busy)
        {
            return;
        }
        WindowChanged();
        if (!DateOnly.TryParseExact(_dateInput, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var date))
        {
            SetMessage("Choose the starting-sunrise report date.", error: true);
            return;
        }
        _busy = true;
        _windows = [];
        _windowInput = string.Empty;
        try
        {
            var result = await Service.PreviewBackfillAsync(Definition.Definition.DefinitionId, Definition.Version, date, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || !Accept(result))
            {
                return;
            }
            _windows = result.Value ?? [];
            if (_windows.Count == 1)
            {
                _windowInput = _windows[0].StartUtc.UtcTicks.ToString(Invariant);
            }
            if (_windows.Count == 0)
            {
                SetMessage("This date has no window that has settled within the seven-day backfill limit.", error: false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task QueueAsync()
    {
        if (!CanQueue)
        {
            return;
        }
        var window = SelectedWindow;
        var command = (Definition.Definition.DefinitionId, Definition.Version, Run?.RunKey, window?.IdentitySha256, _reason.Trim());
        if (_lastCommand != command)
        {
            _lastCommand = command;
            _commandKey = Guid.NewGuid().ToString("N");
        }
        var (id, version, runKey, identity, reason) = command;
        _busy = true;
        _message = null;
        try
        {
            var result = runKey is not null
                ? await Service.RetryAsync(new(id, runKey, version, _commandKey, string.Empty, reason), CancellationToken.None)
                : window is not null
                    ? await Service.BackfillAsync(new(id, window.ReportingPeriod.ReportDate,
                        window.Policy.Kind == LocalAutomationSourceWindowKind.CompletedCivilHour ? window.StartUtc : null,
                        version, _commandKey, string.Empty, reason)
                    { ExpectedWindowIdentitySha256 = identity }, CancellationToken.None)
                    : await Service.RunNowAsync(new(id, version, _commandKey, string.Empty, reason), CancellationToken.None);
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }
            if (Accept(result))
            {
                SetMessage(result.Value?.Status switch
                {
                    LocalAutomationCommandStatus.Unchanged => "This occurrence already has a recorded run.",
                    LocalAutomationCommandStatus.Replayed => "This request was already recorded. See run history for its outcome.",
                    _ => "One attempt is queued. Follow its outcome in run history."
                }, error: false);
                _queued = Backfill || Run is not null;
                if (!_queued)
                {
                    _lastCommand = null;
                    _reason = string.Empty;
                }
            }
            if (result.IsSuccess || result.Kind is OperatorUiResultKind.Conflict or OperatorUiResultKind.NotFound)
            {
                await Changed.InvokeAsync();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private bool Accept<T>(OperatorUiResult<T> result)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            Navigation.NavigateTo("/Account/AccessDenied");
        }
        else if (!result.IsSuccess)
        {
            SetMessage(result.Message ?? "This automation operation is unavailable.", error: true);
        }
        return result.IsSuccess;
    }

    private void SetMessage(string message, bool error)
    {
        _message = message;
        _error = error;
    }

    internal static string PeriodLabel(LocalAutomationSourceWindow window)
        => $"{WindowTime(window.StartUtc, window)} to {WindowTime(window.EndUtc, window)}";

    internal static string WindowTime(DateTimeOffset utc, LocalAutomationSourceWindow window)
    {
        var utcText = "UTC " + utc.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Invariant);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(window.ReportingPeriod.Site.TimeZoneId);
            var rules = SiteTimeZoneRules.GetValue(zone, SiteTimeZone.ComputeRuleSha256);
            if (string.Equals(rules, window.ReportingPeriod.TimeZoneRulesSha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"{TimeZoneInfo.ConvertTime(utc, zone).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", Invariant)} / {utcText}";
            }
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
        }
        return utcText + " (retained local offset unavailable)";
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
