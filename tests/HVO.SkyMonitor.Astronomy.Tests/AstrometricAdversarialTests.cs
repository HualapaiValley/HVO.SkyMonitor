#pragma warning disable CA5394 // Deterministic synthetic geometry and injected false detections.
using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricAdversarialTests
{
    [TestMethod]
    public void NearbyNarrowAliases_PreserveDifferentCorrespondencesAndRejectAmbiguity()
    {
        var random = new Random(8912); var stars = new List<CelestialCatalogObject>(); var ra = 4.37 * Math.PI / 12; var dec = 25 * Math.PI / 180;
        var center = new EnuVector(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        var east = EnuVector.Cross(new(0, 0, 1), center).Normalize(); var north = EnuVector.Cross(center, east);
        for (var i = 0; i < 80; i++)
        {
            var radius = .3 * Math.PI / 180 * Math.Sqrt(random.NextDouble()); var angle = random.NextDouble() * Math.PI * 2;
            var v = center * Math.Cos(radius) + (east * Math.Cos(angle) + north * Math.Sin(angle)) * Math.Sin(radius);
            stars.Add(new($"field-{i}", "Artificial narrow field", (Math.Atan2(v.North, v.East) * 12 / Math.PI + 24) % 24, Math.Asin(v.Up) * 180 / Math.PI, 2 + random.NextDouble() * 3));
        }
        var h = CoordinateTransforms.EquatorialToHorizontal(EquatorialPrecession.PrecessJ2000(new(4.37, 25), AstrometricTestFixture.Utc), AstrometricTestFixture.Utc, 35.347, -113.878);
        var truth = new ProjectionContext(ProjectionModel.Perspective, 363.25, 207.75, 45000, 43650, 640, 480, ProjectionAperture.Rectangular, null, h.AltitudeDegrees, h.AzimuthDegrees, 38, true);
        var catalog = Data(stars); var calibration = AstrometricTestFixture.Calibration(truth); var detected = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc); var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc);
        var original = AstrometricSolver.Solve(frame, calibration, catalog, detected);
        Assert.IsTrue(original.Assessment.HasMeasuredMapping, original.Assessment.Reason);
        Assert.IsLessThan(.02, AstrometricTestFixture.RotationError(truth, AstrometricMapping.Projection(calibration, original.Assessment)));
        // Copy the sky pattern by0.75degrees in RA, preserving the independent verification partition.
        var next = 0; var duplicate = stars.Select(s =>
        {
            string id; do { id = $"alias-{next++}"; } while (Verification(id) != Verification(s.Id));
            return s with { Id = id, RightAscensionHours = (s.RightAscensionHours + .05) % 24 };
        }).ToArray();
        var alias = AstrometricSolver.Solve(frame, calibration, Data(duplicate), detected); Assert.IsTrue(alias.Assessment.HasMeasuredMapping, alias.Assessment.Reason);
        var ambiguous = AstrometricSolver.Solve(frame, calibration, Data(stars.Concat(duplicate)), detected);
        Assert.AreEqual("ambiguous", ambiguous.Assessment.ReasonCode); Assert.IsFalse(ambiguous.Assessment.HasMeasuredMapping);
    }
    [TestMethod]
    public void WideWrongParityAndModel_DoNotProduceAcceptedSolutions()
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth(); var detected = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc); var nominal = AstrometricTestFixture.Calibration(truth).Projection;
        foreach (var wrong in new[] { nominal with { HorizontalFlip = false }, nominal with { Model = ProjectionModel.EquisolidFisheye } })
        {
            var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), new(wrong, "wrong-model-test", AstrometricTestFixture.Hash("readout")), catalog, detected);
            Assert.IsFalse(result.Assessment.HasMeasuredMapping); Assert.IsNull(result.Assessment.Parameters);
        }
    }
    [TestMethod]
    public void FalseSources_DoNotCreateIncorrectCatalogAssociations()
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth(); var detected = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc).ToList(); var correctIds = detected.Select(d => d.Index).ToHashSet(); var random = new Random(913);
        for (var i = 0; i < 10; i++) detected.Add(new(1000 + i, new(220 + random.NextDouble() * 70, 220 + random.NextDouble() * 70), 50000));
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), AstrometricTestFixture.Calibration(truth), catalog, detected);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        Assert.IsTrue(result.Associations.All(a => correctIds.Contains(a.DetectionIndex)));
    }
    [TestMethod]
    [DataRow(ProjectionModel.OrthographicFisheye, 1d)]
    [DataRow(ProjectionModel.EquisolidFisheye, 2d)]
    [DataRow(ProjectionModel.EquidistantFisheye, Math.PI)]
    public void RadialBoundary_SkipsNonphysicalFocalTrials(ProjectionModel model, double radiusFactor)
    {
        var projection = new ProjectionContext(model, 320, 320, 300, 300, 640, 640, ProjectionAperture.Circular, 300 * radiusFactor);
        var points = Enumerable.Range(0, 12).Select(i => new AstrometricDetection(i, new(160 + i * 20, 230 + i % 3 * 25), 1000 - i)).ToArray();
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), new(projection, "boundary", AstrometricTestFixture.Hash("readout")), Data([]), points);
        Assert.IsFalse(result.Assessment.HasMeasuredMapping);
    }
    private static AstrometricCatalogData Data(IEnumerable<CelestialCatalogObject> stars)
    {
        var array = stars.ToArray(); var checksum = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(array)));
        return new(new("Synthetic geometry", "1", new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"), checksum, "test-generated", "1"), array, true);
    }
    private static bool Verification(string id)
    { uint hash = 2166136261; foreach (var c in id) hash = unchecked((hash ^ c) * 16777619); return hash % 5 == 0; }
}
