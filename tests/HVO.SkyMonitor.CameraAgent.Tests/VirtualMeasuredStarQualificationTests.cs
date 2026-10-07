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

    // Declared on #1168 before evidence. Trail metadata gates only its own envelope row, never solving or recall.
    private const double MinimumScoredTrailPixels = 3;
    private const double MaximumTrailLengthErrorPixels = .5;
    private const double MaximumTrailLengthRelativeError = .15;
    private const double MaximumTrailAngleErrorDegrees = 10;

    // Declared on #1103 from the tuning partition after review R4, before the corrected held-out run.
    private const double MaximumMeasurementAllocatedBytesPerPixel = 64;
    private const long MaximumPeakWorkingSetBytes = 2L << 30;
    private const int WindowPressureStreakSamples = 600;

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
        // Tiered recompilation and PGO otherwise change the v1/v2 time ratio between cases and runs.
        Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), "The declared measurement disables tiered compilation.");
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        Assert.HasCount(1637, catalog.Stars); Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        if (!tuning) VirtualAstrometryQualificationTests.RequireRevision();
        Assert.AreEqual(VirtualAstrometryFixture.Variants[0].Name, VirtualAstrometryFixture.Variant.Name, "Measured-star evidence renders only the unvaried scene.");
        var exposureSeconds = VirtualAstrometryFixture.ExposureSeconds; var exposure = VirtualAstrometryFixture.Exposure;
        // The v1 detector is the comparison baseline only where it is a supported measurer: untrailed 1 s frames.
        var comparesV1 = exposureSeconds == 1;
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
                var truth = Truth(profile.Config.Rig, sceneUtc, exposure, catalog, nominal);
                var legacy = LegacyTruth(profile.Config.Rig, truth, nominal);

                var (v1, v1Resources) = Sample(() => VirtualAstrometryFixture.Measure(input, nominal));
                var v1Ms = v1Resources.WallMs;
                var v1Solved = VirtualAstrometryFixture.Solve(input, nominal, catalog, v1);
                var (v2, v2Resources) = Sample(() => VirtualAstrometryFixture.MeasureV2(input, nominal));
                var v2Ms = v2Resources.WallMs;
                var v2Solved = VirtualAstrometryFixture.SolveV2(input, nominal, catalog, v2);
                var diagnostics = VirtualAstrometryFixture.Diagnose(nominal, catalog, v2, v2Solved);

                var baseline = Score(truth, v1.Detections, v1Solved);
                var candidate = Score(truth, v2.Detections, v2Solved);
                var missed = MissedReasons(truth, v2);
                var trails = TrailMetadata(truth, v2);
                var legacyBaseline = LegacyScore(legacy, v1.Detections, v1Solved);
                var legacyCandidate = LegacyScore(legacy, v2.Detections, v2Solved);
                var legacyMissed = LegacyMissedReasons(legacy, v2);
                var width = nominal.Projection.WidthPixels; var height = nominal.Projection.HeightPixels;
                failures.AddRange(InvariantFailures(caseId, Reclassification(profile.Config.Rig, sceneUtc, exposure, truth, legacy,
                    [("v1", v1.Detections), ("v2", v2.Detections)]), (pixel, widen) => PointInterior(profile.Config.Rig, width, height, pixel, widen)));
                if (!v2Solved.Assessment.HasMeasuredMapping) failures.Add($"{caseId}: v2 {v2Solved.Assessment.Reason}");
                if (candidate.FalseAssociations > 0) failures.Add($"{caseId}: v2 {candidate.FalseAssociations} false associations");
                if (candidate.CentroidRmsPixels > MaximumCentroidRmsPixels ||
                    comparesV1 && candidate.CentroidRmsPixels > baseline.CentroidRmsPixels + MaximumCentroidRmsRegressionPixels)
                    failures.Add($"{caseId}: v2 centroid RMS {candidate.CentroidRmsPixels:F3} vs v1 {baseline.CentroidRmsPixels:F3}");
                if (comparesV1 && candidate.Recall < baseline.Recall - MaximumRecallRegression)
                    failures.Add($"{caseId}: v2 recall {candidate.Recall:F3} vs v1 {baseline.Recall:F3}");
                if (v2Ms > MaximumMeasurementMilliseconds || comparesV1 && v2Ms > v1Ms * MaximumMeasurementTimeRatio)
                    failures.Add($"{caseId}: v2 measurement {v2Ms:F0} ms vs v1 {v1Ms:F0} ms");
                var bytesPerPixel = v2Resources.AllocatedBytes / (double)(input.Layout.Width * input.Layout.Height);
                if (bytesPerPixel > MaximumMeasurementAllocatedBytesPerPixel)
                    failures.Add($"{caseId}: v2 allocated {bytesPerPixel:F1} B/pixel");
                if (v2Resources.PeakWorkingSetBytes > MaximumPeakWorkingSetBytes)
                    failures.Add($"{caseId}: peak working set {v2Resources.PeakWorkingSetBytes / 1048576.0:F0} MiB");
                if (diagnostics.Residuals.Count != v2Solved.Associations.Count)
                    failures.Add($"{caseId}: diagnostics do not reproduce every association");
                TestContext.WriteLine($"{caseId}: v1 recall {baseline.Recall:F3} rms {baseline.CentroidRmsPixels:F3} false {baseline.FalseAssociations} " +
                    $"{v1Ms:F0} ms | v2 recall {candidate.Recall:F3} rms {candidate.CentroidRmsPixels:F3} false {candidate.FalseAssociations} {v2Ms:F0} ms " +
                    $"cpu {v2Resources.CpuMs:F0} ms alloc {bytesPerPixel:F1} B/px peak {v2Resources.PeakWorkingSetBytes / 1048576.0:F0} MiB; " +
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
                    truncatedTruthTrails = truth.Count(t => t.Trail.Truncated),
                    trailMetadata = trails,
                    // The #1126 point-rule scores, so continuity can require them to reproduce the base exactly.
                    legacy = new
                    {
                        eligibleTruthStars = legacy.Count(t => t.Eligible),
                        v1 = new { baseline = legacyBaseline },
                        v2 = new { candidate = legacyCandidate, missedEligibleReasonCounts = legacyMissed }
                    },
                    v1 = new
                    {
                        algorithm = StellarDetector.AlgorithmVersion,
                        measurementMs = v1Ms,
                        resources = v1Resources,
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
                        resources = v2Resources,
                        allocatedBytesPerPixel = bytesPerPixel,
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
        var pressure = WindowPressure(VirtualAstrometryFixture.Profiles(partitions[0].Seed)[0]);
        if (pressure.ExtendedRegionExclusions < 1 || pressure.Detections > 0)
            failures.Add($"window pressure: {pressure.ExtendedRegionExclusions} extended-region exclusions, {pressure.Detections} detections");
        if (pressure.Resources.WallMs > MaximumMeasurementMilliseconds)
            failures.Add($"window pressure: {pressure.Resources.WallMs:F0} ms");
        if (pressure.AllocatedBytesPerPixel > MaximumMeasurementAllocatedBytesPerPixel)
            failures.Add($"window pressure: allocated {pressure.AllocatedBytesPerPixel:F1} B/pixel");
        TestContext.WriteLine($"window pressure: {pressure.StreakSamples}-sample diagonal, {pressure.Resources.WallMs:F0} ms " +
            $"(streak-free {pressure.StreakFreeBaseline.WallMs:F0} ms) cpu {pressure.Resources.CpuMs:F0} ms alloc {pressure.AllocatedBytesPerPixel:F1} B/px; " +
            $"{pressure.ExtendedRegionExclusions} extended-region");
        var root = Path.Combine(TestContext.TestRunDirectory!, partition); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "virtual-measured-stars.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-measured-stars-v2",
            partition,
            projectionFamily = VirtualAstrometryFixture.Family.Name,
            exposureSeconds,
            v1ComparisonGated = comparesV1,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
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
                MaximumMeasurementAllocatedBytesPerPixel,
                MaximumPeakWorkingSetBytes,
                falseAssociations = 0,
                trailMetadata = new { MinimumScoredTrailPixels, MaximumTrailLengthErrorPixels, MaximumTrailLengthRelativeError, MaximumTrailAngleErrorDegrees }
            },
            reports,
            windowPressure = pressure,
            failures
        }, EvidenceJsonOptions)).ConfigureAwait(false);
        TestContext.WriteLine(path);
        TestContext.AddResultFile(path);
        Assert.AreEqual(partitions.Length * VirtualAstrometryFixture.Profiles(0).Count, reports.Count);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private sealed record TruthStar(string Id, PixelPoint Pixel, bool Eligible, VirtualAstrometryReference.TruthTrail Trail);

    // Wall and process CPU time, exact single-thread allocation, and the process's lifetime peak working set.
    // Process CPU time is only attributable when the harness runs alone, as the evidence commands require.
    internal sealed record ResourceSample(double WallMs, double CpuMs, long AllocatedBytes, long PeakWorkingSetBytes);

    internal sealed record WindowPressureResult(int StreakSamples, int Detections, int ExtendedRegionExclusions,
        double AllocatedBytesPerPixel, ResourceSample Resources, ResourceSample StreakFreeBaseline);

    private static (T Value, ResourceSample Resources) Sample<T>(Func<T> measure)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var value = measure();
        var wall = clock.Elapsed.TotalMilliseconds;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        process.Refresh();
        return (value, new(wall, (process.TotalProcessorTime - cpu).TotalMilliseconds, allocatedBytes, process.PeakWorkingSet64));
    }

    // A sparse one-sample-wide diagonal streak: few component samples but a window spanning the streak's bounding box.
    private static WindowPressureResult WindowPressure(VirtualAstrometryProfile profile)
    {
        var width = profile.Config.Rig.Readout!.Roi.Width / profile.Config.Rig.Readout.BinX;
        var height = profile.Config.Rig.Readout.Roi.Height / profile.Config.Rig.Readout.BinY;
        var pixels = new double[width * height];
        Array.Fill(pixels, 100);
        var random = new Random(110399);
        for (var index = 0; index < pixels.Length; index++) pixels[index] += Noise(random, 3);
        var valid = new bool[pixels.Length];
        Array.Fill(valid, true);
        var options = VirtualAstrometryFixture.MeasurementOptions(new FrameLayoutDescriptor(width, height, width * 2, CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, null, null, width * height * 2L));
        var (_, baseline) = Sample(() => StellarSourceMeasurer.Measure(pixels, valid, [], width, height, options));
        int x0 = (width - WindowPressureStreakSamples) / 2, y0 = (height - WindowPressureStreakSamples) / 2;
        for (var step = 0; step < WindowPressureStreakSamples; step++) pixels[(y0 + step) * width + x0 + step] += 5000;
        var (result, resources) = Sample(() => StellarSourceMeasurer.Measure(pixels, valid, [], width, height, options));
        return new(WindowPressureStreakSamples, result.Detections.Count, result.ExclusionCounts.GetValueOrDefault(StellarExclusionReasons.ExtendedRegion),
            resources.AllocatedBytes / (double)pixels.Length, resources, baseline);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded, reproducible synthetic noise.")]
    private static double Noise(Random random, double sigma) =>
        sigma * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    internal sealed record MeasuredStarScore(int EligibleStars, int Recovered, double Recall, int Associations,
        int FalseAssociations, double CentroidRmsPixels, double CentroidP95Pixels);

    // Eligible truth stars use the solver's final geometry: a whole trail, every sample interior and 6 px from the readout
    // border, isolated by 12 px from every other visible trail. The scored position is the mid-exposure sample.
    private static TruthStar[] Truth(CameraRigConfig rig, DateTimeOffset utc, TimeSpan exposure, AstrometricCatalogData catalog, AstrometricCalibration nominal)
    {
        var visible = VirtualAstrometryReference.Trails(rig, utc, exposure, catalog.Stars).Where(trail => trail.Mid is not null).ToArray();
        var width = nominal.Projection.WidthPixels; var height = nominal.Projection.HeightPixels;
        bool Interior(PixelPoint pixel) => pixel.X > 6 && pixel.Y > 6 && pixel.X < width - 6 && pixel.Y < height - 6 &&
            VirtualAstrometryReference.IsApertureInterior(rig, pixel, 12);
        return [.. visible.Select(v => new TruthStar(v.Star.Id, v.Mid!.Value,
            !v.Truncated && v.Samples.All(sample => Interior(sample!.Value)) &&
            visible.All(o => ReferenceEquals(o, v) || o.BoundsGapTo(v) > 12 || v.Samples.All(sample => o.DistanceTo(sample!.Value) > 12)), v))];
    }

    // The #1126 point rule, verbatim from base 0639e27d, over the same visible stars in the same order: each star's
    // mid-exposure sample is the base's scene-time projection. Only continuity and the invariants read it.
    private static TruthStar[] LegacyTruth(CameraRigConfig rig, TruthStar[] truth, AstrometricCalibration nominal)
    {
        var visible = truth.Select(t => (t.Id, t.Pixel, t.Trail)).ToArray();
        var width = nominal.Projection.WidthPixels; var height = nominal.Projection.HeightPixels;
        return [.. visible.Select(v => new TruthStar(v.Id, v.Pixel,
            v.Pixel.X > 6 && v.Pixel.Y > 6 && v.Pixel.X < width - 6 && v.Pixel.Y < height - 6 &&
            VirtualAstrometryReference.IsApertureInterior(rig, v.Pixel, 12) &&
            visible.All(o => o.Id == v.Id || VirtualAstrometryReference.Distance(o.Pixel, v.Pixel) > 12), v.Trail))];
    }

    private static SortedDictionary<string, int> LegacyMissedReasons(TruthStar[] truth, StellarMeasurementResult measured)
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

    private static MeasuredStarScore LegacyScore(TruthStar[] truth, IReadOnlyList<StellarDetection> detections, AstrometricSolveResult solved)
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

    /// <summary>
    /// One visible star's eligibility under both rules, with the geometry that can explain a difference. The reach is the
    /// largest distance from the mid-exposure pixel to any sample above the horizon, projected without image-circle or
    /// readout clipping; a sample above the horizon outside the projection's supported domain sets OutsideDomain instead.
    /// </summary>
    internal sealed record ReclassificationStar(string Id, PixelPoint Mid, double ReachPixels, bool BelowHorizon, bool OutsideDomain,
        bool LegacyEligible, bool TrailEligible, (string Measurer, bool Legacy, bool Trail)[] Recovered);

    // Rounding allowance for the invariant 3 bounds, far below any pixel quantity the rules compare.
    internal const double InvariantEpsilonPixels = 1e-9;

    private static ReclassificationStar[] Reclassification(CameraRigConfig rig, DateTimeOffset sceneUtc, TimeSpan exposure, TruthStar[] truth,
        TruthStar[] legacy, (string Measurer, IReadOnlyList<StellarDetection> Detections)[] measured) =>
        [.. truth.Select((t, i) =>
        {
            if (legacy[i].Id != t.Id) throw new InvalidOperationException($"legacy truth order differs at {i}");
            var rays = Enumerable.Range(0, VirtualAstrometryReference.TrailSamples).Select(k => VirtualAstrometryReference.ToEnu(
                VirtualAstrometryReference.J2000(t.Trail.Star), VirtualAstrometryReference.SampleUtc(sceneUtc, exposure, k), VirtualAstrometryFixture.Observer)).ToArray();
            var unclipped = rays.Where(ray => ray.Z > 0).Select(ray => VirtualAstrometryReference.Project(rig, ray, clip: false)).ToArray();
            var reach = unclipped.Where(p => p is not null).Select(p => VirtualAstrometryReference.Distance(p!.Value, t.Pixel)).DefaultIfEmpty(0).Max();
            return new ReclassificationStar(t.Id, t.Pixel, reach, rays.Any(ray => ray.Z <= 0), unclipped.Any(p => p is null), legacy[i].Eligible, t.Eligible,
                [.. measured.Select(m => (m.Measurer, m.Detections.Any(d => VirtualAstrometryReference.Distance(d.Pixel, t.Pixel) < 1.5),
                    m.Detections.Any(d => t.Trail.DistanceTo(d.Pixel) < 1.5)))]);
        })];

    // The #1126 point rule's interior test with both margins widened by widen px: 6 from the nominal frame border, 12 from a circular aperture.
    private static bool PointInterior(CameraRigConfig rig, double width, double height, PixelPoint pixel, double widen) =>
        pixel.X > 6 + widen && pixel.Y > 6 + widen && pixel.X < width - 6 - widen && pixel.Y < height - 6 - widen &&
        VirtualAstrometryReference.IsApertureInterior(rig, pixel, 12 + widen);

    /// <summary>
    /// The three declared trail-eligibility invariants, checked at every exposure:
    /// 1. every trail-eligible star is legacy-eligible;
    /// 2. a trail-eligible star that a measurer recovers at its mid-exposure point is recovered on its trail;
    /// 3. a star that is legacy-eligible but not trail-eligible, with r its reach plus <see cref="InvariantEpsilonPixels"/>, fails
    ///    the point rule's interior test widened by r, or has another visible star whose mid-exposure pixel is within
    ///    12 + r + that star's reach, or has a sample below the horizon, or a sample outside the projection's supported domain.
    /// </summary>
    internal static List<string> InvariantFailures(string caseId, IReadOnlyList<ReclassificationStar> stars, Func<PixelPoint, double, bool> pointInterior)
    {
        var failures = new List<string>();
        foreach (var star in stars)
        {
            if (star.TrailEligible && !star.LegacyEligible)
                failures.Add($"{caseId}: invariant 1: {star.Id} is trail-eligible but not legacy-eligible");
            if (star.TrailEligible)
                foreach (var (measurer, _, _) in star.Recovered.Where(r => r.Legacy && !r.Trail))
                    failures.Add($"{caseId}: invariant 2: {measurer} recovers {star.Id} at its mid-exposure point but not on its trail");
            if (!star.LegacyEligible || star.TrailEligible) continue;
            var reach = star.ReachPixels + InvariantEpsilonPixels;
            if (!pointInterior(star.Mid, reach) || star.BelowHorizon || star.OutsideDomain ||
                stars.Any(o => !ReferenceEquals(o, star) && VirtualAstrometryReference.Distance(o.Mid, star.Mid) <= 12 + reach + o.ReachPixels)) continue;
            failures.Add($"{caseId}: invariant 3: {star.Id} lost eligibility with no geometric cause within its {star.ReachPixels:F3} px reach");
        }
        return failures;
    }

    // Why each unrecovered eligible star was lost: the nearest reason-coded exclusion within 2 px of its trail, else no candidate.
    private static SortedDictionary<string, int> MissedReasons(TruthStar[] truth, StellarMeasurementResult measured)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var star in truth.Where(t => t.Eligible && !measured.Detections.Any(d => t.Trail.DistanceTo(d.Pixel) < 1.5)))
        {
            var reason = measured.Exclusions.Where(e => star.Trail.DistanceTo(e.Peak) < 2)
                .MinBy(e => star.Trail.DistanceTo(e.Peak))?.ReasonCode ?? "no-candidate";
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
        }
        return counts;
    }

    /// <summary>
    /// Trail metadata for eligible truth trails of at least <see cref="MinimumScoredTrailPixels"/>: the nearest recovered
    /// detection's reported length and angle. A recovered long trail reported without trail metadata is counted, not scored.
    /// </summary>
    private static object TrailMetadata(TruthStar[] truth, StellarMeasurementResult measured)
    {
        var lengthErrors = new List<double>(); var angleErrors = new List<double>(); var unreported = 0; var unrecovered = 0;
        var scored = truth.Where(t => t.Eligible && t.Trail.LengthPixels >= MinimumScoredTrailPixels).ToArray();
        foreach (var star in scored)
        {
            var detection = measured.Detections.Where(d => star.Trail.DistanceTo(d.Pixel) < 1.5).MinBy(d => star.Trail.DistanceTo(d.Pixel));
            if (detection is null) { unrecovered++; continue; }
            if (detection.TrailLengthPixels is not { } length || detection.TrailAngleDegrees is not { } angle) { unreported++; continue; }
            var truthLength = star.Trail.LengthPixels;
            lengthErrors.Add(Math.Abs(length - truthLength) / Math.Max(MaximumTrailLengthErrorPixels, MaximumTrailLengthRelativeError * truthLength));
            var difference = Math.Abs(angle - star.Trail.AngleDegrees!.Value) % 180;
            angleErrors.Add(Math.Min(difference, 180 - difference));
        }
        var length95 = P95(lengthErrors); var angle95 = P95(angleErrors);
        return new
        {
            scoredTrails = scored.Length,
            reported = lengthErrors.Count,
            unreported,
            unrecovered,
            normalizedLengthErrorP95 = length95,
            angleErrorP95Degrees = angle95,
            status = scored.Length == 0 ? "not-applicable"
                : unreported == 0 && length95 <= 1 && angle95 <= MaximumTrailAngleErrorDegrees ? "met" : "not-met"
        };
    }

    private static double? P95(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1];
    }

    private static MeasuredStarScore Score(TruthStar[] truth, IReadOnlyList<StellarDetection> detections, AstrometricSolveResult solved)
    {
        var eligible = truth.Where(t => t.Eligible).ToArray();
        var recovered = eligible.Count(t => detections.Any(d => t.Trail.DistanceTo(d.Pixel) < 1.5));
        var byIndex = detections.ToDictionary(d => d.Index);
        var byTrail = truth.ToDictionary(t => t.Trail);
        var errors = new List<double>(); var falseAssociations = 0;
        foreach (var association in solved.Associations)
        {
            var pixel = byIndex[association.DetectionIndex].Pixel;
            var nearest = byTrail[VirtualAstrometryReference.Nearest(byTrail.Keys, pixel)!];
            if (nearest.Id != association.CatalogId) { falseAssociations++; continue; }
            // A truncated trail has no whole-exposure centroid to score against; its association still counts for identity.
            if (!nearest.Trail.Truncated) errors.Add(VirtualAstrometryReference.Distance(pixel, nearest.Pixel));
        }
        var sorted = errors.Order().ToArray();
        return new(eligible.Length, recovered, eligible.Length == 0 ? 0 : recovered / (double)eligible.Length, solved.Associations.Count,
            falseAssociations, sorted.Length == 0 ? double.NaN : Math.Sqrt(sorted.Sum(e => e * e) / sorted.Length),
            sorted.Length == 0 ? double.NaN : sorted[(int)Math.Ceiling(.95 * sorted.Length) - 1]);
    }
}
