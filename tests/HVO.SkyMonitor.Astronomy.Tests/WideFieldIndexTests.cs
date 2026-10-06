#pragma warning disable CA5394 // Fixed seeds define reproducible test catalogs, never security material.
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Issue #1126: a wide rectilinear field must index only the brightest stars expected to supply its detection triangles,
/// while a telescope field keeps indexing every above-horizon training star exactly as before.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class WideFieldIndexTests
{
    // Virtual ASI174 (1936x1216, 5.86 um) behind an ideal 6 mm pinhole, at the qualification harness's nominal focal (truth / 1.037).
    private static readonly ProjectionContext SixMillimetreTruth = new(ProjectionModel.Perspective, 968, 608, 6 / .00586, 6 / .00586,
        1936, 1216, ProjectionAperture.Rectangular, null, 62, 205, 14, true);

    [TestMethod]
    public void WideFieldIndexCount_PinsTheNarrowestSearchedSolidAngle()
    {
        var nominal = SolverOptics.From(AstrometricTestFixture.Calibration(SixMillimetreTruth).Projection);
        var options = new CoreSolverOptions();
        // Sensor rectangle at the largest searched focal scale: 1.3240809825 sr, so 2 * 28 * 2pi / omega = 265.74 stars.
        Assert.AreEqual(266, AstrometricSolverCore.WideFieldIndexCount(nominal, options.MaximumScale, options.DetectionTriangleStars));
        Assert.AreEqual(235, AstrometricSolverCore.WideFieldIndexCount(nominal, 1, options.DetectionTriangleStars));
        // An 8 mm field is narrower and therefore indexes more stars.
        var eight = nominal with { FocalX = nominal.FocalX * 8 / 6, FocalY = nominal.FocalY * 8 / 6 };
        Assert.AreEqual(406, AstrometricSolverCore.WideFieldIndexCount(eight, options.MaximumScale, options.DetectionTriangleStars));
    }

    [TestMethod]
    public void TelescopeField_RequestsMoreStarsThanAnyCatalogSoEveryTrainingStarIsIndexed()
    {
        // The 50 mm virtual sample rig: a 12.9 degree field asks for 13,264 stars, above the 2,500-entry catalog bound,
        // so the solver keeps the complete above-horizon training set exactly as solver v1 did.
        var telescope = SolverOptics.From(SixMillimetreTruth with { FocalLengthXPixels = 50 / .00586, FocalLengthYPixels = 50 / .00586 });
        var options = new CoreSolverOptions();
        var count = AstrometricSolverCore.WideFieldIndexCount(telescope, options.MaximumScale, options.DetectionTriangleStars);
        Assert.AreEqual(13264, count);
        Assert.IsGreaterThan(AstrometricCatalogData.MaximumEntries, count);
    }

    [TestMethod]
    public void WideRectilinearBlindSolve_IndexesTheBrightestStarsWithinTheTriangleBound()
    {
        var catalog = DenseCatalog();
        var calibration = AstrometricTestFixture.Calibration(SixMillimetreTruth);
        var detections = AstrometricTestFixture.Centroids(catalog, SixMillimetreTruth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);

        var options = new CoreSolverOptions();
        var expected = AstrometricSolverCore.WideFieldIndexCount(SolverOptics.From(calibration.Projection), options.MaximumScale, options.DetectionTriangleStars);
        var training = catalog.Stars.Count(s => AstrometricTestFixture.Horizontal(s, AstrometricTestFixture.Utc).AltitudeDegrees > 0 &&
            !AstrometricSolverCore.IsVerification(s.Id));
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        // Solver v1 indexed every training star here; this catalog has several times the selected count above the horizon.
        Assert.IsGreaterThan(2 * expected, training);
        Assert.AreEqual(expected, result.Metrics.IndexStars);
        Assert.IsLessThan(2_000_000, result.Metrics.IndexTriangles);
        Assert.IsLessThan(.1, AstrometricTestFixture.RotationError(AstrometricMapping.Projection(calibration, result.Assessment), SixMillimetreTruth));
    }

    // Roughly the whole-sky density of a magnitude-6 catalog, so a 6 mm field holds hundreds of stars.
    private static AstrometricCatalogData DenseCatalog()
    {
        var random = new Random(112601);
        var stars = Enumerable.Range(0, 2400).Select(i => new CelestialCatalogObject($"WFI{i:0000}", $"Artificial {i}",
            random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI, 1 + 5 * random.NextDouble())).ToArray();
        return new(new("Generated dense uniform test catalog", "1", new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"),
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stars))), "test-generated", "1"), stars, true, 7);
    }
}
