using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricReviewRegressionTests
{
    private static readonly Lazy<(AstrometricCatalogData Catalog, AstrometricCalibration Calibration, AstrometricDetection[] Detections, AstrometricSolveResult Result)> Accepted = new(() =>
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth(); var calibration = AstrometricTestFixture.Calibration(truth);
        var detections = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason); return (catalog, calibration, detections, result);
    });

    [TestMethod]
    public void CatalogPackageIdentityIsBoundWithoutChangingSelectionOrLegacyIdentity()
    {
        var catalog = AstrometricTestFixture.Catalog();
        var legacyHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { metadata = catalog.Metadata, coordinateModel = catalog.CoordinateModel })));
        Assert.AreEqual(legacyHash, catalog.IdentitySha256);
        var package = new AstrometricCatalogProvenance("hyg-fixture", "package-a", "fixture", "3");
        var first = new AstrometricCatalogData(catalog.Metadata, catalog.Stars, true,
            catalog.CompletenessMagnitudeLimit, provenance: package);
        var second = new AstrometricCatalogData(catalog.Metadata, catalog.Stars, true,
            catalog.CompletenessMagnitudeLimit, provenance: package with { PackageVersion = "package-b" });

        Assert.AreEqual(package, first.Provenance);
        Assert.AreNotEqual(catalog.IdentitySha256, first.IdentitySha256);
        Assert.AreNotEqual(first.IdentitySha256, second.IdentitySha256);
        Assert.AreEqual(catalog.SelectionIdentitySha256, first.SelectionIdentitySha256);
        Assert.AreEqual(first.SelectionIdentitySha256, second.SelectionIdentitySha256);
    }

    [TestMethod]
    public void CatalogPackageIdentityRejectsMalformedFields()
    {
        var catalog = AstrometricTestFixture.Catalog();
        var valid = new AstrometricCatalogProvenance("hyg-fixture", "package-a", "fixture", "3");
        foreach (var invalid in new[]
        {
            valid with { CatalogId = " " }, valid with { PackageVersion = null! },
            valid with { PreprocessingVersion = new string('x', 257) },
            valid with { PackageKind = "unapproved" }
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new AstrometricCatalogData(catalog.Metadata,
                catalog.Stars, true, catalog.CompletenessMagnitudeLimit, provenance: invalid));
        }
    }

    [TestMethod]
    public void WarmPriorRejectsChangedPackageProvenance()
    {
        var accepted = Accepted.Value;
        var changed = new AstrometricCatalogData(accepted.Catalog.Metadata, accepted.Catalog.Stars,
            true, accepted.Catalog.CompletenessMagnitudeLimit,
            provenance: new("hyg-fixture", "new-package", "fixture", "3"));
        var result = AstrometricSolver.Refine(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc),
            accepted.Calibration, changed, accepted.Detections, accepted.Result.Assessment);

        Assert.AreEqual("warm-context-incompatible", result.Assessment.ReasonCode);
        Assert.IsFalse(result.Assessment.HasMeasuredMapping);
        Assert.AreEqual(0, result.Metrics.Hypotheses);
    }

    [TestMethod]
    public void WarmFixedScale_DoesNotFitOutsideTheDeclaredAbsoluteInterval()
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth();
        var calibration = new AstrometricCalibration(truth with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 0, RollDegrees = 0 }, "fixed-scale", AstrometricTestFixture.Hash("readout"));
        var options = new AstrometricSolverOptions(MinimumFocalScale: 1, MaximumFocalScale: 1);
        var first = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc), options);
        Assert.IsTrue(first.Assessment.HasMeasuredMapping, first.Assessment.Reason); Assert.AreEqual(1, first.Assessment.Parameters!.FocalScale);
        var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc.AddSeconds(30));
        var warm = AstrometricSolver.Refine(frame, calibration, catalog, AstrometricTestFixture.Centroids(catalog, Scale(truth, 1.01), frame.MidpointUtc), first.Assessment, options);
        Assert.IsFalse(warm.Assessment.HasMeasuredMapping, "A fixed-scale model must not accept the enlarged image by fitting scale1.01.");
        Assert.IsNull(warm.Assessment.Parameters); Assert.AreEqual(1, first.Assessment.Parameters.FocalScale);
    }

    [TestMethod]
    [DataRow(1.08, .01)]
    [DataRow(.92, -.01)]
    public void WarmCumulativeUpdates_StayWithinOriginalAbsoluteBounds(double initialScale, double step)
    {
        var catalog = AstrometricTestFixture.Catalog(); var nominal = AstrometricTestFixture.Truth();
        var calibration = new AstrometricCalibration(nominal with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 0, RollDegrees = 0 }, "drift-test", AstrometricTestFixture.Hash("readout"));
        var options = new AstrometricSolverOptions(); var firstTruth = Scale(nominal, initialScale);
        var first = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, AstrometricTestFixture.Centroids(catalog, firstTruth, AstrometricTestFixture.Utc), options);
        Assert.IsTrue(first.Assessment.HasMeasuredMapping, first.Assessment.Reason); var prior = first.Assessment; var accepted = 0; var rejected = 0;
        for (var i = 1; i <= 4; i++)
        {
            var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc.AddSeconds(15 * i));
            var warm = AstrometricSolver.Refine(frame, calibration, catalog, AstrometricTestFixture.Centroids(catalog, Scale(nominal, initialScale + step * i), frame.MidpointUtc), prior, options);
            if (warm.Assessment.HasMeasuredMapping)
            {
                var scale = warm.Assessment.Parameters!.FocalScale;
                Assert.IsGreaterThanOrEqualTo(options.MinimumFocalScale, scale); Assert.IsLessThanOrEqualTo(options.MaximumFocalScale, scale);
                prior = warm.Assessment; accepted++;
            }
            else { Assert.IsNull(warm.Assessment.Parameters); rejected++; }
        }
        Assert.IsGreaterThanOrEqualTo(1, accepted); Assert.IsGreaterThanOrEqualTo(1, rejected);
    }

    [TestMethod]
    public void WarmPriorOutsideSettings_IsRejectedBeforeRefinement()
    {
        var f = Accepted.Value; var prior = f.Result.Assessment with { Parameters = f.Result.Assessment.Parameters! with { FocalScale = 1.11 }, IdentitySha256 = string.Empty };
        prior = prior with { IdentitySha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(prior))) };
        var result = AstrometricSolver.Refine(prior.Frame, f.Calibration, f.Catalog, f.Detections, prior);
        Assert.AreEqual("warm-context-incompatible", result.Assessment.ReasonCode); Assert.IsNull(result.Assessment.Parameters);
    }

    [TestMethod]
    public void CatalogCompleteness_DeclarationIsFiniteAndBoundToSelectionIdentity()
    {
        var f = Accepted.Value;
        var shallow = new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars, true, 5);
        var incomplete = new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars, false, 7);
        Assert.AreEqual(f.Catalog.IdentitySha256, shallow.IdentitySha256);
        Assert.AreNotEqual(f.Catalog.SelectionIdentitySha256, shallow.SelectionIdentitySha256);
        Assert.AreNotEqual(f.Catalog.SelectionIdentitySha256, incomplete.SelectionIdentitySha256);
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentException>(() => new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars, true, invalid));
    }

    [TestMethod]
    public void CatalogCompleteness_LargerRequestedCeilingRejectsSyncAndWarm()
    {
        var f = Accepted.Value; var shallow = new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars.Where(s => s.Magnitude <= 5), true, 5);
        foreach (var result in new[] { AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, shallow, f.Detections), AstrometricSolver.Refine(f.Result.Assessment.Frame, f.Calibration, shallow, f.Detections, f.Result.Assessment) })
        {
            Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, result.Assessment.Status); Assert.AreEqual("catalog-incomplete", result.Assessment.ReasonCode);
            Assert.IsNull(result.Assessment.Parameters); Assert.AreEqual(0, result.Metrics.Hypotheses);
        }
    }

    [TestMethod]
    [DataRow(5d, true)]
    [DataRow(7d, false)]
    public async Task CatalogCompleteness_ProviderCannotSubstituteInsufficientCoverage(double ceiling, bool complete)
    {
        var f = Accepted.Value; var source = new CapturingSource(new(f.Catalog.Metadata, f.Catalog.Stars, complete, ceiling));
        var result = await AstrometricSolver.SolveAsync(f.Result.Assessment.Frame, f.Calibration, source, f.Detections).ConfigureAwait(false);
        Assert.AreEqual(7, source.RequestedMagnitude); Assert.AreEqual(2500, source.RequestedEntries);
        Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, result.Assessment.Status); Assert.AreEqual("catalog-incomplete", result.Assessment.ReasonCode); Assert.IsNull(result.Assessment.Parameters);
    }

    [TestMethod]
    public void CatalogCompleteness_MatchingCeilingCanSolveAndRefine()
    {
        var f = Accepted.Value; var shallow = new AstrometricCatalogData(f.Catalog.Metadata, f.Catalog.Stars.Where(s => s.Magnitude <= 5), true, 5);
        var options = new AstrometricSolverOptions(MaximumCatalogMagnitude: 5); var frame = f.Result.Assessment.Frame;
        var detected = AstrometricTestFixture.Centroids(shallow, AstrometricTestFixture.Truth(), frame.MidpointUtc);
        var cold = AstrometricSolver.Solve(frame, f.Calibration, shallow, detected, options);
        Assert.IsTrue(cold.Assessment.HasMeasuredMapping, cold.Assessment.Reason);
        var warm = AstrometricSolver.Refine(frame, f.Calibration, shallow, detected, cold.Assessment, options);
        Assert.IsTrue(warm.Assessment.HasMeasuredMapping, warm.Assessment.Reason);
    }

    private static ProjectionContext Scale(ProjectionContext value, double scale) => value with { FocalLengthXPixels = value.FocalLengthXPixels * scale, FocalLengthYPixels = value.FocalLengthYPixels * scale };
    private sealed class CapturingSource(AstrometricCatalogData value) : IAstrometricCatalogSource
    {
        internal double RequestedMagnitude { get; private set; }
        internal int RequestedEntries { get; private set; }
        public ValueTask<AstrometricCatalogData> ReadAsync(double maximumMagnitude, int maximumEntries, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); RequestedMagnitude = maximumMagnitude; RequestedEntries = maximumEntries; return ValueTask.FromResult(value); }
    }
}
