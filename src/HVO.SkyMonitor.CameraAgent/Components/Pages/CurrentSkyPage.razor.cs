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
    private CameraAgentCurrentSkyOperations? _operations;
    private CameraAgentCaptureTransientView? _transient;
    private string? _transientMessage;
    private long _transientGeneration;
    private CameraAgentProductDetail? _combinedProduct;
    private TimeSpan? _combinedObservationSpan;
    private string? _lineageMessage;
    private readonly HashSet<Guid> _failedSourcePreviews = [];
    private Guid? _lineageArtifactId;
    private CameraAgentLayeredPresentation? _layers;
    private CancellationTokenSource? _layerCancellation;
    private IJSObjectReference? _layerModule;
    private bool _moduleImportAttempted;

    // Full screen must be requested while the click still holds transient user activation. Importing the interop
    // module lazily inside the click handler can consume that activation on a cold cache, so the module is warmed
    // once as soon as a display capture exists. A failed warmup is harmless: the lazy import retries on demand.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort interop warmup; failure only falls back to the lazy import.")]
    private async Task WarmInteropModuleAsync()
    {
        if (_moduleImportAttempted || _layerModule is not null || _disposeStarted != 0 ||
            _presentation?.DisplayCapture is null) return;
        _moduleImportAttempted = true;
        try
        {
            var module = await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CurrentSkyPage.razor.js");
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                await module.DisposeAsync();
                return;
            }
            _layerModule = module;
        }
        catch (Exception)
        {
            // The lazy import path remains available.
        }
    }
    private ElementReference _layerRoot;
    private ElementReference _figure;
    private bool _viewerOpen;
    private DotNetObjectReference<CurrentSkyPage>? _viewerReference;
    private string? _viewerError;
    private (Guid CaptureId, long Generation, OperatorUiResult<CameraAgentLayeredPresentation> Result)? _deferredLayers;
    private string? _layerMessage;
    private string? _saveMessage;
    private string? _saveError;
    private string? _savedArtifactUrl;
    private string? _savedOriginalUrl;
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
    private string? _technicalEvidenceError;
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
    [Parameter] public CameraAgentCaptureDetailView? ArchivedView { get; set; }
    [Parameter] public RenderFragment? ArchiveNavigation { get; set; }

    /// <summary>Archived-capture evidence rendered in the image column, beside the inspector.</summary>
    [Parameter] public RenderFragment? Evidence { get; set; }

    private bool IsArchived => ArchivedView is not null;
    private CameraAgentCaptureDetailView? _appliedArchive;

    protected override void OnParametersSet()
    {
        if (ArchivedView is not { } archive || ReferenceEquals(archive, _appliedArchive)) return;
        _appliedArchive = archive;
        var capture = archive.Capture;
        var sameCapture = _runCaptureId == capture.CaptureId;
        _runCaptureId = capture.CaptureId;
        var display = new CameraAgentPresentationCapture(capture.CaptureId, capture.CaptureSequence,
            capture.ExposureStartedUtc, 0, capture.EvidenceOrigin);
        _presentation = new CameraAgentCurrentImagePresentation(capture.DurableIngressUtc,
            CameraAgentPresentationImageFreshness.Historical,
            new(CameraAgentPresentationSystemState.Standby, "Retained capture evidence, not current camera health.", capture.DurableIngressUtc),
            display, display, false, archive.Presentation.SelectedStage, archive.Presentation.Stages, false, false);
        _facts = archive.Facts?.CaptureId == capture.CaptureId ? archive.Facts : null;
        _operations = null;
        _transient = null;
        _transientMessage = null;
        _initialLoading = false;
        _selectedStage = ResolveSelection(_presentation, sameCapture ? _selectedStage : null);
        _liveExecutionId = null;
        _combinedProduct = null;
        _combinedObservationSpan = null;
        _lineageArtifactId = _facts?.CombinedLineage?.ArtifactId;
        _lineageMessage = null;
        _failedSourcePreviews.Clear();
        ResetLayers(capture.CaptureId, _lifetime!.Token);
        TryLoadLayers(capture.CaptureId);
        if (_lineageArtifactId is { } artifactId)
            _ = LoadLineageAsync(artifactId, capture.CaptureId, _lifetime.Token);
        _ = LoadLiveRunLinkAsync(capture.CaptureId, _lifetime.Token);
        _ = LoadTransientAsync(capture.CaptureId, _lifetime.Token);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await WarmInteropModuleAsync();
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
        if (IsArchived) return;
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
        if (IsArchived || _accessDenied || _disposeStarted != 0 || _viewerOpen) return;
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
                await InvokeAsync(RevokeAccess).ConfigureAwait(false);
                return;
            }
            var displayCaptureId = result.IsSuccess ? result.Value?.Presentation.DisplayCapture?.CaptureId : null;
            await InvokeAsync(() =>
            {
                if (_accessDenied || _disposeStarted != 0 || _viewerOpen) { _refreshing = false; return; }
                if (result.IsSuccess && result.Value is not null)
                {
                    var wasLayered = ShowLayeredHero;
                    var previousBaseUrl = ProcessedBaseSlot?.PreviewUrl;
                    _presentation = result.Value.Presentation;
                    _facts = result.Value.Facts;
                    _operations = result.Value.Operations;
                    if (_runCaptureId != displayCaptureId)
                    {
                        _transient = null;
                        _transientMessage = null;
                        _liveExecutionId = null;
                        _runCaptureId = displayCaptureId;
                        _combinedProduct = null;
                        _combinedObservationSpan = null;
                        _lineageArtifactId = null;
                        _lineageMessage = null;
                        ResetLayers(displayCaptureId, cancellationToken);
                    }
                    var combinedId = _facts?.CombinedLineage?.ArtifactId;
                    if (_lineageArtifactId != combinedId)
                    {
                        _combinedProduct = null;
                        _combinedObservationSpan = null;
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
            if (!_accessDenied && _disposeStarted == 0 && displayCaptureId is { } captureId)
            {
                // The image is already visible. A slow optional run-link lookup cannot make
                // the five-second current-sky refresh appear to have failed.
                _ = LoadLiveRunLinkAsync(captureId, cancellationToken);
                _ = LoadTransientAsync(captureId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await InvokeAsync(() =>
            {
                if (_accessDenied || _disposeStarted != 0) return;
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
        if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || cancellationToken.IsCancellationRequested) return;
        var lineage = _facts?.CombinedLineage;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await OperatorService.GetProductDetailAsync(artifactId, timeout.Token).ConfigureAwait(false);
            if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || timeout.IsCancellationRequested) return;
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                await InvokeAsync(RevokeAccess).ConfigureAwait(false);
                return;
            }
            TimeSpan? span = null;
            if (result.IsSuccess && result.Value is { } product && product.Product.ArtifactId == artifactId &&
                product.Product.CaptureId == captureId && lineage?.ArtifactId == artifactId &&
                !product.SourcesTruncated && product.Sources.Count == lineage.SourceCount && lineage.SourceCount is > 0 and <= 8 &&
                product.Product.SourceCount == lineage.SourceCount &&
                product.Sources.Select(static source => source.ArtifactId).SequenceEqual(lineage.SourceArtifactIds))
            {
                var sources = new List<CameraAgentGalleryCapture>(lineage.SourceCount);
                try
                {
                    foreach (var source in product.Sources)
                    {
                        if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || timeout.IsCancellationRequested) return;
                        if (source.CaptureId is not { } sourceCaptureId) break;
                        var read = await OperatorService.GetSourceCaptureAsync(sourceCaptureId, timeout.Token).ConfigureAwait(false);
                        if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || timeout.IsCancellationRequested) return;
                        if (read.Kind == OperatorUiResultKind.Unauthorized)
                        {
                            await InvokeAsync(RevokeAccess).ConfigureAwait(false);
                            return;
                        }
                        if (!read.IsSuccess || read.Value is not { } capture) break;
                        sources.Add(capture);
                    }
                    span = CameraAgentCombinedSpanProjector.Project(lineage, product, sources, captureId!.Value);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* Optional source timing never hides a successfully read product. */ }
            }
            await InvokeAsync(() =>
            {
                if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || _runCaptureId != captureId || _lineageArtifactId != artifactId) return;
                if (result.Kind == OperatorUiResultKind.Unauthorized)
                {
                    RevokeAccess();
                    return;
                }
                if (result.IsSuccess && result.Value is { } detail && detail.Product.ArtifactId == artifactId && detail.Product.CaptureId == captureId)
                {
                    _combinedProduct = detail;
                    _combinedObservationSpan = span;
                }
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
                if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || _runCaptureId != captureId || _lineageArtifactId != artifactId) return;
                _lineageMessage = "Source details are unavailable; the recorded source artifact IDs remain visible.";
                StateHasChanged();
            }).ConfigureAwait(false);
        }
    }

    private void RevokeAccess()
    {
        if (_disposeStarted != 0) return;
        _deferredLayers = null;
        _accessDenied = true;
        _lifetime?.Cancel();
        _layerCancellation?.Cancel();
        _viewerOpen = false;
        _presentation = null;
        _facts = null;
        _operations = null;
        _transient = null;
        _transientMessage = null;
        _layers = null;
        _combinedProduct = null;
        _combinedObservationSpan = null;
        _selectedLayers.Clear();
        StateHasChanged();
        NavigationManager.NavigateTo("/Account/AccessDenied");
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
        _deferredLayers = null;
        _layerMessage = null;
        _saveMessage = null;
        _saveError = null;
        _savedArtifactUrl = null;
        _savedOriginalUrl = null;
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
        if (captureId is not { } id || _layers is not null || _layerLoading || _accessDenied || _viewerOpen ||
            _layerCancellation is not { IsCancellationRequested: false } cancellation) return;
        _layerLoading = true;
        _ = LoadLayersAsync(id, Volatile.Read(ref _layerGeneration), cancellation.Token);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional layers must never break current-image refresh.")]
    private async Task LoadLayersAsync(Guid captureId, long generation, CancellationToken cancellation)
    {
        OperatorUiResult<CameraAgentLayeredPresentation> result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            result = await OperatorService.GetLayeredPresentationAsync(captureId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = OperatorUiResult<CameraAgentLayeredPresentation>.Failure(OperatorUiResultKind.Unavailable,
                "Structured layers are temporarily unavailable.");
        }
        if (cancellation.IsCancellationRequested || Volatile.Read(ref _disposeStarted) != 0) return;
        await InvokeAsync(() => ApplyLayerResult(captureId, generation, result)).ConfigureAwait(false);
    }

    private void ApplyLayerResult(Guid captureId, long generation, OperatorUiResult<CameraAgentLayeredPresentation> result)
    {
        if (generation != _layerGeneration || _runCaptureId != captureId || _disposeStarted != 0 || _accessDenied ||
            _layerCancellation?.IsCancellationRequested != false) return;
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            RevokeAccess();
            return;
        }
        // Keep the actual displayed base and controls frozen, including terminal absence/failure.
        // Retain one completed result, not a waiting task or its timeout/cancellation resources.
        if (_viewerOpen)
        {
            _deferredLayers = (captureId, generation, result);
            StateHasChanged();
            return;
        }
        _layerLoading = false;
        if (result.IsSuccess && result.Value is { } value && value.CaptureId == captureId)
        {
            _layers = value;
            _layersNotRetained = false;
            _layerMessage = null;
            _selectedLayers = value.Layers.Where(static layer => layer.EnabledByDefault)
                .Select(static layer => layer.IdentitySha256).ToHashSet(StringComparer.Ordinal);
            _bindLayers = true;
        }
        else
        {
            _layersNotRetained = result.Kind == OperatorUiResultKind.NotFound;
            _layerMessage = result.Message ?? "Structured layers are unavailable for this capture.";
        }
        StateHasChanged();
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
    private string? SavedOriginalUrl => _savedOriginalUrl;

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

    // Data-driven frame, never the prototype's 1936x1216 fixture: the layered canvas takes the retained payload's
    // own ratio so the SVG viewBox registers exactly. The plain image reserves height through CaptureImage's own
    // min-height and sizes to its bytes, so no fixed ratio is applied that could clip it.
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
        _ => IsArchived ? "Archived presentation" : "Current baseline"
    };

    private string ProductBadgeClass => _selectedStage is CameraAgentPresentationStage.Calibrated or CameraAgentPresentationStage.Raw ? "source" : "current";

    private string CentralStatusLabel => IsArchived ? "Historical capture" : _operations switch
    {
        { CentralIntegrationEnabled: false } => "Disabled",
        { ArtifactUploadEnabled: false } => "Uploads disabled",
        { ArtifactUploadEnabled: true } => "Delivery configured",
        _ => "Unavailable"
    };

    private string CentralStatusReason => IsArchived
        ? "Current LogicHost connectivity is not a historical capture fact."
        : _operations switch
        {
            { CentralIntegrationEnabled: false } => "Standalone mode; no central delivery is configured.",
            { ArtifactUploadEnabled: false } => "Central integration is configured, but artifact upload is disabled.",
            { ArtifactUploadEnabled: true } => "Open Delivery for retained acknowledgements; configuration alone does not prove connectivity.",
            _ => "The current delivery configuration could not be read."
        };

    private string StorageStatusLabel => IsArchived ? "Retained evidence" : _operations switch
    {
        { ProductReconciliation.Succeeded: false } => "Recovery failed",
        { RawReconciliation: { } raw } when raw.MissingEvidence > 0 || raw.IndexProjectionFailures > 0 => "Evidence missing",
        { RawReconciliation.Quarantined: > 0 } => "Review required",
        { ProductReconciliation: { } products } when products.Missing > 0 || products.Quarantined > 0 => "Review required",
        { RawReconciliation: not null, ProductReconciliation: not null } => "Recovery verified",
        _ => "Not verified yet"
    };

    private string StorageStatusReason => IsArchived
        ? "Available retained artifacts are identified below; current free space is not a capture fact."
        : _operations is { RawReconciliation: { } raw, ProductReconciliation: { } products }
            ? $"Last recovery checks: raw {raw.CompletedUtc:yyyy-MM-dd HH:mm} UTC; derived {products.CompletedUtc:yyyy-MM-dd HH:mm} UTC. This is recovery evidence, not a free-space measurement."
            : "A complete local recovery check is not available. Open Storage for its recorded results.";

    private string StageFactLabel => ShowLayeredHero && _layerInteractive
        ? "Processed base + selected overlays"
        : ProcessedBaseFallback
            ? ProcessedBaseSlot is null ? "Processed base unavailable" : "Unannotated Combined base (processed layers pending or unavailable)"
            : SelectedSlot?.Label ?? "Unavailable";

    // Image-stage availability and retained detector outcomes remain separate facts.
    private string CaptureOutcomeIconClass => _presentation?.DisplayCapture is null
        ? "pending"
        : UnavailableStageCount > 0 ? "warning" : "success";

    private string CaptureOutcomeTitle => _presentation?.DisplayCapture is null
        ? "No durable capture"
        : UnavailableStageCount > 0 ? "Partial image stages" : "All image stages retained";

    private string CaptureOutcomeSubtitle => _presentation?.DisplayCapture is null
        ? "Waiting for the first displayable capture"
        : UnavailableStageCount > 0
            ? $"{UnavailableStageCount} of {_presentation.Stages.Count} stages unavailable / {TransientOutcomeLabel}"
            : TransientOutcomeLabel;

    private string TransientOutcomeLabel => _transient?.Run switch
    {
        { WorkState: "quarantined" } or { FrameState: "quarantined" } => "Detector failed",
        { WorkState: "abandoned" } or { FrameState: "abandoned" } => "Detector work abandoned",
        { FrameState: "retry_wait" } => "Detector retrying",
        { CandidateCount: > 0 } run => $"{run.CandidateCount} causal candidate{(run.CandidateCount == 1 ? "" : "s")}",
        { CausalSucceeded: true } => "No causal candidates",
        { FrameState: null or "queued" } => "Waiting for detector",
        not null => "Not assessed",
        _ when _transientMessage is not null => "Outcome unavailable",
        _ when _transient is not null => "No retained outcome",
        _ => "Outcome not loaded"
    };

    private string TransientOutcomeReason => _transient?.Run switch
    {
        { WorkState: "quarantined" } or { FrameState: "quarantined" } =>
            "The recorded detector work is quarantined. Open Operations for its retained evidence.",
        { WorkState: "abandoned" } or { FrameState: "abandoned" } => "The recorded detector work was abandoned.",
        { FrameState: "retry_wait" } run => $"Recorded attempt {run.FrameAttempts}; the detector is waiting to retry.",
        { CandidateCount: > 0 } run => $"{run.CompletedCandidates} complete / {run.QuarantinedCandidates} quarantined. Candidates are not confirmed events.",
        { CausalSucceeded: true } => "The recorded causal pass completed without candidates.",
        not null => "No successful causal pass is recorded for this capture.",
        _ => _transientMessage ?? (_transient?.CurrentMode switch
        {
            HVO.SkyMonitor.CameraAgent.Common.Options.TransientOperatingMode.Off => "Detection is off now; this capture has no retained local outcome.",
            HVO.SkyMonitor.CameraAgent.Common.Options.TransientOperatingMode.Central => "Central detection is configured now; no local outcome is retained for this capture.",
            not null => "This capture has no retained local detector outcome.",
            _ => "Reading this capture's retained detector outcome."
        })
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An optional detector read must not fail or hide the image.")]
    private async Task LoadTransientAsync(Guid captureId, CancellationToken cancellationToken)
    {
        if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || cancellationToken.IsCancellationRequested) return;
        var generation = Interlocked.Increment(ref _transientGeneration);
        OperatorUiResult<CameraAgentCaptureTransientView> result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            result = await OperatorService.GetCaptureTransientAsync(captureId, timeout.Token).AsTask()
                .WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception)
        {
            result = OperatorUiResult<CameraAgentCaptureTransientView>.Failure(
                OperatorUiResultKind.Unavailable, "The retained transient outcome is temporarily unavailable.");
        }
        await InvokeAsync(() =>
        {
            if (Volatile.Read(ref _disposeStarted) != 0 || _accessDenied || _runCaptureId != captureId ||
                generation != Volatile.Read(ref _transientGeneration)) return;
            if (result.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
            _transient = result.IsSuccess && result.Value?.CaptureId == captureId ? result.Value : null;
            _transientMessage = _transient is null ? "The retained transient outcome is temporarily unavailable." : null;
            StateHasChanged();
        }).ConfigureAwait(false);
    }

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

    private string CaptureCadenceLabel => _facts?.CaptureProfile is not { } profile
        ? "Not retained for this capture"
        : profile.CadenceMode == HVO.SkyMonitor.AgentCore.CaptureCadenceMode.Continuous
            ? "Continuous at capture time"
            : FormattableString.Invariant($"{profile.EffectiveInterval.TotalSeconds:0.###} s recorded interval");

    private string SensorSampleLabel => _facts?.CaptureProfile?.PixelSizeMicrons is { } pixelSize
        ? FormattableString.Invariant($"{pixelSize:0.###} µm")
        : "Not retained for this capture";

    private string OpticsLabel => _facts?.CaptureProfile is { } profile
        ? $"{(profile.FocalLengthMillimeters is { } focal ? FormattableString.Invariant($"{focal:0.###} mm") : "Focal length not retained")} · {(profile.FieldOfViewDegrees is { } fieldAngle ? FormattableString.Invariant($"{fieldAngle:0.###}° field") : "Field angle not retained")}"
        : "Not retained for this capture";

    private string ProjectionLabel => _facts?.CaptureProfile is { } profile
        ? $"{profile.ProjectionModel} (configured; no measured fit)"
        : "No retained model or measured fit";

    private string SiteLabel => _facts?.Location switch
    {
        { Availability: "Available", LatitudeDegrees: { } latitude, LongitudeDegrees: { } longitude, ElevationMeters: { } elevation } =>
            FormattableString.Invariant($"{latitude:0.###}°, {longitude:0.###}° / {elevation:0.#} m"),
        { Availability: "NotRetained" } => "Location not retained for this capture",
        _ => "Historical location unavailable"
    };

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
        _savedOriginalUrl = null;
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
                // The saved stack is full-resolution packed pixels; preview JPEG may be scaled to 2048px.
                _savedArtifactUrl = $"/api/v1/operations/artifacts/{receipt.ArtifactId:D}/preview?download=1";
                _savedOriginalUrl = $"/api/v1/operations/artifacts/{receipt.ArtifactId:D}/content";
                var downloaded = await TryStartDownloadAsync(_savedArtifactUrl);
                if (!IsCurrentLayer(generation, captureId, cancellation)) return;
                _saveMessage = (receipt.Replayed ? "This exact stack was already saved" : "Stack saved as a new immutable artifact") +
                    (downloaded ? "; display JPEG download started (up to 2048 pixels per side)." : ".");
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

    private async Task<bool> TryStartDownloadAsync(string url)
    {
        try
        {
            var module = _layerModule ?? await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CurrentSkyPage.razor.js");
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                if (!ReferenceEquals(module, _layerModule)) await module.DisposeAsync();
                return false;
            }
            _layerModule = module;
            await module.InvokeVoidAsync("downloadUrl", url);
            return true;
        }
        catch (Exception exception) when (exception is JSException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The manual link below remains available.
            return false;
        }
    }

    private bool IsCurrentLayer(long generation, Guid captureId, CancellationTokenSource cancellation) =>
        generation == Volatile.Read(ref _layerGeneration) && _runCaptureId == captureId &&
        ReferenceEquals(cancellation, _layerCancellation) && !cancellation.IsCancellationRequested;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "This optional link read must not fail the current sky image.")]
    private async Task LoadLiveRunLinkAsync(Guid captureId, CancellationToken cancellationToken)
    {
        if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || cancellationToken.IsCancellationRequested) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var run = await GraphService.GetLiveExecutionIdAsync(captureId, timeout.Token).ConfigureAwait(false);
            if (run.Kind == OperatorUiResultKind.Unauthorized && _runCaptureId == captureId && Volatile.Read(ref _disposeStarted) == 0)
            {
                await InvokeAsync(() =>
                {
                    if (_runCaptureId != captureId || Volatile.Read(ref _disposeStarted) != 0) return;
                    RevokeAccess();
                });
            }
            else if (run.IsSuccess && _runCaptureId == captureId && Volatile.Read(ref _disposeStarted) == 0)
            {
                await InvokeAsync(() =>
                {
                    if (_runCaptureId != captureId || Volatile.Read(ref _disposeStarted) != 0 || _accessDenied) return;
                    _liveExecutionId = run.Value?.ExecutionId;
                    StateHasChanged();
                }).ConfigureAwait(false);
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
            if (IsArchived) TryLoadLayers(_runCaptureId);
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

    // Pin the exact figure before interop: an optional read may complete while the browser is entering full screen.
    private async Task OpenFullScreenAsync()
    {
        if (_viewerOpen || DisplaySlot is null || _disposeStarted != 0) return;
        _viewerOpen = true;
        _viewerError = null;
        try
        {
            var module = _layerModule ?? await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CurrentSkyPage.razor.js");
            if (Volatile.Read(ref _disposeStarted) != 0) return;
            _layerModule = module;
            _viewerReference ??= DotNetObjectReference.Create(this);
            if (await module.InvokeAsync<bool>("requestFullScreen", _figure, _viewerReference)) return;
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException)
        {
            // A rejected request restores inline updates below.
        }
        await ViewerClosedAsync();
        if (_disposeStarted == 0)
            _viewerError = "The browser could not open full screen. The selected image remains available here; please retry.";
    }

    [JSInvokable]
    public Task ViewerClosedAsync()
    {
        if (_disposeStarted != 0) return Task.CompletedTask;
        return InvokeAsync(() =>
        {
            _viewerOpen = false;
            var pending = _deferredLayers;
            _deferredLayers = null;
            if (pending is { } result) ApplyLayerResult(result.CaptureId, result.Generation, result.Result);
            StateHasChanged();
        });
    }

    private async Task CloseFullScreenAsync()
    {
        if (_layerModule is null || !_viewerOpen) return;
        try { await _layerModule.InvokeVoidAsync("exitFullScreen", _figure); }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException)
        {
            await ViewerClosedAsync();
        }
    }

    private CameraAgentPresentationSlot? SelectedSlot => _presentation?.Stages.SingleOrDefault(slot =>
        slot.Stage == _selectedStage && slot.Availability == CameraAgentPresentationSlotAvailability.Available);

    private CameraAgentPresentationCapture? FactCapture =>
        _presentation?.DisplayCapture ?? _presentation?.LatestCapture;

    private string DetailUrl => IsArchived
        ? $"/gallery/{ArchivedView!.Capture.CaptureId:D}{new Uri(NavigationManager.Uri).Query}#technical-evidence"
        : FactCapture is { } capture
        ? $"/gallery/{capture.CaptureId:D}"
        : "/gallery";

    private async Task OpenTechnicalEvidenceAsync()
    {
        if (!IsArchived || _disposeStarted != 0) return;
        _technicalEvidenceError = null;
        try
        {
            var module = _layerModule ?? await JSRuntime.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/CurrentSkyPage.razor.js");
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                if (!ReferenceEquals(module, _layerModule)) await module.DisposeAsync();
                return;
            }
            _layerModule = module;
            await module.InvokeVoidAsync("openTechnicalEvidence", _layerRoot);
        }
        catch (Exception exception) when (exception is JSException or OperationCanceledException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _disposeStarted) != 0) return;
            _technicalEvidenceError = "Could not open evidence automatically. Exit fullscreen with Escape if needed, then use Evidence and downloads below the image.";
        }
    }

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

    private string SystemLabel => IsArchived ? "Historical capture" : _presentation?.System.State switch
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
        _deferredLayers = null;
        _viewerReference?.Dispose();
        if (_layerModule is not null)
        {
            try
            {
                await _layerModule.InvokeVoidAsync("disconnect", _layerRoot, _figure).ConfigureAwait(false);
                await _layerModule.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException) { }
        }
    }
}
