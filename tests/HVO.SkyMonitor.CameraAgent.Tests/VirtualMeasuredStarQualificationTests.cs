using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Explicit HYG evidence comparing the v1 detector with the v2 measurer on identical actual-module frames.
/// Only final pixels reach either path; the independent truth projection scores recall, identity, and centroid error.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualMeasuredStarQualificationTests
{
    // Predeclared on #1103 before held-out evidence; tuning results may not change them.
    private const double MaximumCentroidRmsPixels = .25;
    private const double MaximumCentroidRmsRegressionPixels = .02;
    private const double MaximumRecallRegression = .02;
    private const double MaximumMeasurementMilliseconds = 2500;
    private const double MaximumMeasurementTimeRatio = 4;

    // Undefined RMS (no associations) and unbounded conditioning are evidence, not serialization failures.
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TuningMeasuredStars() => RunAsync(tuning: true);

    [TestMethod]
    public Task HeldOutMeasuredStars() => RunAsync(tuning: false);

    private async Task RunAsync(bool tuning)
    {
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        Assert.HasCount(1637, catalog.Stars); Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        if (!tuning) VirtualAstrometryQualificationTests.RequireRevision();
        var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110220) }
            : [(Month: 1, Day: 15, Seed: 110201), (Month: 5, Day: 15, Seed: 110205), (Month: 9, Day: 15, Seed: 110209)];
        var partition = tuning ? "measured-stars-tuning-not-final" : "measured-stars-held-out";
        var reports = new List<object>(); var failures = new List<string>();
        foreach (var (month, day, seed) in partitions)
        {
            foreach (var profile in VirtualAstrometryFixture.Profiles(seed))
            {
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
                var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero);
                var captured = await module.CaptureAsync(VirtualAstrometryQualificationTests.Request(utc), CancellationToken.None).ConfigureAwait(false);
                var input = VirtualAstrometryPixels.FromCapture(captured.Frame!);
                var sceneUtc = captured.Frame!.Metadata.Scene!.SceneUtc!.Value;
                var nominal = VirtualAstrometryFixture.NominalCalibration(profile);
                var caseId = $"{month:D2}-{profile.Name}";
                var truth = Truth(profile.Config.Rig, sceneUtc, catalog, nominal);

                var clock = Stopwatch.StartNew();
                var v1 = VirtualAstrometryFixture.Measure(input, nominal);
                var v1Ms = clock.Elapsed.TotalMilliseconds;
                var v1Solved = VirtualAstrometryFixture.Solve(input, nominal, catalog, v1);
                clock.Restart();
                var v2 = VirtualAstrometryFixture.MeasureV2(input, nominal);
                var v2Ms = clock.Elapsed.TotalMilliseconds;
                var v2Solved = VirtualAstrometryFixture.SolveV2(input, nominal, catalog, v2);
                var diagnostics = VirtualAstrometryFixture.Diagnose(nominal, catalog, v2, v2Solved);

                var baseline = Score(truth, v1.Detections, v1Solved);
                var candidate = Score(truth, v2.Detections, v2Solved);
                var missed = MissedReasons(truth, v2);
                if (!v2Solved.Assessment.HasMeasuredMapping) failures.Add($"{caseId}: v2 {v2Solved.Assessment.Reason}");
                if (candidate.FalseAssociations > 0) failures.Add($"{caseId}: v2 {candidate.FalseAssociations} false associations");
                if (candidate.CentroidRmsPixels > MaximumCentroidRmsPixels ||
                    candidate.CentroidRmsPixels > baseline.CentroidRmsPixels + MaximumCentroidRmsRegressionPixels)
                    failures.Add($"{caseId}: v2 centroid RMS {candidate.CentroidRmsPixels:F3} vs v1 {baseline.CentroidRmsPixels:F3}");
                if (candidate.Recall < baseline.Recall - MaximumRecallRegression)
                    failures.Add($"{caseId}: v2 recall {candidate.Recall:F3} vs v1 {baseline.Recall:F3}");
                if (v2Ms > MaximumMeasurementMilliseconds || v2Ms > v1Ms * MaximumMeasurementTimeRatio)
                    failures.Add($"{caseId}: v2 measurement {v2Ms:F0} ms vs v1 {v1Ms:F0} ms");
                if (diagnostics.Residuals.Count != v2Solved.Associations.Count)
                    failures.Add($"{caseId}: diagnostics do not reproduce every association");
                TestContext.WriteLine($"{caseId}: v1 recall {baseline.Recall:F3} rms {baseline.CentroidRmsPixels:F3} false {baseline.FalseAssociations} " +
                    $"{v1Ms:F0} ms | v2 recall {candidate.Recall:F3} rms {candidate.CentroidRmsPixels:F3} false {candidate.FalseAssociations} {v2Ms:F0} ms; " +
                    $"{v2Solved.Assessment.Status} {v2Solved.Associations.Count} associations, {v2.Exclusions.Count} exclusions; " +
                    $"measurement {v2.Status} {v2.CandidateCount} candidates, background {v2.MedianBackground:F1} noise {v2.MedianNoiseSigma:F2}; " +
                    $"missed {string.Join(", ", missed.Select(m => $"{m.Key}={m.Value}"))}");
                reports.Add(new
                {
                    caseId,
                    profile.Name,
                    utc,
                    sceneUtc,
                    input.ParentPayloadSha256,
                    eligibleTruthStars = truth.Count(t => t.Eligible),
                    v1 = new
                    {
                        algorithm = StellarDetector.AlgorithmVersion,
                        measurementMs = v1Ms,
                        detected = v1.Detections.Count,
                        v1.CandidateCount,
                        baseline,
                        v1Solved.Assessment.Status,
                        v1Solved.Assessment.ReasonCode,
                        associations = v1Solved.Associations.Count
                    },
                    v2 = new
                    {
                        algorithm = StellarSourceMeasurer.AlgorithmVersion,
                        settingsIdentitySha256 = StellarSourceMeasurer.SettingsIdentity(VirtualAstrometryFixture.MeasurementOptions(input.Layout)),
                        measurementMs = v2Ms,
                        measurementStatus = v2.Status.ToString(),
                        detected = v2.Detections.Count,
                        v2.CandidateCount,
                        v2.ExclusionCounts,
                        missedEligibleReasonCounts = missed,
                        v2.MedianBackground,
                        v2.MedianNoiseSigma,
                        candidate,
                        v2Solved.Assessment.Status,
                        v2Solved.Assessment.ReasonCode,
                        associations = v2Solved.Associations.Count
                    },
                    diagnostics = new
                    {
                        diagnostics.IdentitySha256,
                        diagnostics.FittingRmsPixels,
                        diagnostics.VerificationRmsPixels,
                        diagnostics.MedianNormalizedResidualSquared,
                        diagnostics.CovarianceStatus,
                        diagnostics.Conditioning,
                        diagnostics.Occupancy,
                        diagnostics.ParameterBoundHits,
                        diagnostics.UnmatchedPredictionReasonCounts,
                        diagnostics.UnassociatedDetectionReasonCounts,
                        bins = diagnostics.Bins
                    }
                });
            }
        }
        var root = Path.Combine(TestContext.TestRunDirectory!, partition); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "virtual-measured-stars.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-measured-stars-v1",
            partition,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            snapshot.DatabaseSha256,
            catalog.IdentitySha256,
            catalog.SelectionIdentitySha256,
            tolerances = new
            {
                MaximumCentroidRmsPixels,
                MaximumCentroidRmsRegressionPixels,
                MaximumRecallRegression,
                MaximumMeasurementMilliseconds,
                MaximumMeasurementTimeRatio,
                falseAssociations = 0
            },
            reports,
            failures
        }, EvidenceJsonOptions)).ConfigureAwait(false);
        TestContext.WriteLine(path);
        TestContext.AddResultFile(path);
        Assert.AreEqual(partitions.Length * 7, reports.Count);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private sealed record TruthStar(string Id, PixelPoint Pixel, bool Eligible);

    internal sealed record MeasuredStarScore(int EligibleStars, int Recovered, double Recall, int Associations,
        int FalseAssociations, double CentroidRmsPixels, double CentroidP95Pixels);

    // Eligible truth stars use the solver's final geometry: interior, 6 px from the readout border, isolated by 12 px.
    private static TruthStar[] Truth(CameraRigConfig rig, DateTimeOffset utc, AstrometricCatalogData catalog, AstrometricCalibration nominal)
    {
        var visible = catalog.Stars.Select(star => (star.Id, Ray: VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.J2000(star), utc, VirtualAstrometryFixture.Observer)))
            .Where(item => item.Ray.Z > 0).Select(item => (item.Id, Pixel: VirtualAstrometryReference.Project(rig, item.Ray)))
            .Where(item => item.Pixel is not null).Select(item => (item.Id, Pixel: item.Pixel!.Value)).ToArray();
        var cx = (rig.Optics.PrincipalPointX!.Value - rig.Readout!.Roi.X) / rig.Readout.BinX;
        var cy = (rig.Optics.PrincipalPointY!.Value - rig.Readout.Roi.Y) / rig.Readout.BinY;
        var interior = rig.Optics.ImageCircleRadiusPixels!.Value / rig.Readout.BinX - 12;
        var width = nominal.Projection.WidthPixels; var height = nominal.Projection.HeightPixels;
        return [.. visible.Select(v => new TruthStar(v.Id, v.Pixel,
            v.Pixel.X > 6 && v.Pixel.Y > 6 && v.Pixel.X < width - 6 && v.Pixel.Y < height - 6 &&
            Math.Pow(v.Pixel.X - cx, 2) + Math.Pow(v.Pixel.Y - cy, 2) < interior * interior &&
            visible.All(o => o.Id == v.Id || VirtualAstrometryReference.Distance(o.Pixel, v.Pixel) > 12)))];
    }

    // Why each unrecovered eligible star was lost: the nearest reason-coded exclusion within 2 px, else no candidate.
    private static SortedDictionary<string, int> MissedReasons(TruthStar[] truth, StellarMeasurementResult measured)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var star in truth.Where(t => t.Eligible && !measured.Detections.Any(d => VirtualAstrometryReference.Distance(d.Pixel, t.Pixel) < 1.5)))
        {
            var reason = measured.Exclusions.Where(e => VirtualAstrometryReference.Distance(e.Peak, star.Pixel) < 2)
                .MinBy(e => VirtualAstrometryReference.Distance(e.Peak, star.Pixel))?.ReasonCode ?? "no-candidate";
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
        }
        return counts;
    }

    private static MeasuredStarScore Score(TruthStar[] truth, IReadOnlyList<StellarDetection> detections, AstrometricSolveResult solved)
    {
        var eligible = truth.Where(t => t.Eligible).ToArray();
        var recovered = eligible.Count(t => detections.Any(d => VirtualAstrometryReference.Distance(d.Pixel, t.Pixel) < 1.5));
        var byIndex = detections.ToDictionary(d => d.Index);
        var byId = truth.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var errors = new List<double>(); var falseAssociations = 0;
        foreach (var association in solved.Associations)
        {
            var pixel = byIndex[association.DetectionIndex].Pixel;
            var nearest = truth.MinBy(t => VirtualAstrometryReference.Distance(t.Pixel, pixel))!;
            if (nearest.Id != association.CatalogId || !byId.ContainsKey(association.CatalogId)) { falseAssociations++; continue; }
            errors.Add(VirtualAstrometryReference.Distance(pixel, nearest.Pixel));
        }
        var sorted = errors.Order().ToArray();
        return new(eligible.Length, recovered, eligible.Length == 0 ? 0 : recovered / (double)eligible.Length, solved.Associations.Count,
            falseAssociations, sorted.Length == 0 ? double.NaN : Math.Sqrt(sorted.Sum(e => e * e) / sorted.Length),
            sorted.Length == 0 ? double.NaN : sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1]);
    }
}
