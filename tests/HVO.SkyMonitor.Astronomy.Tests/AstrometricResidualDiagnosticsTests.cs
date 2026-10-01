namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricResidualDiagnosticsTests
{
    private sealed record Solved(AstrometricCatalogData Catalog, AstrometricCalibration Calibration, AstrometricDetection[] Detections, AstrometricSolveResult Result);

    private static readonly string[] ExpectedExclusionReasons = ["blended", "image-edge"];
    private static readonly Lazy<Solved> Accepted = new(() =>
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth(); var calibration = AstrometricTestFixture.Calibration(truth);
        var detections = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason); return new(catalog, calibration, detections, result);
    });

    [TestMethod]
    public void AcceptedSolve_ResidualVectorsReproduceAssociationsAndBindIdentities()
    {
        var f = Accepted.Value; var d = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections);
        var q = f.Result.Assessment.Quality!;
        Assert.AreEqual(AstrometricResidualDiagnostics.CurrentSchemaVersion, d.SchemaVersion); Assert.IsTrue(d.HasMeasuredMapping);
        Assert.AreEqual(f.Result.Associations.Count, d.Residuals.Count);
        Assert.AreEqual(q.InlierCount, d.Residuals.Count(r => !r.Verification)); Assert.AreEqual(q.VerificationCount, d.Residuals.Count(r => r.Verification));
        Assert.IsLessThan(1e-6, d.MaximumAssociationResidualDiscrepancyPixels!.Value);
        Assert.AreEqual(q.FittingRmsPixels, d.FittingRmsPixels!.Value, 1e-6); Assert.AreEqual(q.VerificationRmsPixels, d.VerificationRmsPixels!.Value, 1e-6);
        foreach (var r in d.Residuals)
        {
            Assert.AreEqual(r.Measured.X - r.Predicted.X, r.DeltaX, 1e-12); Assert.AreEqual(r.Measured.Y - r.Predicted.Y, r.DeltaY, 1e-12);
            Assert.IsTrue(r.RadiusFraction is >= 0 and <= 1); Assert.IsTrue(r.AzimuthDegrees is >= 0 and < 360);
        }
        Assert.AreEqual(f.Result.Assessment.IdentitySha256, d.AssessmentIdentitySha256); Assert.AreEqual(f.Calibration.IdentitySha256, d.CalibrationIdentitySha256);
        Assert.AreEqual(f.Catalog.IdentitySha256, d.CatalogIdentitySha256); Assert.AreEqual(f.Catalog.SelectionIdentitySha256, d.CatalogSelectionIdentitySha256);
        Assert.AreEqual(new AstrometricSolverOptions().IdentitySha256, d.SolverSettingsIdentitySha256);
        Assert.AreEqual(f.Result.Assessment.Frame.DetectionSettingsIdentitySha256, d.DetectionSettingsIdentitySha256);
        Assert.AreEqual(new AstrometricDiagnosticsOptions().IdentitySha256, d.DiagnosticsSettingsIdentitySha256);
        Assert.AreEqual(AstrometricResidualAnalyzer.CovarianceUnavailable, d.CovarianceStatus); Assert.IsNull(d.MedianNormalizedResidualSquared);
        Assert.AreEqual(d.IdentitySha256, AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections).IdentitySha256);
        Assert.AreNotEqual(d.IdentitySha256, AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, options: new(IsolationPixels: 13)).IdentitySha256);
    }

    [TestMethod]
    public void Summaries_PartitionEveryAssociationByRadiusAzimuthAndMagnitude()
    {
        var f = Accepted.Value; var d = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections);
        var unmatchedEligible = d.UnmatchedPredictions.Count(u => u.ReasonCode is not (AstrometricDiagnosticReasons.OutsideAperture or AstrometricDiagnosticReasons.NearEdge or AstrometricDiagnosticReasons.CrowdedPrediction));
        foreach (var dimension in new[] { "radius-fraction", "azimuth-degrees", "catalog-magnitude" })
        {
            var bins = d.Bins.Where(b => b.Dimension == dimension).ToArray();
            Assert.AreEqual(d.Residuals.Count, bins.Sum(b => b.FittingCount + b.VerificationCount), dimension);
            Assert.AreEqual(unmatchedEligible, bins.Sum(b => b.UnmatchedEligibleCount), dimension);
            Assert.IsTrue(bins.All(b => b.Recall is >= 0 and <= 1 && b.Upper > b.Lower), dimension);
            CollectionAssert.AreEqual(bins.Select(b => b.Index).Order().ToArray(), bins.Select(b => b.Index).ToArray(), dimension);
        }
        Assert.IsTrue(d.Bins.Where(b => b.Dimension == "radius-fraction").All(b => b.Index is >= 0 and < 4));
        Assert.IsGreaterThan(4, d.Bins.Count(b => b.Dimension == "azimuth-degrees" && b.FittingCount > 0));
        Assert.AreEqual(d.EligiblePredictionCount, d.Residuals.Count + unmatchedEligible);
        Assert.AreEqual("well-conditioned", d.Conditioning!.Status); Assert.IsGreaterThan(1, d.Conditioning.ConditionNumber!.Value);
        Assert.AreEqual(d.Residuals.Count(r => !r.Verification), d.Conditioning.FittingStars);
        Assert.IsGreaterThan(.5, d.Occupancy!.OccupiedFraction!.Value); Assert.IsEmpty(d.ParameterBoundHits);
        Assert.AreEqual(d.PredictedStarCount - d.EligiblePredictionCount, d.UnmatchedPredictions.Count(u => !IsEligible(u.ReasonCode)));
    }

    [TestMethod]
    public void UnmatchedPredictionsAndUnassociatedDetections_CarryDistinctReasons()
    {
        var f = Accepted.Value; var associated = f.Result.Associations.Where(a => a.Verification).OrderBy(a => a.CatalogId, StringComparer.Ordinal).Take(4).ToArray();
        var removed = associated.Select(a => f.Detections.Single(d => d.Index == a.DetectionIndex)).ToArray();
        // Replace one centroid with two equidistant sources: the solver's 1.5x ratio test must leave the star unmatched as ambiguous.
        var split = removed[2]; var next = f.Detections.Max(d => d.Index) + 1;
        // Displace another centroid beyond the association radius, as a merged blend would, but within the offset-source radius.
        var offset = removed[3] with { Pixel = new(removed[3].Pixel.X + 2.8, removed[3].Pixel.Y) };
        var detections = f.Detections.Where(d => !removed.Contains(d)).Append(offset).Concat([
            new AstrometricDetection(next, new(split.Pixel.X - .55, split.Pixel.Y), split.Flux / 2), new AstrometricDetection(next + 1, new(split.Pixel.X + .55, split.Pixel.Y), split.Flux / 2),
            new AstrometricDetection(next + 2, new(256, 256), 10)]).ToArray();
        var falseStar = detections[^1];
        Assert.IsTrue(IsFarFromPredictions(falseStar.Pixel), "Test false star must not coincide with a catalog prediction.");
        var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, detections);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        var exclusion = new AstrometricMeasurementExclusion(new(removed[1].Pixel.X + .4, removed[1].Pixel.Y), "blended");
        var d = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), result, detections, [exclusion, new(new(3, 3), "image-edge")]);
        string Reason(string id) => d.UnmatchedPredictions.Single(u => u.CatalogId == id).ReasonCode;
        Assert.AreEqual(AstrometricDiagnosticReasons.NoMeasuredSource, Reason(associated[0].CatalogId));
        Assert.AreEqual(AstrometricDiagnosticReasons.MeasurementExcludedPrefix + "blended", Reason(associated[1].CatalogId));
        Assert.AreEqual(AstrometricDiagnosticReasons.Ambiguous, Reason(associated[2].CatalogId));
        Assert.AreEqual(AstrometricDiagnosticReasons.OffsetMeasuredSource, Reason(associated[3].CatalogId));
        Assert.IsFalse(result.Associations.Any(a => a.DetectionIndex == offset.Index));
        Assert.AreEqual(AstrometricDiagnosticReasons.NoCatalogPrediction, d.UnassociatedDetections.Single(u => u.DetectionIndex == falseStar.Index).ReasonCode);
        Assert.IsTrue(d.UnassociatedDetections.Where(u => u.DetectionIndex is var i && (i == next || i == next + 1)).All(u => u.ReasonCode == AstrometricDiagnosticReasons.NearUnassociatedPrediction));
        Assert.IsFalse(result.Associations.Any(a => a.DetectionIndex == falseStar.Index || a.DetectionIndex == next || a.DetectionIndex == next + 1));
        CollectionAssert.AreEqual(ExpectedExclusionReasons, d.MeasurementExclusionReasonCounts.Select(c => c.ReasonCode).ToArray());
        Assert.AreEqual(d.UnmatchedPredictions.Count, d.UnmatchedPredictionReasonCounts.Sum(c => c.Count));
        Assert.AreEqual(d.UnassociatedDetections.Count, d.UnassociatedDetectionReasonCounts.Sum(c => c.Count));
        Assert.IsTrue(d.UnmatchedPredictions.Any(u => u.ReasonCode == AstrometricDiagnosticReasons.CrowdedPrediction));
    }

    [TestMethod]
    public void FocalScaleBoundHit_IsReported()
    {
        var f = Accepted.Value; var options = new AstrometricSolverOptions(MaximumFocalScale: 1.036);
        var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, f.Detections, options);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        var d = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, options, result, f.Detections);
        var hit = d.ParameterBoundHits.Single(); Assert.AreEqual("focal-scale", hit.Parameter); Assert.AreEqual("maximum", hit.Bound); Assert.AreEqual(1.036, hit.Limit);
    }

    [TestMethod]
    public void Covariance_ProducesUnvalidatedNormalizedResiduals()
    {
        var f = Accepted.Value; var variance = Math.Pow(.04, 2) / 12;
        var covariances = f.Detections.Select(d => new AstrometricPixelCovariance(d.Index, variance, 0, variance)).ToArray();
        var d = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, covariances: covariances);
        Assert.AreEqual(AstrometricResidualAnalyzer.CovarianceUnvalidated, d.CovarianceStatus);
        Assert.IsTrue(d.Residuals.All(r => r.NormalizedResidualSquared is >= 0)); Assert.IsTrue(double.IsFinite(d.MedianNormalizedResidualSquared!.Value));
        var r0 = d.Residuals[0]; Assert.AreEqual((r0.DeltaX * r0.DeltaX + r0.DeltaY * r0.DeltaY) / variance, r0.NormalizedResidualSquared!.Value, 1e-9 * r0.NormalizedResidualSquared.Value);
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, covariances: [new(f.Detections[0].Index, 1, 2, 1)]));
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, covariances: [new(99999, 1, 0, 1)]));
    }

    [TestMethod]
    public void RejectedAssessment_ReportsOnlyMeasurementEvidence()
    {
        var f = Accepted.Value; var sparse = f.Detections.Take(5).ToArray();
        var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, sparse);
        var d = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), result, sparse, [new(new(10, 10), "low-snr")]);
        Assert.IsFalse(d.HasMeasuredMapping); Assert.AreEqual("insufficient-detections", d.ReasonCode);
        Assert.IsEmpty(d.Residuals); Assert.IsEmpty(d.UnmatchedPredictions); Assert.IsNull(d.Conditioning); Assert.AreEqual(5, d.DetectionCount);
        Assert.AreEqual("low-snr", d.MeasurementExclusionReasonCounts.Single().ReasonCode);
    }

    [TestMethod]
    public void MismatchedEvidence_IsRejected()
    {
        var f = Accepted.Value;
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(MaximumCatalogMagnitude: 6), f.Result, f.Detections));
        var other = AstrometricTestFixture.Calibration(AstrometricTestFixture.Truth(180));
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(other, f.Catalog, new(), f.Result, f.Detections));
        var missing = f.Detections.Where(d => d.Index != f.Result.Associations[0].DetectionIndex).ToArray();
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, missing));
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, [new(new(1, 1), " ")]));
        Assert.Throws<ArgumentException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, options: new(RadiusBinCount: 0)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections, cancellationToken: cancelled.Token));
    }

    [TestMethod]
    public void SummarizeByTime_AggregatesFramesIntoFixedBins()
    {
        var f = Accepted.Value; var first = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), f.Result, f.Detections);
        var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc.AddSeconds(30));
        var detections = AstrometricTestFixture.Centroids(f.Catalog, AstrometricTestFixture.Truth(), frame.MidpointUtc);
        var warm = AstrometricSolver.Refine(frame, f.Calibration, f.Catalog, detections, f.Result.Assessment);
        Assert.IsTrue(warm.Assessment.HasMeasuredMapping, warm.Assessment.Reason);
        var second = AstrometricResidualAnalyzer.Analyze(f.Calibration, f.Catalog, new(), warm, detections);
        var split = AstrometricResidualAnalyzer.SummarizeByTime([second, first], TimeSpan.FromSeconds(20));
        Assert.HasCount(2, split); Assert.AreEqual(0, split[0].Index); Assert.AreEqual(1, split[1].Index); Assert.AreEqual(AstrometricTestFixture.Utc, split[0].StartUtc);
        Assert.AreEqual(first.Residuals.Count(r => !r.Verification), split[0].FittingCount);
        var merged = AstrometricResidualAnalyzer.SummarizeByTime([first, second], TimeSpan.FromMinutes(1)).Single();
        Assert.AreEqual(2, merged.MappedFrameCount); Assert.AreEqual(first.Residuals.Count + second.Residuals.Count, merged.FittingCount + merged.VerificationCount);
        Assert.IsEmpty(AstrometricResidualAnalyzer.SummarizeByTime([], TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AstrometricResidualAnalyzer.SummarizeByTime([first], TimeSpan.Zero));
    }

    private static bool IsEligible(string reason) => reason is not (AstrometricDiagnosticReasons.OutsideAperture or AstrometricDiagnosticReasons.NearEdge or AstrometricDiagnosticReasons.CrowdedPrediction);

    private static bool IsFarFromPredictions(PixelPoint pixel)
    {
        var truth = ProjectorFactory.Create(AstrometricTestFixture.Truth());
        return AstrometricTestFixture.Catalog().Stars.Select(s => AstrometricTestFixture.Horizontal(s, AstrometricTestFixture.Utc)).Where(h => h.AltitudeDegrees > 0)
            .Select(truth.Project).All(p => p is not { } q || AstrometricTestFixture.Distance(q, pixel) > 3);
    }
}
