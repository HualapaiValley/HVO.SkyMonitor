using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CurrentSkyPage
{
    private CameraAgentRetainedAstrometry? _astrometry;
    private CameraAgentLatestAstrometry? _latestAstrometry;
    private long _latestAstrometryRevision;
    private long _retainedAstrometryInvalidationRevision;
    private long _astrometryObservationRevision;
    private bool _latestAstrometryLoading;
    private bool _latestAstrometryRefreshQueued;
    private bool? _latestAstrometryReadSucceeded;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional summary reads must not fail the image view.")]
    private async Task LoadLatestAstrometryAsync(bool manual = false)
    {
        if (IsArchived || _accessDenied || _disposeStarted != 0) return;
        if (_latestAstrometryLoading)
        {
            _latestAstrometryRefreshQueued |= manual;
            return;
        }

        _latestAstrometryRefreshQueued = false;
        _latestAstrometryLoading = true;
        var revision = ++_latestAstrometryRevision;
        var previous = _latestAstrometry;
        var selected = SelectedMeasurementIdentity();
        var selectedRevision = _astrometryReadRevision;
        var invalidationRevision = _retainedAstrometryInvalidationRevision;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token, deadline.Token);
            var result = await OperatorService.GetLatestAcceptedAstrometryAsync(bounded.Token, previous, selected).ConfigureAwait(false);
            await InvokeAsync(async () =>
            {
                await ApplyLatestAstrometryReadAsync(result, previous, selected, selectedRevision,
                    invalidationRevision, revision, bounded.Token);
                ReplayQueuedLatestAstrometryRefresh(revision);
            });
        }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                if (!IsCurrentLatestAstrometryRead(revision)) return;
                _latestAstrometryLoading = false;
                _latestAstrometryReadSucceeded = false;
                StateHasChanged();
                ReplayQueuedLatestAstrometryRefresh(revision);
            });
        }
    }

    private void ReplayQueuedLatestAstrometryRefresh(long revision)
    {
        // Even a successful read can have verified its result before the explicit refresh.
        // A new read consumes the queued request and takes its own current identities and deadline.
        if (IsCurrentLatestAstrometryRead(revision) && _latestAstrometryRefreshQueued)
            _ = LoadLatestAstrometryAsync();
    }

    private async Task ApplyLatestAstrometryReadAsync(OperatorUiResult<CameraAgentLatestAstrometryRead> result,
        CameraAgentLatestAstrometry? previous, CameraAgentAstrometryAssessmentIdentity? requestedSelection,
        long requestedSelectionRevision, long requestedInvalidationRevision, long revision, CancellationToken cancellation)
    {
        if (result.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
        if (!IsCurrentLatestAstrometryRead(revision)) return;

        var read = result.Value;
        if (read is not null) Interlocked.Increment(ref _astrometryObservationRevision);
        var summary = SummaryIdentity(read?.Summary);
        var previousInvalid = read?.PreviousSummaryInvalidated == true ? SummaryIdentity(previous) : null;
        var selectedInvalid = read?.SelectedAssessmentInvalidated == true ? requestedSelection : null;
        if (SameIdentity(previousInvalid, selectedInvalid)) selectedInvalid = null;

        var requiresFreshSummary = _retainedAstrometryInvalidationRevision != requestedInvalidationRevision;
        var rejectedSummary = SameIdentity(summary, previousInvalid) || SameIdentity(summary, selectedInvalid);
        var reloadReportedSelection = false;
        var hasReportedInvalidation = previousInvalid is not null || selectedInvalid is not null;
        // An exact rejection suspends matching tools before any asynchronous recheck. A fresh
        // authorized layer or standalone read can install a repaired fit, including the same identity.
        if (previousInvalid is not null) reloadReportedSelection |= InvalidateAstrometryEvidence(previousInvalid);
        if (selectedInvalid is not null) reloadReportedSelection |= InvalidateAstrometryEvidence(selectedInvalid);
        var reportedInvalidationRevision = _retainedAstrometryInvalidationRevision;
        if (hasReportedInvalidation)
        {
            StateHasChanged();
            if (reloadReportedSelection) TryLoadLayers(_runCaptureId);
        }

        // Keep the bounded history flight active until its exact observations are applied.
        // A newer manual refresh is queued even while this response is awaiting a point check.
        while (IsCurrentLatestAstrometryRead(revision))
        {
            var selected = SelectedMeasurementIdentity();
            var selectionRevision = _astrometryReadRevision;
            var invalidationRevision = _retainedAstrometryInvalidationRevision;
            var selectedState = CameraAgentAstrometryPublicationState.Indeterminate;
            var summaryState = CameraAgentAstrometryPublicationState.Available;
            var checkedSelection = read is not null && selected is not null && selectionRevision != requestedSelectionRevision;
            if (checkedSelection)
            {
                selectedState = await ObserveAstrometryPublicationAsync(selected!, cancellation);
                if (!IsCurrentLatestAstrometryRead(revision)) return;
            }

            // A selected measurement may have proved this headline invalid while the history read
            // was returning. An older successful response cannot undo that observation.
            if (summary is not null && !rejectedSummary &&
                (requiresFreshSummary || invalidationRevision != reportedInvalidationRevision))
                summaryState = checkedSelection && SameIdentity(summary, selected)
                    ? selectedState : await ObserveAstrometryPublicationAsync(summary, cancellation);

            if (!IsCurrentLatestAstrometryRead(revision)) return;
            if (selectionRevision != _astrometryReadRevision || invalidationRevision != _retainedAstrometryInvalidationRevision)
                continue;

            var completed = read?.Completed == true && result.IsSuccess && !cancellation.IsCancellationRequested &&
                !rejectedSummary && summaryState == CameraAgentAstrometryPublicationState.Available;
            var reloadSelected = false;
            var currentInvalid = selectedState == CameraAgentAstrometryPublicationState.Invalidated ? selected : null;
            // This observation belongs to the selection revision checked above. A repair can
            // republish the same identity after the earlier history flags were applied.
            ApplyInvalidation(currentInvalid);
            if (summaryState == CameraAgentAstrometryPublicationState.Invalidated && !SameIdentity(summary, previousInvalid) &&
                !SameIdentity(summary, selectedInvalid) && !SameIdentity(summary, currentInvalid))
                ApplyInvalidation(summary);

            _latestAstrometryLoading = false;
            _latestAstrometryReadSucceeded = completed;
            if (completed) _latestAstrometry = read!.Summary;
            StateHasChanged();
            if (reloadSelected) TryLoadLayers(_runCaptureId);
            return;

            void ApplyInvalidation(CameraAgentAstrometryAssessmentIdentity? identity)
            {
                if (identity is null || selectedState == CameraAgentAstrometryPublicationState.Available &&
                    SameIdentity(identity, selected)) return;
                if (SameIdentity(identity, summary)) completed = false;
                reloadSelected |= InvalidateAstrometryEvidence(identity);
            }
        }
    }

    private bool IsCurrentLatestAstrometryRead(long revision) =>
        revision == _latestAstrometryRevision && _disposeStarted == 0 && !_accessDenied && !IsArchived;

    private CameraAgentAstrometryAssessmentIdentity? SelectedMeasurementIdentity()
        => RetainedIdentity(_layers?.Astrometry) ?? RetainedIdentity(_astrometry);

    private static CameraAgentAstrometryAssessmentIdentity? SummaryIdentity(CameraAgentLatestAstrometry? value)
        => value is null ? null : new(value.CaptureId, value.ArtifactId, value.SolutionIdentitySha256);

    private static bool SameIdentity(CameraAgentAstrometryAssessmentIdentity? first, CameraAgentAstrometryAssessmentIdentity? second)
        => first is not null && second is not null && first.CaptureId == second.CaptureId && first.ArtifactId == second.ArtifactId &&
           string.Equals(first.SolutionIdentitySha256, second.SolutionIdentitySha256, StringComparison.OrdinalIgnoreCase);

    private bool InvalidateAstrometryEvidence(CameraAgentAstrometryAssessmentIdentity identity, bool retryIncomingPrefetch = true)
    {
        Interlocked.Increment(ref _retainedAstrometryInvalidationRevision);
        Interlocked.Increment(ref _astrometryObservationRevision);
        WithdrawLatestAstrometry(identity);
        // A confirmed invalidation only fences a pending switch to the affected capture.
        if (retryIncomingPrefetch &&
            Volatile.Read(ref _incomingLayerPrefetch) is { } prefetch && prefetch.CaptureId == identity.CaptureId)
            prefetch.Invalidate();
        if (_runCaptureId != identity.CaptureId) return false;

        if (IsCenteredMode)
        {
            if (Matches(_astrometry, identity) || RetainedIdentity(_astrometry) is null)
            {
                ClearAstrometry();
            }
            return false;
        }

        var layered = _layers?.Astrometry;
        var matchingEvidence = Matches(layered, identity) || Matches(_astrometry, identity);
        var hasVerifiedEvidence = RetainedIdentity(layered) is not null || RetainedIdentity(_astrometry) is not null;
        // A pending read can carry the invalidated fit even if neither projection has installed it yet.
        // Rotate both read lifetimes. Keep a separately verified replacement already on the same frame.
        ResetLayers(identity.CaptureId, _lifetime!.Token, discardDisplayedEvidence: matchingEvidence || !hasVerifiedEvidence);
        return true;
    }

    private void WithdrawLatestAstrometry(CameraAgentAstrometryAssessmentIdentity identity)
    {
        if (!SameIdentity(SummaryIdentity(_latestAstrometry), identity)) return;
        _latestAstrometry = null;
        _latestAstrometryReadSucceeded = false;
    }

    private static CameraAgentAstrometryAssessmentIdentity? RetainedIdentity(CameraAgentRetainedAstrometry? value)
        => value is { Solution.Assessment.HasMeasuredMapping: true, ArtifactId: { } artifact, SolutionIdentitySha256: { } hash }
            ? new(value.CaptureId, artifact, hash) : null;

    private static bool Matches(CameraAgentRetainedAstrometry? value, CameraAgentAstrometryAssessmentIdentity identity)
        => value is not null && value.CaptureId == identity.CaptureId && value.ArtifactId == identity.ArtifactId &&
            string.Equals(value.SolutionIdentitySha256, identity.SolutionIdentitySha256, StringComparison.OrdinalIgnoreCase);

    private sealed record ObservedAstrometryResult<T>(OperatorUiResult<T> Result, long? ObservationRevision = null)
    {
        public OperatorUiResultKind Kind => Result.Kind;
        public bool IsSuccess => Result.IsSuccess;
        public T? Value => Result.Value;
    }

    private bool IsCurrentAstrometryObservation<T>(ObservedAstrometryResult<T>? observed)
        => observed?.ObservationRevision is not { } revision || revision == Volatile.Read(ref _astrometryObservationRevision);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An unverified optional measurement is never published.")]
    private async ValueTask<OperatorUiResult<CameraAgentAstrometryPublicationState>> ReadAstrometryObservationAsync(
        CameraAgentAstrometryAssessmentIdentity identity, CancellationToken cancellation)
    {
        if (!cancellation.IsCancellationRequested)
        {
            try
            {
                return await OperatorService.ReadAstrometryPublicationStateAsync(identity, cancellation).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A failed point read is indeterminate. It supplies no evidence of quarantine.
            }
        }
        return OperatorUiResult<CameraAgentAstrometryPublicationState>.Success(CameraAgentAstrometryPublicationState.Indeterminate);
    }

    private async ValueTask<CameraAgentAstrometryPublicationState> ObserveAstrometryPublicationAsync(
        CameraAgentAstrometryAssessmentIdentity identity, CancellationToken cancellation)
    {
        var observation = await ReadAstrometryObservationAsync(identity, cancellation);
        if (observation.Kind == OperatorUiResultKind.Unauthorized) RevokeAccess();
        return !cancellation.IsCancellationRequested && observation.IsSuccess
            ? observation.Value : CameraAgentAstrometryPublicationState.Indeterminate;
    }

    private async ValueTask<ObservedAstrometryResult<T>> ValidateAstrometryPublicationAsync<T>(
        Guid captureId, OperatorUiResult<T> result, CameraAgentRetainedAstrometry? evidence, CancellationToken cancellation)
    {
        if (!result.IsSuccess) return new(result);
        if (RetainedIdentity(evidence) is { } identity && identity.CaptureId == captureId)
        {
            var generation = Volatile.Read(ref _layerGeneration);
            while (!cancellation.IsCancellationRequested)
            {
                var revision = Volatile.Read(ref _astrometryObservationRevision);
                var observation = await ReadAstrometryObservationAsync(identity, cancellation).ConfigureAwait(false);
                if (observation.Kind == OperatorUiResultKind.Unauthorized)
                    return new(OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, observation.Message ?? "Access denied."));
                if (cancellation.IsCancellationRequested || !observation.IsSuccess ||
                    observation.Value == CameraAgentAstrometryPublicationState.Indeterminate) break;
                if (revision != Volatile.Read(ref _astrometryObservationRevision)) continue;
                if (observation.Value == CameraAgentAstrometryPublicationState.Available) return new(result, revision);
                if (observation.Value == CameraAgentAstrometryPublicationState.Invalidated)
                {
                    var withdrawn = false;
                    await InvokeAsync(() => withdrawn = ApplyObservedAstrometryInvalidation(identity, generation, revision, cancellation)).ConfigureAwait(false);
                    if (!withdrawn && revision != Volatile.Read(ref _astrometryObservationRevision) &&
                        generation == Volatile.Read(ref _layerGeneration)) continue;
                }
                break;
            }
        }
        return new(OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, "Selected measured evidence is temporarily unavailable."));
    }

    private bool ApplyObservedAstrometryInvalidation(CameraAgentAstrometryAssessmentIdentity identity, long generation,
        long revision, CancellationToken cancellation)
    {
        if (generation != _layerGeneration || revision != _astrometryObservationRevision || cancellation.IsCancellationRequested ||
            _disposeStarted != 0 || _accessDenied) return false;

        // An incoming fit can be invalid before either view has installed it. Fence outstanding
        // history without restarting the same prefetch whose result is already being rejected.
        InvalidateAstrometryEvidence(identity, retryIncomingPrefetch: false);
        StateHasChanged();
        return true;
    }

    private bool _astrometryLoading;
    private long _astrometryReadRevision;
    private string? _calculatorResult;
    private double? _pixelX;
    private double? _pixelY;
    private double? _skyFirst;
    private double? _skySecond;
    private string _skyCoordinates = "equatorial";
    private string AstrometryStatus => _astrometryLoading ? "Loading" : _astrometry?.Solution?.Assessment.Status switch
    {
        AstrometricAssessmentStatus.BudgetExceeded => "Budget exceeded",
        { } status => status.ToString(),
        _ => "Unavailable"
    };
    private string AstrometryChipClass => _astrometry?.Solution?.Assessment.Status switch
    {
        AstrometricAssessmentStatus.Accepted => "success",
        AstrometricAssessmentStatus.Rejected => "failure",
        AstrometricAssessmentStatus.BudgetExceeded => "warning",
        _ => "pending"
    };
    private bool CanCalculate => !_accessDenied && _astrometry?.Mapping is not null &&
        _astrometry.Solution?.Assessment.HasMeasuredMapping == true && _astrometry.CaptureId == _runCaptureId &&
        (DisplaySlot is { } slot &&
         (slot.ArtifactId == _astrometry.Solution?.Assessment.Frame.SourceArtifactId ||
          _astrometry.CompatibleBaseArtifactId is { } baseId && (slot.DisplayArtifactId == baseId || slot.ArtifactId == baseId)) ||
         _centeredDisplay is { } centered &&
         centered.View.Base.Definition.Reference.Capture.CaptureId == _astrometry.CaptureId &&
         centered.View.Base.Definition.Reference.Input.Raw.Artifact.ArtifactId == _astrometry.Solution?.Assessment.Frame.SourceArtifactId &&
         centered.View.Base.Definition.Preview.Transform.ReferenceWidth == _astrometry.WidthPixels &&
         centered.View.Base.Definition.Preview.Transform.ReferenceHeight == _astrometry.HeightPixels);
    private string CalculatorAvailability => CanCalculate
        ? "Source output pixels (zero-based); equatorial coordinates are mean of date at the capture midpoint. Geometric horizon; no refraction. Never updates rig calibration."
        : "Requires an accepted retained mapping and an exact source mapping to the selected image. Never updates rig calibration.";

    private void ClearAstrometry()
    {
        _astrometryReadRevision++;
        _astrometry = null;
        _astrometryLoading = false;
        _calculatorResult = null;
        _pixelX = _pixelY = _skyFirst = _skySecond = null;
    }

    private void ApplyAstrometry(Guid captureId, CameraAgentRetainedAstrometry value)
    {
        // Layer evidence supersedes any earlier standalone read for this capture, including its error path.
        _astrometryReadRevision++;
        if (value.CaptureId != captureId || value.Solution is { } solution &&
            (solution.Assessment.Frame.CaptureId != captureId || FactCapture?.ExposureStartedUtc != solution.Assessment.Frame.ExposureStartUtc ||
                value.SourceDescriptor is { } source &&
                (source.Capture.CaptureId != captureId || source.Artifact.ArtifactId != solution.Assessment.Frame.SourceArtifactId)))
            value = new(captureId, "Unavailable", "capture-source-mismatch");
        if (_astrometry?.SolutionIdentitySha256 != value.SolutionIdentitySha256) _calculatorResult = null;
        _astrometry = value;
        _astrometryLoading = false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional retained evidence cannot fail the image view.")]
    private async Task LoadAstrometryAsync(Guid captureId, long generation, CancellationToken cancellation, CenteredViewArtifact? evidencePin = null)
    {
        if (_astrometry?.Solution is not null || _astrometryLoading || _accessDenied || cancellation.IsCancellationRequested) return;
        _astrometryLoading = true;
        var revision = ++_astrometryReadRevision;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            var result = await OperatorService.GetRetainedAstrometryAsync(captureId, evidencePin, bounded.Token).ConfigureAwait(false);
            var observed = result.Value?.Solution?.Assessment.HasMeasuredMapping == true
                ? await ValidateAstrometryPublicationAsync(captureId, result, result.Value, bounded.Token).ConfigureAwait(false)
                : new(result);
            await InvokeAsync(() =>
            {
                // Authorization revocation is never hidden by a late response or by a changed capture.
                if (observed.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
                if (revision != _astrometryReadRevision || generation != _layerGeneration || captureId != _runCaptureId || _disposeStarted != 0 || _accessDenied || cancellation.IsCancellationRequested) return;
                if (IsCenteredMode && _centeredAstrometryEvidence != evidencePin) return;
                var eligible = IsCurrentAstrometryObservation(observed) ? observed.Value : null;
                if (evidencePin is not null && eligible is { Solution: not null } && eligible.ArtifactId != evidencePin.ArtifactId)
                {
                    eligible = new(captureId, "Unavailable", "centered-evidence-pin-mismatch");
                }
                ApplyAstrometry(captureId, eligible ?? new(captureId, "Unavailable", "retained-read-unavailable"));
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                if (revision != _astrometryReadRevision || generation != _layerGeneration || captureId != _runCaptureId || _disposeStarted != 0 || _accessDenied) return;
                if (IsCenteredMode && _centeredAstrometryEvidence != evidencePin) return;
                ApplyAstrometry(captureId, new(captureId, "Unavailable", "retained-read-unavailable"));
                StateHasChanged();
            });
        }
    }

    private void CalculatePixelToSky((long Revision, CameraAgentPresentationStage? Stage) requested)
    {
        if (requested != (_astrometryReadRevision, _selectedStage) || !CanCalculate) return;
        _calculatorResult = "Enter finite source-pixel coordinates inside the retained aperture and above the geometric horizon.";
        if (_pixelX is not { } x || _pixelY is not { } y || !InsideSource(x, y)) return;
        var point = _astrometry!.Mapping!.PixelToSky(new(x, y));
        if (point is not { AboveGeometricHorizon: true }) return;
        _skyCoordinates = "equatorial";
        _skyFirst = point.EquatorialMeanOfDate.RightAscensionHours;
        _skySecond = point.EquatorialMeanOfDate.DeclinationDegrees;
        _calculatorResult = FormattableString.Invariant($"RA {_skyFirst:0.########} h, Dec {_skySecond:0.########}° (mean of date); altitude {point.Horizontal.AltitudeDegrees:0.########}°, azimuth {point.Horizontal.AzimuthDegrees:0.########}°. Epoch {point.CoordinateEpochUtc:O}. Point uncertainty withheld; fit RMS is not prediction uncertainty.");
    }

    private void CalculateSkyToPixel((long Revision, CameraAgentPresentationStage? Stage) requested)
    {
        if (requested != (_astrometryReadRevision, _selectedStage) || !CanCalculate) return;
        _calculatorResult = "Enter finite RA [0, 24) h / Dec [−90, 90]°, or altitude [0, 90]° / azimuth [0, 360)°. The point must lie within the retained aperture above the horizon.";
        if (_skyFirst is not { } first || _skySecond is not { } second || !double.IsFinite(first) || !double.IsFinite(second)) return;
        EquatorialPoint equatorial;
        if (_skyCoordinates == "horizontal")
        {
            if (first < 0 || first > 90 || second < 0 || second >= 360) return;
            var frame = _astrometry!.Solution!.Assessment.Frame;
            equatorial = CoordinateTransforms.HorizontalToEquatorial(new(first, second), frame.MidpointUtc,
                frame.Observer.LatitudeDegrees, frame.Observer.LongitudeDegrees);
        }
        else
        {
            if (first < 0 || first >= 24 || second < -90 || second > 90) return;
            equatorial = new(first, second);
        }
        var mapping = _astrometry!.Mapping!;
        if (mapping.SkyToPixel(equatorial) is not { } pixel || !InsideSource(pixel.X, pixel.Y) || mapping.PixelToSky(pixel) is not { AboveGeometricHorizon: true }) return;
        _pixelX = pixel.X; _pixelY = pixel.Y;
        _calculatorResult = FormattableString.Invariant($"X {pixel.X:0.########}, Y {pixel.Y:0.########} source pixels. Point uncertainty withheld; fit RMS is not prediction uncertainty.");
    }

    private bool InsideSource(double x, double y) => double.IsFinite(x) && double.IsFinite(y) &&
        x >= 0 && y >= 0 && x < _astrometry!.WidthPixels && y < _astrometry.HeightPixels;

    private string AstrometryAbsence => _astrometry?.Reason switch
    {
        "assessment-not-retained" => "No astrometric assessment was retained for this capture.",
        "capture-source-mismatch" => "The retained assessment does not match this capture's source. Mapping is unavailable.",
        _ => "Retained astrometric evidence is unavailable for this capture."
    };

    private static string UncertaintyComponent(AstrometricPoseCovarianceComponent component) => component.BoresightStandardErrorDegrees is { } sigma
        ? FormattableString.Invariant($"{component.Status}; boresight σ {sigma:0.########}°, roll σ {component.RollStandardErrorDegrees:0.########}°, focal ratio σ {component.FocalScaleStandardError:0.########}")
        : $"{component.Status}: {component.ReasonCode}";
}
