using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class CurrentSkyPage
{
    private CameraAgentRetainedAstrometry? _astrometry;
    private bool _astrometryLoading;
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
        _astrometry = null;
        _astrometryLoading = false;
        _calculatorResult = null;
        _pixelX = _pixelY = _skyFirst = _skySecond = null;
    }

    private void ApplyAstrometry(Guid captureId, CameraAgentRetainedAstrometry value)
    {
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
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            var result = await OperatorService.GetRetainedAstrometryAsync(captureId, bounded.Token).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                // Authorization revocation is never hidden by a late response or by a changed capture.
                if (result.Kind == OperatorUiResultKind.Unauthorized) { RevokeAccess(); return; }
                if (generation != _layerGeneration || captureId != _runCaptureId || _disposeStarted != 0 || _accessDenied || cancellation.IsCancellationRequested) return;
                ApplyAstrometry(captureId, result.Value ?? new(captureId, "Unavailable", "retained-read-unavailable"));
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await InvokeAsync(() =>
            {
                if (generation != _layerGeneration || captureId != _runCaptureId || _disposeStarted != 0 || _accessDenied) return;
                ApplyAstrometry(captureId, new(captureId, "Unavailable", "retained-read-unavailable"));
                StateHasChanged();
            });
        }
    }

    private void CalculatePixelToSky()
    {
        _calculatorResult = "Enter finite source-pixel coordinates inside the retained aperture and above the geometric horizon.";
        if (!CanCalculate || _pixelX is not { } x || _pixelY is not { } y || !InsideSource(x, y)) return;
        var point = _astrometry!.Mapping!.PixelToSky(new(x, y));
        if (point is not { AboveGeometricHorizon: true }) return;
        _skyCoordinates = "equatorial";
        _skyFirst = point.EquatorialMeanOfDate.RightAscensionHours;
        _skySecond = point.EquatorialMeanOfDate.DeclinationDegrees;
        _calculatorResult = FormattableString.Invariant($"RA {_skyFirst:0.########} h, Dec {_skySecond:0.########}° (mean of date); altitude {point.Horizontal.AltitudeDegrees:0.########}°, azimuth {point.Horizontal.AzimuthDegrees:0.########}°. Epoch {point.CoordinateEpochUtc:O}. Point uncertainty withheld; fit RMS is not prediction uncertainty.");
    }

    private void CalculateSkyToPixel()
    {
        _calculatorResult = "Enter finite RA [0, 24) h / Dec [−90, 90]°, or altitude [0, 90]° / azimuth [0, 360)°. The point must lie within the retained aperture above the horizon.";
        if (!CanCalculate || _skyFirst is not { } first || _skySecond is not { } second || !double.IsFinite(first) || !double.IsFinite(second)) return;
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
