using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricSolverTests
{
    private static readonly Lazy<(AstrometricCatalogData Catalog, AstrometricCalibration Calibration, AstrometricDetection[] Detections, AstrometricSolveResult Result)> Accepted = new(() =>
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth(); var calibration = AstrometricTestFixture.Calibration(truth);
        var detections = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason); return (catalog, calibration, detections, result);
    });
    [TestMethod]
    public void BlindSolve_UnknownCorrespondencesRecoversNativeOrientationAndScale()
    {
        var fixture = Accepted.Value; var actual = AstrometricMapping.Projection(fixture.Calibration, fixture.Result.Assessment);
        Assert.IsLessThan(.02, AstrometricTestFixture.RotationError(actual, AstrometricTestFixture.Truth()));
        Assert.AreEqual(1.037, fixture.Result.Assessment.Parameters!.FocalScale, .0002);
        Assert.IsGreaterThanOrEqualTo(12, fixture.Result.Assessment.Quality!.InlierCount);
        Assert.IsGreaterThanOrEqualTo(4, fixture.Result.Assessment.Quality.VerificationCount);
        Assert.IsFalse(fixture.Result.Assessment.Quality.IsCalibratedProbability);
        Assert.IsNull(fixture.Result.Assessment.Quality.OrientationUncertaintyDegrees);
        Assert.AreEqual(fixture.Result.Associations.Count, fixture.Result.Associations.Select(a => a.CatalogId).Distinct().Count());
        Assert.AreEqual(fixture.Result.Associations.Count, fixture.Result.Associations.Select(a => a.DetectionIndex).Distinct().Count());
    }
    [TestMethod]
    public void Assessment_CanonicalRoundTripRejectsTamperingAndOmitsCatalogRows()
    {
        var a = Accepted.Value.Result.Assessment; var bytes = AstrometricEvidenceJson.Serialize(a); var parsed = AstrometricEvidenceJson.Parse(bytes);
        CollectionAssert.AreEqual(bytes, AstrometricEvidenceJson.Serialize(parsed)); Assert.AreEqual(a.IdentitySha256, parsed.IdentitySha256);
        Assert.IsLessThan(6000, bytes.Length); Assert.DoesNotContain(System.Text.Encoding.UTF8.GetString(bytes), "Synthetic");
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Serialize(a with { Frame = a.Frame with { ExposureEndUtc = a.Frame.ExposureEndUtc.AddSeconds(1) } }));
    }
    [TestMethod]
    public void Mapping_RoundTripsAndReportsMeanOfDateBoresightAndLocalFisheyeScale()
    {
        var f = Accepted.Value; var mapping = new AstrometricMapping(f.Calibration, f.Result.Assessment); var pixel = new PixelPoint(280, 240);
        var sky = mapping.PixelToSky(pixel)!; var recovered = mapping.SkyToPixel(sky.EquatorialMeanOfDate)!.Value;
        Assert.IsLessThan(1e-7, AstrometricTestFixture.Distance(pixel, recovered));
        var boresight = mapping.PixelToSky(new(256, 256))!;
        Assert.AreEqual(boresight.EquatorialMeanOfDate.RightAscensionHours, f.Result.Assessment.Parameters!.BoresightRightAscensionHours, 1e-10);
        Assert.AreEqual(boresight.EquatorialMeanOfDate.DeclinationDegrees, f.Result.Assessment.Parameters.BoresightDeclinationDegrees, 1e-10);
        var center = mapping.LocalPixelScale(new(256, 256)); var edge = mapping.LocalPixelScale(new(440, 256));
        Assert.AreEqual(center.XArcsecondsPerPixel!.Value, center.YArcsecondsPerPixel!.Value, 1e-7);
        Assert.AreNotEqual(edge.XArcsecondsPerPixel, edge.YArcsecondsPerPixel);
    }
    [TestMethod]
    public void WarmSolve_UsesPriorWithoutGlobalSearchAndPreservesCalibrationIdentity()
    {
        var f = Accepted.Value; var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc.AddSeconds(30));
        var d = AstrometricTestFixture.Centroids(f.Catalog, AstrometricTestFixture.Truth(), frame.MidpointUtc);
        var result = AstrometricSolver.Refine(frame, f.Calibration, f.Catalog, d, f.Result.Assessment);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason); Assert.AreEqual(0, result.Metrics.Hypotheses);
        Assert.AreEqual(f.Result.Assessment.IdentitySha256, result.Assessment.PreviousAssessmentIdentitySha256);
        Assert.AreEqual(f.Calibration.IdentitySha256, result.Assessment.CalibrationIdentitySha256);
        Assert.AreEqual(f.Result.Assessment.Frame.MidpointUtc, AstrometricTestFixture.Utc);
    }
    [TestMethod]
    public void WarmSolve_StaleOrDifferentObserverDoesNotOfferReplacement()
    {
        var f = Accepted.Value; var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc.AddHours(1));
        var stale = AstrometricSolver.Refine(frame, f.Calibration, f.Catalog, f.Detections, f.Result.Assessment);
        Assert.AreEqual("warm-context-incompatible", stale.Assessment.ReasonCode); Assert.IsNull(stale.Assessment.Parameters);
        var other = AstrometricSolver.Refine(f.Result.Assessment.Frame with { ObserverIdentitySha256 = AstrometricTestFixture.Hash("other") }, f.Calibration, f.Catalog, f.Detections, f.Result.Assessment);
        Assert.AreEqual("warm-context-incompatible", other.Assessment.ReasonCode); Assert.IsNull(other.Assessment.Parameters);
    }
    [TestMethod]
    public void RejectedSparseFrame_CannotCreateMeasuredMapping()
    {
        var f = Accepted.Value; var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, f.Detections.Take(7).ToArray());
        Assert.AreEqual("insufficient-detections", result.Assessment.ReasonCode); Assert.IsNull(result.Assessment.Parameters);
        Assert.Throws<ArgumentException>(() => new AstrometricMapping(f.Calibration, result.Assessment));
    }
    [TestMethod]
    public void Catalog_IncompleteOrUnsupportedEpochIsUnavailable()
    {
        var f = Accepted.Value;
        foreach (var data in new[] { new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars, false), new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars, true, "unknown-proper-motion") })
        { var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, data, f.Detections); Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, result.Assessment.Status); Assert.IsNull(result.Assessment.Parameters); }
    }
    [TestMethod]
    public void CancelledOrExpiredWork_NeverReturnsAnAcceptedMapping()
    {
        var f = Accepted.Value; using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, f.Detections, cancellationToken: cts.Token));
        var expired = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, f.Detections, new(ColdBudgetMilliseconds: .000001));
        Assert.AreEqual(AstrometricAssessmentStatus.BudgetExceeded, expired.Assessment.Status); Assert.IsNull(expired.Assessment.Parameters);
    }
    [TestMethod]
    public void InputValidation_RejectsDuplicatesNonfiniteAndMalformedTiming()
    {
        var f = Accepted.Value;
        Assert.Throws<ArgumentException>(() => AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, [f.Detections[0], f.Detections[0]]));
        Assert.Throws<ArgumentException>(() => AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, [new(0, new(double.NaN, 2), 3)]));
        Assert.Throws<ArgumentException>(() => (f.Result.Assessment.Frame with { ExposureEndUtc = f.Result.Assessment.Frame.ExposureStartUtc.AddSeconds(-1) }).Validate());
        Assert.Throws<ArgumentException>(() => new AstrometricSolverOptions(FocalScaleStep: 0).Validate());
    }
    [TestMethod]
    public void InputOrdering_DoesNotChangeSemanticAssessmentIdentity()
    {
        var f = Accepted.Value; var reverse = new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars.Reverse(), true);
        var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, reverse, f.Detections.Reverse().ToArray());
        Assert.AreEqual(f.Result.Assessment.IdentitySha256, result.Assessment.IdentitySha256);
    }
    [TestMethod]
    public void EvidenceParser_RejectsForgedAcceptedGatesAndSerializedDerivedFlags()
    {
        var a = Accepted.Value.Result.Assessment;
        static AstrometricFrameAssessment Rehash(AstrometricFrameAssessment value) => value with { IdentitySha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value with { IdentitySha256 = string.Empty }))) };
        var emptyEvidence = Rehash(a with { Quality = a.Quality! with { InlierCount = 0, VerificationCount = 0, ExpectedIsolatedCount = 0 }, AssociationIdentitySha256 = null });
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Validate(emptyEvidence));
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Validate(Rehash(a with { Quality = a.Quality! with { VerificationCount = int.MaxValue } })));
        Assert.Throws<ArgumentException>(() => new AstrometricMapping(Accepted.Value.Calibration, emptyEvidence));
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Validate(Rehash(a with { Mode = AstrometricSolveMode.Warm, PreviousAssessmentIdentitySha256 = null })));
        var json = System.Text.Encoding.UTF8.GetString(AstrometricEvidenceJson.Serialize(a));
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Parse(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"IsCalibratedProbability\":false", "\"IsCalibratedProbability\":true", StringComparison.Ordinal))));
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Parse(System.Text.Encoding.UTF8.GetBytes("{\"HasMeasuredMapping\":false," + json[1..])));
        var changedMidpoint = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        changedMidpoint["Frame"]!["MidpointUtc"] = "2030-01-01T00:00:00Z";
        Assert.Throws<ArgumentException>(() => AstrometricEvidenceJson.Parse(System.Text.Encoding.UTF8.GetBytes(changedMidpoint.ToJsonString())));
        Assert.Throws<JsonException>(() => AstrometricEvidenceJson.Parse(System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":\"duplicate\"," + json[1..])));
    }
    [TestMethod]
    public void WarmSolve_ChangedSelectionWithSameMetadataIsNotTrusted()
    {
        var f = Accepted.Value;
        var changed = new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars.Select(s => s with { RightAscensionHours = (s.RightAscensionHours + .01) % 24 }), true);
        Assert.AreEqual(f.Catalog.IdentitySha256, changed.IdentitySha256); Assert.AreNotEqual(f.Catalog.SelectionIdentitySha256, changed.SelectionIdentitySha256);
        var result = AstrometricSolver.Refine(f.Result.Assessment.Frame, f.Calibration, changed, f.Detections, f.Result.Assessment);
        Assert.AreEqual("warm-context-incompatible", result.Assessment.ReasonCode); Assert.IsNull(result.Assessment.Parameters);
    }
    [TestMethod]
    public void IncorrectTimestamp_CanFitWellButRemainsExplicitInputProvenance()
    {
        var f = Accepted.Value; var incorrect = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc.AddMinutes(10));
        var result = AstrometricSolver.Solve(incorrect, f.Calibration, f.Catalog, f.Detections);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        Assert.IsGreaterThan(2, AstrometricTestFixture.RotationError(AstrometricMapping.Projection(f.Calibration, result.Assessment), AstrometricTestFixture.Truth()));
        Assert.AreEqual(incorrect.MidpointUtc, result.Assessment.Frame.MidpointUtc); Assert.IsFalse(result.Assessment.Quality!.IsCalibratedProbability);
    }
}
