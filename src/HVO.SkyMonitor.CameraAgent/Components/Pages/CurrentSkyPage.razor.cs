using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

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
    private CameraAgentCurrentSkyFacts? _facts;
    private CameraAgentLayeredPresentation? _layers;
    private CancellationTokenSource? _layerCancellation;
    private IJSObjectReference? _layerModule;
    private ElementReference _layerRoot;
    private string? _layerMessage;
    private string? _saveMessage;
    private string? _saveError;
    private string? _savedArtifactUrl;
    private HashSet<string> _selectedLayers = new(StringComparer.Ordinal);
    private bool _layerInteractive;
    private bool _layerImageFailed;
    private int _layerPreviewAttempt;
    private bool _layerLoading;
    private bool _accessDenied;
    private long _layerGeneration;
    private long _bindGeneration;
    private bool _bindLayers;
    private bool _saving;
    private string? _factsUnavailableReason;
    private CameraAgentPresentationStage? _selectedStage;
    private string? _errorMessage;
    private bool _initialLoading = true;
    private bool _refreshing;
    private bool _viewerOpen;
    private Guid? _liveExecutionId;
    private int _refreshRequested;
    private int _disposeStarted;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal ICameraAgentProcessingGraphUiService GraphService { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_bindLayers || !ShowLayeredHero) return;
        _bindLayers = false;
        var generation = _layerGeneration;
        var bindGeneration = _bindGeneration;
        var captureId = _layers!.CaptureId;
        try
        {
            var module = _layerModule ?? await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CurrentSkyPage.razor.js");
            if (!IsCurrentBind(generation, bindGeneration, captureId)) return;
            _layerModule = module;
            var verification = await module.InvokeAsync<string>("bindLayerToggles", _layerRoot, _layers.WidthPixels, _layers.HeightPixels);
            if (!IsCurrentBind(generation, bindGeneration, captureId)) return;
            if (verification == "valid")
            {
                _layerInteractive = true;
                _layerMessage = null;
            }
            else
            {
                LayerImageFailed(verification == "mismatch"
                    ? "The layered preview dimensions do not match the overlay. Showing the standard image instead. Refresh image to retry."
                    : "The layered preview could not be verified. Showing the standard image instead. Refresh image to retry.");
            }
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or OperationCanceledException)
        {
            if (!IsCurrentBind(generation, bindGeneration, captureId)) return;
            LayerImageFailed("The layered preview could not be verified. Showing the standard image instead. Refresh image to retry.");
            await InvokeAsync(StateHasChanged);
        }
    }

    private bool IsCurrentBind(long generation, long bindGeneration, Guid captureId) =>
        generation == _layerGeneration && bindGeneration == _bindGeneration &&
        _disposeStarted == 0 && _layers?.CaptureId == captureId &&
        _layerCancellation?.IsCancellationRequested == false && ShowLayeredHero;

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
        if (_accessDenied || _disposeStarted != 0) return;
        Interlocked.Exchange(ref _refreshRequested, 1);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!_accessDenied && Interlocked.Exchange(ref _refreshRequested, 0) != 0)
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
            var result = await OperatorService.GetCurrentSkyViewAsync(timeout.Token).ConfigureAwait(false);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _accessDenied = true;
                await InvokeAsync(() => NavigationManager.NavigateTo("/Account/AccessDenied")).ConfigureAwait(false);
                return;
            }
            var displayCaptureId = result.IsSuccess ? result.Value?.Presentation.DisplayCapture?.CaptureId : null;
            await InvokeAsync(() =>
            {
                if (result.IsSuccess && result.Value is not null)
                {
                    _presentation = result.Value.Presentation;
                    _facts = result.Value.Facts;
                    if (_runCaptureId != displayCaptureId)
                    {
                        _liveExecutionId = null;
                        _runCaptureId = displayCaptureId;
                        ResetLayers(displayCaptureId, cancellationToken);
                    }
                    _factsUnavailableReason = result.Value.FactsUnavailableReason;
                    _selectedStage = ResolveSelection(result.Value.Presentation, _selectedStage);
                    if (_selectedStage is null || ShowLayeredHero || ProcessedBaseFallback)
                    {
                        _viewerOpen = false;
                    }
                    TryLoadLayers(displayCaptureId);
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
            if (displayCaptureId is { } captureId)
            {
                // The image is already visible. A slow optional run-link lookup cannot make
                // the five-second current-sky refresh appear to have failed.
                _ = LoadLiveRunLinkAsync(captureId, cancellationToken);
            }
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

    private Guid? _runCaptureId;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The optional read only uses the captured cancellation token; its generation guard discards late results.")]
    private void ResetLayers(Guid? captureId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _layerGeneration);
        _bindGeneration++;
        var previous = _layerCancellation;
        _layerCancellation = null;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
        _layers = null;
        _layerMessage = null;
        _saveMessage = null;
        _saveError = null;
        _savedArtifactUrl = null;
        _selectedLayers = new(StringComparer.Ordinal);
        _layerInteractive = false;
        _layerImageFailed = false;
        _layerPreviewAttempt = 0;
        _layerLoading = false;
        _saving = false;
        _bindLayers = false;
        if (captureId is { } id)
        {
            var next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _layerCancellation = next;
        }
    }

    private void TryLoadLayers(Guid? captureId)
    {
        if (captureId is not { } id || _layers is not null || _layerLoading || _accessDenied ||
            _layerCancellation is not { IsCancellationRequested: false } cancellation) return;
        _layerLoading = true;
        _ = LoadLayersAsync(id, Volatile.Read(ref _layerGeneration), cancellation.Token);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional layers must never break current-image refresh.")]
    private async Task LoadLayersAsync(Guid captureId, long generation, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await OperatorService.GetLayeredPresentationAsync(captureId, timeout.Token).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _layerGeneration) || _runCaptureId != captureId || cancellation.IsCancellationRequested || _accessDenied) return;
                if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    _accessDenied = true;
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                }
                else if (result.IsSuccess && result.Value is { } value && value.CaptureId == captureId)
                {
                    _layers = value;
                    _layerMessage = ProcessedBaseSlot?.ArtifactId != value.BaseArtifactId
                        ? "The layered base does not match the available Combined stage. Showing the unannotated base instead when available."
                        : null;
                    _selectedLayers = value.Layers.Where(static layer => layer.EnabledByDefault)
                        .Select(static layer => layer.IdentitySha256).ToHashSet(StringComparer.Ordinal);
                    _viewerOpen = false;
                    _bindLayers = true;
                    StateHasChanged();
                }
                else
                {
                    _layerMessage = result.Message ?? "Structured layers are unavailable for this capture.";
                    StateHasChanged();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer capture, disposal, or the optional deadline superseded this read.
        }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _layerGeneration) || cancellation.IsCancellationRequested || _accessDenied) return;
                _layerMessage = "Structured layers are temporarily unavailable.";
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            await InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _layerGeneration) || cancellation.IsCancellationRequested) return;
                _layerLoading = false;
                StateHasChanged();
            }).ConfigureAwait(false);
        }
    }

    private MarkupString LayerSvg => new(_layers is null ? string.Empty : Encoding.UTF8.GetString(_layers.Svg.Span));

    private string LayerAspectRatio => ((double)_layers!.WidthPixels / _layers.HeightPixels)
        .ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    private bool ShowLayeredHero => !_layerImageFailed && _selectedStage == CameraAgentPresentationStage.Annotated &&
        _layers is not null &&
        _presentation?.DisplayCapture?.CaptureId == _layers.CaptureId &&
        SelectedSlot is not null;

    private bool ProcessedBaseFallback => _selectedStage == CameraAgentPresentationStage.Annotated &&
        _presentation?.StructuredLayersAvailable == true;

    private CameraAgentPresentationSlot? ProcessedBaseSlot => _presentation?.Stages.SingleOrDefault(slot =>
        slot.Stage == CameraAgentPresentationStage.Combined &&
        slot.Availability == CameraAgentPresentationSlotAvailability.Available && slot.PreviewUrl is not null);

    private CameraAgentPresentationSlot? DisplaySlot => ProcessedBaseFallback && !ShowLayeredHero
        ? ProcessedBaseSlot : SelectedSlot;

    private string? SavedArtifactUrl => _savedArtifactUrl;

    private void LayerImageFailed(string message)
    {
        _layerInteractive = false;
        _layerImageFailed = true;
        _layerMessage = message.Replace("Showing the standard image instead", "Showing the unannotated base instead", StringComparison.Ordinal);
    }

    private void SelectLayer(string identity, bool enabled)
    {
        if (enabled) _selectedLayers.Add(identity);
        else _selectedLayers.Remove(identity);
    }

    private static string LayerLabel(string kind) => kind switch
    {
        "scene-annotation" or "star-annotations" => "Star annotations",
        "scene-cardinals" or "cardinal-directions" => "Cardinal directions",
        "scene-image-circle" or "image-circle" => "Image circle",
        "scene-constellations" or "constellations" => "Constellations",
        "environment" or "corner-annotations" => "Corner annotations",
        _ => OperationsPage.SplitWords(kind)
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Service errors must be sanitized for the optional save UI.")]
    private async Task SaveSelectedStackAsync()
    {
        if (!ShowLayeredHero || !_layerInteractive || _layers is null || _saving || _layerCancellation is not { } cancellation) return;
        var generation = Volatile.Read(ref _layerGeneration);
        var captureId = _layers.CaptureId;
        _saving = true;
        _saveError = null;
        _saveMessage = null;
        _savedArtifactUrl = null;
        try
        {
            var selected = _layers.Layers.Where(layer => _selectedLayers.Contains(layer.IdentitySha256))
                .Select(static layer => layer.IdentitySha256).ToArray();
            if (!IsCurrentLayer(generation, captureId, cancellation)) return;
            var result = await OperatorService.SaveLayeredPresentationAsync(captureId, selected, cancellation.Token);
            if (!IsCurrentLayer(generation, captureId, cancellation)) return;
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _accessDenied = true;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is { } receipt)
            {
                _saveMessage = receipt.Replayed ? "This exact flattened stack was already saved." : "Flattened stack saved as a new immutable artifact.";
                _savedArtifactUrl = $"/api/v1/operations/artifacts/{receipt.ArtifactId:D}/content";
            }
            else
                _saveError = result.Message ?? "The presentation stack could not be saved.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentLayer(generation, captureId, cancellation))
                _saveError = "The presentation stack could not be saved. Please try again.";
        }
        catch (Exception)
        {
            if (IsCurrentLayer(generation, captureId, cancellation))
                _saveError = "The presentation stack could not be saved. Please try again.";
        }
        finally
        {
            if (IsCurrentLayer(generation, captureId, cancellation)) _saving = false;
        }
    }

    private bool IsCurrentLayer(long generation, Guid captureId, CancellationTokenSource cancellation) =>
        generation == Volatile.Read(ref _layerGeneration) && _runCaptureId == captureId &&
        ReferenceEquals(cancellation, _layerCancellation) && !cancellation.IsCancellationRequested;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This optional link read must not fail the current sky image.")]
    private async Task LoadLiveRunLinkAsync(Guid captureId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var run = await GraphService.GetLiveExecutionIdAsync(captureId, timeout.Token).ConfigureAwait(false);
            if (run.IsSuccess && _runCaptureId == captureId)
            {
                await InvokeAsync(() => { _liveExecutionId = run.Value?.ExecutionId; StateHasChanged(); }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Keep the previously rendered image even when the graph journal is unavailable.
        }
    }

    private async Task RefreshNowAsync()
    {
        if (_lifetime is not null)
        {
            await RequestRefreshAsync(_lifetime.Token);
            await InvokeAsync(() =>
            {
                if (!_layerImageFailed || _errorMessage is not null || _accessDenied || _layers?.CaptureId != _runCaptureId) return;
                _layerPreviewAttempt++;
                _bindGeneration++;
                _layerImageFailed = false;
                _layerInteractive = false;
                _layerMessage = null;
                _bindLayers = true;
                StateHasChanged();
            });
        }
    }

    private void SelectStage(CameraAgentPresentationStage stage)
    {
        if (_presentation?.Stages.Any(slot =>
                slot.Stage == stage && slot.Availability == CameraAgentPresentationSlotAvailability.Available) == true)
        {
            _selectedStage = stage;
            _viewerOpen = false;
            _bindGeneration++;
            _layerInteractive = false;
            if (stage == CameraAgentPresentationStage.Annotated && _layers is not null) _bindLayers = true;
        }
    }

    private void OpenViewer() => _viewerOpen = DisplaySlot is not null && !ShowLayeredHero;

    private CameraAgentPresentationSlot? SelectedSlot => _presentation?.Stages.SingleOrDefault(slot =>
        slot.Stage == _selectedStage && slot.Availability == CameraAgentPresentationSlotAvailability.Available);

    private CameraAgentPresentationCapture? FactCapture =>
        _presentation?.DisplayCapture ?? _presentation?.LatestCapture;

    private string DetailUrl => FactCapture is { } capture
        ? $"/gallery/{capture.CaptureId:D}"
        : "/gallery";

    private string ViewerTitle => DisplaySlot is null ? "Large sky image" : $"{DisplaySlot.Label} sky image";

    private string ImageAlt => _presentation?.DisplayCapture is { } capture && DisplaySlot is { } slot
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
        CameraAgentPresentationImageFreshness.Current => "hvo-chip--success",
        CameraAgentPresentationImageFreshness.Delayed or CameraAgentPresentationImageFreshness.Historical =>
            "hvo-chip--warning",
        CameraAgentPresentationImageFreshness.Stale => "hvo-chip--danger",
        _ => "hvo-chip--neutral"
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
        CameraAgentPresentationSystemState.Capturing => "hvo-chip--success",
        CameraAgentPresentationSystemState.Standby or CameraAgentPresentationSystemState.Paused =>
            "hvo-chip--warning",
        CameraAgentPresentationSystemState.Unavailable => "hvo-chip--danger",
        _ => "hvo-chip--neutral"
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

    private string StageStatus => ShowLayeredHero && _layerInteractive
        ? "Showing processed base image with selected presentation overlays; not the separate processed artifact."
        : ShowLayeredHero
        ? "Showing unannotated Combined base while processed layers are pending verification. Layer toggles do not affect this image yet."
        : ProcessedBaseFallback && ProcessedBaseSlot is null
        ? "Processed layers are pending or unavailable; the unannotated Combined base is unavailable. No processed image is displayed."
        : ProcessedBaseFallback
        ? $"Showing unannotated Combined base; processed layers are {(_layerLoading ? "pending" : "unavailable")}. Layer toggles do not affect this image."
        : SelectedSlot is { } selected
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

    private string ObservingNightLabel => _facts is { } facts
        ? facts.ObservingDay.TimeZoneFallback
            ? $"{facts.ObservingDay.Date:yyyy-MM-dd} (UTC day; no deployment time zone)"
            : $"{facts.ObservingDay.Date:yyyy-MM-dd} ({facts.ObservingDay.TimeZoneId})"
        : "Unavailable";

    private static string FormatExposure(double? milliseconds) => milliseconds switch
    {
        null => "Unavailable",
        >= 1000 => FormattableString.Invariant($"{milliseconds.Value / 1000d:0.###} s"),
        _ => FormattableString.Invariant($"{milliseconds.Value:0.#} ms")
    };

    private static string FormatCloud(CameraAgentCurrentSkyCloudFacts cloud)
    {
        if (cloud.Status is null)
        {
            return cloud.Availability == "Unavailable" ? "Not assessed" : cloud.Availability;
        }
        var coverage = cloud.CoverageMillionths is { } millionths
            ? FormattableString.Invariant($", {millionths / 10000d:0.#}% cover")
            : string.Empty;
        return $"{cloud.Status}{coverage}";
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
        Interlocked.Increment(ref _layerGeneration);
        if (_layerCancellation is not null)
        {
            await _layerCancellation.CancelAsync().ConfigureAwait(false);
            _layerCancellation.Dispose();
            _layerCancellation = null;
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
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        _lifetime.Dispose();
        _refreshGate.Dispose();
        if (_layerModule is not null)
        {
            try { await _layerModule.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
        }
    }
}
