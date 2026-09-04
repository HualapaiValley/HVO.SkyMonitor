using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ProcessingExecutionsPage : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private PeriodicTimer? _timer;
    private Task? _pollTask;
    private CameraAgentProcessingExecutionsView? _view;
    private string? _message;
    private bool _loading = true;
    private bool _polling;

    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;

    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;

    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _lifetime = new CancellationTokenSource();
        await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
        _timer = new PeriodicTimer(RefreshInterval, TimeProvider);
        _pollTask = PollAsync(_lifetime.Token);
    }

    // The timer only re-reads while an execution is pending or running, so an idle journal costs nothing.
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_view?.HasActiveWork == true)
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshNowAsync()
    {
        if (_lifetime is { IsCancellationRequested: false })
        {
            await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loading = true;
            var result = await GraphService.GetExecutionsAsync(CameraAgentProcessingExecutionProjection.MaximumPerClass, cancellationToken).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                    return;
                }
                if (result.IsSuccess && result.Value is not null)
                {
                    _view = result.Value;
                    _message = null;
                }
                else
                {
                    _message = result.Message ?? "Current execution data is unavailable.";
                }
                _polling = _view?.HasActiveWork == true;
                _loading = false;
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string StatusClass(ProcessingGraphExecutionStatus status) => status switch
    {
        ProcessingGraphExecutionStatus.Completed => "state-chip--completed",
        ProcessingGraphExecutionStatus.Running => "state-chip--running",
        ProcessingGraphExecutionStatus.Pending => "state-chip--pending",
        _ => "state-chip--failed"
    };

    internal static string Short(Guid value) => value.ToString("N")[..12];

    internal static string FormatAge(TimeSpan age)
        => age.TotalHours >= 48 ? $"{age.TotalDays:F1} d" : age.TotalMinutes >= 120 ? $"{age.TotalHours:F1} h" : age.TotalSeconds >= 120 ? $"{age.TotalMinutes:F0} min" : $"{Math.Max(0, age.TotalSeconds):F0} s";

    internal static string FormatDuration(TimeSpan? duration)
        => duration is null ? "In progress or not started" : duration.Value.TotalSeconds >= 120 ? $"{duration.Value.TotalMinutes:F1} min" : $"{duration.Value.TotalSeconds:F1} s";

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        _timer?.Dispose();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        // Wait for any in-flight refresh to release the gate before disposing it.
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _lifetime?.Dispose();
        _gate.Dispose();
    }
}
