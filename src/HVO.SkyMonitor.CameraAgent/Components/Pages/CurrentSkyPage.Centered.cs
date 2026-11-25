using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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
        bool RetainedFromPriorRun);

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

    private bool IsCenteredLive => !IsArchived && (_centeredLiveContext is not null || _centeredDisplay is not null);
    private bool IsCenteredArchived => IsArchived && _centeredArchive is not null;
    private bool IsCenteredMode => IsCenteredLive || IsCenteredArchived;
    private bool HasCenteredSelectionError => IsCenteredArchived && _centeredArchive?.SelectionError is not null;
    private bool HasActiveSavedSelection => IsCenteredArchived && _centeredArchive?.SavedSelection is { } saved &&
        _centeredDisplay?.View is { } view &&
        saved.BaseId == view.Base.BaseId &&
        saved.RevisionId == view.Revision.RevisionId;

    private bool ShowCenteredLayeredHero =>
        IsCenteredMode &&
        !_layerImageFailed &&
        _centeredDisplay is { Presentation: { } presentation } centered &&
        CameraAgentCenteredViewProjection.IsPresentationCompatible(centered.View, presentation) &&
        centered.PreviewUrl is not null;

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
            ? FormattableString.Invariant($"{view.Base.BaseId}:{view.Revision.RevisionId}")
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
        _ = InvokeCenteredWakeRefreshAsync(lifetime.Token);
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
            _layers = null;
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
            RetainedFromPriorRun: false);

        ApplyCenteredDisplayCore(
            nextDisplay,
            previousBase,
            explicitSavedSelection: centered.SavedSelection,
            captureChanged: true,
            cancellationToken: _lifetime!.Token);
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
                _presentation = (_presentation ?? view.Presentation) with
                {
                    ObservedUtc = observed,
                    System = retainedSystem,
                    LatestCapture = view.Presentation.LatestCapture,
                    DisplayCapture = displayCapture
                };
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
            incoming.RetainedFromPriorRun);

        var referenceCaptureId = nextView.Base.Definition.Reference.Capture.CaptureId;
        var referenceChanged = _runCaptureId != referenceCaptureId;
        var baseChanged = previous?.View.Base.BaseId != nextView.Base.BaseId;

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

        ApplyCenteredDisplayCore(
            nextDisplay,
            previous?.View.Base,
            explicitSavedSelection: null,
            captureChanged: referenceChanged,
            cancellationToken: cancellationToken);
    }

    private void ApplyCenteredDisplayCore(
        CenteredDisplayState nextDisplay,
        CenteredViewBase? previousBase,
        CenteredViewSavedSelection? explicitSavedSelection,
        bool captureChanged,
        CancellationToken cancellationToken)
    {
        var baseChanged = previousBase?.BaseId != nextDisplay.View.Base.BaseId;
        var revisionChanged = _centeredDisplay?.View.Revision.RevisionId != nextDisplay.View.Revision.RevisionId;
        var referenceCaptureId = nextDisplay.View.Base.Definition.Reference.Capture.CaptureId;

        if (captureChanged)
        {
            ResetLayers(referenceCaptureId, cancellationToken, discardDisplayedEvidence: true);
        }

        _centeredDisplay = nextDisplay;
        _layerLoading = false;
        _layersNotRetained = false;

        if (nextDisplay.Presentation is { } presentation &&
            CameraAgentCenteredViewProjection.IsPresentationCompatible(nextDisplay.View, presentation))
        {
            _layers = presentation;
            _layerMessage = null;
            _layerImageFailed = false;
            _selectedLayers = ResolveCenteredSelectedLayers(nextDisplay.View, presentation, previousBase, explicitSavedSelection);
            if (baseChanged || revisionChanged || !_layerInteractive)
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
            _layerMessage = nextDisplay.PresentationReason ??
                (nextDisplay.PresentationStatus == CameraAgentLayeredPresentationStatus.NotRetained
                    ? null
                    : "Centered overlays are unavailable for this base. Showing the verified base image.");
        }

        if (_astrometry?.Solution is null && !_astrometryLoading && _layerCancellation is { IsCancellationRequested: false } astrometryCancellation)
        {
            _ = LoadAstrometryAsync(referenceCaptureId, Volatile.Read(ref _layerGeneration), astrometryCancellation.Token);
        }
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
                return next.Revision.Order > currentView.Revision.Order
                    ? CenteredUpdateAction.SwapInPlace
                    : CenteredUpdateAction.NoOp;
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
                return current.Presentation is null && incoming.Presentation is not null
                    ? CenteredUpdateAction.SwapInPlace
                    : CenteredUpdateAction.NoOp;
            }

            return incoming.Mode == CenteredLiveMode.Centered &&
                   next.Base.Definition.Kind > currentView.Base.Definition.Kind
                ? CenteredUpdateAction.SwapInPlace
                : CenteredUpdateAction.Hold;
        }

        if (incoming.Mode == CenteredLiveMode.Single)
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

        var compatibleBase = previousBase is null ||
            CameraAgentCenteredViewProjection.IsVisibilityCarryOverCompatible(previousBase, view.Base);
        if (!compatibleBase)
        {
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
        if (!_livePaused && _lifetime is not null)
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

            var (readinessLabel, readinessCode) = layer.Readiness switch
            {
                CenteredViewReadiness.Ready => ("Ready", "Ready"),
                CenteredViewReadiness.Partial => ("Partial", "Partial"),
                CenteredViewReadiness.Waiting => ("Pending", "Pending"),
                CenteredViewReadiness.AssessedEmpty => ("No detection", "NoDetection"),
                CenteredViewReadiness.Incompatible => ("Incompatible", "Incompatible"),
                CenteredViewReadiness.NotConfigured => ("Disabled", "Disabled"),
                _ => ("Unavailable", "Unavailable")
            };

            var description = DescribeCenteredLayerReadiness(layer, presentationLayer, readinessLabel);
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
        "MeasuredTransientTrails" or "measured-source-markers" or AstrometricDiagnosticPresentation.AssociationsLayer => "Measured",
        "scene-deep-sky" or "scene-annotation" or "star-annotations" or "scene-constellations" or "constellations" or "scene-cardinals" or "cardinal-directions" => "Sky context",
        _ => "Diagnostics"
    };
}
