using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Host-neutral location and reason of a measured source excluded before association.</summary>
public sealed record AstrometricMeasurementExclusion(PixelPoint Pixel, string ReasonCode);

/// <summary>Per-detection centroid covariance in square pixels. It is an unvalidated engineering estimate.</summary>
public sealed record AstrometricPixelCovariance(int DetectionIndex, double Xx, double Xy, double Yy);

/// <summary>Declared diagnostic geometry. Defaults mirror the solver's final association, border and isolation radii.</summary>
public sealed record AstrometricDiagnosticsOptions(double AssociationRadiusPixels = 1.5, double EdgeMarginPixels = 6,
    double IsolationPixels = 12, double ExclusionRadiusPixels = 2, int RadiusBinCount = 4, int AzimuthSectorCount = 8,
    double MagnitudeBinWidth = 1, int OccupancyGridSize = 4, double MaximumConditionNumber = 1e8, double OffsetSourceRadiusPixels = 4)
{
    [JsonIgnore]
    public string IdentitySha256 => AstrometricIdentity.Hash(new { schema = "astrometric-residual-diagnostics-settings-v1", options = this });
    public void Validate()
    {
        if (!double.IsFinite(AssociationRadiusPixels) || AssociationRadiusPixels is <= 0 or > 64 || !double.IsFinite(EdgeMarginPixels) || EdgeMarginPixels is < 0 or > 1024 ||
            !double.IsFinite(IsolationPixels) || IsolationPixels is < 0 or > 1024 || !double.IsFinite(ExclusionRadiusPixels) || ExclusionRadiusPixels is <= 0 or > 64 ||
            RadiusBinCount is < 1 or > 64 || AzimuthSectorCount is < 1 or > 360 || !double.IsFinite(MagnitudeBinWidth) || MagnitudeBinWidth is < .1 or > 10 ||
            OccupancyGridSize is < 1 or > 64 || !double.IsFinite(MaximumConditionNumber) || MaximumConditionNumber <= 1 ||
            !double.IsFinite(OffsetSourceRadiusPixels) || OffsetSourceRadiusPixels <= AssociationRadiusPixels || OffsetSourceRadiusPixels > 64)
            throw new ArgumentException("Invalid astrometric residual diagnostic settings.");
    }
}

/// <summary>Measured-minus-predicted centroid for one solver association.</summary>
public sealed record AstrometricResidual(string CatalogId, int DetectionIndex, bool Verification, double Magnitude,
    PixelPoint Predicted, PixelPoint Measured, double DeltaX, double DeltaY, double ResidualPixels,
    double RadiusFraction, double AzimuthDegrees, double? NormalizedResidualSquared);

/// <summary>Residual summary for one bin of a declared dimension; unmatched counts include only association-eligible predictions.</summary>
public sealed record AstrometricResidualBin(string Dimension, int Index, double Lower, double Upper, int FittingCount,
    int VerificationCount, int UnmatchedEligibleCount, double? RmsPixels, double? MeanDeltaX, double? MeanDeltaY, double? Recall);

public sealed record AstrometricUnmatchedPrediction(string CatalogId, double Magnitude, PixelPoint Predicted, string ReasonCode);
public sealed record AstrometricUnassociatedDetection(int DetectionIndex, PixelPoint Pixel, string ReasonCode);
public sealed record AstrometricReasonCount(string ReasonCode, int Count);
public sealed record AstrometricParameterBoundHit(string Parameter, string Bound, double Value, double Limit);
/// <summary>Spectral conditioning of the four-parameter (three rotations, log focal scale) normal matrix over fitting stars.</summary>
public sealed record AstrometricConditioning(int FittingStars, double? ConditionNumber, double? SmallestEigenvalue, double? LargestEigenvalue, string Status);
public sealed record AstrometricSpatialOccupancy(int GridSize, int CellsWithEligiblePredictions, int CellsWithFittingStars, double? OccupiedFraction);
public sealed record AstrometricTimeResidualBin(int Index, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int FrameCount, int MappedFrameCount,
    int FittingCount, int VerificationCount, int UnmatchedEligibleCount, double? RmsPixels, double? MeanDeltaX, double? MeanDeltaY, double? Recall);

/// <summary>Per-frame residual evidence bound to the exact assessment, calibration, catalog, solver, detector and diagnostic identities.</summary>
public sealed record AstrometricResidualDiagnostics(string SchemaVersion, string IdentitySha256,
    string AssessmentIdentitySha256, string CalibrationIdentitySha256, string CatalogIdentitySha256, string CatalogSelectionIdentitySha256,
    string SolverSettingsIdentitySha256, string DetectionAlgorithmVersion, string DetectionSettingsIdentitySha256,
    string MeasurementInputIdentitySha256, string DiagnosticsSettingsIdentitySha256, DateTimeOffset MidpointUtc,
    AstrometricAssessmentStatus Status, string ReasonCode, bool HasMeasuredMapping, int DetectionCount, int MeasurementExclusionCount,
    int PredictedStarCount, int EligiblePredictionCount, IReadOnlyList<AstrometricResidual> Residuals, IReadOnlyList<AstrometricResidualBin> Bins,
    IReadOnlyList<AstrometricUnmatchedPrediction> UnmatchedPredictions, IReadOnlyList<AstrometricUnassociatedDetection> UnassociatedDetections,
    IReadOnlyList<AstrometricReasonCount> UnmatchedPredictionReasonCounts, IReadOnlyList<AstrometricReasonCount> UnassociatedDetectionReasonCounts,
    IReadOnlyList<AstrometricReasonCount> MeasurementExclusionReasonCounts, IReadOnlyList<AstrometricParameterBoundHit> ParameterBoundHits,
    AstrometricConditioning? Conditioning, AstrometricSpatialOccupancy? Occupancy, double? FittingRmsPixels, double? VerificationRmsPixels,
    double? MaximumAssociationResidualDiscrepancyPixels, double? MedianNormalizedResidualSquared, string CovarianceStatus)
{
    public const string CurrentSchemaVersion = "astrometric-residual-diagnostics-v1";
}

/// <summary>Reason codes. Prediction reasons are evaluated in declared order; detection reasons by nearest prediction.</summary>
public static class AstrometricDiagnosticReasons
{
    public const string OutsideAperture = "outside-aperture";
    public const string NearEdge = "near-edge";
    public const string CrowdedPrediction = "crowded-prediction";
    public const string Ambiguous = "ambiguous";
    public const string ClaimedByOther = "claimed-by-other";
    public const string NotAssociatedBySolver = "not-associated-by-solver";
    public const string MeasurementExcludedPrefix = "measurement-excluded:";
    public const string OffsetMeasuredSource = "offset-measured-source";
    public const string NoMeasuredSource = "no-measured-source";
    public const string NoCatalogPrediction = "no-catalog-prediction";
    public const string NearAssociatedPrediction = "near-associated-prediction";
    public const string NearIneligiblePrediction = "near-ineligible-prediction";
    public const string NearUnassociatedPrediction = "near-unassociated-prediction";
}

/// <summary>Deterministic read-only diagnostics over a completed solve. It never refits, re-associates or changes acceptance.</summary>
public static class AstrometricResidualAnalyzer
{
    public const int MaximumMeasurementExclusions = 100000;
    public const string CovarianceUnavailable = "unavailable-no-measurement-covariance";
    public const string CovarianceUnvalidated = "measurement-covariance-only-unvalidated";

    public static AstrometricResidualDiagnostics Analyze(AstrometricCalibration calibration, AstrometricCatalogData catalog,
        AstrometricSolverOptions solverOptions, AstrometricSolveResult result, IReadOnlyList<AstrometricDetection> detections,
        IReadOnlyList<AstrometricMeasurementExclusion>? exclusions = null, IReadOnlyList<AstrometricPixelCovariance>? covariances = null,
        AstrometricDiagnosticsOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calibration); ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(solverOptions);
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(detections);
        options ??= new(); options.Validate(); solverOptions.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var assessment = result.Assessment; AstrometricEvidenceJson.Validate(assessment);
        if (assessment.CalibrationIdentitySha256 != calibration.IdentitySha256 || assessment.CatalogIdentitySha256 != catalog.IdentitySha256 ||
            assessment.CatalogSelectionIdentitySha256 != catalog.SelectionIdentitySha256 || assessment.SettingsIdentitySha256 != solverOptions.IdentitySha256)
            throw new ArgumentException("Diagnostics require the exact calibration, catalog selection and solver settings of the assessment.", nameof(result));
        var associations = result.Associations.ToArray();
        var associationIdentity = associations.Length > 0 ? AstrometricIdentity.Hash(associations) : null;
        if (!string.Equals(associationIdentity, assessment.AssociationIdentitySha256, StringComparison.Ordinal))
            throw new ArgumentException("Associations do not match the assessment's association identity.", nameof(result));

        if (detections.Count > 10000) throw new ArgumentException("Detection count exceeds10000.", nameof(detections));
        var measured = detections.ToArray();
        if (measured.Any(d => d is null || d.Index < 0 || !double.IsFinite(d.Pixel.X) || !double.IsFinite(d.Pixel.Y) || !double.IsFinite(d.Flux) || d.Flux < 0 ||
            !calibration.Projection.ContainsSample(d.Pixel.X, d.Pixel.Y)) || measured.Select(d => d.Index).Distinct().Count() != measured.Length)
            throw new ArgumentException("Invalid or duplicate measured centroids.", nameof(detections));
        measured = [.. measured.OrderBy(d => d.Index)];
        var byIndex = measured.ToDictionary(d => d.Index);
        var stars = catalog.Stars.ToDictionary(s => s.Id, StringComparer.Ordinal);
        if (associations.Any(a => !byIndex.ContainsKey(a.DetectionIndex) || !stars.ContainsKey(a.CatalogId)))
            throw new ArgumentException("Associations reference detections or catalog stars that were not supplied.", nameof(detections));

        var excluded = (exclusions ?? []).ToArray();
        if (excluded.Length > MaximumMeasurementExclusions) throw new ArgumentException("Measurement exclusion count exceeds100000.", nameof(exclusions));
        if (excluded.Any(e => e is null || !double.IsFinite(e.Pixel.X) || !double.IsFinite(e.Pixel.Y) || string.IsNullOrWhiteSpace(e.ReasonCode) || e.ReasonCode.Length > 128))
            throw new ArgumentException("Invalid measurement exclusion.", nameof(exclusions));
        var covariance = (covariances ?? []).ToArray();
        if (covariance.Any(c => c is null || !byIndex.ContainsKey(c.DetectionIndex) || !double.IsFinite(c.Xx) || !double.IsFinite(c.Xy) || !double.IsFinite(c.Yy) ||
            c.Xx <= 0 || c.Yy <= 0 || c.Xx * c.Yy - c.Xy * c.Xy <= 0) || covariance.Select(c => c.DetectionIndex).Distinct().Count() != covariance.Length)
            throw new ArgumentException("Covariances must be unique, positive definite and reference supplied detections.", nameof(covariances));
        var covarianceByIndex = covariance.ToDictionary(c => c.DetectionIndex);
        var measurementIdentity = AstrometricIdentity.Hash(new { schema = "astrometric-measurement-input-v1", measured, excluded, covariance = covariance.OrderBy(c => c.DetectionIndex).ToArray() });
        var exclusionCounts = Counts(excluded.Select(e => e.ReasonCode));

        AstrometricResidualDiagnostics Create(bool mapped, int predicted, int eligibleCount, AstrometricResidual[] residuals, AstrometricResidualBin[] bins,
            AstrometricUnmatchedPrediction[] unmatched, AstrometricUnassociatedDetection[] unassociated, AstrometricParameterBoundHit[] bounds,
            AstrometricConditioning? conditioning, AstrometricSpatialOccupancy? occupancy, double? discrepancy)
        {
            var normalized = residuals.Where(r => r.NormalizedResidualSquared is not null).Select(r => r.NormalizedResidualSquared!.Value).Order().ToArray();
            var value = new AstrometricResidualDiagnostics(AstrometricResidualDiagnostics.CurrentSchemaVersion, string.Empty, assessment.IdentitySha256,
                calibration.IdentitySha256, catalog.IdentitySha256, catalog.SelectionIdentitySha256, solverOptions.IdentitySha256,
                assessment.Frame.DetectionAlgorithmVersion, assessment.Frame.DetectionSettingsIdentitySha256, measurementIdentity, options.IdentitySha256,
                assessment.Frame.MidpointUtc, assessment.Status, assessment.ReasonCode, mapped, measured.Length, excluded.Length, predicted, eligibleCount,
                residuals, bins, unmatched, unassociated, Counts(unmatched.Select(u => u.ReasonCode)), Counts(unassociated.Select(u => u.ReasonCode)),
                exclusionCounts, bounds, conditioning, occupancy, Rms(residuals.Where(r => !r.Verification)), Rms(residuals.Where(r => r.Verification)),
                discrepancy, normalized.Length == 0 ? null : normalized.Length % 2 == 1 ? normalized[normalized.Length / 2] : (normalized[normalized.Length / 2 - 1] + normalized[normalized.Length / 2]) / 2,
                covariance.Length == 0 ? CovarianceUnavailable : CovarianceUnvalidated);
            return value with { IdentitySha256 = AstrometricIdentity.Hash(value) };
        }
        if (!assessment.HasMeasuredMapping) return Create(false, 0, 0, [], [], [], [], [], null, null, null);

        var projection = AstrometricMapping.Projection(calibration, assessment);
        // Sensor bounds are not enforced so that clipped-readout predictions reach the outside-aperture classification.
        var projector = ProjectorFactory.Create(projection with { EnforceSensorBounds = false });
        var utc = assessment.Frame.MidpointUtc; var site = new CoreSite(assessment.Frame.Observer.LatitudeDegrees, assessment.Frame.Observer.LongitudeDegrees);
        var predictions = new List<Prediction>();
        foreach (var star in catalog.Stars.Where(s => s.Magnitude <= solverOptions.MaximumCatalogMagnitude))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var horizontal = AstrometricMath.Horizontal(star, utc, site);
            if (horizontal.AltitudeDegrees <= 0 || projector.Project(horizontal) is not { } pixel || !double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y)) continue;
            predictions.Add(new(star, horizontal, pixel));
        }
        double m = options.EdgeMarginPixels; int width = projection.WidthPixels, height = projection.HeightPixels;
        foreach (var p in predictions)
            p.Ineligible = !projection.ContainsSample(p.Pixel.X, p.Pixel.Y) ? AstrometricDiagnosticReasons.OutsideAperture :
                p.Pixel.X <= m || p.Pixel.Y <= m || p.Pixel.X >= width - m || p.Pixel.Y >= height - m ? AstrometricDiagnosticReasons.NearEdge : null;
        var inBounds = predictions.Where(p => p.Ineligible is null).ToArray(); var predictionGrid = new PointGrid<Prediction>(inBounds, p => p.Pixel, Math.Max(8, options.IsolationPixels));
        foreach (var p in inBounds)
            if (predictionGrid.Near(p.Pixel, options.IsolationPixels, inclusive: true).Any(q => !ReferenceEquals(q, p))) p.Ineligible = AstrometricDiagnosticReasons.CrowdedPrediction;
        var eligibleCount = predictions.Count(p => p.Ineligible is null);
        cancellationToken.ThrowIfCancellationRequested();

        var associated = associations.ToDictionary(a => a.CatalogId, StringComparer.Ordinal);
        var usedDetections = associations.ToDictionary(a => a.DetectionIndex);
        var predictionById = predictions.ToDictionary(p => p.Star.Id, StringComparer.Ordinal);
        double? discrepancy = null; var residuals = new List<AstrometricResidual>();
        foreach (var association in associations)
        {
            var star = stars[association.CatalogId]; var detection = byIndex[association.DetectionIndex];
            var horizontal = AstrometricMath.Horizontal(star, utc, site);
            if (!predictionById.TryGetValue(star.Id, out var prediction))
                throw new ArgumentException("An association references a star the accepted mapping does not project.", nameof(result));
            var dx = detection.Pixel.X - prediction.Pixel.X; var dy = detection.Pixel.Y - prediction.Pixel.Y; var distance = Math.Sqrt(dx * dx + dy * dy);
            discrepancy = Math.Max(discrepancy ?? 0, Math.Abs(distance - association.ResidualPixels));
            double? chi = covarianceByIndex.TryGetValue(detection.Index, out var c) ? (c.Yy * dx * dx - 2 * c.Xy * dx * dy + c.Xx * dy * dy) / (c.Xx * c.Yy - c.Xy * c.Xy) : null;
            residuals.Add(new(star.Id, detection.Index, association.Verification, star.Magnitude, prediction.Pixel, detection.Pixel, dx, dy, distance,
                RadiusFraction(projection, prediction.Pixel), Normalize(horizontal.AzimuthDegrees), chi));
        }

        var detectionGrid = new PointGrid<AstrometricDetection>(measured, d => d.Pixel, 8);
        var exclusionGrid = new PointGrid<AstrometricMeasurementExclusion>(excluded, e => e.Pixel, 8);
        var unmatched = new List<AstrometricUnmatchedPrediction>();
        foreach (var p in predictions.Where(p => !associated.ContainsKey(p.Star.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = p.Ineligible;
            if (reason is null)
            {
                var near = detectionGrid.Near(p.Pixel, options.AssociationRadiusPixels, inclusive: false).Select(d => (Detection: d, Distance: AstrometricMath.Distance(d.Pixel, p.Pixel)))
                    .OrderBy(d => d.Distance).ThenBy(d => d.Detection.Index).Take(2).ToArray();
                if (near.Length > 1 && near[1].Distance < near[0].Distance * 1.5) reason = AstrometricDiagnosticReasons.Ambiguous;
                else if (near.Length > 0) reason = usedDetections.ContainsKey(near[0].Detection.Index) ? AstrometricDiagnosticReasons.ClaimedByOther : AstrometricDiagnosticReasons.NotAssociatedBySolver;
                else if (exclusionGrid.Near(p.Pixel, options.ExclusionRadiusPixels, inclusive: true).Select(e => (Exclusion: e, Distance: AstrometricMath.Distance(e.Pixel, p.Pixel)))
                    .OrderBy(e => e.Distance).ThenBy(e => e.Exclusion.ReasonCode, StringComparer.Ordinal).FirstOrDefault() is { Exclusion: { } exclusion })
                    reason = AstrometricDiagnosticReasons.MeasurementExcludedPrefix + exclusion.ReasonCode;
                else if (detectionGrid.Near(p.Pixel, options.OffsetSourceRadiusPixels, inclusive: false).Any(d => !usedDetections.ContainsKey(d.Index)))
                    reason = AstrometricDiagnosticReasons.OffsetMeasuredSource;
                else reason = AstrometricDiagnosticReasons.NoMeasuredSource;
            }
            unmatched.Add(new(p.Star.Id, p.Star.Magnitude, p.Pixel, reason));
        }

        var allPredictions = new PointGrid<Prediction>(predictions, p => p.Pixel, 8);
        var unassociated = new List<AstrometricUnassociatedDetection>();
        foreach (var d in measured.Where(d => !usedDetections.ContainsKey(d.Index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nearest = allPredictions.Near(d.Pixel, options.AssociationRadiusPixels, inclusive: false).OrderBy(p => AstrometricMath.Distance(p.Pixel, d.Pixel))
                .ThenBy(p => p.Star.Id, StringComparer.Ordinal).FirstOrDefault();
            unassociated.Add(new(d.Index, d.Pixel, nearest is null ? AstrometricDiagnosticReasons.NoCatalogPrediction :
                associated.ContainsKey(nearest.Star.Id) ? AstrometricDiagnosticReasons.NearAssociatedPrediction :
                nearest.Ineligible is not null ? AstrometricDiagnosticReasons.NearIneligiblePrediction : AstrometricDiagnosticReasons.NearUnassociatedPrediction));
        }

        var unmatchedEligible = predictions.Where(p => p.Ineligible is null && !associated.ContainsKey(p.Star.Id)).ToArray();
        var bins = new List<AstrometricResidualBin>();
        void Dimension(string name, Func<AstrometricResidual, int> residualKey, Func<Prediction, int> predictionKey, Func<int, (double Lower, double Upper)> edges)
        {
            foreach (var key in residuals.Select(residualKey).Concat(unmatchedEligible.Select(predictionKey)).Distinct().Order())
            {
                var members = residuals.Where(r => residualKey(r) == key).ToArray(); var missing = unmatchedEligible.Count(p => predictionKey(p) == key);
                var (lower, upper) = edges(key);
                bins.Add(Bin(name, key, lower, upper, members, missing));
            }
        }
        int RadiusKey(double f) => Math.Min(options.RadiusBinCount - 1, (int)Math.Floor(f * options.RadiusBinCount));
        var sector = 360d / options.AzimuthSectorCount;
        int AzimuthKey(double azimuth) => Math.Min(options.AzimuthSectorCount - 1, (int)Math.Floor(azimuth / sector));
        int MagnitudeKey(double magnitude) => (int)Math.Floor(magnitude / options.MagnitudeBinWidth);
        Dimension("radius-fraction", r => RadiusKey(r.RadiusFraction), p => RadiusKey(RadiusFraction(projection, p.Pixel)), k => (k / (double)options.RadiusBinCount, (k + 1) / (double)options.RadiusBinCount));
        Dimension("azimuth-degrees", r => AzimuthKey(r.AzimuthDegrees), p => AzimuthKey(Normalize(p.Horizontal.AzimuthDegrees)), k => (k * sector, (k + 1) * sector));
        Dimension("catalog-magnitude", r => MagnitudeKey(r.Magnitude), p => MagnitudeKey(p.Star.Magnitude), k => (k * options.MagnitudeBinWidth, (k + 1) * options.MagnitudeBinWidth));

        var bounds = new List<AstrometricParameterBoundHit>(); var scale = assessment.Parameters!.FocalScale;
        if (Math.Abs(scale - solverOptions.MinimumFocalScale) <= 1e-9 * Math.Max(1, scale)) bounds.Add(new("focal-scale", "minimum", scale, solverOptions.MinimumFocalScale));
        if (Math.Abs(scale - solverOptions.MaximumFocalScale) <= 1e-9 * Math.Max(1, scale)) bounds.Add(new("focal-scale", "maximum", scale, solverOptions.MaximumFocalScale));

        var grid = options.OccupancyGridSize; int Cell(PixelPoint p) => Math.Clamp((int)(p.Y / height * grid), 0, grid - 1) * grid + Math.Clamp((int)(p.X / width * grid), 0, grid - 1);
        var predictedCells = predictions.Where(p => p.Ineligible is null).Select(p => Cell(p.Pixel)).Distinct().Count();
        var fittedCells = residuals.Where(r => !r.Verification).Select(r => Cell(r.Measured)).Distinct().Count();
        var occupancy = new AstrometricSpatialOccupancy(grid, predictedCells, fittedCells, predictedCells == 0 ? null : fittedCells / (double)predictedCells);

        var fitting = residuals.Where(r => !r.Verification).Select(r => stars[r.CatalogId]).ToArray();
        var conditioning = Conditioning(calibration, assessment.Parameters, fitting, utc, site, options.MaximumConditionNumber);
        return Create(true, predictions.Count, eligibleCount, [.. residuals.OrderBy(r => r.CatalogId, StringComparer.Ordinal)], [.. bins],
            [.. unmatched.OrderBy(u => u.CatalogId, StringComparer.Ordinal)], [.. unassociated.OrderBy(u => u.DetectionIndex)], [.. bounds], conditioning, occupancy, discrepancy);
    }

    /// <summary>Aggregates per-frame diagnostics into fixed-width time bins measured from the earliest exposure midpoint.</summary>
    public static IReadOnlyList<AstrometricTimeResidualBin> SummarizeByTime(IReadOnlyList<AstrometricResidualDiagnostics> frames, TimeSpan binWidth)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(binWidth, TimeSpan.Zero);
        if (frames.Count > 100000 || frames.Any(f => f is null || f.SchemaVersion != AstrometricResidualDiagnostics.CurrentSchemaVersion))
            throw new ArgumentException("Time summaries require at most 100000 current-schema diagnostics.", nameof(frames));
        if (frames.Count == 0) return [];
        var origin = frames.Min(f => f.MidpointUtc);
        if ((frames.Max(f => f.MidpointUtc) - origin).Ticks / binWidth.Ticks > int.MaxValue)
            throw new ArgumentException("The frames span more time bins than an index can represent at this bin width.", nameof(binWidth));
        return [.. frames.GroupBy(f => (int)((f.MidpointUtc - origin).Ticks / binWidth.Ticks)).OrderBy(g => g.Key).Select(g =>
        {
            var residuals = g.SelectMany(f => f.Residuals).ToArray(); var missing = g.Sum(f => f.UnmatchedPredictions.Count(u => IsEligibleReason(u.ReasonCode)));
            var bin = Bin("time", g.Key, 0, 0, residuals, missing);
            var start = origin + binWidth * g.Key;
            return new AstrometricTimeResidualBin(g.Key, start, start + binWidth, g.Count(), g.Count(f => f.HasMeasuredMapping), bin.FittingCount, bin.VerificationCount,
                bin.UnmatchedEligibleCount, bin.RmsPixels, bin.MeanDeltaX, bin.MeanDeltaY, bin.Recall);
        })];
    }

    private static bool IsEligibleReason(string reason) => reason is not (AstrometricDiagnosticReasons.OutsideAperture or AstrometricDiagnosticReasons.NearEdge or AstrometricDiagnosticReasons.CrowdedPrediction);

    private static AstrometricResidualBin Bin(string dimension, int key, double lower, double upper, AstrometricResidual[] members, int missing)
    {
        var fit = members.Count(r => !r.Verification); var verification = members.Length - fit;
        return new(dimension, key, lower, upper, fit, verification, missing, Rms(members), members.Length == 0 ? null : members.Average(r => r.DeltaX),
            members.Length == 0 ? null : members.Average(r => r.DeltaY), members.Length + missing == 0 ? null : members.Length / (double)(members.Length + missing));
    }

    private static double? Rms(IEnumerable<AstrometricResidual> residuals)
    { var values = residuals.ToArray(); return values.Length == 0 ? null : Math.Sqrt(values.Average(r => r.DeltaX * r.DeltaX + r.DeltaY * r.DeltaY)); }

    private static AstrometricReasonCount[] Counts(IEnumerable<string> reasons) =>
        [.. reasons.GroupBy(r => r, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new AstrometricReasonCount(g.Key, g.Count()))];

    private static double RadiusFraction(ProjectionContext projection, PixelPoint p) => Math.Clamp(Math.Sqrt(projection.NormalizedRadiusSquared(p.X, p.Y)), 0, 1);
    private static double Normalize(double degrees) { var value = degrees % 360; return value < 0 ? value + 360 : value; }

    private static AstrometricConditioning Conditioning(AstrometricCalibration calibration, AstrometricFitParameters fit,
        CelestialCatalogObject[] fitting, DateTimeOffset utc, CoreSite site, double maximum)
    {
        if (fitting.Length < 2) return new(fitting.Length, null, null, null, "unavailable-insufficient-fitting-stars");
        const double step = 1e-6;
        var optics = SolverOptics.From(calibration.Projection);
        var rotation = AstrometricRotation.FromPose(new(fit.BoresightAltitudeDegrees, fit.BoresightAzimuthDegrees, fit.RollDegrees));
        var plus = new[] { (new AstrometricRayCamera(optics, fit.FocalScale), rotation.Increment(step, 0, 0)), (new AstrometricRayCamera(optics, fit.FocalScale), rotation.Increment(0, step, 0)),
            (new AstrometricRayCamera(optics, fit.FocalScale), rotation.Increment(0, 0, step)), (new AstrometricRayCamera(optics, fit.FocalScale * Math.Exp(step)), rotation) };
        var minus = new[] { (new AstrometricRayCamera(optics, fit.FocalScale), rotation.Increment(-step, 0, 0)), (new AstrometricRayCamera(optics, fit.FocalScale), rotation.Increment(0, -step, 0)),
            (new AstrometricRayCamera(optics, fit.FocalScale), rotation.Increment(0, 0, -step)), (new AstrometricRayCamera(optics, fit.FocalScale * Math.Exp(-step)), rotation) };
        var normal = new double[16]; var used = 0;
        foreach (var star in fitting)
        {
            var ray = CameraBasis.FromHorizontal(AstrometricMath.Horizontal(star, utc, site));
            var dx = new double[4]; var dy = new double[4]; var valid = true;
            for (var k = 0; k < 4 && valid; k++)
            {
                if (plus[k].Item1.Pixel(ray, plus[k].Item2) is not { } a || minus[k].Item1.Pixel(ray, minus[k].Item2) is not { } b) { valid = false; break; }
                dx[k] = (a.X - b.X) / (2 * step); dy[k] = (a.Y - b.Y) / (2 * step);
            }
            if (!valid) continue; used++;
            for (var j = 0; j < 4; j++) for (var k = 0; k < 4; k++) normal[j * 4 + k] += dx[j] * dx[k] + dy[j] * dy[k];
        }
        if (used < 2) return new(used, null, null, null, "unavailable-insufficient-fitting-stars");
        var eigen = SymmetricEigenvalues(normal, 4);
        var largest = eigen.Max(); var smallest = eigen.Min();
        if (!(largest > 0) || !double.IsFinite(largest)) return new(used, null, smallest, largest, "unavailable-degenerate");
        if (smallest <= largest * 1e-15) return new(used, null, smallest, largest, "singular");
        var condition = largest / smallest;
        return new(used, condition, smallest, largest, condition <= maximum ? "well-conditioned" : "ill-conditioned");
    }

    /// <summary>Cyclic Jacobi eigenvalues for a small symmetric matrix.</summary>
    private static double[] SymmetricEigenvalues(double[] matrix, int n)
    {
        var a = (double[])matrix.Clone();
        for (var sweep = 0; sweep < 100; sweep++)
        {
            var off = 0d; for (var p = 0; p < n; p++) for (var q = p + 1; q < n; q++) off += a[p * n + q] * a[p * n + q];
            if (off < 1e-30) break;
            for (var p = 0; p < n; p++) for (var q = p + 1; q < n; q++)
            {
                var apq = a[p * n + q]; if (Math.Abs(apq) < 1e-300) continue;
                var theta = (a[q * n + q] - a[p * n + p]) / (2 * apq);
                var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                for (var k = 0; k < n; k++)
                {
                    var akp = a[k * n + p]; var akq = a[k * n + q];
                    a[k * n + p] = c * akp - s * akq; a[k * n + q] = s * akp + c * akq;
                }
                for (var k = 0; k < n; k++)
                {
                    var apk = a[p * n + k]; var aqk = a[q * n + k];
                    a[p * n + k] = c * apk - s * aqk; a[q * n + k] = s * apk + c * aqk;
                }
            }
        }
        return [.. Enumerable.Range(0, n).Select(i => a[i * n + i])];
    }

    private sealed class Prediction(CelestialCatalogObject star, AltAzPoint horizontal, PixelPoint pixel)
    {
        public CelestialCatalogObject Star { get; } = star;
        public AltAzPoint Horizontal { get; } = horizontal;
        public PixelPoint Pixel { get; } = pixel;
        public string? Ineligible { get; set; }
    }

    private sealed class PointGrid<T>
    {
        private readonly Dictionary<(int, int), List<T>> cells = [];
        private readonly Func<T, PixelPoint> location; private readonly double size;
        public PointGrid(IEnumerable<T> values, Func<T, PixelPoint> location, double size)
        {
            this.location = location; this.size = size;
            foreach (var value in values)
            { var p = location(value); var key = ((int)Math.Floor(p.X / size), (int)Math.Floor(p.Y / size)); if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = []; cell.Add(value); }
        }
        public IEnumerable<T> Near(PixelPoint p, double radius, bool inclusive)
        {
            for (var y = (int)Math.Floor((p.Y - radius) / size); y <= (int)Math.Floor((p.Y + radius) / size); y++)
                for (var x = (int)Math.Floor((p.X - radius) / size); x <= (int)Math.Floor((p.X + radius) / size); x++)
                    if (cells.TryGetValue((x, y), out var values))
                        foreach (var value in values)
                        { var d = AstrometricMath.Distance(location(value), p); if (inclusive ? d <= radius : d < radius) yield return value; }
        }
    }
}
