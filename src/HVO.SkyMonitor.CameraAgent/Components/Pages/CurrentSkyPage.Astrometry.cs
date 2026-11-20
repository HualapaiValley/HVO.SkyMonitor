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
    private bool _latestAstrometryLoading;
    private bool? _latestAstrometryReadSucceeded;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional summary reads must not fail the image view.")]
    private async Task LoadLatestAstrometryAsync()
    {
        if (IsArchived || _accessDenied || _disposeStarted != 0 || _latestAstrometryLoading) return;
        _latestAstrometryLoading = true;
        var revision = ++_latestAstrometryRevision;
        var previous = _latestAstrometry;
        // A held image may have a different retained fit from the global headline.
        var selected = RetainedIdentity(_layers?.Astrometry) ?? RetainedIdentity(_astrometry);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token, deadline.Token);
            var result = await OperatorService.GetLatestAcceptedAstrometryAsync(bounded.Token, previous, selected).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                _latestAstrometryLoading = false;
                if (result.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
                if (revision != _latestAstrometryRevision || _disposeStarted != 0 || _accessDenied || IsArchived) return;
                var read = result.Value;
                CameraAgentAstrometryAssessmentIdentity? invalidatedPrevious = null;
                var reloadSelected = false;
                if (read?.PreviousSummaryInvalidated == true && previous is not null)
                {
                    _latestAstrometry = null;
                    invalidatedPrevious = new(previous.CaptureId, previous.ArtifactId, previous.SolutionIdentitySha256);
                    reloadSelected = InvalidateSelectedAssessment(invalidatedPrevious);
                }
                if (read?.SelectedAssessmentInvalidated == true && selected is not null && selected != invalidatedPrevious)
                    reloadSelected |= InvalidateSelectedAssessment(selected);
                var completed = read?.Completed == true && result.IsSuccess && !bounded.IsCancellationRequested;
                _latestAstrometryReadSucceeded = completed;
                if (completed) _latestAstrometry = read!.Summary;
                StateHasChanged();
                if (reloadSelected) TryLoadLayers(_runCaptureId);
            });
        }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                _latestAstrometryLoading = false;
                if (revision != _latestAstrometryRevision || _disposeStarted != 0 || _accessDenied || IsArchived) return;
                _latestAstrometryReadSucceeded = false;
                StateHasChanged();
            });
        }
    }

    private bool InvalidateSelectedAssessment(CameraAgentAstrometryAssessmentIdentity identity)
    {
        Interlocked.Increment(ref _retainedAstrometryInvalidationRevision);
        // A confirmed invalidation only fences a pending switch to the affected capture.
        if (Volatile.Read(ref _incomingLayerPrefetch) is { } prefetch && prefetch.CaptureId == identity.CaptureId)
            prefetch.Invalidate();
        if (_runCaptureId != identity.CaptureId) return false;

        var layered = _layers?.Astrometry;
        var matchingEvidence = Matches(layered, identity) || Matches(_astrometry, identity);
        var hasVerifiedEvidence = RetainedIdentity(layered) is not null || RetainedIdentity(_astrometry) is not null;
        // A pending read can carry the invalidated fit even if neither projection has installed it yet.
        // Rotate both read lifetimes. Keep a separately verified replacement already on the same frame.
        ResetLayers(identity.CaptureId, _lifetime!.Token, discardDisplayedEvidence: matchingEvidence || !hasVerifiedEvidence);
        return true;
    }

    private static CameraAgentAstrometryAssessmentIdentity? RetainedIdentity(CameraAgentRetainedAstrometry? value)
        => value is { Solution.Assessment.HasMeasuredMapping: true, ArtifactId: { } artifact, SolutionIdentitySha256: { } hash }
            ? new(value.CaptureId, artifact, hash) : null;

    private static bool Matches(CameraAgentRetainedAstrometry? value, CameraAgentAstrometryAssessmentIdentity identity)
        => value is not null && value.CaptureId == identity.CaptureId && value.ArtifactId == identity.ArtifactId &&
            string.Equals(value.SolutionIdentitySha256, identity.SolutionIdentitySha256, StringComparison.OrdinalIgnoreCase);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An unverified optional measurement is never published.")]
    private async ValueTask<OperatorUiResult<T>> ValidateAstrometryPublicationAsync<T>(
        Guid captureId, OperatorUiResult<T> result, CameraAgentRetainedAstrometry? evidence, CancellationToken cancellation)
    {
        if (!result.IsSuccess) return result;
        if (RetainedIdentity(evidence) is { } identity && identity.CaptureId == captureId)
        {
            var generation = Volatile.Read(ref _layerGeneration);
            try
            {
                // The response has arrived but has not yet reached the view. Check its exact inputs
                // without reconstructing the fit or competing for the raw reader.
                var observation = await OperatorService.ReadAstrometryPublicationStateAsync(identity, cancellation).ConfigureAwait(false);
                if (observation.Kind == OperatorUiResultKind.Unauthorized)
                    return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, observation.Message ?? "Access denied.");
                if (!cancellation.IsCancellationRequested && observation.IsSuccess)
                {
                    if (observation.Value == CameraAgentAstrometryPublicationState.Available) return result;
                    if (observation.Value == CameraAgentAstrometryPublicationState.Invalidated)
                        await InvokeAsync(() => WithdrawObservedSelectedFit(identity, generation, cancellation)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A failed point read can recover on the next refresh. Existing verified evidence
                // remains visible until the reader confirms that its exact lineage is invalid.
            }
        }
        return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, "Selected measured evidence is temporarily unavailable.");
    }

    private void WithdrawObservedSelectedFit(CameraAgentAstrometryAssessmentIdentity identity, long generation, CancellationToken cancellation)
    {
        if (generation != _layerGeneration || cancellation.IsCancellationRequested ||
            _disposeStarted != 0 || _accessDenied || _runCaptureId != identity.CaptureId) return;

        // There may only be pending evidence. A positive check started before this observation
        // must retire with the same lifetime even when no fit has reached the renderer.
        InvalidateSelectedAssessment(identity);
        StateHasChanged();
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
        _astrometry.Solution?.Assessment.HasMeasuredMapping == true && _astrometry.CaptureId == _runCaptureId && DisplaySlot is { } slot &&
        (slot.ArtifactId == _astrometry.Solution?.Assessment.Frame.SourceArtifactId ||
         _astrometry.CompatibleBaseArtifactId is { } baseId && (slot.DisplayArtifactId == baseId || slot.ArtifactId == baseId));
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
    private async Task LoadAstrometryAsync(Guid captureId, long generation, CancellationToken cancellation)
    {
        if (_astrometry?.Solution is not null || _astrometryLoading || _accessDenied || cancellation.IsCancellationRequested) return;
        _astrometryLoading = true;
        var revision = ++_astrometryReadRevision;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            var result = await OperatorService.GetRetainedAstrometryAsync(captureId, bounded.Token).ConfigureAwait(false);
            if (result.Value?.Solution?.Assessment.HasMeasuredMapping == true)
                result = await ValidateAstrometryPublicationAsync(captureId, result, result.Value, bounded.Token).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                // Authorization revocation is never hidden by a late response or by a changed capture.
                if (result.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
                if (revision != _astrometryReadRevision || generation != _layerGeneration || captureId != _runCaptureId || _disposeStarted != 0 || _accessDenied || cancellation.IsCancellationRequested) return;
                ApplyAstrometry(captureId, result.Value ?? new(captureId, "Unavailable", "retained-read-unavailable"));
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                if (revision != _astrometryReadRevision || generation != _layerGeneration || captureId != _runCaptureId || _disposeStarted != 0 || _accessDenied) return;
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
