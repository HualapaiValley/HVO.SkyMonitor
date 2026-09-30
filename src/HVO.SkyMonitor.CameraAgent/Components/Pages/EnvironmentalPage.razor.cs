using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class EnvironmentalPage : ComponentBase, IAsyncDisposable
{
    private const string AcquireTriggerId = "environment-acquire";
    private const string FocusFallbackId = "environment-refresh";
    private const int HistoryPageSize = 50;

    /// <summary>
    /// The prototype's eight reading slots. Each names the observation kinds it shows in preference order; a slot
    /// never borrows a value from an unrelated kind, and one with nothing retained says so instead of showing zero.
    /// </summary>
    private static readonly ReadingSlot[] ReadingSlots =
    [
        new("air-temperature", "Air temperature", [EnvironmentalObservationKind.AirTemperature]),
        new("relative-humidity", "Relative humidity", [EnvironmentalObservationKind.RelativeHumidity]),
        new("wind", "Wind", [EnvironmentalObservationKind.WindSpeed]),
        new("cloud", "Cloud estimate", [EnvironmentalObservationKind.CloudCover]),
        new("pressure", "Pressure", [EnvironmentalObservationKind.AtmosphericPressure]),
        new("sky-quality", "Sky quality", [EnvironmentalObservationKind.SkyQuality, EnvironmentalObservationKind.SkyBrightness]),
        new("precipitation", "Precipitation", [EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.PrecipitationRate]),
        new("sensor", "Sensor", [EnvironmentalObservationKind.CameraSensorTemperature]),
    ];

    private readonly CancellationTokenSource _lifetime = new();
    private EnvironmentalUiStatus? _status;
    private IReadOnlyList<EnvironmentalUiObservation>? _latest;
    private EnvironmentalUiHistoryPage? _history;
    private EnvironmentalObservationKind? _historyKind;
    private string? _historyCursor;
    private int _historyGeneration;
    private string? _error;
    private string? _latestError;
    private string? _selectedSourceId;
    private string _reason = string.Empty;
    private string? _commandMessage;
    private string? _commandRefreshWarning;
    private bool _commandMessageIsError;
    private EnvironmentalOnDemandAcquisitionResult? _commandResult;
    private string? _commandKey;
    private string? _commandPayload;
    private bool _pinnedRetry;
    private Task? _commandTask;
    private bool _loading = true;
    private bool _submitting;
    private bool _disposed;
    private bool _dialogOpen;
    private bool _showDialog;
    private string? _focusTargetId;
    private IJSObjectReference? _module;
    private ElementReference _dialogElement;

    [Inject] internal ICameraAgentEnvironmentalUiService EnvironmentalService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    private string? AcquireUnavailableReason => _status switch
    {
        null => "Environmental status is not loaded.",
        { Enabled: false } => "Environmental acquisition is disabled in this agent's configuration.",
        _ when !_status.Sources.Any(static source => source.SupportsOnDemand) =>
            "No configured source supports on-demand acquisition; observations arrive on their schedule.",
        _ => null,
    };

    private string NextPollText
    {
        get
        {
            var next = _status?.Sources.Where(static source => source.NextPollUtc is not null)
                .Min(static source => source.NextPollUtc);
            if (next is null || _status is null)
            {
                return "No scheduled poll";
            }
            var seconds = (next.Value - _status.EvaluatedUtc).TotalSeconds;
            return seconds <= 0 ? "Poll due" : $"Next poll {FormatDuration(seconds)}";
        }
    }

    protected override Task OnInitializedAsync() => LoadAsync();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_showDialog)
        {
            _showDialog = false;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/EnvironmentalPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("showModal", _dialogElement).ConfigureAwait(false);
        }
        else if (_focusTargetId is not null)
        {
            var target = _focusTargetId;
            _focusTargetId = null;
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Pages/EnvironmentalPage.razor.js").ConfigureAwait(false);
            await _module.InvokeVoidAsync("focusById", target, FocusFallbackId).ConfigureAwait(false);
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        _error = null;
        try
        {
            var status = await EnvironmentalService.GetStatusAsync(_lifetime.Token);
            if (_disposed)
            {
                return;
            }
            if (status.Kind == OperatorUiResultKind.Unauthorized)
            {
                DenyAccess();
            }
            else if (!status.IsSuccess || status.Value is null)
            {
                _status = null;
                _error = status.Message ?? "Environmental status is unavailable.";
            }
            else
            {
                _status = status.Value;
                if (await LoadLatestAsync().ConfigureAwait(false))
                {
                    await LoadHistoryAsync(null).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed)
            {
                _loading = false;
            }
        }
    }

    /// <returns>False when access was denied and the page navigated away.</returns>
    private async Task<bool> LoadLatestAsync()
    {
        var latest = await EnvironmentalService.GetLatestReadingsAsync(_lifetime.Token);
        if (_disposed)
        {
            return false;
        }
        if (latest.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
            return false;
        }
        if (latest.IsSuccess && latest.Value is not null)
        {
            _latest = latest.Value;
            _latestError = null;
        }
        else
        {
            _latest = null;
            _latestError = latest.Message ?? "Current environmental readings are unavailable.";
        }
        return true;
    }

    private void OpenAcquireDialog()
    {
        if (_submitting || AcquireUnavailableReason is not null || _status is null)
        {
            return;
        }
        if (_selectedSourceId is null || !_status.Sources.Any(source =>
                source.SupportsOnDemand && source.Id == _selectedSourceId))
        {
            _selectedSourceId = _status.Sources.First(static source => source.SupportsOnDemand).Id;
        }
        _commandMessage = null;
        _commandResult = null;
        _commandRefreshWarning = null;
        _focusTargetId = null;
        _dialogOpen = true;
        _showDialog = true;
    }

    /// <summary>Ignored while the request is in flight so its outcome is always observed.</summary>
    private void CancelDialog()
    {
        if (_submitting)
        {
            return;
        }
        CloseDialog();
        _commandMessage = null;
        _commandKey = null;
        _commandPayload = null;
        _pinnedRetry = false;
    }

    private void CloseDialog()
    {
        if (_dialogOpen)
        {
            _focusTargetId = AcquireTriggerId;
        }
        _dialogOpen = false;
        _showDialog = false;
    }

    private void DismissResult()
    {
        _commandMessage = null;
        _commandResult = null;
        _commandRefreshWarning = null;
    }

    private async Task SubmitAsync()
    {
        if (_submitting || _disposed)
        {
            return;
        }
        var reason = _reason.Trim();
        if (_selectedSourceId is null || reason.Length is < 1 or > 128 || reason.Any(char.IsControl))
        {
            _commandResult = null;
            _commandRefreshWarning = null;
            _commandMessageIsError = true;
            _commandMessage = "Select an on-demand source and enter a reason of 1 to 128 characters.";
            return;
        }
        var payload = string.Concat(_selectedSourceId, "\n", reason);
        if (!string.Equals(payload, _commandPayload, StringComparison.Ordinal) || _commandKey is null)
        {
            _commandPayload = payload;
            _commandKey = $"ui-{Guid.NewGuid():N}";
        }
        _submitting = true;
        _commandResult = null;
        _commandRefreshWarning = null;
        _commandMessageIsError = false;
        _commandMessage = "Requesting an environmental observation.";
        _commandTask = RunCommandAsync(_selectedSourceId, _commandKey, reason);
        try
        {
            await _commandTask;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed)
            {
                _submitting = false;
            }
        }
    }

    private async Task RunCommandAsync(string sourceId, string idempotencyKey, string reason)
    {
        var result = await EnvironmentalService.AcquireAsync(
            sourceId, idempotencyKey, reason, CancellationToken.None);
        if (_disposed)
        {
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
            return;
        }
        if (!result.IsSuccess || result.Value is null)
        {
            _commandMessageIsError = true;
            _commandMessage = result.Message ?? "The environmental request could not be completed.";
            // Only an unavailable outcome leaves the durable result unknown; the same key must then be retried
            // unchanged. Any other failure was a definite rejection, so the operator may edit and resubmit.
            _pinnedRetry = result.Kind == OperatorUiResultKind.Unavailable;
            if (!_pinnedRetry)
            {
                _commandKey = null;
                _commandPayload = null;
            }
            return;
        }
        _commandResult = result.Value;
        _commandMessageIsError = result.Value.Receipt.Disposition is
            EnvironmentalAcquisitionDisposition.Missing or
            EnvironmentalAcquisitionDisposition.Failed or
            EnvironmentalAcquisitionDisposition.TimedOut;
        _commandMessage = result.Value.Replayed
            ? "The existing idempotent receipt was returned."
            : result.Value.Receipt.Disposition == EnvironmentalAcquisitionDisposition.Produced
                ? "The environmental observation was committed."
                : "A durable receipt was recorded without a new observation.";
        _commandKey = null;
        _commandPayload = null;
        _pinnedRetry = false;
        _reason = string.Empty;
        CloseDialog();
        await RefreshAfterCommandAsync();
    }

    private async Task RefreshAfterCommandAsync()
    {
        var status = await EnvironmentalService.GetStatusAsync(_lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (status.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
            return;
        }
        if (status.IsSuccess && status.Value is not null)
        {
            _status = status.Value;
        }
        else
        {
            _commandRefreshWarning = "The receipt is durable, but environmental status could not be refreshed.";
        }
        if (!await LoadLatestAsync().ConfigureAwait(false))
        {
            return;
        }
        var generation = ++_historyGeneration;
        var history = await EnvironmentalService.GetHistoryAsync(_historyKind, HistoryPageSize, null, _lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (history.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
        }
        else if (generation != _historyGeneration)
        {
            return;
        }
        else if (history.IsSuccess && history.Value is not null)
        {
            _history = history.Value;
            _historyCursor = null;
        }
        else
        {
            _commandRefreshWarning = "The receipt is durable, but environmental history could not be refreshed.";
        }
    }

    private Task OlderAsync() => LoadHistoryAsync(_history?.NextCursor);
    private Task NewestAsync() => LoadHistoryAsync(null);

    private async Task HistoryKindChanged(ChangeEventArgs args)
    {
        _historyKind = Enum.TryParse<EnvironmentalObservationKind>(args.Value?.ToString(), out var kind)
            && Enum.IsDefined(kind)
                ? kind
                : null;
        await LoadHistoryAsync(null);
    }

    /// <summary>
    /// Reads one history page. Only the most recent request may assign the page and cursor, so a slower read for a
    /// former kind or page cannot replace the one the operator asked for last.
    /// </summary>
    private async Task LoadHistoryAsync(string? cursor)
    {
        var generation = ++_historyGeneration;
        var result = await EnvironmentalService.GetHistoryAsync(_historyKind, HistoryPageSize, cursor, _lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            DenyAccess();
        }
        else if (generation != _historyGeneration)
        {
            return;
        }
        else if (result.IsSuccess && result.Value is not null)
        {
            _history = result.Value;
            _historyCursor = cursor;
        }
        else
        {
            _error = result.Message ?? "Environmental history is unavailable.";
        }
    }

    private void DenyAccess()
    {
        _status = null;
        _latest = null;
        _history = null;
        _commandResult = null;
        _commandMessage = null;
        _dialogOpen = false;
        _showDialog = false;
        NavigationManager.NavigateTo("/Account/AccessDenied");
    }

    private ReadingView Reading(ReadingSlot slot)
    {
        var observation = slot.Kinds
            .Select(kind => _latest?.FirstOrDefault(item => item.Kind == kind))
            .FirstOrDefault(static item => item is not null);
        if (observation is null)
        {
            var configured = _status?.Sources.Any(source => slot.Kinds.Contains(source.Kind)) == true;
            return new(null, "--", configured ? "No observation yet" : "No source configured", null);
        }
        var evaluated = _status?.EvaluatedUtc ?? observation.ObservedAtUtc;
        var freshness = evaluated > observation.StaleAfterUtc ? "Stale" : "Fresh";
        var age = Math.Max(0, (evaluated - observation.ObservedAtUtc).TotalSeconds);
        var state = $"{freshness} / {FormatDuration(age)} ago";
        if (observation.SourceKind != EnvironmentalObservationSourceKind.Measured)
        {
            state += $" / {OperationsPage.SplitWords(observation.SourceKind.ToString())}";
        }
        if (observation.Quality != EnvironmentalObservationQuality.Good)
        {
            state += $" / {observation.Quality} quality";
        }
        return new(observation, FormatReading(observation), state, ReadingDetail(slot, observation));
    }

    private string? ReadingDetail(ReadingSlot slot, EnvironmentalUiObservation observation)
    {
        if (slot.Key == "wind")
        {
            var parts = new List<string>(2);
            if (Latest(EnvironmentalObservationKind.WindGust) is { NumericValue: { } gust })
            {
                parts.Add(FormattableString.Invariant($"Gust {gust:0.#} m/s"));
            }
            if (Latest(EnvironmentalObservationKind.WindDirection) is { NumericValue: { } direction })
            {
                parts.Add(FormattableString.Invariant($"From {direction:0} deg"));
            }
            return parts.Count == 0 ? null : string.Join(" / ", parts);
        }
        return observation.Kind switch
        {
            EnvironmentalObservationKind.SkyQuality => SkyBrightnessUnit,
            EnvironmentalObservationKind.SkyBrightness => "Sky brightness, " + SkyBrightnessUnit,
            EnvironmentalObservationKind.PrecipitationRate => "Rate",
            _ => null,
        };
    }

    private EnvironmentalUiObservation? Latest(EnvironmentalObservationKind kind)
        => _latest?.FirstOrDefault(item => item.Kind == kind);

    private const string SkyBrightnessUnit = "mag/arcsec²";

    private static string FormatReading(EnvironmentalUiObservation item)
    {
        if (item.BooleanValue is { } flag)
        {
            return item.Kind == EnvironmentalObservationKind.RainState ? flag ? "Rain" : "None" : flag ? "Yes" : "No";
        }
        if (item.NumericValue is not { } value)
        {
            return "--";
        }
        return item.Unit switch
        {
            EnvironmentalObservationUnit.DegreesCelsius => FormattableString.Invariant($"{value:0.0} C"),
            EnvironmentalObservationUnit.Percent => FormattableString.Invariant($"{value:0}%"),
            EnvironmentalObservationUnit.Fraction => FormattableString.Invariant($"{value * 100:0}%"),
            EnvironmentalObservationUnit.Pascals => FormattableString.Invariant($"{value / 100:0} hPa"),
            EnvironmentalObservationUnit.MetersPerSecond => FormattableString.Invariant($"{value:0.0} m/s"),
            EnvironmentalObservationUnit.MillimetersPerHour => FormattableString.Invariant($"{value:0.0} mm/h"),
            EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond => FormattableString.Invariant($"{value:0.0}"),
            EnvironmentalObservationUnit.DegreesTrue => FormattableString.Invariant($"{value:0} deg"),
            _ => FormattableString.Invariant($"{value:0.###}"),
        };
    }

    private static string TriggerText(EnvironmentalUiSource source)
        => source.Triggers.Count == 0
            ? "Not scheduled"
            : string.Join(", ", source.Triggers.Select(trigger => trigger switch
            {
                EnvironmentalAcquisitionTrigger.Periodic => FormattableString.Invariant($"Every {source.PeriodSeconds} seconds"),
                EnvironmentalAcquisitionTrigger.BeforeCapture => "Before each capture",
                EnvironmentalAcquisitionTrigger.AfterCapture => "After each capture",
                EnvironmentalAcquisitionTrigger.EveryNthCapture => source.EveryNthCapture <= 1
                    ? "Every capture"
                    : FormattableString.Invariant($"Every {source.EveryNthCapture} captures"),
                EnvironmentalAcquisitionTrigger.RegimeChange => "On regime change",
                EnvironmentalAcquisitionTrigger.OnDemand => "On demand",
                _ => OperationsPage.SplitWords(trigger.ToString()),
            }));

    internal const string OutboxNotReadText = "Not read yet";

    private static string OldestPendingText(EnvironmentalUiDelivery delivery)
        => delivery.OldestPendingUtc is { } oldest ? FormatUtc(oldest)
            : delivery.ExportEnabled && delivery.PendingCount is null ? OutboxNotReadText
            : "None";

    private static string PendingText(EnvironmentalUiDelivery delivery)
    {
        if (delivery.PendingCount is not { } pending)
        {
            return OutboxNotReadText;
        }
        var text = pending.ToString("N0", CultureInfo.InvariantCulture);
        if (delivery.RetryCount > 0)
        {
            text += FormattableString.Invariant($" ({delivery.RetryCount:N0} retrying)");
        }
        if (delivery.QuarantineCount > 0)
        {
            text += FormattableString.Invariant($", {delivery.QuarantineCount:N0} quarantined");
        }
        return text;
    }

    private static string FreshnessTone(string freshness) => freshness switch
    {
        "Fresh" => "success",
        "Stale" => "warning",
        _ => "pending",
    };

    private static string DispositionTone(EnvironmentalAcquisitionDisposition disposition) => disposition switch
    {
        EnvironmentalAcquisitionDisposition.Produced => "success",
        EnvironmentalAcquisitionDisposition.Duplicate or EnvironmentalAcquisitionDisposition.Coalesced => "pending",
        EnvironmentalAcquisitionDisposition.Missing => "warning",
        _ => "failure",
    };

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatAge(double? seconds)
        => seconds is null ? "Never" : FormatDuration(seconds.Value);

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalSeconds < 90
            ? FormattableString.Invariant($"{Math.Round(span.TotalSeconds):0}s")
            : span.TotalMinutes < 90
                ? FormattableString.Invariant($"{Math.Round(span.TotalMinutes):0}m")
                : span.TotalHours < 48
                    ? FormattableString.Invariant($"{Math.Round(span.TotalHours):0}h")
                    : FormattableString.Invariant($"{Math.Round(span.TotalDays):0}d");
    }

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

    private sealed record ReadingSlot(string Key, string Label, EnvironmentalObservationKind[] Kinds);

    private sealed record ReadingView(EnvironmentalUiObservation? Observation, string Value, string State, string? Detail);
}
