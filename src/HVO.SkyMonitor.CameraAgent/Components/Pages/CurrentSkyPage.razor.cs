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
    private CameraAgentProductDetail? _combinedProduct;
    private string? _lineageMessage;
    private Guid? _lineageArtifactId;
    private CameraAgentLayeredPresentation? _layers;
    private CancellationTokenSource? _layerCancellation;
    private IJSObjectReference? _layerModule;
    private ElementReference _layerRoot;
    private ElementReference _figure;
    private string? _layerMessage;
    private string? _saveMessage;
    private string? _saveError;
    private string? _savedArtifactUrl;
    private HashSet<string> _selectedLayers = new(StringComparer.Ordinal);
    private bool _layerInteractive;
    private bool _layerImageFailed;
    private int _layerPreviewAttempt;
    private bool _layerLoading;
    private bool _layersNotRetained;
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
                    var wasLayered = ShowLayeredHero;
                    var previousBaseUrl = ProcessedBaseSlot?.PreviewUrl;
                    _presentation = result.Value.Presentation;
                    _facts = result.Value.Facts;
                    if (_runCaptureId != displayCaptureId)
                    {
                        _liveExecutionId = null;
                        _runCaptureId = displayCaptureId;
                        _combinedProduct = null;
                        _lineageArtifactId = null;
                        _lineageMessage = null;
                        ResetLayers(displayCaptureId, cancellationToken);
                    }
                    var combinedId = _facts?.CombinedLineage?.ArtifactId;
                    if (_lineageArtifactId != combinedId)
                    {
                        _combinedProduct = null;
                        _lineageMessage = null;
                        _lineageArtifactId = combinedId;
                        if (combinedId is { } artifactId)
                            _ = LoadLineageAsync(artifactId, displayCaptureId, cancellationToken);
                    }
                    _factsUnavailableReason = result.Value.FactsUnavailableReason;
                    _selectedStage = ResolveSelection(result.Value.Presentation, _selectedStage);
                    if (_layers is not null && (wasLayered != ShowLayeredHero || previousBaseUrl != ProcessedBaseSlot?.PreviewUrl))
                    {
                        // Verification belongs to a particular rendered base. A same-capture outage destroys
                        // that DOM; its replacement must reapply the retained checkbox selection before use.
                        _layerInteractive = false;
                        _bindGeneration++;
                        _bindLayers = true;
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional lineage does not interrupt the current image.")]
    private async Task LoadLineageAsync(Guid artifactId, Guid? captureId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await OperatorService.GetProductDetailAsync(artifactId, timeout.Token).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (_accessDenied || _disposeStarted != 0 || _runCaptureId != captureId || _lineageArtifactId != artifactId) return;
                if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    _accessDenied = true;
                    NavigationManager.NavigateTo("/Account/AccessDenied");
                    return;
                }
                if (result.IsSuccess && result.Value is { } detail && detail.Product.ArtifactId == artifactId && detail.Product.CaptureId == captureId)
                    _combinedProduct = detail;
                else
                    _lineageMessage = "Source details are unavailable; the recorded source artifact IDs remain visible.";
                StateHasChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                if (_disposeStarted != 0 || _runCaptureId != captureId || _lineageArtifactId != artifactId) return;
                _lineageMessage = "Source details are unavailable; the recorded source artifact IDs remain visible.";
                StateHasChanged();
            }).ConfigureAwait(false);
        }
    }

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
        _layersNotRetained = false;
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
                    _layersNotRetained = false;
                    _layerMessage = null;
                    _selectedLayers = value.Layers.Where(static layer => layer.EnabledByDefault)
                        .Select(static layer => layer.IdentitySha256).ToHashSet(StringComparer.Ordinal);
                    _bindLayers = true;
                    StateHasChanged();
                }
                else
                {
                    _layersNotRetained = result.Kind == OperatorUiResultKind.NotFound;
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

    private bool ShowLayeredHero => !_layerImageFailed && _selectedStage == CameraAgentPresentationStage.Annotated &&
        _layers is not null &&
        _presentation?.DisplayCapture?.CaptureId == _layers.CaptureId &&
        LayerBaseMatches && SelectedSlot is not null;

    private bool LayerBaseMatches => _layers is not null &&
        ProcessedBaseSlot is { DisplayBasis: CameraAgentPresentationDisplayBasis.RetainedDerivative, DisplayArtifactId: { } displayId } &&
        displayId == _layers.BaseArtifactId;

    private string? LayerMessage => _layers is not null && !LayerBaseMatches
        ? "The retained layer manifest base does not match the resolved Combined display derivative. Showing the unannotated Combined base without layers."
        : _layerMessage;

    // Unknown or failed layer reads are not proof that a capture has no retained layers.
    private bool ProcessedBaseFallback => _selectedStage == CameraAgentPresentationStage.Annotated && !_layersNotRetained;

    private string? LayerPreviewUrl => ProcessedBaseSlot?.PreviewUrl is { } url
        ? Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url.OriginalString, "attempt",
            _layerPreviewAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : null;

    // The Processed hero and its overlays-off/pending fallback share the Combined slot's display artifact:
    // the lineage-matched retained derivative when one exists, never a generic Preview or the annotated
    // artifact. The slot's ArtifactId stays the linear Combined frame for identity and download.
    private CameraAgentPresentationSlot? ProcessedBaseSlot => _presentation?.Stages.SingleOrDefault(slot =>
        slot.Stage == CameraAgentPresentationStage.Combined &&
        slot.Availability == CameraAgentPresentationSlotAvailability.Available && slot.PreviewUrl is not null);

    private CameraAgentPresentationSlot? DisplaySlot => ProcessedBaseFallback
        ? ProcessedBaseSlot : SelectedSlot;

    private string? DisplayContentUrl => DisplaySlot?.ArtifactId is { } artifactId
        ? FormattableString.Invariant($"/api/v1/operations/artifacts/{artifactId:D}/content")
        : null;

    private string DisplayBasisLabel => DisplaySlot switch
    {
        null => "Unavailable",
        { DisplayBasis: CameraAgentPresentationDisplayBasis.RetainedDerivative } => "Retained display derivative",
        { DisplayOperation: CameraAgentPreviewOperation.EncodeOnly } => "Encoding only; no display stretch",
        { DisplayOperation: CameraAgentPreviewOperation.EncodedPassthrough } => "Retained encoded bytes; no display stretch",
        { DisplayOperation: CameraAgentPreviewOperation.PerImageStretch, DisplayReferenceId: not null } => "Capture-bound per-image normalization",
        { DisplayOperation: CameraAgentPreviewOperation.PerImageStretch } => "Per-image normalization",
        _ => "Preview operation unavailable"
    };

    private string DisplayBasisPhrase => DisplaySlot switch
    {
        null => "unavailable",
        { DisplayBasis: CameraAgentPresentationDisplayBasis.RetainedDerivative } => "retained display derivative",
        { DisplayOperation: CameraAgentPreviewOperation.EncodeOnly } => "encoding only; no display stretch",
        { DisplayOperation: CameraAgentPreviewOperation.EncodedPassthrough } => "retained encoded bytes; no display stretch",
        { DisplayOperation: CameraAgentPreviewOperation.PerImageStretch, DisplayReferenceId: not null } => "capture-bound per-image normalization",
        { DisplayOperation: CameraAgentPreviewOperation.PerImageStretch } => "per-image normalization",
        _ => "preview operation unavailable"
    };

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

    private void RestoreLayerDefaults()
    {
        if (_layers is null || !ShowLayeredHero || !_layerInteractive) return;
        _selectedLayers = _layers.Layers.Where(static layer => layer.EnabledByDefault)
            .Select(static layer => layer.IdentitySha256).ToHashSet(StringComparer.Ordinal);
        _bindGeneration++;
        _bindLayers = true;
    }

    private int SelectedLayerCount => _layers?.Layers.Count(layer => _selectedLayers.Contains(layer.IdentitySha256)) ?? 0;

    // Mirrors the prototype's count wording; the number is the operator's current selection, never a retained total.
    private string LayerCountLabel => _layers is null || _presentation?.DisplayCapture?.CaptureId != _layers.CaptureId
        ? "Not retained for this capture"
        : ShowLayeredHero
            ? $"{SelectedLayerCount} selected"
            : $"{SelectedLayerCount} selected / hidden at this stage";

    // Data-driven frame, never the prototype's 1936x1216 fixture: the layered canvas uses the retained payload
    // dimensions; the plain image lets its own bytes define height.
    private string StageAspectStyle => _layers is { } layers && ShowLayeredHero
        ? FormattableString.Invariant($"aspect-ratio: {layers.WidthPixels} / {layers.HeightPixels};")
        : string.Empty;

    private bool HasLayer(string first, string second) => _layers?.Layers.Any(layer => layer.Kind == first || layer.Kind == second) == true;

    private IEnumerable<CameraAgentPresentationLayer> RetainedLayers(string group) =>
        _layers?.Layers.Where(layer => LayerGroup(layer.Kind) == group) ?? [];

    private static string LayerGroup(string kind) => kind switch
    {
        "scene-annotation" or "star-annotations" or "scene-constellations" or "constellations" or "scene-cardinals" or "cardinal-directions" => "Sky context",
        _ => "Diagnostics"
    };

    private static string LayerDescription(string kind) => kind switch
    {
        "scene-annotation" or "star-annotations" => "expected / projected catalog, not measured associations (#526)",
        "scene-constellations" or "constellations" => "expected / HYG topology",
        "scene-cardinals" or "cardinal-directions" => "configured rig geometry",
        "scene-image-circle" or "image-circle" => "native sensor coordinates",
        "environment" or "corner-annotations" => "source and coordinate provenance",
        "cloud-mask" => "image analyzer / retained tile mask",
        "cloud-labels" => "image analyzer / retained assessment",
        _ => "Retained presentation layer"
    };

    // Prototype order: Processed, Live mean, Calibrated, Raw. The service order is not a display contract.
    private IEnumerable<CameraAgentPresentationSlot> OrderedStages => (_presentation?.Stages ?? [])
        .OrderBy(static slot => slot.Stage switch
        {
            CameraAgentPresentationStage.Annotated => 0,
            CameraAgentPresentationStage.Preview => 1,
            CameraAgentPresentationStage.Combined => 2,
            CameraAgentPresentationStage.Calibrated => 3,
            _ => 4
        });

    private static string StageCaption(CameraAgentPresentationStage stage) => stage switch
    {
        CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview => "Layers applied",
        CameraAgentPresentationStage.Combined => "Causal mean",
        CameraAgentPresentationStage.Calibrated => "Reference capture",
        _ => "Immutable source"
    };

    private static string StageUnavailableReason(CameraAgentPresentationSlot slot) => slot.Reason switch
    {
        "NotProduced" => "This stage was not produced.",
        "ProcessingFailed" => "This stage could not be created.",
        "ProcessingSkipped" => "This stage was skipped because required input was unavailable.",
        "ArtifactUnavailable" => "The produced artifact is unavailable.",
        _ => "This stage is currently unavailable."
    };

    private string ImageHeading => _selectedStage switch
    {
        CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview => "Processed presentation",
        CameraAgentPresentationStage.Combined => "Unregistered live mean",
        CameraAgentPresentationStage.Calibrated => "Calibrated reference frame",
        CameraAgentPresentationStage.Raw => "Immutable raw source",
        _ => "Current image"
    };

    private string ImageSubtitle
    {
        get
        {
            if (FactCapture is not { } capture) return "Waiting for a durable image.";
            var sources = _facts?.CombinedLineage?.SourceCount;
            return _selectedStage switch
            {
                CameraAgentPresentationStage.Annotated or CameraAgentPresentationStage.Preview =>
                    sources is { } layered ? $"Unregistered causal mean of {layered} source frames with the selected presentation layers." : "Retained presentation with the selected presentation layers.",
                CameraAgentPresentationStage.Combined =>
                    sources is { } combined ? $"{combined} sources ending at capture #{capture.CaptureSequence}, combined without geometric registration." : $"Sources ending at capture #{capture.CaptureSequence}, combined without geometric registration.",
                CameraAgentPresentationStage.Calibrated => $"Capture #{capture.CaptureSequence} after calibration, before temporal combination or presentation layers.",
                CameraAgentPresentationStage.Raw => $"Capture #{capture.CaptureSequence} as acquired. Display conversion does not modify retained sensor evidence.",
                _ => "Source capture and processing state remain explicit."
            };
        }
    }

    private string ProductBadgeLabel => _selectedStage switch
    {
        CameraAgentPresentationStage.Calibrated => "Single frame",
        CameraAgentPresentationStage.Raw => "Primary evidence",
        _ => "Current baseline"
    };

    private string ProductBadgeClass => _selectedStage is CameraAgentPresentationStage.Calibrated or CameraAgentPresentationStage.Raw ? "source" : "current";

    private string StageFactLabel => ShowLayeredHero && _layerInteractive
        ? "Processed base + selected overlays"
        : ProcessedBaseFallback
            ? ProcessedBaseSlot is null ? "Processed base unavailable" : "Unannotated Combined base (processed layers pending or unavailable)"
            : SelectedSlot?.Label ?? "Unavailable";

    // The prototype caption names an event outcome; no transient detection exists yet (#1005), so the caption
    // reports the capture's own processing state instead of a fixture event.
    private string CaptureOutcomeIconClass => _presentation?.DisplayCapture is null
        ? "pending"
        : UnavailableStageCount > 0 ? "warning" : "success";

    private string CaptureOutcomeTitle => _presentation?.DisplayCapture is null
        ? "No durable capture"
        : UnavailableStageCount > 0 ? "Partial image stages" : "All image stages retained";

    private string CaptureOutcomeSubtitle => _presentation?.DisplayCapture is null
        ? "Waiting for the first displayable capture"
        : UnavailableStageCount > 0
            ? $"{UnavailableStageCount} of {_presentation.Stages.Count} stages unavailable / transient detection arrives with #1005"
            : "Transient detection arrives with #1005; no event is claimed for this capture";

    private string LiveIndicatorLabel => _presentation?.ImageFreshness switch
    {
        CameraAgentPresentationImageFreshness.Current => $"Image age {FormatDuration(FactCapture?.AgeSeconds ?? 0)}",
        CameraAgentPresentationImageFreshness.Delayed => "Next image delayed",
        CameraAgentPresentationImageFreshness.Stale => "Image stale",
        CameraAgentPresentationImageFreshness.Historical => "Historical image",
        _ => "No image"
    };

    private string SystemIconClass => _presentation?.System.State switch
    {
        CameraAgentPresentationSystemState.Capturing => "success",
        CameraAgentPresentationSystemState.Standby or CameraAgentPresentationSystemState.Paused => "warning",
        CameraAgentPresentationSystemState.Unavailable => "failure",
        _ => "pending"
    };

    private string FreshnessChipClass => _presentation?.ImageFreshness switch
    {
        CameraAgentPresentationImageFreshness.Current => "success",
        CameraAgentPresentationImageFreshness.Delayed or CameraAgentPresentationImageFreshness.Historical => "warning",
        CameraAgentPresentationImageFreshness.Stale => "failure",
        _ => "pending"
    };

    private string ProcessingStripLabel => _presentation is null
        ? "Loading"
        : $"{_presentation.Stages.Count(static slot => slot.Availability == CameraAgentPresentationSlotAvailability.Available)} of {_presentation.Stages.Count} stages";

    private string IncludedFramesLabel(CameraAgentCombinedLineage lineage) =>
        _combinedProduct is { } product && product.Product.ArtifactId == lineage.ArtifactId && !product.SourcesTruncated
            ? $"{product.Sources.Count} of {product.Product.SourceCount} frames"
            : $"{lineage.SourceCount} recorded";

    private string LineageDisclosure(CameraAgentCombinedLineage lineage)
    {
        var recipe = _combinedProduct is { } product && product.Product.ArtifactId == lineage.ArtifactId
            ? product.Product.Recipe?.Name
            : lineage.RecipeName;
        var sources = lineage.SourceCount == 1 ? "1 source frame" : $"{lineage.SourceCount} source frames";
        var named = recipe is null ? string.Empty : $", recipe {recipe}";
        return $"this unregistered causal arithmetic mean of {sources}{named} has no geometric registration; it is not a registered stack.";
    }

    private string TotalIntegrationLabel(CameraAgentCombinedLineage lineage) =>
        _combinedProduct is { } product && product.Product.ArtifactId == lineage.ArtifactId
            ? FormattableString.Invariant($"{product.Product.TotalIntegration.TotalSeconds:0.###} seconds")
            : "Unavailable";

    private static string LayerLabel(string kind) => kind switch
    {
        "scene-annotation" or "star-annotations" => "Catalog stars",
        "scene-cardinals" or "cardinal-directions" => "Cardinal directions",
        "scene-image-circle" or "image-circle" => "Image geometry",
        "scene-constellations" or "constellations" => "Constellation lines",
        "environment" or "corner-annotations" => "Frame facts",
        "cloud-mask" => "Measured cloud mask",
        "cloud-labels" => "Measured cloud assessment",
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
            _bindGeneration++;
            _layerInteractive = false;
            if (stage == CameraAgentPresentationStage.Annotated && _layers is not null) _bindLayers = true;
        }
    }

    // Full screen is the prototype's native figure.requestFullscreen(): the exact selected base and SVG layers
    // scale together and nothing is re-fetched or re-stretched.
    private async Task OpenFullScreenAsync()
    {
        if (DisplaySlot is null || _disposeStarted != 0) return;
        try
        {
            var module = _layerModule ?? await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CurrentSkyPage.razor.js");
            if (Volatile.Read(ref _disposeStarted) != 0) return;
            _layerModule = module;
            await module.InvokeVoidAsync("requestFullScreen", _figure);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException)
        {
            // The browser refused full screen; the inline figure remains the same image.
        }
    }

    private CameraAgentPresentationSlot? SelectedSlot => _presentation?.Stages.SingleOrDefault(slot =>
        slot.Stage == _selectedStage && slot.Availability == CameraAgentPresentationSlotAvailability.Available);

    private CameraAgentPresentationCapture? FactCapture =>
        _presentation?.DisplayCapture ?? _presentation?.LatestCapture;

    private string DetailUrl => FactCapture is { } capture
        ? $"/gallery/{capture.CaptureId:D}"
        : "/gallery";

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

    private string SystemLabel => _presentation?.System.State switch
    {
        CameraAgentPresentationSystemState.Capturing => "Capturing",
        CameraAgentPresentationSystemState.Standby => "Standby",
        CameraAgentPresentationSystemState.Paused => "Paused",
        CameraAgentPresentationSystemState.Unavailable => "Camera unavailable",
        _ => "Starting"
    };

    private string SummaryMessage => _presentation is null
        ? "Loading current image and system state."
        : _presentation.DisplayCapture is null
            ? "No displayable durable image was found. CameraAgent state remains available independently."
            : _presentation.IsHistoricalFallback
                ? "A newer capture exists, but this is the latest retained capture that can be displayed safely."
                : _presentation.System.Message;

    private string StageStatus => ShowLayeredHero && _layerInteractive
        ? "Showing processed base image with selected presentation overlays; the base is the retained Combined display derivative, not the separate processed artifact."
        : ShowLayeredHero
        ? "Showing the unannotated Combined display base while processed layers are pending verification. Layer toggles do not affect this image yet."
        : ProcessedBaseFallback && ProcessedBaseSlot is null
        ? "Processed layers are pending or unavailable; the unannotated Combined base is unavailable. No processed image is displayed."
        : ProcessedBaseFallback
        ? $"Showing unannotated Combined base ({DisplayBasisPhrase}); processed layers are {(_layerLoading ? "pending" : "unavailable")}. Layer toggles do not affect this image."
        : SelectedSlot is { } selected
        ? $"Showing {selected.Label} ({DisplayBasisPhrase})."
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
