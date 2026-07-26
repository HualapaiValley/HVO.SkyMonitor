using System.Globalization;
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
    private bool _loading = true;

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
                await LoadHistoryAsync(null).ConfigureAwait(false);
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

    private Task OlderAsync() => LoadHistoryAsync(_history?.NextCursor);
    private Task NewestAsync() => LoadHistoryAsync(null);

    private async Task LoadHistoryAsync(string? cursor)
    {
        var result = await EnvironmentalService.GetHistoryAsync(null, 50, cursor, _lifetime.Token);
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
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
