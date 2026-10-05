using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class MainLayoutFooter : SiteTimeComponent, IAsyncDisposable
{
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _clockCancellation;
    private Task? _clockTask;
    private DateTimeOffset _clockInstantUtc;
    private string _localTimeDisplay = "Synchronizing…";
    private string _localTimeTooltip = "Resolving site time zone…";

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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Footer clock must continue running even when InvokeAsync throws; exceptions are logged and the loop keeps ticking.")]
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
        _clockInstantUtc = TimeProvider.GetUtcNow().ToUniversalTime();
        _localTimeDisplay = SiteTime.Format(_clockInstantUtc, "HH:mm:ss");
        _localTimeTooltip = $"Observing site time zone: {SiteTime.Label}";
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
