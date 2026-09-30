namespace HVO.SkyMonitor.Astronomy;

/// <summary>Host-neutral, bounded measured astrometry. This API proposes evidence and never mutates last-good state.</summary>
public static class AstrometricSolver
{
    public static AstrometricSolveResult Solve(AstrometricFrameContext frame, AstrometricCalibration calibration,
        AstrometricCatalogData catalog, IReadOnlyList<AstrometricDetection> detections,
        AstrometricSolverOptions? options = null, CancellationToken cancellationToken = default) =>
        Execute(frame, calibration, catalog, detections, options ?? new(), null, cancellationToken);

    public static AstrometricSolveResult Refine(AstrometricFrameContext frame, AstrometricCalibration calibration,
        AstrometricCatalogData catalog, IReadOnlyList<AstrometricDetection> detections,
        AstrometricFrameAssessment previousAccepted, AstrometricSolverOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previousAccepted);
        return Execute(frame, calibration, catalog, detections, options ?? new(), previousAccepted, cancellationToken);
    }

    /// <summary>Materializes an injected catalog before the bounded numerical solve. Provider cancellation is mandatory.</summary>
    public static async ValueTask<AstrometricSolveResult> SolveAsync(AstrometricFrameContext frame,
        AstrometricCalibration calibration, IAstrometricCatalogSource source, IReadOnlyList<AstrometricDetection> detections,
        AstrometricSolverOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var catalog = await source.ReadAsync(options.MaximumCatalogMagnitude, 2500, cancellationToken).ConfigureAwait(false);
        return Solve(frame, calibration, catalog, detections, options, cancellationToken);
    }

    private static AstrometricSolveResult Execute(AstrometricFrameContext frame, AstrometricCalibration calibration,
        AstrometricCatalogData catalog, IReadOnlyList<AstrometricDetection> detections, AstrometricSolverOptions options,
        AstrometricFrameAssessment? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(detections);
        frame.Validate(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (detections.Count > 10000) throw new ArgumentException("Detection count exceeds10000.", nameof(detections));
        var copy = detections.ToArray();
        if (copy.Any(d => d is null || d.Index < 0 || !double.IsFinite(d.Pixel.X) || !double.IsFinite(d.Pixel.Y) ||
            !calibration.Projection.ContainsSample(d.Pixel.X, d.Pixel.Y) || !double.IsFinite(d.Flux) || d.Flux < 0) ||
            copy.Select(d => d.Index).Distinct().Count() != copy.Length) throw new ArgumentException("Invalid or duplicate measured centroids.", nameof(detections));
        copy = copy.OrderBy(d => d.Index).ToArray();
        if (previous is not null) AstrometricEvidenceJson.Validate(previous);
        var mode = previous is null ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm;
        var control = new AstrometricWorkControl(previous is null ? options.ColdBudgetMilliseconds : options.WarmBudgetMilliseconds, cancellationToken);
        AstrometricSolveResult Failure(AstrometricAssessmentStatus status, string code, string reason) =>
            Create(frame, calibration, catalog, options, mode, status, code, reason, null, null, [], new(control.ElapsedMilliseconds, 0, 0, 0, 0, 0, status == AstrometricAssessmentStatus.BudgetExceeded), previous?.IdentitySha256);
        if (!catalog.IsCompleteForRequestedMagnitude) return Failure(AstrometricAssessmentStatus.Unavailable, "catalog-incomplete", "Catalog source reported incomplete requested coverage; no fit was attempted.");
        if (catalog.CoordinateModel != AstrometricConventions.CoordinateModel) return Failure(AstrometricAssessmentStatus.Unavailable, "coordinate-model-unsupported", "Only explicitly declared fixed-position J2000 precession is supported.");
        if (copy.Length < 12) return Failure(AstrometricAssessmentStatus.Rejected, "insufficient-detections", "At least12 measured sources are required.");
        if (previous is not null)
        {
            AstrometricEvidenceJson.Validate(previous);
            if (!previous.HasMeasuredMapping || previous.CalibrationIdentitySha256 != calibration.IdentitySha256 ||
                previous.CatalogIdentitySha256 != catalog.IdentitySha256 || previous.CatalogSelectionIdentitySha256 != catalog.SelectionIdentitySha256 || previous.SettingsIdentitySha256 != options.IdentitySha256 ||
                previous.SolverVersion != AstrometricConventions.SolverVersion || previous.Frame.Observer != frame.Observer ||
                previous.Frame.ObserverIdentitySha256 != frame.ObserverIdentitySha256 || previous.Frame.MidpointUtc > frame.MidpointUtc ||
                (frame.MidpointUtc - previous.Frame.MidpointUtc).TotalSeconds > options.MaximumWarmAgeSeconds)
                return Failure(AstrometricAssessmentStatus.Rejected, "warm-context-incompatible", "Warm prior is rejected, stale, or belongs to different calibration/catalog/settings/observer evidence; reacquire explicitly.");
        }
        try
        {
            control.Check();
            var measured = copy.Select(d => new CoreDetection(d.Index, d.Pixel.X, d.Pixel.Y, d.Flux, d.Flux)).ToArray();
            var site = new CoreSite(frame.Observer.LatitudeDegrees, frame.Observer.LongitudeDegrees);
            CoreResult core;
            if (previous is null)
            {
                var search = new CoreSolverOptions(options.MinimumFocalScale, options.MaximumFocalScale, options.FocalScaleStep,
                    options.TriangleDetectionCount, options.ImageTriangleLimit, options.HypothesisLimit, options.CandidateLimit, options.MaximumCatalogMagnitude);
                core = AstrometricSolverCore.Solve(measured, catalog.Stars, SolverOptics.From(calibration.Projection), site, frame.MidpointUtc, search, control);
            }
            else
            {
                var prior = new CoreResult(true, "accepted", "trusted-prior", SolverOptics.From(AstrometricMapping.Projection(calibration, previous)), 1, null, [], 0, 0, 0, 0, 0, false, 0, "warm");
                core = AstrometricSolverCore.SolveWarm(measured, catalog.Stars, prior, site, frame.MidpointUtc, options.WarmBudgetMilliseconds, options.MaximumCatalogMagnitude, control);
            }
            control.Check();
            AstrometricFitParameters? parameters = null;
            if (core.Accepted && core.Solution is { } solution)
            {
                var equatorial = CoordinateTransforms.HorizontalToEquatorial(new(solution.BoresightAltitude, solution.BoresightAzimuth), frame.MidpointUtc, site.Latitude, site.Longitude);
                parameters = new(solution.BoresightAltitude, solution.BoresightAzimuth, solution.Roll,
                    solution.FocalX / calibration.Projection.FocalLengthXPixels, equatorial.RightAscensionHours, equatorial.DeclinationDegrees);
            }
            var quality = core.Quality is { } q ? new AstrometricFitQuality(q.Score0To100, q.FittingStars, q.VerificationStars, q.ExpectedFittingStars,
                q.FittingRmsPixels, q.VerificationRmsPixels, q.WidthCoverage, q.HeightCoverage) : null;
            var associations = core.Associations.Select(a => new AstrometricAssociation(a.CatalogId, a.DetectionIndex, a.ResidualPixels, a.Verification)).OrderBy(a => a.CatalogId, StringComparer.Ordinal).ToArray();
            var limit = core.BudgetExhausted || core.Reason.Contains("limit", StringComparison.OrdinalIgnoreCase) || core.Reason.Contains("bound", StringComparison.OrdinalIgnoreCase) && !core.Accepted;
            var status = core.Accepted ? AstrometricAssessmentStatus.Accepted : limit ? AstrometricAssessmentStatus.BudgetExceeded : AstrometricAssessmentStatus.Rejected;
            var reasonCode = core.Accepted ? "accepted" : core.Reason.StartsWith("Ambiguous", StringComparison.Ordinal) ? "ambiguous" : limit ? "resource-limit" : "acquisition-or-quality-failed";
            return Create(frame, calibration, catalog, options, mode, status, reasonCode, core.Reason, parameters, quality, associations,
                new(control.ElapsedMilliseconds, core.CatalogIndexStars, core.CatalogTriangles, core.ImageTriangles, core.Hypotheses, core.DistinctCandidates, limit), previous?.IdentitySha256);
        }
        catch (TimeoutException)
        {
            return Failure(AstrometricAssessmentStatus.BudgetExceeded, "time-budget", "Cooperative numerical-work budget exceeded; no proposed mapping replaces last-good evidence.");
        }
    }
    private static AstrometricSolveResult Create(AstrometricFrameContext frame, AstrometricCalibration calibration,
        AstrometricCatalogData catalog, AstrometricSolverOptions options, AstrometricSolveMode mode, AstrometricAssessmentStatus status,
        string code, string reason, AstrometricFitParameters? parameters, AstrometricFitQuality? quality,
        AstrometricAssociation[] associations, AstrometricExecutionMetrics metrics, string? previousIdentity)
    {
        var assessment = new AstrometricFrameAssessment(AstrometricFrameAssessment.CurrentSchemaVersion, string.Empty, frame,
            calibration.IdentitySha256, catalog.IdentitySha256, catalog.SelectionIdentitySha256,
            AstrometricConventions.SolverVersion, options.IdentitySha256, AstrometricConventions.CoordinateModel,
            AstrometricConventions.Refraction, AstrometricConventions.PixelCoordinates, mode, status, code, reason,
            parameters, quality, associations.Length > 0 ? AstrometricIdentity.Hash(associations) : null, previousIdentity);
        assessment = assessment with { IdentitySha256 = AstrometricIdentity.Hash(assessment) };
        return new(assessment, associations, metrics);
    }
}
