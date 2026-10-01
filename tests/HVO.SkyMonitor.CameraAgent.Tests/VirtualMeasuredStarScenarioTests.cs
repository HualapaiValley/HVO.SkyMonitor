#pragma warning disable CA5394 // Fixed seeds define a reproducible synthetic sky, never security material.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using R = HVO.SkyMonitor.Astronomy.AstrometricDiagnosticReasons;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Measured-star scenarios rendered by the actual VirtualSky module. Only final emulator pixels reach measurement,
/// solving, and diagnostics; the independent Cartesian truth projection is used only to score associations.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class VirtualMeasuredStarScenarioTests
{
    private const string Profile = "mono-native";
    // The native 12-bit ASI174 readout rejects clouds and sensor-plane tracks, so those scenarios use a 16-bit linear readout.
    private const string Linear = "mono-linear";
    private static readonly DateTimeOffset Utc = new(2026, 3, 20, 7, 0, 0, TimeSpan.Zero);
    private static readonly SemaphoreSlim ClearGate = new(1, 1);
    private static readonly Dictionary<string, Run> Clear = new(StringComparer.Ordinal);
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Clear_AcquiresWithTruthCorrectAssociationsAndCompleteDiagnostics()
    {
        var run = await ClearAsync(Profile).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        var d = run.Diagnostics;
        Assert.AreEqual(run.Solved.Associations.Count, d.Residuals.Count);
        Assert.AreEqual(d.Residuals.Count, d.Bins.Where(b => b.Dimension == "radius-fraction").Sum(b => b.FittingCount + b.VerificationCount));
        Assert.IsTrue(d.FittingRmsPixels < .5 && d.VerificationRmsPixels < .5, $"{d.FittingRmsPixels} {d.VerificationRmsPixels}");
        Assert.AreEqual("well-conditioned", d.Conditioning!.Status);
        Assert.AreEqual(AstrometricResidualAnalyzer.CovarianceUnvalidated, d.CovarianceStatus);
        Assert.IsTrue(d.Residuals.All(r => r.NormalizedResidualSquared is not null));
    }

    [TestMethod]
    public async Task ThinCloud_StillAcceptsWithoutFalseAssociations()
    {
        var run = await RefineAsync("thin-cloud", o => o["cloudScenario"] = Cloud(1, .35), profileName: Linear).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        var attenuation = MedianFluxRatio(Clear[Linear], run);
        TestContext.WriteLine($"  median associated flux ratio {attenuation:F3}");
        Assert.IsTrue(attenuation < .95, "thin cloud attenuates the measured stellar flux");
    }

    [TestMethod]
    public async Task OpaqueObstruction_ReportsPredictionsWithoutMeasuredSources()
    {
        var run = await RefineAsync("opaque-cloud", o => o["cloudScenario"] = Cloud(.45, 1), profileName: Linear).ConfigureAwait(false);
        Report(run);
        AssertNoFalseAssociation(run);
        Assert.IsTrue(run.Measured.Detections.Count < Clear[Linear].Measured.Detections.Count * .8, $"{run.Measured.Detections.Count}");
        if (run.Solved.Assessment.Status == AstrometricAssessmentStatus.Accepted)
            Assert.IsTrue(Count(run.Diagnostics.UnmatchedPredictionReasonCounts, R.NoMeasuredSource) > 25);
        else
            Assert.IsFalse(run.Diagnostics.HasMeasuredMapping);
    }

    [TestMethod]
    public async Task Noisy_ExcludesLowSignalToNoiseSourcesAndStillAccepts()
    {
        var run = await RefineAsync("noisy", o => o["readNoiseStandardDeviation"] = 12, profileName: Linear).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        Assert.IsTrue(run.Measured.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.LowSignalToNoise) > 0);
        Assert.IsTrue(run.Measured.MedianNoiseSigma > Clear[Linear].Measured.MedianNoiseSigma * 2);
    }

    [TestMethod]
    public async Task StrongVignetting_KeepsRadialResidualsBounded()
    {
        var run = await RefineAsync("vignetted", o => o["vignettingStrength"] = .8).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        var outer = run.Diagnostics.Bins.Single(b => b.Dimension == "radius-fraction" && b.Index == 3);
        Assert.IsTrue(outer.RmsPixels is null or < .75, $"{outer.RmsPixels}");
    }

    [TestMethod]
    public async Task Saturation_IsFlaggedOrExcludedAndNeverFalselyAssociated()
    {
        var run = await RefineAsync("saturated", o => o["magnitudeZeroElectronsPerSecond"] = 2_000_000).ConfigureAwait(false);
        Report(run);
        AssertNoFalseAssociation(run);
        var flagged = run.Measured.Detections.Count(d => d.Conditions.HasFlag(StellarSourceConditions.Saturated));
        Assert.IsTrue(flagged + run.Measured.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.SaturatedExcessive) > 10, $"{flagged}");
        Assert.IsTrue(run.Measured.Detections.Where(d => d.Conditions.HasFlag(StellarSourceConditions.Saturated))
            .All(d => d.SaturatedSampleCount is > 0 and <= 16));
    }

    [TestMethod]
    public async Task LongExposure_ReportsTrailLengthAndAngle()
    {
        var run = await RefineAsync("trailed", o =>
        {
            o["maximumStellarStepPixels"] = .5;
            o["magnitudeZeroElectronsPerSecond"] = 500;
        }, TimeSpan.FromSeconds(180)).ConfigureAwait(false);
        Report(run);
        AssertNoFalseAssociation(run);
        var trailed = run.Measured.Detections.Where(d => d.Conditions.HasFlag(StellarSourceConditions.Trailed)).ToArray();
        Assert.IsTrue(trailed.Length > 50, $"{trailed.Length}");
        Assert.IsTrue(trailed.All(d => d.TrailLengthPixels is >= 3 and <= 16 && d.TrailAngleDegrees is >= -90 and < 90));
    }

    [TestMethod]
    public async Task ClippedReadout_ReportsEdgeExclusionsAndIneligiblePredictions()
    {
        var run = await RefineAsync("clipped", _ => { }, profileName: "mono-roi").ConfigureAwait(false);
        Report(run);
        AssertNoFalseAssociation(run);
        Assert.IsTrue(Count(run.Diagnostics.UnmatchedPredictionReasonCounts, R.NearEdge) +
            Count(run.Diagnostics.UnmatchedPredictionReasonCounts, R.OutsideAperture) > 0);
        Assert.IsTrue(run.Measured.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.ImageEdge) > 0);
    }

    [TestMethod]
    public async Task HotPixels_AreExcludedBeforeSolving()
    {
        var spots = FarFromStars(24, 1);
        var run = await RefineAsync("hot-pixels", o => o["transientScenario"] = Sensor(spots, 400_000, .25), profileName: Linear).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        Assert.IsTrue(run.Measured.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.HotPixelOrCosmicRay) >= spots.Length / 2);
        Assert.IsFalse(run.Measured.Detections.Any(d => spots.Any(s => Distance(d.Pixel, s) < 2)));
    }

    [TestMethod]
    public async Task FalseStars_AreMeasuredButNeverAssociated()
    {
        var spots = FarFromStars(16, 2);
        var run = await RefineAsync("false-stars", o => o["transientScenario"] = Sensor(spots, 4_000, 1), profileName: Linear).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        var measured = run.Measured.Detections.Where(d => spots.Any(s => Distance(d.Pixel, s) < 1.5)).Select(d => d.Index).ToHashSet();
        Assert.IsTrue(measured.Count >= spots.Length * 3 / 4, $"{measured.Count}");
        Assert.IsFalse(run.Solved.Associations.Any(a => measured.Contains(a.DetectionIndex)));
        Assert.IsTrue(run.Diagnostics.UnassociatedDetections.Count(u => measured.Contains(u.DetectionIndex) && u.ReasonCode == R.NoCatalogPrediction)
            >= measured.Count * 3 / 4);
    }

    [TestMethod]
    public async Task PlantedCatalogPairs_AreBlendedOrCrowdedAndNeverAssociated()
    {
        // The planted pairs are part of every scenario sky, so the clear acquisition already contains them.
        var run = await ClearAsync(Profile).ConfigureAwait(false);
        var ids = Pairs.Value.SelectMany(p => new[] { p.Primary.Id, p.Companion.Id }).ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(run.Solved.Associations.Any(a => ids.Contains(a.CatalogId)));
        Assert.IsTrue(run.Measured.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.Blended) +
            run.Measured.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.Crowded) >= Pairs.Value.Length);
        Assert.AreEqual(2 * Pairs.Value.Length, run.Diagnostics.UnmatchedPredictions.Count(u => ids.Contains(u.CatalogId)));
        Assert.IsTrue(run.Diagnostics.UnmatchedPredictions.Where(u => ids.Contains(u.CatalogId)).All(u => u.ReasonCode == R.CrowdedPrediction));
    }

    [TestMethod]
    public async Task AmbiguousCompanion_LeavesTheCatalogStarUnassociated()
    {
        var paired = Pairs.Value.Select(p => p.Primary.Id).ToHashSet(StringComparer.Ordinal);
        var primaries = IsolatedStars(16, 2).Where(p => !paired.Contains(p.Star.Id)).Take(8).ToArray();
        var spots = primaries.Select(p => new PixelPoint(p.Pixel.X + 3, p.Pixel.Y)).ToArray();
        var flux = primaries.Select(p => 60_000 * Math.Pow(10, -.4 * p.Star.Magnitude)).ToArray();
        var run = await RefineAsync("ambiguous-companion", o => o["transientScenario"] = Sensor(spots, flux, 1), profileName: Linear).ConfigureAwait(false);
        Report(run);
        AssertAccepted(run);
        var ids = primaries.Select(p => p.Star.Id).ToHashSet(StringComparer.Ordinal);
        Assert.IsFalse(run.Solved.Associations.Any(a => ids.Contains(a.CatalogId)));
        var reasons = run.Diagnostics.UnmatchedPredictions.Where(u => ids.Contains(u.CatalogId)).Select(u => u.ReasonCode).ToArray();
        Assert.IsTrue(reasons.All(r => r is R.Ambiguous or R.OffsetMeasuredSource or R.CrowdedPrediction or R.NearEdge || r.StartsWith(R.MeasurementExcludedPrefix, StringComparison.Ordinal)),
            string.Join(",", reasons));
    }

    internal sealed record Run(VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels, AstrometricCalibration Nominal,
        CelestialCatalogObject[] Stars, StellarMeasurementResult Measured, AstrometricSolveResult Solved,
        AstrometricResidualDiagnostics Diagnostics, DateTimeOffset SceneUtc, double CaptureMs, double MeasureMs, double SolveMs);

    // One blind acquisition per readout; every other scenario on that readout refines from it.
    private static async Task<Run> ClearAsync(string profileName)
    {
        await ClearGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Clear.TryGetValue(profileName, out var run))
                Clear[profileName] = run = await RunAsync("clear-" + profileName, profileName, _ => { }, null, null).ConfigureAwait(false);
            return run;
        }
        finally { ClearGate.Release(); }
    }

    private static async Task<Run> RefineAsync(string scenario, Action<JsonObject> patch, TimeSpan? exposure = null, string profileName = Profile)
    {
        var prior = await ClearAsync(profileName).ConfigureAwait(false);
        return await RunAsync(scenario, profileName, patch, exposure, prior.Solved.Assessment).ConfigureAwait(false);
    }

    private static VirtualAstrometryProfile LoadProfile(string profileName)
    {
        if (profileName != Linear) return VirtualAstrometryFixture.Profiles(1103).Single(p => p.Name == profileName);
        var native = LoadProfile(Profile);
        var options = JsonNode.Parse(native.Config.Module.Options!.Value.GetRawText())!.AsObject();
        options["asi174Sensor"] = new JsonObject { ["enabled"] = false };
        options["backgroundElectronsPerSecond"] = 20;
        var rig = native.Config.Rig with
        {
            Readout = native.Config.Rig.Readout! with
            {
                SampleDepthBits = 16, StoredCodeTransform = FrameStoredCodeTransform.IdentityV1, WhiteLevel = 65535
            }
        };
        return native with
        {
            Name = Linear,
            Config = native.Config with { Rig = rig, Module = native.Config.Module with { Options = JsonSerializer.SerializeToElement(options) } }
        };
    }

    private static async Task<Run> RunAsync(string scenario, string profileName, Action<JsonObject> patch, TimeSpan? exposure,
        AstrometricFrameAssessment? prior)
    {
        var stars = Sky.Value;
        var catalog = Catalog(stars);
        var profile = LoadProfile(profileName);
        var options = JsonNode.Parse(profile.Config.Module.Options!.Value.GetRawText())!.AsObject();
        patch(options);
        profile = profile with
        {
            Name = scenario,
            Config = profile.Config with { Module = profile.Config.Module with { Options = JsonSerializer.SerializeToElement(options) } }
        };
        var module = new VirtualSkyCameraModule(TimeProvider.System, new InMemoryCelestialCatalog(stars), new ProjectedSceneStore());
        await using var lifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        var captured = await module.CaptureAsync(new(Utc, TimeSpan.FromSeconds(600), CaptureMode.Still,
            new(exposure ?? TimeSpan.FromSeconds(1), profileName == Linear ? 1 : 150, null, null)), CancellationToken.None).ConfigureAwait(false);
        var captureMs = clock.Elapsed.TotalMilliseconds;
        var pixels = VirtualAstrometryPixels.FromCapture(captured.Frame!);
        var nominal = VirtualAstrometryFixture.NominalCalibration(profile);
        clock.Restart();
        var measured = VirtualAstrometryFixture.MeasureV2(pixels, nominal);
        var measureMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var solved = VirtualAstrometryFixture.SolveV2(pixels, nominal, catalog, measured, prior);
        if (prior is not null && solved.Assessment.Status != AstrometricAssessmentStatus.Accepted)
            solved = VirtualAstrometryFixture.SolveV2(pixels, nominal, catalog, measured);
        var solveMs = clock.Elapsed.TotalMilliseconds;
        var diagnostics = VirtualAstrometryFixture.Diagnose(nominal, catalog, measured, solved);
        return new(profile, pixels, nominal, stars, measured, solved, diagnostics, captured.Frame!.Metadata.Scene!.SceneUtc!.Value,
            captureMs, measureMs, solveMs);
    }

    private void Report(Run run)
    {
        var d = run.Diagnostics;
        TestContext.WriteLine($"{run.Profile.Name}: capture {run.CaptureMs:F0} ms, measure {run.MeasureMs:F0} ms, solve {run.SolveMs:F0} ms; " +
            $"{run.Measured.Detections.Count} detections, {run.Measured.Exclusions.Count} exclusions; {run.Solved.Assessment.Mode} " +
            $"{run.Solved.Assessment.Status} {run.Solved.Assessment.ReasonCode}; {run.Solved.Associations.Count} associations");
        TestContext.WriteLine($"  background {run.Measured.MedianBackground:F1} noise {run.Measured.MedianNoiseSigma:F2} candidates {run.Measured.CandidateCount}");
        TestContext.WriteLine("  excluded " + string.Join(", ", run.Measured.ExclusionCounts.Select(c => $"{c.Key}={c.Value}")));
        TestContext.WriteLine("  unmatched " + string.Join(", ", d.UnmatchedPredictionReasonCounts.Select(c => $"{c.ReasonCode}={c.Count}")));
        TestContext.WriteLine("  unassociated " + string.Join(", ", d.UnassociatedDetectionReasonCounts.Select(c => $"{c.ReasonCode}={c.Count}")));
    }

    private static void AssertAccepted(Run run)
    {
        Assert.AreEqual(AstrometricAssessmentStatus.Accepted, run.Solved.Assessment.Status, run.Solved.Assessment.Reason);
        Assert.IsTrue(run.Diagnostics.HasMeasuredMapping);
        Assert.IsTrue(run.Solved.Associations.Count >= 50, $"{run.Solved.Associations.Count}");
        AssertNoFalseAssociation(run);
    }

    // Every association must name the catalog star whose independent truth pixel is nearest the measured centroid.
    private static void AssertNoFalseAssociation(Run run)
    {
        var truth = Truth(run.Profile.Config.Rig, run.SceneUtc, run.Stars);
        var detections = run.Measured.Detections.ToDictionary(d => d.Index);
        foreach (var association in run.Solved.Associations)
        {
            var pixel = detections[association.DetectionIndex].Pixel;
            var nearest = truth.MinBy(t => Distance(t.Pixel, pixel));
            Assert.AreEqual(nearest.Star.Id, association.CatalogId, $"{run.Profile.Name}: false association at {pixel}");
            Assert.IsTrue(Distance(nearest.Pixel, pixel) < 1.5, $"{run.Profile.Name}: {association.CatalogId} centroid error {Distance(nearest.Pixel, pixel)}");
        }
        Assert.AreEqual(run.Solved.Associations.Count, run.Solved.Associations.Select(a => a.DetectionIndex).Distinct().Count());
    }

    private static (CelestialCatalogObject Star, PixelPoint Pixel)[] Truth(CameraRigConfig rig, DateTimeOffset utc, IEnumerable<CelestialCatalogObject> stars) =>
        [.. stars.Select(star => (Star: star, Ray: VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.J2000(star), utc, VirtualAstrometryFixture.Observer)))
            .Where(item => item.Ray.Z > 0).Select(item => (item.Star, Pixel: VirtualAstrometryReference.Project(rig, item.Ray)))
            .Where(item => item.Pixel is not null).Select(item => (item.Star, item.Pixel!.Value))];

    private static CameraRigConfig Rig => LoadProfile(Profile).Config.Rig;

    private static (CelestialCatalogObject Star, PixelPoint Pixel)[] Interior()
    {
        var rig = Rig;
        var cx = rig.Optics.PrincipalPointX!.Value;
        var cy = rig.Optics.PrincipalPointY!.Value;
        var radius = rig.Optics.ImageCircleRadiusPixels!.Value * .8;
        return [.. Truth(rig, Utc, BaseSky.Value).Where(t => Math.Pow(t.Pixel.X - cx, 2) + Math.Pow(t.Pixel.Y - cy, 2) < radius * radius)];
    }

    private static (CelestialCatalogObject Star, PixelPoint Pixel)[] IsolatedStars(int count, int stride)
    {
        var all = Truth(Rig, Utc, BaseSky.Value);
        return [.. Interior().Where(t => t.Star.Magnitude < 3.5 && all.All(o => o.Star.Id == t.Star.Id || Distance(o.Pixel, t.Pixel) > 30))
            .Where((_, i) => i % stride == 0).Take(count)];
    }

    private static PixelPoint[] FarFromStars(int count, int seed)
    {
        var random = new Random(seed);
        var rig = Rig;
        var cx = rig.Optics.PrincipalPointX!.Value;
        var cy = rig.Optics.PrincipalPointY!.Value;
        var all = Truth(rig, Utc, BaseSky.Value);
        var spots = new List<PixelPoint>();
        while (spots.Count < count)
        {
            var spot = new PixelPoint(Math.Round(cx + (random.NextDouble() - .5) * 700) + .5, Math.Round(cy + (random.NextDouble() - .5) * 700) + .5);
            if (all.All(t => Distance(t.Pixel, spot) > 20) && spots.All(s => Distance(s, spot) > 20)) spots.Add(spot);
        }
        return [.. spots];
    }

    private static JsonNode Cloud(double coverage, double opacity) => JsonSerializer.SerializeToNode(new VirtualCloudScenarioDefinition
    {
        ScenarioId = "measured-star-cloud",
        Seed = 1103,
        EpochUtc = Utc.AddHours(-1),
        Keyframes = [new() { Coverage = coverage, MaximumOpacity = opacity, ScatterFraction = .2 }]
    }, VirtualAstrometryFixture.JsonOptions)!;

    private static JsonNode Sensor(IReadOnlyList<PixelPoint> spots, double electronsPerSecond, double sigma) =>
        Sensor(spots, [.. spots.Select(_ => electronsPerSecond)], sigma);

    private static JsonNode Sensor(IReadOnlyList<PixelPoint> spots, double[] electronsPerSecond, double sigma) =>
        JsonSerializer.SerializeToNode(new VirtualTransientScenarioDefinition
        {
            ScenarioId = "measured-star-sensor",
            Seed = 1103,
            EpochUtc = Utc.AddHours(-1),
            SensorTracks = [.. spots.Select((s, i) => new VirtualTransientSensorTrack
            {
                PrimitiveId = $"s-{i:000}",
                Keyframes =
                [
                    new() { OffsetSeconds = 0, PixelX = s.X, PixelY = s.Y, ElectronsPerSecond = electronsPerSecond[i], SigmaPixels = sigma },
                    new() { OffsetSeconds = 7200, PixelX = s.X, PixelY = s.Y, ElectronsPerSecond = electronsPerSecond[i], SigmaPixels = sigma }
                ]
            })]
        }, VirtualAstrometryFixture.JsonOptions)!;

    // Median flux ratio, candidate over reference, for catalog stars associated in both runs.
    private static double MedianFluxRatio(Run reference, Run candidate)
    {
        var flux = reference.Solved.Associations.ToDictionary(a => a.CatalogId,
            a => reference.Measured.Detections.Single(d => d.Index == a.DetectionIndex).Flux, StringComparer.Ordinal);
        var ratios = candidate.Solved.Associations.Where(a => flux.ContainsKey(a.CatalogId))
            .Select(a => candidate.Measured.Detections.Single(d => d.Index == a.DetectionIndex).Flux / flux[a.CatalogId]).Order().ToArray();
        return ratios[ratios.Length / 2];
    }

    private static int Count(IEnumerable<AstrometricReasonCount> counts, string code) => counts.Where(c => c.ReasonCode == code).Sum(c => c.Count);
    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // A seeded all-sky catalog with a realistic magnitude distribution through V=5 (roughly 1,600 stars).
    private static readonly Lazy<CelestialCatalogObject[]> BaseSky = new(() =>
    {
        var random = new Random(110300);
        return [.. Enumerable.Range(0, 1600).Select(i => new CelestialCatalogObject($"SKY{i:0000}", $"Synthetic {i}",
            random.NextDouble() * 24, Math.Asin(2 * random.NextDouble() - 1) * 180 / Math.PI,
            2 * Math.Log10(1 + random.NextDouble() * (Math.Pow(10, 2.5) - 1))))];
    });

    // Eight isolated bright stars each receive a catalog companion about three pixels away.
    private static readonly Lazy<(CelestialCatalogObject Primary, CelestialCatalogObject Companion)[]> Pairs = new(() =>
        [.. IsolatedStars(8, 3).Select((p, i) => (p.Star, new CelestialCatalogObject($"PAIR{i:00}", $"Planted companion {i}",
            p.Star.RightAscensionHours, p.Star.DeclinationDegrees + .5, p.Star.Magnitude + .3)))]);

    private static readonly Lazy<CelestialCatalogObject[]> Sky = new(() => [.. BaseSky.Value, .. Pairs.Value.Select(p => p.Companion)]);

    private static AstrometricCatalogData Catalog(CelestialCatalogObject[] stars) => new(new("Synthetic measured-star scenario sky", "1",
        new Uri("https://github.com/HualapaiValley/HVO.SkyMonitor"), Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stars))),
        "test-generated", "1"), stars, true, 5);
}
