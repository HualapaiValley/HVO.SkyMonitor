using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class EnvironmentalPage : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private EnvironmentalUiStatus? _status;
    private EnvironmentalUiHistoryPage? _history;
    private string? _historyCursor;
    private string? _error;
    private string? _selectedSourceId;
    private string _reason = string.Empty;
    private string? _commandMessage;
    private string? _commandRefreshWarning;
    private bool _commandMessageIsError;
    private EnvironmentalOnDemandAcquisitionResult? _commandResult;
    private string? _commandKey;
    private string? _commandPayload;
    private Task? _commandTask;
    private bool _loading = true;
    private bool _submitting;
    private bool _disposed;

    [Inject] internal ICameraAgentEnvironmentalUiService EnvironmentalService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override Task OnInitializedAsync() => LoadAsync();

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
                _status = null;
                _history = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (!status.IsSuccess || status.Value is null)
            {
                _error = status.Message ?? "Environmental status is unavailable.";
            }
            else
            {
                _status = status.Value;
                if (_selectedSourceId is null || !_status.Sources.Any(source =>
                        source.SupportsOnDemand && source.Id == _selectedSourceId))
                {
                    _selectedSourceId = _status.Sources.FirstOrDefault(static source => source.SupportsOnDemand)?.Id;
                }
                await LoadHistoryAsync(null).ConfigureAwait(false);
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
            _status = null;
            _history = null;
            _commandResult = null;
            _commandMessage = null;
            NavigationManager.NavigateTo("/Account/AccessDenied");
            return;
        }
        if (!result.IsSuccess || result.Value is null)
        {
            _commandMessageIsError = true;
            _commandMessage = result.Message ?? "The environmental request could not be completed.";
            if (result.Kind != OperatorUiResultKind.Unavailable)
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
            _status = null;
            _history = null;
            NavigationManager.NavigateTo("/Account/AccessDenied");
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
        var history = await EnvironmentalService.GetHistoryAsync(null, 50, null, _lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (history.Kind == OperatorUiResultKind.Unauthorized)
        {
            _status = null;
            _history = null;
            NavigationManager.NavigateTo("/Account/AccessDenied");
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

    private async Task LoadHistoryAsync(string? cursor)
    {
        var result = await EnvironmentalService.GetHistoryAsync(null, 50, cursor, _lifetime.Token);
        if (_disposed)
        {
            return;
        }
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            _status = null;
            _history = null;
            NavigationManager.NavigateTo("/Account/AccessDenied");
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

    private static string FormatTime(DateTimeOffset? value)
        => value?.ToString("u", CultureInfo.InvariantCulture) ?? "Not scheduled";

    private static string FormatAge(double? seconds)
        => seconds is null ? "Never observed" : FormattableString.Invariant($"{TimeSpan.FromSeconds(seconds.Value):g}");

    private static string FormatValue(EnvironmentalUiObservation item)
        => item.BooleanValue?.ToString() ?? FormattableString.Invariant($"{item.NumericValue:0.###} {OperationsPage.SplitWords(item.Unit.ToString())}");

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
