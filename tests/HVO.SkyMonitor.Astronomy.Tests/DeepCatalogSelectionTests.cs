#pragma warning disable CA5394 // Fixed seeds define reproducible synthetic inputs, never security material.
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Issue #1167: opt-in catalog selections deeper than the legacy 2,500-entry bound. Settings without a profile keep
/// their identity and bound; every consumer handles a selection up to the materialization ceiling; and the two solver
/// rewrites that make deep selections affordable are pinned against the solver v2 code they replace.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class DeepCatalogSelectionTests
{
    private const string Profile = AstrometricCatalogSelectionProfile.HygDeepSelectionV1Name;

    // Settings JSON and identities of solver v2 before #1167. The default identity was computed from base 94883101's
    // build; the magnitude-5 identity is the settingsIdentitySha256 recorded in the retained #1126 virtual evidence.
    private const string DefaultSettingsJson = "{\"MinimumFocalScale\":0.9,\"MaximumFocalScale\":1.1,\"FocalScaleStep\":0.01,\"MaximumCatalogMagnitude\":7," +
        "\"TriangleDetectionCount\":28,\"ImageTriangleLimit\":192,\"HypothesisLimit\":200000,\"CandidateLimit\":32,\"ColdBudgetMilliseconds\":15000," +
        "\"WarmBudgetMilliseconds\":500,\"MaximumWarmAgeSeconds\":600}";
    private const string DefaultSettingsIdentity = "f1edbb794f179f3d5dbec3d270e34f865291ec7ffb27c83655f3a6af4dcb2bb7";
    private const string Magnitude5SettingsIdentity = "e52c026406415332c9f638456ac94b79aa290998d644ac0bc818f31144b1f824";

    private static readonly AstrometricSolverOptions Deep = new(MaximumCatalogMagnitude: 6, CatalogSelectionProfile: Profile);

    private sealed record Solved(AstrometricCatalogData Catalog, AstrometricCalibration Calibration, AstrometricDetection[] Detections, AstrometricSolveResult Result);

    private static readonly Lazy<Solved> Shallow = new(() => SolveUnderProfile(AstrometricTestFixture.Catalog()));
    private static readonly Lazy<Solved> Ceiling = new(() => SolveUnderProfile(Padded(AstrometricCatalogData.MaterializationCeiling)));

    [TestMethod]
    public void NullProfileKeepsEverySettingsIdentity()
    {
        var defaults = new AstrometricSolverOptions();
        Assert.AreEqual(DefaultSettingsJson, JsonSerializer.Serialize(defaults));
        Assert.AreEqual(DefaultSettingsIdentity, defaults.IdentitySha256);
        Assert.AreEqual(Magnitude5SettingsIdentity, new AstrometricSolverOptions(MaximumCatalogMagnitude: 5).IdentitySha256);

        var deep = new AstrometricSolverOptions(CatalogSelectionProfile: Profile);
        Assert.AreEqual(DefaultSettingsJson[..^1] + ",\"CatalogSelectionProfile\":\"hyg-deep-selection-v1\"}", JsonSerializer.Serialize(deep));
        Assert.AreNotEqual(DefaultSettingsIdentity, deep.IdentitySha256);
    }

    [TestMethod]
    public void DeclaredProfilesAreFixedAndSetTheCeiling()
    {
        var profile = AstrometricCatalogSelectionProfile.HygDeepSelectionV1;
        Assert.AreEqual("hyg-deep-selection-v1", profile.Name);
        Assert.AreEqual(8192, profile.MaximumEntries);
        Assert.AreEqual(6d, profile.QualifiedMaximumMagnitude);
        Assert.HasCount(1, AstrometricCatalogSelectionProfile.Declared);
        Assert.AreSame(profile, AstrometricCatalogSelectionProfile.Find(Profile));
        Assert.IsNull(AstrometricCatalogSelectionProfile.Find("HYG-DEEP-SELECTION-V1"));
        Assert.IsNull(AstrometricCatalogSelectionProfile.Find(string.Empty));
        Assert.IsNull(AstrometricCatalogSelectionProfile.Find(null));

        Assert.AreEqual(AstrometricCatalogSelectionProfile.Declared.Max(p => p.MaximumEntries), AstrometricCatalogData.MaterializationCeiling);
        Assert.AreEqual(2500, AstrometricCatalogData.MaximumEntries);
        Assert.AreEqual(AstrometricCatalogData.MaximumEntries, new AstrometricSolverOptions().CatalogEntryBound);
        Assert.AreEqual(profile.MaximumEntries, Deep.CatalogEntryBound);
    }

    [TestMethod]
    [DataRow("hyg-deep-selection-v2", 5d)]
    [DataRow("Hyg-Deep-Selection-V1", 5d)]
    [DataRow("", 5d)]
    [DataRow(Profile, 6.000000000000001)]
    [DataRow(Profile, 7d)]
    [DataRow(Profile, double.NaN)]
    public void ProfileRejectsUndeclaredNamesAndUnqualifiedMagnitudes(string profile, double magnitude) =>
        Assert.ThrowsExactly<ArgumentException>(() => new AstrometricSolverOptions(MaximumCatalogMagnitude: magnitude, CatalogSelectionProfile: profile).Validate());

    [TestMethod]
    public void ProfileAcceptsItsQualifiedRangeAndNullKeepsTheLegacyRange()
    {
        Deep.Validate();
        new AstrometricSolverOptions(MaximumCatalogMagnitude: -1, CatalogSelectionProfile: Profile).Validate();
        // The legacy null profile remains valid at any finite magnitude, bounded only by its 2,500-entry read.
        new AstrometricSolverOptions(MaximumCatalogMagnitude: 7).Validate();
    }

    [TestMethod]
    [DataRow(null, 7d, 2500)]
    [DataRow(Profile, 6d, 8192)]
    public async Task SolveAsyncRequestsTheBoundOfItsProfile(string? profile, double magnitude, int entries)
    {
        var f = Shallow.Value;
        var source = new CapturingSource(new(f.Catalog.Metadata, f.Catalog.Stars, false, magnitude));
        var result = await AstrometricSolver.SolveAsync(f.Result.Assessment.Frame, f.Calibration, source, f.Detections,
            new(MaximumCatalogMagnitude: magnitude, CatalogSelectionProfile: profile)).ConfigureAwait(false);

        Assert.AreEqual(magnitude, source.RequestedMagnitude);
        Assert.AreEqual(entries, source.RequestedEntries);
        Assert.AreEqual("catalog-incomplete", result.Assessment.ReasonCode);
    }

    [TestMethod]
    public void CatalogDataMaterializesUpToTheCeilingOnly()
    {
        Assert.HasCount(AstrometricCatalogData.MaterializationCeiling, Padded(AstrometricCatalogData.MaterializationCeiling).Stars);
        Assert.ThrowsExactly<ArgumentException>(() => Padded(AstrometricCatalogData.MaterializationCeiling + 1));
    }

    [TestMethod]
    public void NullProfileSelectionAboveTheLegacyBoundIsUnsupportedOnSolverAndSession()
    {
        var f = Shallow.Value; var frame = f.Result.Assessment.Frame;
        var catalog = Padded(AstrometricCatalogData.MaximumEntries + 1);
        var legacy = new AstrometricSolverOptions(MaximumCatalogMagnitude: 6);
        foreach (var result in new[]
        {
            AstrometricSolver.Solve(frame, f.Calibration, catalog, f.Detections, legacy),
            AstrometricSolver.Refine(frame, f.Calibration, catalog, f.Detections, f.Result.Assessment, legacy)
        })
        {
            Assert.AreEqual(AstrometricAssessmentStatus.Unavailable, result.Assessment.Status);
            Assert.AreEqual("catalog-selection-unsupported", result.Assessment.ReasonCode);
            Assert.IsNull(result.Assessment.Parameters);
            Assert.AreEqual(0, result.Metrics.Hypotheses);
            AstrometricEvidenceJson.Validate(result.Assessment);
        }

        var session = OpticalCalibrationSession.Fit(SessionNominal, SessionFits(), SessionValidations(), catalog, new(FitRadialDistortion: true), legacy);
        Assert.AreEqual(OpticalCalibrationStatus.Unavailable, session.Status);
        Assert.AreEqual("catalog-selection-unsupported", session.ReasonCode);
        Assert.Contains("catalog-selection-unsupported", session.Rejections);
        Assert.IsNull(session.CalibratedNative);
    }

    [TestMethod]
    public void CeilingSelectionSolvesColdAndWarmExactlyAsItsRisenStarsDo()
    {
        var shallow = Shallow.Value; var deep = Ceiling.Value; var frame = deep.Result.Assessment.Frame;
        Assert.HasCount(AstrometricCatalogData.MaterializationCeiling, deep.Catalog.Stars);
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, deep.Result.Assessment.Status, deep.Result.Assessment.Reason);
        Assert.AreEqual(120, deep.Result.Metrics.IndexStars, "A fisheye indexes only its brightest 120 risen stars at any depth.");
        Assert.AreEqual(deep.Catalog.SelectionIdentitySha256, deep.Result.Assessment.CatalogSelectionIdentitySha256);
        Assert.AreEqual(Json(shallow.Result.Associations), Json(deep.Result.Associations));
        Assert.AreEqual(shallow.Result.Assessment.Parameters, deep.Result.Assessment.Parameters);
        Assert.AreEqual(shallow.Result.Assessment.Quality, deep.Result.Assessment.Quality);
        _ = AstrometricEvidenceJson.Serialize(deep.Result.Assessment);

        var warm = AstrometricSolver.Refine(frame, deep.Calibration, deep.Catalog, deep.Detections, deep.Result.Assessment, Deep);
        var shallowWarm = AstrometricSolver.Refine(frame, shallow.Calibration, shallow.Catalog, shallow.Detections, shallow.Result.Assessment, Deep);
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, warm.Assessment.Status, warm.Assessment.Reason);
        Assert.AreEqual(Json(shallowWarm.Associations), Json(warm.Associations));
        Assert.AreEqual(shallowWarm.Assessment.Parameters, warm.Assessment.Parameters);
    }

    [TestMethod]
    public void CeilingSelectionDiagnosesExactlyAsItsRisenStarsDo()
    {
        var shallow = Shallow.Value; var deep = Ceiling.Value;
        var expected = AstrometricResidualAnalyzer.Analyze(shallow.Calibration, shallow.Catalog, Deep, shallow.Result, shallow.Detections);
        var actual = AstrometricResidualAnalyzer.Analyze(deep.Calibration, deep.Catalog, Deep, deep.Result, deep.Detections);

        Assert.IsTrue(actual.HasMeasuredMapping);
        Assert.AreEqual(expected.PredictedStarCount, actual.PredictedStarCount);
        Assert.AreEqual(expected.EligiblePredictionCount, actual.EligiblePredictionCount);
        Assert.AreEqual(Json(expected.Residuals), Json(actual.Residuals));
        Assert.AreEqual(Json(expected.UnmatchedPredictions), Json(actual.UnmatchedPredictions));
        Assert.AreEqual(Json(expected.Bins), Json(actual.Bins));
    }

    [TestMethod]
    public void CeilingSelectionEstimatesUncertaintyExactlyAsItsRisenStarsDo()
    {
        var shallow = Shallow.Value; var deep = Ceiling.Value; const double sigma = .04 / 3.4641016151377544;
        AstrometricPixelCovariance[] Covariances(Solved f) => [.. f.Detections.Select(d => new AstrometricPixelCovariance(d.Index, sigma * sigma, 0, sigma * sigma))];
        var expected = AstrometricUncertaintyEstimator.Estimate(shallow.Calibration, shallow.Catalog, Deep, shallow.Result, shallow.Detections,
            Covariances(shallow), AstrometricClockFacts.NotSupplied);
        var actual = AstrometricUncertaintyEstimator.Estimate(deep.Calibration, deep.Catalog, Deep, deep.Result, deep.Detections,
            Covariances(deep), AstrometricClockFacts.NotSupplied);

        Assert.IsTrue(actual.IsAvailable, actual.Reason);
        Assert.AreEqual(Json(expected.Estimate), Json(actual.Estimate));
        Assert.AreEqual(Json(expected.Total), Json(actual.Total));
    }

    [TestMethod]
    public void CeilingSelectionCalibratesExactlyAsItsRisenStarsDo()
    {
        var search = Deep with { MinimumFocalScale = .97, MaximumFocalScale = 1.03 };
        var options = new OpticalCalibrationOptions(FitRadialDistortion: true);
        var expected = OpticalCalibrationSession.Fit(SessionNominal, SessionFits(), SessionValidations(), AstrometricTestFixture.Catalog(), options, search);
        var actual = OpticalCalibrationSession.Fit(SessionNominal, SessionFits(), SessionValidations(), Padded(AstrometricCatalogData.MaterializationCeiling), options, search);

        Assert.AreEqual(OpticalCalibrationStatus.Accepted, actual.Status, string.Join(",", actual.Rejections));
        Assert.AreEqual(expected.CalibratedNative, actual.CalibratedNative);
        Assert.AreEqual(Json(expected.Validations.Select(v => v.FocalScale)), Json(actual.Validations.Select(v => v.FocalScale)));
    }

    [TestMethod]
    public void DenseRisenCeilingSelectionEndsReasonCodedWithoutAMapping()
    {
        // Faint stars spread over the whole sky, about half of them risen: far more crowded than the 12 px isolation
        // rule admits, so verification finds no isolated evidence. The search must end with a reason code, never throw.
        var sky = AstrometricTestFixture.Catalog(); var random = new Random(116702);
        var stars = sky.Stars.Concat(Enumerable.Range(0, AstrometricCatalogData.MaterializationCeiling - sky.Stars.Count).Select(i =>
            new CelestialCatalogObject($"DENSE{i:00000}", $"Dense {i}", random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI,
                5.3 + .7 * random.NextDouble()))).ToArray();
        var catalog = new AstrometricCatalogData(Metadata("Generated dense test catalog", stars), stars, true, 7);
        var f = Shallow.Value;

        var result = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, catalog, f.Detections, Deep with { ColdBudgetMilliseconds = 5000 });

        Assert.IsFalse(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        CollectionAssert.Contains(new[] { "acquisition-or-quality-failed", "ambiguous", "resource-limit", "time-budget" }, result.Assessment.ReasonCode);
        _ = AstrometricEvidenceJson.Serialize(result.Assessment);
    }

    [TestMethod]
    public void WarmPriorIsIncompatibleAcrossProfileOptIn()
    {
        var f = Shallow.Value; var frame = f.Result.Assessment.Frame;
        var legacy = new AstrometricSolverOptions(MaximumCatalogMagnitude: 6);
        var legacyCold = AstrometricSolver.Solve(frame, f.Calibration, f.Catalog, f.Detections, legacy);
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, legacyCold.Assessment.Status, legacyCold.Assessment.Reason);
        Assert.AreNotEqual(legacyCold.Assessment.SettingsIdentitySha256, f.Result.Assessment.SettingsIdentitySha256);

        foreach (var (prior, options) in new[] { (f.Result.Assessment, legacy), (legacyCold.Assessment, Deep) })
        {
            var warm = AstrometricSolver.Refine(frame, f.Calibration, f.Catalog, f.Detections, prior, options);
            Assert.AreEqual("warm-context-incompatible", warm.Assessment.ReasonCode);
            Assert.IsNull(warm.Assessment.Parameters);
        }
        foreach (var (prior, options) in new[] { (f.Result.Assessment, Deep), (legacyCold.Assessment, legacy) })
            Assert.AreEqual("accepted", AstrometricSolver.Refine(frame, f.Calibration, f.Catalog, f.Detections, prior, options).Assessment.ReasonCode);
    }

    [TestMethod]
    public void EvidenceBoundIsTheValidatorsPreviousRule()
    {
        int[] counts = [-1, 0, 1, 1249, 1250, 1251, 2499, 2500, 2501, int.MaxValue];
        foreach (var expected in counts)
            foreach (var fitting in counts)
                foreach (var verification in counts)
                {
                    // The accepted-evidence bound exactly as the validator wrote it at 94883101.
                    var previous = expected > 2500 || (long)fitting + verification > 2500;
                    Assert.AreEqual(previous, AstrometricEvidenceJson.ExceedsEvidenceBound(expected, fitting, verification), $"{expected}/{fitting}/{verification}");
                }
    }

    [TestMethod]
    public void IndexPrefixMatchesTheMatrixImplementationAndItsStarGuard()
    {
        var random = new Random(116703);
        EnuVector[] Sky(int count) => [.. Enumerable.Range(0, count).Select(_ =>
        {
            var up = 2 * random.NextDouble() - 1; var azimuth = 2 * Math.PI * random.NextDouble(); var horizontal = Math.Sqrt(1 - up * up);
            return new EnuVector(horizontal * Math.Sin(azimuth), horizontal * Math.Cos(azimuth), up);
        })];
        // A dense cap: every triangle in it fits a wide span, so the triangle bound, not the star count, ends the prefix.
        EnuVector[] Cap(int count, double radius) => [.. Enumerable.Range(0, count).Select(_ =>
        {
            var r = radius * Math.Sqrt(random.NextDouble()); var azimuth = 2 * Math.PI * random.NextDouble();
            return new EnuVector(Math.Sin(r) * Math.Sin(azimuth), Math.Sin(r) * Math.Cos(azimuth), Math.Cos(r));
        })];
        EnuVector[] Lattice(int count) => [.. Enumerable.Range(0, count).Select(i => new EnuVector(Math.Sin(i % 50 * .002), Math.Sin(i / 50 * .002), 1).Normalize())];
        EnuVector[] Pathological() => [new(0, 0, 0), new(double.NaN, 0, 1), new(double.PositiveInfinity, 0, 0), new(1e308, 1e308, 0), new(0, 0, 1), new(0, 0, -1),
            new(0, 0, 1), new(double.Epsilon, 0, 1), new(0, 1, 0), new(1, 0, 0), new(-1, 0, 0), new(0, -1, 0)];

        var cases = new List<(string Name, EnuVector[] Rays, double Span, double Side, long Bound)>
        {
            ("empty", [], Math.PI, .01, 2_000_000), ("one", Sky(1), Math.PI, .01, 2_000_000), ("two", Sky(2), Math.PI, .01, 2_000_000),
            ("three-bound-zero", Sky(3), Math.PI, .001, 0), ("pathological", Pathological(), Math.PI, 0, 1),
            ("pathological-wide", Pathological(), Math.PI, 0, long.MaxValue),
            ("coincident-2500", [.. Enumerable.Repeat(new EnuVector(0, 0, 1), 2500)], Math.PI, .001, 2_000_000),
            ("lattice-2500", Lattice(2500), .05, .002, 2_000_000), ("lattice-2500-tight", Lattice(2500), .05, .002, 50_000),
            ("sky-60", Sky(60), Math.PI / 2, .01, 1000), ("sky-400-wide", Sky(400), Math.PI, .01, 2_000_000),
            ("sky-1500-narrow", Sky(1500), .1, .002, 2_000_000), ("sky-1501-narrow", Sky(1501), .1, .002, 2_000_000),
            ("sky-2500-narrow", Sky(2500), .1, .002, 2_000_000), ("sky-2500-moderate", Sky(2500), .35, .004, 50_000),
            ("cap-2500-at-two-million", Cap(2500, .2), .5, .002, 2_000_000), ("cap-1600-loose", Cap(1600, .4), .05, .004, long.MaxValue),
        };
        // Bounds that the triangles among the first 1,500 (or 1,501) rays meet and the next ray exceeds, so the matrix
        // prefix lands exactly on the index cap and exactly one past it.
        var exact = Sky(1600);
        cases.Add(("exactly-1500", exact, .2, .002, AstrometricSolverCore.CountTriangles(exact[..1500], .2, .002)));
        cases.Add(("exactly-1501", exact, .2, .002, AstrometricSolverCore.CountTriangles(exact[..1501], .2, .002)));
        var oracles = new Dictionary<string, int>(StringComparer.Ordinal);

        var guardFired = 0;
        foreach (var (name, rays, span, side, bound) in cases)
        {
            var oracle = oracles[name] = MatrixIndexPrefix(rays, span, side, bound);
            Assert.AreEqual(oracle, AstrometricSolverCore.IndexPrefix(rays, span, side, bound), name);
            var guarded = AstrometricSolverCore.IndexPrefix(rays, span, side, bound, null, 1500);
            if (oracle <= 1500) Assert.AreEqual(oracle, guarded, $"{name}: the guard must not fire for an index the solver accepts");
            else { Assert.AreEqual(1501, guarded, name); guardFired++; }
            // Run's decision: an index of more than 1,500 stars is rejected with the same reason, whatever its length.
            Assert.AreEqual(oracle > 1500 ? -1 : oracle, guarded > 1500 ? -1 : guarded, name);
        }
        Assert.IsGreaterThan(0, guardFired);
        Assert.AreEqual(1500, oracles["exactly-1500"], "the boundary case must land on the cap");
        Assert.AreEqual(1501, oracles["exactly-1501"], "the boundary case must land one past the cap");
    }

    [TestMethod]
    public void IsolationGridMatchesThePairwiseRuleOnSeededSkies()
    {
        var random = new Random(116704);
        foreach (var (count, width, height, quantum) in new[] { (0, 512, 512, 0d), (1, 512, 512, 0d), (40, 64, 64, 0d), (300, 512, 512, 0d), (2500, 512, 512, 0d),
            (2500, 512, 512, 1d), (2500, 256, 256, .5), (2500, 1936, 1216, 0d), (2500, 4096, 4096, 0d), (8192, 1936, 1216, 0d), (8192, 1936, 1216, 4d) })
        {
            // Quantized coordinates make exact 12 px separations and coincident points common; some identities repeat
            // and some projections are missing or fall outside the image.
            double Coordinate(int size)
            {
                var value = random.NextDouble() * (size + 40) - 20;
                return quantum > 0 ? Math.Round(value / quantum) * quantum : value;
            }
            var stars = Enumerable.Range(0, count).Select(i => (Id: i % 97 == 5 ? $"S{i / 2}" : $"S{i}",
                Pixel: i % 61 == 7 ? (PixelPoint?)null : new PixelPoint(Coordinate(width), Coordinate(height)))).ToArray();
            AssertSameIsolation(stars, width, height, $"{count}@{width}x{height}/{quantum}");
        }
    }

    [TestMethod]
    public void IsolationGridMatchesThePairwiseRuleAtItsEdges()
    {
        var root = 12 / Math.Sqrt(2);
        (string, PixelPoint?)[][] cases =
        [
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(112, 100))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(100, 112))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(Math.BitIncrement(112d), 100))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(Math.BitDecrement(112d), 100))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(100, Math.BitIncrement(112d)))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(100 + root, 100 + root))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(100 + Math.BitIncrement(root), 100 + root))],
            [("a", new PixelPoint(100, 100)), ("b", new PixelPoint(100 + Math.BitDecrement(root), 100 + Math.BitDecrement(root)))],
            // 12 px spanning two cell boundaries, from just below one boundary and from exactly on one.
            [("a", new PixelPoint(Math.BitDecrement(16d), 50)), ("b", new PixelPoint(Math.BitDecrement(28d), 50))],
            [("a", new PixelPoint(16, 50)), ("b", new PixelPoint(28, 50))],
            [("a", new PixelPoint(Math.BitDecrement(16d), Math.BitDecrement(16d))), ("b", new PixelPoint(Math.BitDecrement(16d) + root, Math.BitDecrement(16d) + root))],
            // Coincident points: distinct identities crowd each other; a repeated identity never crowds itself.
            [("a", new PixelPoint(200, 200)), ("b", new PixelPoint(200, 200))],
            [("a", new PixelPoint(200, 200)), ("a", new PixelPoint(200, 200)), ("c", new PixelPoint(300, 300))],
            [("a", new PixelPoint(200, 200)), ("a", new PixelPoint(205, 200)), ("b", new PixelPoint(230, 200))],
            [("a", new PixelPoint(200, 200)), ("a", new PixelPoint(220, 200)), ("b", new PixelPoint(210, 200))],
            // The margin, negative and off-image coordinates; an excluded point never crowds an included one.
            [("m1", new PixelPoint(6, 100)), ("m2", new PixelPoint(Math.BitIncrement(6d), 100)), ("m3", new PixelPoint(506, 100)),
                ("m4", new PixelPoint(Math.BitDecrement(506d), 300)), ("n", new PixelPoint(-50, -50)), ("o", new PixelPoint(1e9, 100))],
            [("in", new PixelPoint(10, 100)), ("out", new PixelPoint(4, 100)), ("neg", new PixelPoint(-2, 100))],
            // Non-finite, extreme and missing projections.
            [("nan", new PixelPoint(double.NaN, 100)), ("nan2", new PixelPoint(100, double.NaN)), ("inf", new PixelPoint(double.PositiveInfinity, 100)),
                ("ninf", new PixelPoint(double.NegativeInfinity, 100)), ("max", new PixelPoint(double.MaxValue, double.MaxValue)),
                ("min", new PixelPoint(double.MinValue, 100)), ("eps", new PixelPoint(double.Epsilon, 100)), ("null", null), ("k", new PixelPoint(100, 100)),
                ("k2", new PixelPoint(105, 100))],
            // The largest coordinates an int-sized sensor admits.
            [("big1", new PixelPoint(2147483640, 2147483640)), ("big2", new PixelPoint(2147483630, 2147483640)), ("big3", new PixelPoint(2147483600, 2147483600)),
                ("big4", new PixelPoint(Math.BitDecrement(2147483641d), 100))],
        ];
        foreach (var stars in cases)
            foreach (var (width, height) in new[] { (512, 512), (13, 13), (5, 5), (int.MaxValue, int.MaxValue) })
                AssertSameIsolation(stars, width, height, $"{string.Join(",", stars.Select(s => s.Item1))}@{width}");
    }

    private static void AssertSameIsolation((string Id, PixelPoint? Pixel)[] stars, int width, int height, string name)
    {
        var expected = PairwiseIsolatedIds(stars, width, height).Order(StringComparer.Ordinal).ToArray();
        var actual = AstrometricSolverCore.IsolatedIds(stars, width, height).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, actual, name);
    }

    /// <summary>Evaluate's isolation exactly as solver v2 shipped it at 94883101, kept as the oracle for the grid.</summary>
    private static HashSet<string> PairwiseIsolatedIds((string Id, PixelPoint? Pixel)[] stars, int width, int height)
    {
        var projected = stars.Where(s => s.Pixel is { } p && p.X > 6 && p.Y > 6 && p.X < width - 6 && p.Y < height - 6).ToArray();
        return projected.Where(s => projected.All(t => t.Id == s.Id || AstrometricMath.Distance(t.Pixel!.Value, s.Pixel!.Value) > 12)).Select(s => s.Id).ToHashSet();
    }

    /// <summary>IndexPrefix exactly as solver v2 shipped it at 94883101, with its full angle matrix, kept as the oracle.</summary>
    private static int MatrixIndexPrefix(EnuVector[] rays, double maxSpan, double minimumSide, long maximumTriangles)
    {
        var distances = new double[rays.Length * rays.Length]; var count = 0L;
        for (var c = 0; c < rays.Length; c++)
        {
            for (var a = 0; a < c; a++) distances[a * rays.Length + c] = distances[c * rays.Length + a] = AstrometricSolverCore.Angle(rays[a], rays[c]);
            for (var a = 0; a < c; a++)
            {
                var ac = distances[a * rays.Length + c]; if (ac < minimumSide || ac > maxSpan) continue;
                for (var b = a + 1; b < c; b++)
                    if (AstrometricSolverCore.IsIndexTriangle(rays[a], rays[b], rays[c], distances[a * rays.Length + b], ac, distances[b * rays.Length + c], maxSpan, minimumSide) &&
                        ++count > maximumTriangles) return c;
            }
        }
        return rays.Length;
    }

    private static Solved SolveUnderProfile(AstrometricCatalogData catalog)
    {
        var truth = AstrometricTestFixture.Truth(); var calibration = AstrometricTestFixture.Calibration(truth);
        var detections = AstrometricTestFixture.Centroids(AstrometricTestFixture.Catalog(), truth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections, Deep);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason);
        return new(catalog, calibration, detections, result);
    }

    /// <summary>
    /// The fixture sky padded to <paramref name="count"/> complete entries with stars south of -56.5° that never rise at the
    /// fixture site (35.35° N), at magnitudes inside every requested ceiling, so the selection is deep but the risen sky is unchanged.
    /// </summary>
    private static AstrometricCatalogData Padded(int count)
    {
        var sky = AstrometricTestFixture.Catalog(); var random = new Random(116701);
        var stars = sky.Stars.Concat(Enumerable.Range(0, count - sky.Stars.Count).Select(i => new CelestialCatalogObject($"PAD{i:00000}", $"Never risen {i}",
            random.NextDouble() * 24, -56.5 - 33 * random.NextDouble(), 5.3 + .7 * random.NextDouble()))).ToArray();
        return new(Metadata("Generated padded test catalog", stars), stars, true, 7);
    }

    private static CatalogMetadata Metadata(string name, CelestialCatalogObject[] stars) => new(name, "1",
        new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"), Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stars))), "test-generated", "1");

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    // The optical calibration session fixture: a fisheye with +1.2% focal, a principal-point offset and k1 = -0.006.
    private const int SessionSize = 512;
    private static readonly ProjectionContext SessionNominal = AstrometricTestFixture.Truth() with { BoresightAltitudeDegrees = 90, BoresightAzimuthDegrees = 0, RollDegrees = 0 };
    private static readonly ProjectionContext SessionTruth = AstrometricTestFixture.Truth() with
    {
        FocalLengthXPixels = SessionNominal.FocalLengthXPixels * 1.012,
        FocalLengthYPixels = SessionNominal.FocalLengthYPixels * 1.012,
        PrincipalPointX = 259.5,
        PrincipalPointY = 253,
        RadialDistortionK1 = -.006
    };
    private static OpticalCalibrationFrame[] SessionFits() => [SessionFrame(0), SessionFrame(1.5), SessionFrame(3)];
    private static OpticalCalibrationFrame[] SessionValidations() => [SessionFrame(2.2)];

    /// <summary>Projects the fixture sky through the session truth into a full-frame readout, with ±0.02 px centroid noise.</summary>
    private static OpticalCalibrationFrame SessionFrame(double hours)
    {
        var readout = new FrameReadoutDescriptor(SessionSize, SessionSize, 0, 0, SessionSize, SessionSize, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null);
        var utc = AstrometricTestFixture.Utc.AddHours(hours);
        var projector = ProjectorFactory.Create(SessionTruth);
        var random = new Random(812 + (int)Math.Round(hours * 100));
        var all = AstrometricTestFixture.Catalog().Stars
            .Select(s => (Star: s, Horizontal: AstrometricTestFixture.Horizontal(s, utc)))
            .Where(s => s.Horizontal.AltitudeDegrees > 5)
            .Select(s => (s.Star, Pixel: projector.Project(s.Horizontal)))
            .Where(s => s.Pixel is { } p && p.X > 6 && p.Y > 6 && p.X < SessionSize - 6 && p.Y < SessionSize - 6).ToArray();
        var detections = all.Where(s => all.All(t => t.Star.Id == s.Star.Id || AstrometricTestFixture.Distance(t.Pixel!.Value, s.Pixel!.Value) > 13))
            .Select((s, i) => new AstrometricDetection(i, new(s.Pixel!.Value.X + (random.NextDouble() - .5) * .04, s.Pixel.Value.Y + (random.NextDouble() - .5) * .04),
                100000 * Math.Pow(10, -.4 * s.Star.Magnitude))).ToArray();
        return new(AstrometricTestFixture.Frame(utc), readout, AstrometricTestFixture.Hash($"readout:{readout}"), detections);
    }

    private sealed class CapturingSource(AstrometricCatalogData value) : IAstrometricCatalogSource
    {
        internal double RequestedMagnitude { get; private set; }
        internal int RequestedEntries { get; private set; }
        public ValueTask<AstrometricCatalogData> ReadAsync(double maximumMagnitude, int maximumEntries, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); RequestedMagnitude = maximumMagnitude; RequestedEntries = maximumEntries; return ValueTask.FromResult(value); }
    }
}
