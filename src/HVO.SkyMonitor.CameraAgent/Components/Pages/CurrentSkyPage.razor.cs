using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CurrentSkyPage : ComponentBase, IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private PeriodicTimer? _timer;
    private Task? _pollTask;
    private CameraAgentCurrentImagePresentation? _presentation;
    private CameraAgentPresentationStage? _selectedStage;
    private string? _errorMessage;
    private bool _initialLoading = true;
    private bool _refreshing;
    private bool _viewerOpen;
    private int _refreshRequested;
    private int _disposeStarted;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _lifetime = new CancellationTokenSource();
        await RequestRefreshAsync(_lifetime.Token);
        _timer = new PeriodicTimer(RefreshInterval, TimeProvider);
        _pollTask = PollAsync(_lifetime.Token);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RequestRefreshAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _refreshRequested, 1);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshTimeout);
        await InvokeAsync(() => _refreshing = true);
        try
        {
            var result = await OperatorService.GetCurrentImagePresentationAsync(timeout.Token).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                await InvokeAsync(() => NavigationManager.NavigateTo("/Account/AccessDenied")).ConfigureAwait(false);
                return;
            }
            await InvokeAsync(() =>
            {
                if (result.IsSuccess && result.Value is not null)
                {
                    _presentation = result.Value;
                    _selectedStage = ResolveSelection(result.Value, _selectedStage);
                    if (_selectedStage is null)
                    {
                        _viewerOpen = false;
                    }
                    _errorMessage = null;
                }
                else
                {
                    _errorMessage = result.Message ?? "The current image projection is unavailable.";
                }
                _initialLoading = false;
                _refreshing = false;
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await InvokeAsync(() =>
            {
                _errorMessage = "The current image refresh exceeded its five-second deadline.";
                _initialLoading = false;
                _refreshing = false;
                StateHasChanged();
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshNowAsync()
    {
        if (_lifetime is not null)
        {
            await RequestRefreshAsync(_lifetime.Token);
        }
    }

    private void SelectStage(CameraAgentPresentationStage stage)
    {
        if (_presentation?.Stages.Any(slot =>
                slot.Stage == stage && slot.Availability == CameraAgentPresentationSlotAvailability.Available) == true)
        {
            _selectedStage = stage;
            _viewerOpen = false;
        }
    }

    private void OpenViewer() => _viewerOpen = SelectedSlot is not null;

    private CameraAgentPresentationSlot? SelectedSlot => _presentation?.Stages.SingleOrDefault(slot =>
        slot.Stage == _selectedStage && slot.Availability == CameraAgentPresentationSlotAvailability.Available);

    private CameraAgentPresentationCapture? FactCapture =>
        _presentation?.DisplayCapture ?? _presentation?.LatestCapture;

    private string DetailUrl => FactCapture is { } capture
        ? $"/gallery/{capture.CaptureId:D}"
        : "/gallery";

    private string ViewerTitle => SelectedSlot is null ? "Large sky image" : $"{SelectedSlot.Label} sky image";

    private string ImageAlt => _presentation?.DisplayCapture is { } capture && SelectedSlot is { } slot
        ? $"{slot.Label} sky capture from {capture.ExposureStartedUtc.ToLocalTime():g}"
        : "Current sky capture";

    private string FreshnessLabel => _presentation?.ImageFreshness switch
    {
        CameraAgentPresentationImageFreshness.Current => "Image current",
        CameraAgentPresentationImageFreshness.Delayed => "Image delayed",
        CameraAgentPresentationImageFreshness.Stale => "Image stale",
        CameraAgentPresentationImageFreshness.Historical => "Historical image",
        _ => "No image"
    };

    private string FreshnessClass => _presentation?.ImageFreshness switch
    {
        CameraAgentPresentationImageFreshness.Current => "status-chip--current",
        CameraAgentPresentationImageFreshness.Delayed or CameraAgentPresentationImageFreshness.Historical =>
            "status-chip--warning",
        CameraAgentPresentationImageFreshness.Stale => "status-chip--danger",
        _ => "status-chip--neutral"
    };

    private string SystemLabel => _presentation?.System.State switch
    {
        CameraAgentPresentationSystemState.Capturing => "Capturing",
        CameraAgentPresentationSystemState.Standby => "Standby",
        CameraAgentPresentationSystemState.Paused => "Paused",
        CameraAgentPresentationSystemState.Unavailable => "Camera unavailable",
        _ => "Starting"
    };

    private string SystemClass => _presentation?.System.State switch
    {
        CameraAgentPresentationSystemState.Capturing => "status-chip--current",
        CameraAgentPresentationSystemState.Standby or CameraAgentPresentationSystemState.Paused =>
            "status-chip--warning",
        CameraAgentPresentationSystemState.Unavailable => "status-chip--danger",
        _ => "status-chip--neutral"
    };

    private string SummaryEyebrow => _presentation?.ImageFreshness switch
    {
        CameraAgentPresentationImageFreshness.Delayed => "Next image delayed",
        CameraAgentPresentationImageFreshness.Stale => "Last image is stale",
        CameraAgentPresentationImageFreshness.Historical => "Last valid retained image",
        _ when _presentation?.System.State == CameraAgentPresentationSystemState.Standby => "Scheduled standby",
        _ => "Durable current view"
    };

    private string SummaryHeading => _presentation?.ImageFreshness switch
    {
        CameraAgentPresentationImageFreshness.Empty => "No sky image yet",
        CameraAgentPresentationImageFreshness.Delayed => "Last valid sky",
        CameraAgentPresentationImageFreshness.Stale => "Last retained sky",
        CameraAgentPresentationImageFreshness.Historical => "Historical sky",
        _ => "Latest sky"
    };

    private string SummaryMessage => _presentation is null
        ? "Loading current image and system state."
        : _presentation.DisplayCapture is null
            ? "No displayable durable image was found. CameraAgent state remains available independently."
            : _presentation.IsHistoricalFallback
                ? "A newer capture exists, but this is the latest retained capture that can be displayed safely."
                : _presentation.System.Message;

    private string StageStatus => SelectedSlot is { } selected
        ? $"Showing {selected.Label}."
        : "No image stage is currently displayable.";

    private int UnavailableStageCount => _presentation?.Stages.Count(static slot =>
        slot.Availability != CameraAgentPresentationSlotAvailability.Available) ?? 0;

    private static CameraAgentPresentationStage? ResolveSelection(
        CameraAgentCurrentImagePresentation presentation,
        CameraAgentPresentationStage? current)
    {
        if (current is not null && presentation.Stages.Any(slot =>
                slot.Stage == current && slot.Availability == CameraAgentPresentationSlotAvailability.Available))
        {
            return current;
        }
        return presentation.SelectedStage;
    }

    private static string FormatDuration(long seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d {duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    private static string EvidenceLabel(GalleryEvidenceOrigin origin) => origin switch
    {
        GalleryEvidenceOrigin.Simulated => "Simulated",
        GalleryEvidenceOrigin.DeveloperFixture => "Developer fixture",
        _ => "Unclassified"
    };

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null || Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
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
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }
}
