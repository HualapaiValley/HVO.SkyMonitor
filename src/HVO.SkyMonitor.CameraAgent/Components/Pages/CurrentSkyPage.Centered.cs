using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CurrentSkyPage
{
    private enum CenteredUpdateAction
    {
        NoOp,
        Hold,
        SwapInPlace,
        Advance
    }

    private sealed record CenteredDisplayState(
        CenteredViewStream Stream,
        CenteredView View,
        CameraAgentLayeredPresentation? Presentation,
        CameraAgentLayeredPresentationStatus PresentationStatus,
        string? PresentationReason,
        Uri? PreviewUrl,
        CenteredLiveMode Mode,
        bool RetainedFromPriorRun,
        bool PresentationDeferred = false);

    private sealed record CenteredLayerRow(
        CenteredViewLayer Layer,
        CameraAgentPresentationLayer? DrawableLayer,
        string Label,
        string ReadinessLabel,
        string ReadinessCode,
        string Description,
        long? ContributorSequence,
        bool IsDrawable);

    private CenteredLiveMode _centeredLiveMode = CenteredLiveMode.Centered;
    private bool _livePaused;
    private CenteredViewLiveContext? _centeredLiveContext;
    private CenteredDisplayState? _centeredDisplay;
    private CameraAgentCenteredArchiveView? _centeredArchive;
    private bool _centeredSaving;
    private string? _centeredSaveMessage;
    private string? _centeredSaveError;
    private string? _centeredSavedUrl;
    private ICenteredViewPublisher? _centeredPublisher;
    private bool _centeredPublisherSubscribed;
    private CenteredViewArtifact? _centeredAstrometryEvidence;
    private bool _centeredAstrometryEvidenceSet;
    private sealed record DeferredCenteredPresentation(
        CenteredView TargetView,
        long Generation,
        bool PreserveDisplayedWorkspace,
        OperatorUiResult<CameraAgentCenteredPresentationRead> Result);

    private long _centeredPresentationGeneration;
    private CancellationTokenSource? _centeredPresentationCancellation;
    private bool _centeredPresentationLoading;
    private (string BaseId, string RevisionId)? _centeredPresentationInFlight;
    private DeferredCenteredPresentation? _deferredCenteredPresentation;

    internal bool HasDeferredCenteredPresentation => _deferredCenteredPresentation is not null;

    private bool IsCenteredLive => !IsArchived && (_centeredLiveContext is not null || _centeredDisplay is not null);
    private bool IsCenteredArchived => IsArchived && _centeredArchive is not null;
    private bool IsCenteredMode => IsCenteredLive || IsCenteredArchived;
    private bool HasCenteredSelectionError => IsCenteredArchived && _centeredArchive?.SelectionError is not null;
    private bool HasActiveSavedSelection => IsCenteredArchived && _centeredArchive?.SavedSelection is { } saved &&
        _centeredDisplay?.View is { } view &&
        saved.BaseId == view.Base.BaseId &&
        saved.RevisionId == view.Revision.RevisionId;

    private bool ShowCenteredLayeredHero =>
        CanShowCenteredLayeredHero(
            IsCenteredMode,
            _layerImageFailed,
            _centeredDisplay?.View,
            _centeredDisplay?.Presentation,
            _layers,
            _centeredDisplay?.PreviewUrl);

    internal static bool CanShowCenteredLayeredHero(
        bool isCenteredMode,
        bool layerImageFailed,
        CenteredView? view,
        CameraAgentLayeredPresentation? displayPresentation,
        CameraAgentLayeredPresentation? activeLayers,
        Uri? previewUrl) =>
        isCenteredMode &&
        !layerImageFailed &&
        view is not null &&
        displayPresentation is not null &&
        activeLayers is not null &&
        ReferenceEquals(activeLayers, displayPresentation) &&
        CameraAgentCenteredViewProjection.IsPresentationCompatible(view, activeLayers) &&
        previewUrl is not null;

    private string? CenteredBasePreviewUrl =>
        _centeredDisplay?.PreviewUrl?.OriginalString is { } url
            ? QueryHelpers.AddQueryString(url, "attempt", _layerPreviewAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;

    private Uri? CenteredBasePreviewUri =>
        CenteredBasePreviewUrl is { } url ? new Uri(url, UriKind.Relative) : null;

    private string? CenteredPreviewDownloadUrl =>
        _centeredDisplay?.View is { } view
            ? CameraAgentCenteredViewProjection.BoundPreviewDownloadUrl(view)
            : null;

    private string? CenteredArchiveRevisionUrl =>
        _centeredDisplay?.View is { } view
            ? CenteredViewLinks.Archive(
                view.Base.Definition.Reference.Capture.CaptureId,
                view.Base.BaseId,
                view.Revision.RevisionId)
            : null;

    private string LayerWorkspaceKey =>
        _centeredDisplay is { View: { } view }
            ? view.Base.BaseId
            : _layers?.CaptureId.ToString("D") ?? string.Empty;

    private string LayerImageKey =>
        _centeredDisplay is { View: { } view }
            ? FormattableString.Invariant($"{view.Base.BaseId}:{_layerPreviewAttempt}")
            : _layerPreviewAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private string CenteredLiveStateLabel => _livePaused
        ? "Paused"
        : _centeredLiveContext?.State switch
        {
            CenteredViewLiveState.Warming => "Warming",
            CenteredViewLiveState.Building => "Building stack",
            CenteredViewLiveState.Ready => "Ready",
            CenteredViewLiveState.Paused => "Paused",
            CenteredViewLiveState.Stale => "Idle / retained",
            CenteredViewLiveState.Gap => "Gap",
            _ => "Loading"
        };

    private string CenteredLiveStateChipClass => _livePaused
        ? "warning"
        : _centeredLiveContext?.State switch
        {
            CenteredViewLiveState.Ready => "success",
            CenteredViewLiveState.Warming or CenteredViewLiveState.Building or CenteredViewLiveState.Paused or CenteredViewLiveState.Stale => "warning",
            CenteredViewLiveState.Gap => "failure",
            _ => "pending"
        };

    private string CenteredReferenceLabel => _centeredDisplay?.View is { } view
        ? FormattableString.Invariant($"#{view.Base.Definition.Reference.Capture.CaptureSequence}")
        : "Warming up";

    private string CenteredAcquisitionHeadLabel => _centeredLiveContext?.AdmissionHead is { } head
        ? FormattableString.Invariant($"#{head.CaptureSequence}")
        : _presentation?.LatestCapture is { } latest
            ? FormattableString.Invariant($"#{latest.CaptureSequence}")
            : "Unavailable";

    private int CenteredContributorCount => _centeredDisplay?.View.Base.Definition.Contributors.Count ?? 0;

    private string CenteredContributorCountLabel => _centeredDisplay?.View is not { } view
        ? "0 frames"
        : view.Base.Definition.Kind == CenteredViewKind.Stack
            ? FormattableString.Invariant($"{view.Base.Definition.Contributors.Count} frames")
            : "1 frame (single)";

    private string CenteredViewKindLabel => _centeredDisplay?.View is not { } view
        ? "Awaiting ready base"
        : view.Base.Definition.Kind == CenteredViewKind.Stack
            ? FormattableString.Invariant($"Live stack centered on #{view.Base.Definition.Reference.Capture.CaptureSequence}")
            : FormattableString.Invariant($"Single exposure #{view.Base.Definition.Reference.Capture.CaptureSequence}");

    private string CenteredImageHeading => _centeredDisplay?.View is not { } view
        ? "Centered live sky"
        : view.Base.Definition.Kind == CenteredViewKind.Stack
            ? FormattableString.Invariant($"Live stack centered on #{view.Base.Definition.Reference.Capture.CaptureSequence}")
            : "Single exposure";

    private string CenteredImageSubtitle
    {
        get
        {
            if (_centeredDisplay?.View is not { } view)
            {
                return _centeredLiveContext?.State == CenteredViewLiveState.Warming
                    ? "Warming up the centered live pipeline. Waiting for the first ready base."
                    : "Waiting for a durable centered base.";
            }
            var definition = view.Base.Definition;
            var sourceText = definition.Preview.Source switch
            {
                CenteredViewImageSource.RegisteredLinear => "registered linear stack",
                CenteredViewImageSource.QualifiedLinear => "qualified linear exposure",
                _ => "raw fallback exposure"
            };
            var revisionNote = FormattableString.Invariant($"Revision #{view.Revision.Order} ({definition.Contributors.Count} contributing {(definition.Contributors.Count == 1 ? "frame" : "frames")}, {sourceText}).");
            if (_centeredDisplay.RetainedFromPriorRun)
            {
                return $"{revisionNote} Retained from the previous run while the current run warms up.";
            }
            if (_livePaused || _centeredLiveContext?.State == CenteredViewLiveState.Paused)
            {
                return $"{revisionNote} Live updates are paused; retaining this view and its age.";
            }
            return revisionNote;
        }
    }

    private string CenteredStageStatus
    {
        get
        {
            if (HasCenteredSelectionError)
            {
                return _centeredArchive!.SelectionError!;
            }
            if (_centeredDisplay is not { } centered)
            {
                return "Waiting for a ready centered base image.";
            }
            var drawableCount = centered.Presentation?.Layers.Count ?? 0;
            var pendingCount = centered.View.Revision.Layers.Count(static layer => layer.Readiness == CenteredViewReadiness.Waiting);
            if (_layerImageFailed)
            {
                return "Showing the unannotated centered base because overlay dimensions could not be verified.";
            }
            if (ShowCenteredLayeredHero && _layerInteractive)
            {
                return pendingCount > 0
                    ? FormattableString.Invariant($"Showing ready centered base with {SelectedLayerCount} of {drawableCount} ready overlays selected ({pendingCount} optional {(pendingCount == 1 ? "layer" : "layers")} still pending).")
                    : FormattableString.Invariant($"Showing ready centered base with {SelectedLayerCount} of {drawableCount} overlays selected.");
            }
            if (ShowCenteredLayeredHero)
            {
                return "Showing ready centered base while overlay registration verifies.";
            }
            if (_centeredPresentationLoading || centered.PresentationDeferred)
            {
                return "Showing ready centered base while optional overlays load.";
            }
            return centered.PresentationReason ?? "Showing ready centered base without interactive overlays.";
        }
    }

    private void SubscribeCenteredPublisher()
    {
        if (IsArchived || _centeredPublisherSubscribed || _disposeStarted != 0)
        {
            return;
        }
        _centeredPublisher = Services.GetService(typeof(ICenteredViewPublisher)) as ICenteredViewPublisher;
        if (_centeredPublisher is null)
        {
            return;
        }
        _centeredPublisher.Changed += OnCenteredViewChanged;
        _centeredPublisherSubscribed = true;
    }

    private void UnsubscribeCenteredPublisher()
    {
        if (!_centeredPublisherSubscribed || _centeredPublisher is null)
        {
            return;
        }
        _centeredPublisher.Changed -= OnCenteredViewChanged;
        _centeredPublisherSubscribed = false;
    }

    private void OnCenteredViewChanged(CenteredViewStream stream)
    {
        if (_disposeStarted != 0 || _accessDenied || _viewerOpen || _livePaused || IsArchived || _lifetime is not { IsCancellationRequested: false } lifetime)
        {
            return;
        }
        var activeStream = _centeredLiveContext?.Stream ?? _centeredDisplay?.Stream;
        if (activeStream is not null && activeStream != stream)
        {
            return;
        }
        var token = lifetime.Token;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => _ = state.Page.InvokeCenteredWakeRefreshAsync(state.Token),
            (Page: this, Token: token),
            preferLocal: false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Publisher wakeup hints are best-effort; periodic polling remains authoritative.")]
    private async Task InvokeCenteredWakeRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Periodic refresh remains active on transient failure or disposal races.
        }
    }

    private void ApplyCenteredArchive(CameraAgentCaptureDetailView archive)
    {
        CancelCenteredPresentationLoad();
        _showingBaseOnlyForMismatch = false;
        _gaveDisplayMismatchOneRefresh = false;
        _centeredArchive = archive.CenteredArchive;
        _centeredLiveContext = null;
        _centeredSaveMessage = null;
        _centeredSaveError = null;
        _centeredSavedUrl = null;
        _centeredSaving = false;

        if (_centeredArchive is not { } centered)
        {
            _centeredDisplay = null;
            return;
        }

        if (centered.SelectedView is not { } selected)
        {
            _centeredDisplay = null;
            _centeredAstrometryEvidence = null;
            _centeredAstrometryEvidenceSet = false;
            _layers = null;
            ClearAstrometry();
            _selectedLayers = new(StringComparer.Ordinal);
            _layerInteractive = false;
            _layerImageFailed = false;
            _bindLayers = false;
            _layerMessage = centered.SelectionError;
            return;
        }

        var previousBase = _centeredDisplay?.View.Base;
        var nextDisplay = new CenteredDisplayState(
            selected.Base.Definition.Stream,
            selected,
            centered.Presentation,
            centered.PresentationStatus,
            centered.PresentationReason,
            centered.PreviewUrl,
            CenteredLiveMode.Centered,
            RetainedFromPriorRun: false,
            PresentationDeferred: centered.PresentationDeferred);

        ApplyCenteredDisplayCore(
            nextDisplay,
            previousBase,
            explicitSavedSelection: centered.SavedSelection,
            captureChanged: true,
            cancellationToken: _lifetime!.Token);

        if (centered.PresentationDeferred)
        {
            StartCenteredPresentationLoad(selected, preserveDisplayedWorkspace: false, _lifetime.Token);
        }
    }

    private void ApplyCenteredLiveResult(
        CameraAgentCurrentSkyView view,
        bool manual,
        long started,
        CancellationToken cancellationToken)
    {
        var incoming = view.Centered!;
        var previous = _centeredDisplay;
        var action = ClassifyCenteredUpdate(previous, incoming, manual, started, view.MinimumRefreshInterval);
        _showingBaseOnlyForMismatch = false;
        _gaveDisplayMismatchOneRefresh = false;
        _centeredLiveContext = incoming.Context;
        _operations = view.Operations;
        _errorMessage = null;

        if (action is CenteredUpdateAction.Hold or CenteredUpdateAction.NoOp)
        {
            if (previous is not null)
            {
                var observed = view.Presentation.ObservedUtc;
                var retainedReference = previous.View.Base.Definition.Reference;
                var displayCapture = new CameraAgentPresentationCapture(
                    retainedReference.Capture.CaptureId,
                    retainedReference.Capture.CaptureSequence,
                    retainedReference.ExposureStartedUtc,
                    Math.Max(0, (long)(observed - retainedReference.ExposureStartedUtc).TotalSeconds),
                    _presentation?.DisplayCapture?.EvidenceOrigin ?? GalleryEvidenceOrigin.Unknown);
                var retainedSystem = _livePaused
                    ? new CameraAgentPresentationSystemStatus(
                        CameraAgentPresentationSystemState.Paused,
                        "Live view updates are paused; retaining the last displayed view and its age.",
                        observed)
                    : view.Presentation.System;
                var sameBase = incoming.View?.Base.BaseId == previous.View.Base.BaseId;
                var resolvedInterval = incoming.ExpectedCaptureInterval ??
                    CameraAgentOperatorUiService.ResolveCenteredExpectedCaptureInterval(
                        Services.GetService(typeof(CaptureScheduleRuntimeCoordinator)) as CaptureScheduleRuntimeCoordinator,
                        _facts);
                var retainedFreshness = sameBase
                    ? view.Presentation.ImageFreshness
                    : CameraAgentOperatorUiService.ComputeCenteredFreshness(
                        previous.View,
                        previous.RetainedFromPriorRun,
                        incoming.Context.State,
                        observed,
                        resolvedInterval);
                var retainedHistorical = sameBase
                    ? view.Presentation.IsHistoricalFallback
                    : previous.RetainedFromPriorRun;
                _presentation = (_presentation ?? view.Presentation) with
                {
                    ObservedUtc = observed,
                    ImageFreshness = retainedFreshness,
                    IsHistoricalFallback = retainedHistorical,
                    System = retainedSystem,
                    LatestCapture = view.Presentation.LatestCapture,
                    DisplayCapture = displayCapture
                };
                RetryCenteredAstrometryIfTransient(previous.View);
                var sameRevisionTarget = incoming.View is { } sameView &&
                    sameView.Base.BaseId == previous.View.Base.BaseId &&
                    sameView.Revision.RevisionId == previous.View.Revision.RevisionId
                        ? sameView
                        : _livePaused && (previous.PresentationDeferred || manual)
                            ? previous.View
                            : null;
                if (sameRevisionTarget is not null &&
                    (incoming.PresentationDeferred || previous.PresentationDeferred) &&
                    !_viewerOpen &&
                    (manual ||
                     previous.Presentation is null ||
                     previous.Presentation.Layers.Count < previous.View.Revision.Manifest.Layers.Count))
                {
                    StartCenteredPresentationLoad(sameRevisionTarget, preserveDisplayedWorkspace: false, cancellationToken);
                }
            }
            else
            {
                _presentation = view.Presentation;
                _facts = view.Facts;
                _factsUnavailableReason = view.FactsUnavailableReason;
            }
            return;
        }

        var nextView = incoming.View!;
        var nextDisplay = new CenteredDisplayState(
            nextView.Base.Definition.Stream,
            nextView,
            incoming.Presentation,
            incoming.PresentationStatus,
            incoming.PresentationReason,
            incoming.PreviewUrl,
            incoming.Mode,
            incoming.RetainedFromPriorRun,
            incoming.PresentationDeferred);

        var referenceCaptureId = nextView.Base.Definition.Reference.Capture.CaptureId;
        var referenceChanged = _runCaptureId != referenceCaptureId;
        var baseChanged = previous?.View.Base.BaseId != nextView.Base.BaseId;
        var revisionChanged = previous?.View.Revision.RevisionId != nextView.Revision.RevisionId;

        _presentation = view.Presentation;
        _facts = view.Facts;
        _factsUnavailableReason = view.FactsUnavailableReason;
        if (action == CenteredUpdateAction.Advance)
        {
            _lastCaptureChangeTimestamp = started;
        }

        if (referenceChanged)
        {
            _transient = null;
            _transientMessage = null;
            _liveExecutionId = null;
            _combinedProduct = null;
            _combinedObservationSpan = null;
            _lineageArtifactId = null;
            _lineageMessage = null;
            _runCaptureId = referenceCaptureId;
        }

        if (baseChanged)
        {
            _centeredSaveMessage = null;
            _centeredSaveError = null;
            _centeredSavedUrl = null;
        }

        // On an overlay-only revision for the SAME displayed base, keep the verified base <img>
        // mounted while fetching the new revision's optional overlays asynchronously so the base
        // image never remounts or flickers.
        if (!baseChanged && revisionChanged && incoming.PresentationDeferred && previous is { Presentation: not null })
        {
            StartCenteredPresentationLoad(nextView, preserveDisplayedWorkspace: true, cancellationToken);
            return;
        }

        ApplyCenteredDisplayCore(
            nextDisplay,
            previous?.View.Base,
            explicitSavedSelection: null,
            captureChanged: referenceChanged,
            cancellationToken: cancellationToken);

        if (incoming.PresentationDeferred)
        {
            StartCenteredPresentationLoad(nextView, preserveDisplayedWorkspace: false, cancellationToken);
        }
    }

    private void ApplyCenteredDisplayCore(
        CenteredDisplayState nextDisplay,
        CenteredViewBase? previousBase,
        CenteredViewSavedSelection? explicitSavedSelection,
        bool captureChanged,
        CancellationToken cancellationToken,
        bool cancelPendingPresentation = true)
    {
        if (cancelPendingPresentation)
        {
            CancelCenteredPresentationLoad();
        }

        var baseChanged = previousBase?.BaseId != nextDisplay.View.Base.BaseId;
        var revisionChanged = _centeredDisplay?.View.Revision.RevisionId != nextDisplay.View.Revision.RevisionId;
        var referenceCaptureId = nextDisplay.View.Base.Definition.Reference.Capture.CaptureId;

        if (!IsCenteredArchived)
        {
            var nextScopeKey = $"{LayerVisibilityKey}:centered:{CenteredVisibilityScopeKey(nextDisplay.View.Base)}";
            var incompatibleBase = previousBase is not null &&
                !CameraAgentCenteredViewProjection.IsVisibilityCarryOverCompatible(previousBase, nextDisplay.View.Base);
            if (incompatibleBase || !string.Equals(_layerVisibilityScopeKey, nextScopeKey, StringComparison.Ordinal))
            {
                _layerVisibility.Clear();
                _layerVisibilityRead = false;
                _layerVisibilityScopeKey = nextScopeKey;
            }
        }

        if (captureChanged || _centeredDisplay is null)
        {
            ResetLayers(referenceCaptureId, cancellationToken, discardDisplayedEvidence: true);
            _centeredAstrometryEvidence = null;
            _centeredAstrometryEvidenceSet = false;
        }

        _showingBaseOnlyForMismatch = false;
        _gaveDisplayMismatchOneRefresh = false;
        _centeredDisplay = nextDisplay;
        _layerLoading = false;
        _layersNotRetained = false;

        if (nextDisplay.Presentation is { } presentation &&
            CameraAgentCenteredViewProjection.IsPresentationCompatible(nextDisplay.View, presentation))
        {
            var presentationChanged = !CenteredPresentationsMatch(_layers, presentation);
            _layers = presentation;
            _layerMessage = null;
            _layerImageFailed = false;
            _selectedLayers = ResolveCenteredSelectedLayers(nextDisplay.View, presentation, previousBase, explicitSavedSelection);
            if (baseChanged || revisionChanged || presentationChanged || !_layerInteractive)
            {
                _layerInteractive = false;
                _bindGeneration++;
                _bindLayers = true;
            }
        }
        else
        {
            _layers = null;
            _selectedLayers = new(StringComparer.Ordinal);
            _layerInteractive = false;
            _bindLayers = false;
            _layerMessage = nextDisplay.PresentationDeferred
                ? null
                : nextDisplay.PresentationReason ??
                  (nextDisplay.PresentationStatus == CameraAgentLayeredPresentationStatus.NotRetained
                      ? null
                      : "Centered overlays are unavailable for this base. Showing the verified base image.");
        }

        var astrometryPin = FindCenteredAstrometryEvidence(nextDisplay.View);
        var retryTransient = astrometryPin is not null && IsTransientCenteredAstrometryFailure(_astrometry);
        if (captureChanged || !_centeredAstrometryEvidenceSet || _centeredAstrometryEvidence != astrometryPin || retryTransient)
        {
            _centeredAstrometryEvidence = astrometryPin;
            _centeredAstrometryEvidenceSet = true;
            ClearAstrometry();
            if (astrometryPin is not null && _layerCancellation is { IsCancellationRequested: false } astrometryCancellation)
            {
                _ = LoadAstrometryAsync(referenceCaptureId, Volatile.Read(ref _layerGeneration), astrometryCancellation.Token, astrometryPin);
            }
            else if (astrometryPin is null)
            {
                _astrometry = new(referenceCaptureId, "NotRetained", "centered-evidence-not-pinned");
            }
        }
    }

    private void CancelCenteredPresentationLoad()
    {
        Interlocked.Increment(ref _centeredPresentationGeneration);
        _centeredPresentationLoading = false;
        _centeredPresentationInFlight = null;
        _deferredCenteredPresentation = null;
        var previous = Interlocked.Exchange(ref _centeredPresentationCancellation, null);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The background optional overlay read captures the token and validates exact generation and (BaseId, RevisionId) before applying.")]
    private void StartCenteredPresentationLoad(
        CenteredView targetView,
        bool preserveDisplayedWorkspace,
        CancellationToken cancellationToken)
    {
        if (_accessDenied || Volatile.Read(ref _disposeStarted) != 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        var targetKey = (targetView.Base.BaseId, targetView.Revision.RevisionId);
        if (_centeredPresentationLoading && _centeredPresentationInFlight == targetKey)
        {
            return;
        }

        CancelCenteredPresentationLoad();
        var generation = Interlocked.Increment(ref _centeredPresentationGeneration);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var prior = Interlocked.Exchange(ref _centeredPresentationCancellation, linked);
        if (prior is not null)
        {
            prior.Cancel();
            prior.Dispose();
        }
        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            CancelCenteredPresentationLoad();
            return;
        }
        _centeredPresentationLoading = true;
        _centeredPresentationInFlight = targetKey;
        var token = linked.Token;
        // Offload starting the optional presentation read off the Blazor renderer thread so even a
        // synchronously blocking presentation read never delays StateHasChanged() for the ready base.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => _ = state.Page.LoadCenteredPresentationAsync(
                state.TargetView,
                state.Generation,
                state.PreserveDisplayedWorkspace,
                state.Token),
            (Page: this, TargetView: targetView, Generation: generation, PreserveDisplayedWorkspace: preserveDisplayedWorkspace, Token: token),
            preferLocal: false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional centered overlay reads must never block or fail the displayed ready base.")]
    private async Task LoadCenteredPresentationAsync(
        CenteredView targetView,
        long generation,
        bool preserveDisplayedWorkspace,
        CancellationToken cancellationToken)
    {
        OperatorUiResult<CameraAgentCenteredPresentationRead> result;
        try
        {
            using var deadline = new CancellationTokenSource(RefreshTimeout, TimeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            result = await OperatorService.GetCenteredPresentationAsync(
                targetView.Base.BaseId,
                targetView.Revision.RevisionId,
                bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = OperatorUiResult<CameraAgentCenteredPresentationRead>.Success(
                new(CameraAgentLayeredPresentationStatus.Unavailable, null,
                    "Centered overlays are temporarily unavailable. Showing the verified base image."));
        }

        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        await InvokeAsync(() => ApplyCenteredPresentationResult(targetView, generation, preserveDisplayedWorkspace, result, cancellationToken))
            .ConfigureAwait(false);
    }

    private void ApplyCenteredPresentationResult(
        CenteredView targetView,
        long generation,
        bool preserveDisplayedWorkspace,
        OperatorUiResult<CameraAgentCenteredPresentationRead> result,
        CancellationToken cancellationToken)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized)
        {
            if (!_accessDenied)
            {
                RevokeAccess();
            }
            return;
        }

        if (generation != Volatile.Read(ref _centeredPresentationGeneration) ||
            _disposeStarted != 0 ||
            _accessDenied ||
            cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _centeredPresentationLoading = false;
        _centeredPresentationInFlight = null;

        if (_viewerOpen)
        {
            _deferredCenteredPresentation = new(targetView, generation, preserveDisplayedWorkspace, result);
            StateHasChanged();
            return;
        }

        if (_centeredDisplay is not { } current)
        {
            StateHasChanged();
            return;
        }

        if (!string.Equals(current.View.Base.BaseId, targetView.Base.BaseId, StringComparison.Ordinal))
        {
            return;
        }

        var revisionAdvanced = !string.Equals(current.View.Revision.RevisionId, targetView.Revision.RevisionId, StringComparison.Ordinal);
        if (_livePaused && revisionAdvanced)
        {
            return;
        }

        if (preserveDisplayedWorkspace)
        {
            if (targetView.Revision.Order < current.View.Revision.Order)
            {
                return;
            }
        }
        else if (revisionAdvanced)
        {
            return;
        }

        CameraAgentLayeredPresentation? validated = null;
        var status = CameraAgentLayeredPresentationStatus.Unavailable;
        string? reason = "Centered overlays are temporarily unavailable. Showing the verified base image.";

        if (result.IsSuccess && result.Value is { } read)
        {
            if (read.Status == CameraAgentLayeredPresentationStatus.Found &&
                CameraAgentCenteredViewProjection.IsPresentationCompatible(targetView, read.Presentation))
            {
                validated = read.Presentation;
                status = CameraAgentLayeredPresentationStatus.Found;
                reason = read.Reason;
            }
            else if (read.Status == CameraAgentLayeredPresentationStatus.Found)
            {
                status = CameraAgentLayeredPresentationStatus.Malformed;
                reason = "The centered overlay presentation does not match the exact base preview.";
            }
            else
            {
                status = read.Status;
                reason = read.Reason;
            }
        }

        var presentationChanged = !CenteredPresentationsMatch(current.Presentation, validated) ||
            current.PresentationStatus != status ||
            !string.Equals(current.PresentationReason, reason, StringComparison.Ordinal) ||
            current.PresentationDeferred;

        if (revisionAdvanced || presentationChanged)
        {
            var nextDisplay = current with
            {
                View = targetView,
                Presentation = validated,
                PresentationStatus = status,
                PresentationReason = reason,
                PresentationDeferred = false
            };
            ApplyCenteredDisplayCore(
                nextDisplay,
                current.View.Base,
                explicitSavedSelection: IsCenteredArchived ? _centeredArchive?.SavedSelection : null,
                captureChanged: false,
                cancellationToken: _lifetime!.Token,
                cancelPendingPresentation: false);
        }

        StateHasChanged();
    }

    private static bool IsTransientCenteredAstrometryFailure(CameraAgentRetainedAstrometry? astrometry) =>
        astrometry is { Solution: null, Availability: "Unavailable" } &&
        astrometry.Reason is "read-capacity" or "retained-read-unavailable" or "retained-evidence-temporarily-unavailable";

    private void RetryCenteredAstrometryIfTransient(CenteredView view)
    {
        if (_accessDenied || _disposeStarted != 0 || _astrometryLoading)
        {
            return;
        }
        var astrometryPin = FindCenteredAstrometryEvidence(view);
        if (astrometryPin is null ||
            _centeredAstrometryEvidence != astrometryPin ||
            !IsTransientCenteredAstrometryFailure(_astrometry) ||
            _layerCancellation is not { IsCancellationRequested: false } astrometryCancellation)
        {
            return;
        }

        ClearAstrometry();
        _ = LoadAstrometryAsync(
            view.Base.Definition.Reference.Capture.CaptureId,
            Volatile.Read(ref _layerGeneration),
            astrometryCancellation.Token,
            astrometryPin);
    }

    private static bool CenteredPresentationsMatch(
        CameraAgentLayeredPresentation? current,
        CameraAgentLayeredPresentation? incoming)
    {
        if (ReferenceEquals(current, incoming))
        {
            return true;
        }
        if (current is null || incoming is null)
        {
            return false;
        }
        if (!string.Equals(current.PresentationIdentitySha256, incoming.PresentationIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(current.ManifestIdentitySha256, incoming.ManifestIdentitySha256, StringComparison.Ordinal) ||
            current.Layers.Count != incoming.Layers.Count)
        {
            return false;
        }
        for (var i = 0; i < current.Layers.Count; i++)
        {
            if (!string.Equals(current.Layers[i].IdentitySha256, incoming.Layers[i].IdentitySha256, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static CenteredViewArtifact? FindCenteredAstrometryEvidence(CenteredView view)
    {
        var referenceCaptureId = view.Base.Definition.Reference.Capture.CaptureId;
        return view.Revision.Layers
            .FirstOrDefault(layer =>
                layer.Key.Scope == CenteredViewLayerScope.Reference &&
                layer.Key.SourceCaptureId == referenceCaptureId &&
                layer.Key.Kind is AstrometricDiagnosticPresentation.ResidualsLayer or AstrometricDiagnosticPresentation.AssociationsLayer &&
                layer.Evidence is not null)
            ?.Evidence;
    }

    private static string CenteredVisibilityScopeKey(CenteredViewBase viewBase)
    {
        var camera = viewBase.Definition.Stream.Camera;
        var compat = viewBase.Definition.Preview.Product.Compatibility;
        return FormattableString.Invariant(
            $"{camera.AgentId}:{camera.CameraId}:{compat.WidthPixels}x{compat.HeightPixels}:{compat.LayoutIdentitySha256}:{compat.CoordinateIdentitySha256}");
    }

    private CenteredUpdateAction ClassifyCenteredUpdate(
        CenteredDisplayState? current,
        CameraAgentCenteredLiveView incoming,
        bool manual,
        long started,
        TimeSpan minimumRefreshInterval)
    {
        if (_livePaused && current is not null && incoming.Mode == current.Mode)
        {
            return CenteredUpdateAction.Hold;
        }

        if (incoming.View is not { } next)
        {
            return current is not null ? CenteredUpdateAction.Hold : CenteredUpdateAction.NoOp;
        }

        if (current is null || incoming.Mode != current.Mode)
        {
            return CenteredUpdateAction.Advance;
        }

        if (incoming.RetainedFromPriorRun && !current.RetainedFromPriorRun)
        {
            return CenteredUpdateAction.Hold;
        }

        var currentView = current.View;
        if (next.Base.Definition.Stream != current.Stream)
        {
            if (!incoming.RetainedFromPriorRun)
            {
                return CenteredUpdateAction.Advance;
            }
            if (next.Base.BaseId == currentView.Base.BaseId)
            {
                if (next.Revision.Order > currentView.Revision.Order)
                {
                    return CenteredUpdateAction.SwapInPlace;
                }
                if (next.Revision.Order == currentView.Revision.Order &&
                    !incoming.PresentationDeferred &&
                    incoming.Presentation is not null &&
                    !CenteredPresentationsMatch(current.Presentation, incoming.Presentation))
                {
                    return CenteredUpdateAction.SwapInPlace;
                }
                return CenteredUpdateAction.NoOp;
            }
            return next.Base.RegisteredUtc > currentView.Base.RegisteredUtc
                ? CenteredUpdateAction.Advance
                : CenteredUpdateAction.Hold;
        }

        var nextOrder = next.Base.Definition.ReferenceOrder;
        var currentOrder = currentView.Base.Definition.ReferenceOrder;
        if (nextOrder < currentOrder)
        {
            return CenteredUpdateAction.Hold;
        }

        if (nextOrder == currentOrder)
        {
            if (next.Base.Definition.Reference.Capture.CaptureId != currentView.Base.Definition.Reference.Capture.CaptureId)
            {
                return CenteredUpdateAction.Hold;
            }

            if (next.Base.BaseId == currentView.Base.BaseId)
            {
                if (next.Revision.Order < currentView.Revision.Order)
                {
                    return CenteredUpdateAction.Hold;
                }
                if (next.Revision.Order > currentView.Revision.Order)
                {
                    return CenteredUpdateAction.SwapInPlace;
                }
                if (!incoming.PresentationDeferred &&
                    incoming.Presentation is not null &&
                    !CenteredPresentationsMatch(current.Presentation, incoming.Presentation))
                {
                    return CenteredUpdateAction.SwapInPlace;
                }
                if (!incoming.PresentationDeferred &&
                    current.Presentation is null &&
                    incoming.PresentationStatus != current.PresentationStatus)
                {
                    return CenteredUpdateAction.SwapInPlace;
                }
                return CenteredUpdateAction.NoOp;
            }

            return incoming.Mode == CenteredLiveMode.Centered &&
                   next.Base.Definition.Kind > currentView.Base.Definition.Kind
                ? CenteredUpdateAction.SwapInPlace
                : CenteredUpdateAction.Hold;
        }

        if (incoming.Mode == CenteredLiveMode.Single ||
            next.Base.Definition.Kind == CenteredViewKind.Single)
        {
            return CenteredUpdateAction.Advance;
        }

        if (!manual && minimumRefreshInterval > TimeSpan.Zero &&
            TimeProvider.GetElapsedTime(_lastCaptureChangeTimestamp, started) + CaptureChangeSlack < minimumRefreshInterval)
        {
            return CenteredUpdateAction.Hold;
        }

        return CenteredUpdateAction.Advance;
    }

    private HashSet<string> ResolveCenteredSelectedLayers(
        CenteredView view,
        CameraAgentLayeredPresentation presentation,
        CenteredViewBase? previousBase,
        CenteredViewSavedSelection? explicitSavedSelection)
    {
        if (explicitSavedSelection is not null &&
            explicitSavedSelection.BaseId == view.Base.BaseId &&
            explicitSavedSelection.RevisionId == view.Revision.RevisionId)
        {
            var savedSet = explicitSavedSelection.EnabledLayerIds.ToHashSet(StringComparer.Ordinal);
            return presentation.Layers
                .Where(layer => savedSet.Contains(layer.IdentitySha256))
                .Select(static layer => layer.IdentitySha256)
                .ToHashSet(StringComparer.Ordinal);
        }

        if (IsCenteredArchived)
        {
            return presentation.Layers
                .Where(static layer => layer.EnabledByDefault)
                .Select(static layer => layer.IdentitySha256)
                .ToHashSet(StringComparer.Ordinal);
        }

        var compatibleBase = previousBase is null ||
            CameraAgentCenteredViewProjection.IsVisibilityCarryOverCompatible(previousBase, view.Base);
        if (!compatibleBase)
        {
            _layerVisibility.Clear();
            _layerVisibilityRead = false;
            return presentation.Layers
                .Where(static layer => layer.EnabledByDefault)
                .Select(static layer => layer.IdentitySha256)
                .ToHashSet(StringComparer.Ordinal);
        }

        return RememberedSelection(presentation);
    }

    private async Task SetCenteredLiveModeAsync(CenteredLiveMode mode)
    {
        if (IsArchived || _centeredLiveMode == mode || _disposeStarted != 0 || _accessDenied)
        {
            return;
        }
        _centeredLiveMode = mode;
        _livePaused = false;
        Interlocked.Exchange(ref _manualRefreshRequested, 1);
        if (_lifetime is not null)
        {
            await RequestRefreshAsync(_lifetime.Token);
        }
    }

    private async Task ToggleLivePausedAsync()
    {
        if (IsArchived || _disposeStarted != 0 || _accessDenied)
        {
            return;
        }
        _livePaused = !_livePaused;
        if (_livePaused)
        {
            // Keep an in-flight overlay load for the exact currently displayed revision so a paused
            // frame finishes loading its own overlays, while cancelling any load targeting a newer revision.
            var loadingDifferentRevision = _centeredDisplay is not { View: { } displayed } ||
                _centeredPresentationInFlight != (displayed.Base.BaseId, displayed.Revision.RevisionId);
            if (loadingDifferentRevision)
            {
                CancelCenteredPresentationLoad();
            }
            StateHasChanged();
            return;
        }
        if (_lifetime is not null)
        {
            Interlocked.Exchange(ref _manualRefreshRequested, 1);
            await RequestRefreshAsync(_lifetime.Token);
        }
        else
        {
            StateHasChanged();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Service errors must be sanitized for the optional save UI.")]
    private async Task SaveCenteredPersonalSelectionAsync()
    {
        if (_centeredDisplay?.View is not { } view || !ShowCenteredLayeredHero || !_layerInteractive ||
            _centeredSaving || _accessDenied || _disposeStarted != 0 || _lifetime is not { } lifetime)
        {
            return;
        }

        var baseId = view.Base.BaseId;
        var revisionId = view.Revision.RevisionId;
        var captureId = view.Base.Definition.Reference.Capture.CaptureId;
        var enabled = (_layers?.Layers ?? [])
            .Where(layer => _selectedLayers.Contains(layer.IdentitySha256))
            .Select(static layer => layer.IdentitySha256)
            .ToArray();

        _centeredSaving = true;
        _centeredSaveError = null;
        _centeredSaveMessage = null;
        _centeredSavedUrl = null;
        try
        {
            var result = await OperatorService.SaveCenteredViewSelectionAsync(
                captureId, baseId, revisionId, enabled, lifetime.Token);
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                RevokeAccess();
                return;
            }
            if (_disposeStarted != 0 || _accessDenied ||
                _centeredDisplay?.View.Base.BaseId != baseId ||
                _centeredDisplay.View.Revision.RevisionId != revisionId)
            {
                return;
            }
            if (result.IsSuccess && result.Value is { } saved)
            {
                _centeredSavedUrl = CenteredViewLinks.ArchiveSelection(captureId, saved.SelectionId);
                _centeredSaveMessage = FormattableString.Invariant(
                    $"Personal view selection saved ({saved.EnabledLayerIds.Count} {(saved.EnabledLayerIds.Count == 1 ? "layer" : "layers")} on rev #{view.Revision.Order}).");
            }
            else
            {
                _centeredSaveError = result.Message ?? "The personal view selection could not be saved.";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (_disposeStarted == 0 && !_accessDenied)
            {
                _centeredSaveError = "The personal view selection could not be saved. Please try again.";
            }
        }
        finally
        {
            if (_disposeStarted == 0 && !_accessDenied)
            {
                _centeredSaving = false;
            }
        }
    }

    private string ArchiveCenteredBaseLabel(CenteredViewArchiveBase archiveBase)
    {
        var definition = archiveBase.Base.Definition;
        var kindLabel = definition.Kind == CenteredViewKind.Stack
            ? FormattableString.Invariant($"Live stack centered on #{definition.Reference.Capture.CaptureSequence} ({definition.Contributors.Count} frames)")
            : "Single exposure";
        return definition.RecomputedFromBaseId is not null ? $"{kindLabel} (recomputed)" : kindLabel;
    }

    private string CenteredArchiveSelectionUrl(string baseId, string revisionId)
    {
        var captureId = _centeredArchive!.ReferenceCaptureId;
        var url = CenteredViewLinks.Archive(captureId, baseId, revisionId);
        var currentQuery = QueryHelpers.ParseQuery(new Uri(NavigationManager.Uri).Query);
        if (currentQuery.TryGetValue("returnUrl", out var returnUrl) && returnUrl.Count == 1 && !string.IsNullOrWhiteSpace(returnUrl[0]))
        {
            url = QueryHelpers.AddQueryString(url, "returnUrl", returnUrl[0]!);
        }
        return url;
    }

    private CameraAgentCenteredArchiveBaseSummary? ActiveCenteredArchiveBaseSummary =>
        _centeredArchive?.Bases.FirstOrDefault(summary =>
            summary.ArchiveBase.Base.BaseId == _centeredDisplay?.View.Base.BaseId);

    private List<CenteredLayerRow> GetCenteredLayerRows(string group)
    {
        if (_centeredDisplay?.View is not { } view)
        {
            return [];
        }

        var drawableById = (_layers?.Layers ?? [])
            .ToDictionary(static layer => layer.IdentitySha256, StringComparer.Ordinal);
        var contributorSequences = view.Base.Definition.Contributors
            .ToDictionary(
                static contributor => contributor.Exposure.Capture.CaptureId,
                static contributor => contributor.Exposure.Capture.CaptureSequence);

        var rows = new List<CenteredLayerRow>();
        foreach (var layer in view.Revision.Layers)
        {
            if (!string.Equals(CenteredLayerGroup(layer.Key.Kind), group, StringComparison.Ordinal))
            {
                continue;
            }

            CameraAgentPresentationLayer? presentationLayer = null;
            if (layer.Drawable is { } drawable &&
                layer.Readiness is CenteredViewReadiness.Ready or CenteredViewReadiness.Partial &&
                drawableById.TryGetValue(drawable.LayerIdentitySha256, out var matched))
            {
                presentationLayer = matched;
            }

            long? contributorSequence = layer.Key.Scope == CenteredViewLayerScope.Contributor &&
                layer.Key.SourceCaptureId is { } sourceId &&
                contributorSequences.TryGetValue(sourceId, out var seq)
                    ? seq
                    : null;

            var baseLabel = LayerLabel(layer.Key.Kind);
            var label = contributorSequence is { } contributorSeq
                ? FormattableString.Invariant($"{baseLabel} · #{contributorSeq}")
                : baseLabel;

            var presentationFailed = !_centeredPresentationLoading &&
                !_centeredDisplay.PresentationDeferred &&
                _layers is null;
            var degradedOnRead = presentationLayer is null &&
                layer.Drawable is not null &&
                layer.Readiness is CenteredViewReadiness.Ready or CenteredViewReadiness.Partial &&
                (_layers is not null || presentationFailed);

            var (readinessLabel, readinessCode) = degradedOnRead
                ? ("Unavailable", "Unavailable")
                : layer.Readiness switch
                {
                    CenteredViewReadiness.Ready => ("Ready", "Ready"),
                    CenteredViewReadiness.Partial => ("Partial", "Partial"),
                    CenteredViewReadiness.Waiting => ("Pending", "Pending"),
                    CenteredViewReadiness.AssessedEmpty => ("No detection", "NoDetection"),
                    CenteredViewReadiness.Incompatible => ("Incompatible", "Incompatible"),
                    CenteredViewReadiness.NotConfigured => ("Disabled", "Disabled"),
                    _ => ("Unavailable", "Unavailable")
                };

            var description = degradedOnRead
                ? "Overlay unavailable · showing the verified base image"
                : DescribeCenteredLayerReadiness(layer, presentationLayer, readinessLabel);
            rows.Add(new CenteredLayerRow(
                layer,
                presentationLayer,
                label,
                readinessLabel,
                readinessCode,
                description,
                contributorSequence,
                presentationLayer is not null));
        }

        return rows;
    }

    private static string DescribeCenteredLayerReadiness(
        CenteredViewLayer layer,
        CameraAgentPresentationLayer? presentationLayer,
        string readinessLabel)
    {
        var baseDescription = LayerDescription(layer.Key.Kind);
        var satelliteDetail = SatelliteDescription(presentationLayer?.Satellite);
        var reasonDetail = FormatCenteredLayerReason(layer.Reason);

        return layer.Readiness switch
        {
            CenteredViewReadiness.Ready => string.IsNullOrWhiteSpace(satelliteDetail)
                ? baseDescription
                : $"{baseDescription} {satelliteDetail}",
            CenteredViewReadiness.Partial => string.IsNullOrWhiteSpace(reasonDetail)
                ? $"Partial · {baseDescription}"
                : $"Partial ({reasonDetail}) · {baseDescription}",
            CenteredViewReadiness.Waiting => "Pending · waiting for optional evidence",
            CenteredViewReadiness.AssessedEmpty => string.IsNullOrWhiteSpace(reasonDetail)
                ? "No detection · assessed empty for this base"
                : $"No detection · {reasonDetail}",
            CenteredViewReadiness.Incompatible => string.IsNullOrWhiteSpace(reasonDetail)
                ? "Incompatible with this base"
                : $"Incompatible · {reasonDetail}",
            CenteredViewReadiness.NotConfigured => "Disabled · not configured for this profile",
            _ => readinessLabel
        };
    }

    private static string FormatCenteredLayerReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }
        return reason switch
        {
            "centered.awaiting-product" => "waiting for optional evidence",
            "centered.layer-not-configured" => "not configured for this profile",
            "centered.registration-map-unavailable" => "registration map unavailable for this contributor",
            "centered.segments-outside-registration" => "segments outside registered domain",
            "centered.rich-text-preview-too-small" => "preview too small for corner text",
            "centered.trail-geometry-unavailable" => "trail geometry unavailable",
            _ => reason.StartsWith("centered.", StringComparison.Ordinal)
                ? OperationsPage.SplitWords(reason["centered.".Length..].Replace('-', ' '))
                : reason
        };
    }

    private static string CenteredLayerGroup(string kind) => kind switch
    {
        "measured-deep-sky" or "MeasuredTransientTrails" or "measured-source-markers" or AstrometricDiagnosticPresentation.AssociationsLayer => "Measured",
        "scene-deep-sky" or "scene-annotation" or "star-annotations" or "scene-constellations" or "constellations" or "scene-cardinals" or "cardinal-directions" => "Sky context",
        _ => "Diagnostics"
    };
}
