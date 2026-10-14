#pragma warning disable CA5394 // Fixed seeds define reproducible test catalogs, never security material.
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Issue #1126: a perspective field whose complete training index fits the triangle bound indexes it exactly as solver v1
/// did, while a field too wide for that indexes the largest magnitude-ordered prefix that fits instead of rejecting.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class WideFieldIndexTests
{
    private const int TriangleBound = 2_000_000;

    // Virtual ASI174 (1936x1216, 5.86 um) behind an ideal 6 mm pinhole, at the qualification harness's nominal focal (truth / 1.037).
    private static readonly ProjectionContext SixMillimetreTruth = new(ProjectionModel.Perspective, 968, 608, 6 / .00586, 6 / .00586,
        1936, 1216, ProjectionAperture.Rectangular, null, 62, 205, 14, true);

    [TestMethod]
    public void FittingField_IndexesEveryTrainingStarExactlyAsSolverV1()
    {
        // Values recorded by this same solve at solver v1 (b13f0d0e); every one must be reproduced to the last bit.
        var catalog = AstrometricTestFixture.Catalog();
        var calibration = AstrometricTestFixture.Calibration(SixMillimetreTruth);
        var detections = AstrometricTestFixture.Centroids(catalog, SixMillimetreTruth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);

        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        Assert.AreEqual(Training(catalog), result.Metrics.IndexStars);
        Assert.AreEqual(V1IndexTriangles, result.Metrics.IndexTriangles);
        Assert.AreEqual(V1ImageTriangles, result.Metrics.ImageTriangles);
        Assert.AreEqual(V1Hypotheses, result.Metrics.Hypotheses);
        var solved = AstrometricMapping.Projection(calibration, result.Assessment);
        Assert.AreEqual(V1Solution, Fingerprint(solved));
    }

    [TestMethod]
    [DataRow(6d, 1936, 1216, 968d, 608d, DisplayName = "6 mm full frame")]
    [DataRow(8d, 1936, 1216, 968d, 608d, DisplayName = "8 mm full frame")]
    [DataRow(6d, 1440, 1024, 728d, 512d, DisplayName = "6 mm ROI 1440x1024")]
    public void WideField_IndexesTheBrightestPrefixWithinTheTriangleBoundAndSolves(double focalMillimetres, int width, int height,
        double principalX, double principalY)
    {
        var truth = SixMillimetreTruth with
        {
            FocalLengthXPixels = focalMillimetres / .00586,
            FocalLengthYPixels = focalMillimetres / .00586,
            WidthPixels = width,
            HeightPixels = height,
            PrincipalPointX = principalX,
            PrincipalPointY = principalY,
        };
        var catalog = DenseCatalog();
        var calibration = AstrometricTestFixture.Calibration(truth);
        var detections = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);

        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        // Solver v1 indexed every training star here and rejected the frame at the triangle bound.
        Assert.IsLessThan(Training(catalog), result.Metrics.IndexStars);
        Assert.IsLessThanOrEqualTo(TriangleBound, result.Metrics.IndexTriangles);
        Assert.IsLessThan(.1, AstrometricTestFixture.RotationError(AstrometricMapping.Projection(calibration, result.Assessment), truth));
    }

    [TestMethod]
    public void IndexPrefix_AdmitsTheLargestLeadingRunWhoseTrianglesFitTheBound()
    {
        var random = new Random(112602);
        var rays = Enumerable.Range(0, 60).Select(_ =>
        {
            var up = random.NextDouble(); var azimuth = 2 * Math.PI * random.NextDouble(); var horizontal = Math.Sqrt(1 - up * up);
            return new EnuVector(horizontal * Math.Sin(azimuth), horizontal * Math.Cos(azimuth), up);
        }).ToArray();
        const double span = Math.PI / 2, side = .01;
        var all = AstrometricSolverCore.CountTriangles(rays, span, side);
        Assert.IsGreaterThan(1000, all);

        Assert.AreEqual(rays.Length, AstrometricSolverCore.IndexPrefix(rays, span, side, all));
        foreach (var bound in new[] { all - 1, all / 2, all / 7 })
        {
            var admitted = AstrometricSolverCore.IndexPrefix(rays, span, side, bound);
            Assert.IsLessThanOrEqualTo(bound, AstrometricSolverCore.CountTriangles(rays[..admitted], span, side), $"bound {bound}");
            Assert.IsGreaterThan(bound, AstrometricSolverCore.CountTriangles(rays[..(admitted + 1)], span, side), $"bound {bound}");
        }
    }

    private const int V1IndexTriangles = 1_061_529;
    private const int V1ImageTriangles = 4032;
    private const int V1Hypotheses = 2134;
    private const string V1Solution = "408fff253231bda0 408fff253231bda0 404efffe3da1fcde 40699ffeadb76b6b 402bffd2a504d553";

    private static string Fingerprint(ProjectionContext p) => string.Join(' ', new[] { p.FocalLengthXPixels, p.FocalLengthYPixels,
        p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees }.Select(v => BitConverter.DoubleToInt64Bits(v).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));

    private static int Training(AstrometricCatalogData catalog) => catalog.Stars.Count(s => s.Magnitude <= new AstrometricSolverOptions().MaximumCatalogMagnitude &&
        AstrometricTestFixture.Horizontal(s, AstrometricTestFixture.Utc).AltitudeDegrees > 0 && !AstrometricSolverCore.IsVerification(s.Id));

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
