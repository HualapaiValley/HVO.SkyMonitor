using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Components.Layout;

public sealed partial class MainLayoutFooter : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _clockCancellation;
    private Task? _clockTask;
    private string _localTimeDisplay = "Synchronizing…";
    private string _localTimeTooltip = "Detecting local time zone…";

    [Inject]
    public TimeProvider? InjectableTimeProvider { get; set; }

    [Inject]
    public ILogger<MainLayoutFooter>? Logger { get; set; }

    private TimeProvider TimeProvider => InjectableTimeProvider ?? TimeProvider.System;

    private string LocalTimeDisplay => _localTimeDisplay;

    private string LocalTimeTooltip => _localTimeTooltip;

    protected override void OnInitialized()
    {
        base.OnInitialized();

        UpdateLocalTime();
        StartClock();
    }

    private void StartClock()
    {
        CancelClock();

        _clockCancellation = new CancellationTokenSource();
        _clockTask = RunClockAsync(_clockCancellation.Token);
    }

    private async Task RunClockAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                UpdateLocalTime();
                await InvokeAsync(StateHasChanged);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to update footer clock display.");
            }

            try
            {
                await Task.Delay(ClockInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void UpdateLocalTime()
    {
        try
        {
            var now = TimeProvider.GetLocalNow();
            var timeZone = TimeZoneInfo.Local;
            var localTime = now.LocalDateTime;
            var zoneName = timeZone.IsDaylightSavingTime(localTime)
                ? timeZone.DaylightName
                : timeZone.StandardName;

            _localTimeDisplay = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss} {1}", localTime, zoneName);
            _localTimeTooltip = string.Format(CultureInfo.InvariantCulture, "Local time zone: {0}", timeZone.DisplayName);
        }
        catch (TimeZoneNotFoundException ex)
        {
            Logger?.LogWarning(ex, "Unable to resolve local time zone.");
            _localTimeDisplay = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss} Local", DateTime.Now);
            _localTimeTooltip = "Local time zone unavailable";
        }
        catch (InvalidTimeZoneException ex)
        {
            Logger?.LogWarning(ex, "Invalid local time zone configuration detected.");
            _localTimeDisplay = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss} Local", DateTime.Now);
            _localTimeTooltip = "Local time zone unavailable";
        }
    }

    private void CancelClock()
    {
        if (_clockCancellation is null)
        {
            return;
        }

        try
        {
            if (!_clockCancellation.IsCancellationRequested)
            {
                _clockCancellation.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelClock();

        if (_clockCancellation is null)
        {
            return;
        }

        try
        {
            if (_clockTask is not null)
            {
                await Task.WhenAny(_clockTask, Task.Delay(ClockInterval));
            }
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        finally
        {
            _clockCancellation.Dispose();
            _clockCancellation = null;
        }
    }
}
