using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;
using static HVO.SkyMonitor.CameraAgent.Tests.VirtualAstrometryReference;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Explicit HYG evidence that published astrometric uncertainty covers the truth on actual VirtualSky frames. Only final
/// pixels, the measured centroid covariance and the nominal (or session-calibrated) rig reach the estimator. Truth is the
/// rendering rig, carried to the exposure midpoint and compared through the independent reference equations: the pose error
/// is the small-angle ENU rotation vector between reference bases, never the production rotation code.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualAstrometricUncertaintyQualificationTests
{
    // Predeclared on #1105 before held-out evidence; tuning results may not change them.
    private const double CoverageToleranceSigma = 3.5;
    private static readonly double[] Levels = [.6827, .95, .99];
    private static readonly double[] Ellipsoid = [4.7198, 9.4877, 13.2767];
    private static readonly double[] Interval = [1.0, 1.95996, 2.57583];
    // Upper 0.1% point of chi-square with four degrees of freedom: one session chain's total ellipsoid must contain its truth.
    private const double SessionEllipsoidLimit = 18.4668;
    // An accepted frame may be withheld only at the declared model-validity alpha, with this binomial allowance.
    private const double MaximumFalseWithholdFraction = .02;
    private const double MaximumEstimatorMilliseconds = 250;
    private const long MaximumEstimatorAllocatedBytes = 32L << 20;
    // The estimator may cost at most this fraction of the frame's own measurement and warm solve.
    private const double MaximumEstimatorToFrameRatio = .5;
    // Declared per-star systematic floor (readout pixels, per axis) by pixel format and binning. Set from the tuning partition
    // only: the #1103 centroid covariance omits pixel-phase sampling and interpolation error, so real VirtualSky centroids need a
    // floor of 0.037-0.045 px (mono), 0.024-0.031 px (mono 2x2) and 0.062-0.065 px (CFA) for chi-square per degree to reach one.
    // Each declared value is the tuning median, rounded to 0.001 px. A margin above it was tried and rejected on tuning: it
    // inflates the covariance and over-covers, which the two-sided coverage test fails as surely as under-coverage. The
    // held-out partition must pass with these unchanged. Floors are keyed by exposure seconds, then view: the #1105 values
    // are the 1 s floors, and each longer exposure's floors are declared by the same rule from its own #1168 tuning partition.
    private static readonly Dictionary<int, Dictionary<string, double>> DeclaredFloorPixels = new()
    {
        [1] = new(StringComparer.Ordinal)
        {
            ["mono-native"] = .042,
            ["mono-roi"] = .042,
            ["mono-mirror"] = .042,
            ["mono-roll"] = .042,
            ["mono-bin2"] = .028,
            ["mono-roi-bin2"] = .028,
            ["cfa-native"] = .063
        },
        [20] = new(StringComparer.Ordinal)
        {
            ["mono-native"] = .015,
            ["mono-roi"] = .015,
            ["mono-mirror"] = .015,
            ["mono-roll"] = .015,
            ["mono-bin2"] = .019,
            ["mono-roi-bin2"] = .019,
            ["cfa-native"] = .044
        },
        [60] = new(StringComparer.Ordinal)
        {
            ["mono-native"] = .022,
            ["mono-roi"] = .022,
            ["mono-mirror"] = .022,
            ["mono-roll"] = .022,
            ["mono-bin2"] = .016,
            ["mono-roi-bin2"] = .016,
            ["cfa-native"] = .038
        }
    };

    private static AstrometricUncertaintyOptions Budget(IReadOnlyDictionary<string, double> floors, string profile) =>
        new(SystematicPixelSigma: floors[profile]);

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    private sealed record Realization(string Profile, int Index, string Status, string ReasonCode, double[]? Error, double[]? Covariance,
        double? Mahalanobis, double? ChiSquare, int DegreesOfFreedom, double? HeldOutChiSquare, int HeldOutDegreesOfFreedom, double? RequiredFloorPixels,
        VirtualMeasuredStarQualificationTests.ResourceSample Frame, VirtualMeasuredStarQualificationTests.ResourceSample Estimator, string? IdentitySha256);

    [TestMethod]
    public Task TuningAstrometricUncertainty() => RunAsync(tuning: true);

    [TestMethod]
    public Task HeldOutAstrometricUncertainty() => RunAsync(tuning: false);

    private async Task RunAsync(bool tuning)
    {
        Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), "The declared measurement disables tiered compilation.");
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        Assert.HasCount(1637, catalog.Stars); Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        if (!tuning) VirtualAstrometryQualificationTests.RequireRevision();
        Assert.AreEqual(VirtualAstrometryFixture.Variants[0].Name, VirtualAstrometryFixture.Variant.Name, "Uncertainty evidence renders only the unvaried scene.");
        var exposureSeconds = VirtualAstrometryFixture.ExposureSeconds;
        // Tuning an undeclared exposure estimates at the 1 s floors only so that it can report each frame's required floor.
        var declared = DeclaredFloorPixels.TryGetValue(exposureSeconds, out var floors);
        Assert.IsTrue(declared || tuning, $"Held-out uncertainty evidence requires floors declared from tuning at {exposureSeconds} s.");
        floors ??= DeclaredFloorPixels[1];
        var floorSource = declared ? $"declared-{exposureSeconds}s" : "provisional-1s-tuning-only";
        var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110520) }
            : [(Month: 1, Day: 15, Seed: 110501), (Month: 5, Day: 15, Seed: 110505), (Month: 9, Day: 15, Seed: 110509)];
        var realizations = tuning ? 8 : 20;
        var partition = tuning ? "uncertainty-tuning-not-final" : "uncertainty-held-out";
        var failures = new List<string>(); var frames = new List<Realization>(); var sessions = new List<object>();
        foreach (var (month, day, seed) in partitions)
        {
            foreach (var name in VirtualAstrometryFixture.Profiles(seed).Select(p => p.Name))
                frames.AddRange(await ProfileAsync(snapshot, catalog, floors, month, day, seed, name, realizations, failures).ConfigureAwait(false));
            sessions.Add(await SessionChainAsync(snapshot, catalog, floors, month, day, seed, failures).ConfigureAwait(false));
        }

        // Coverage is judged per profile and pooled; an unsupported or withheld frame is a failure, never an exclusion.
        var coverage = new List<object>();
        foreach (var group in frames.GroupBy(f => f.Profile).Append(frames.GroupBy(_ => "pooled").Single()))
        {
            var label = group.Key; var all = group.ToArray();
            var available = all.Where(f => f.Status == AstrometricUncertaintyEstimator.Available).ToArray();
            var withheld = all.Length - available.Length;
            if (withheld > MaximumFalseWithholdFraction * all.Length + 1)
                failures.Add($"{label}: {withheld} of {all.Length} accepted frames withheld: {string.Join(",", all.Where(f => f.Status != AstrometricUncertaintyEstimator.Available).Select(f => f.ReasonCode).Distinct())}");
            var rows = new List<object>();
            for (var level = 0; level < 3; level++)
            {
                var n = available.Length; if (n == 0) break;
                var bound = CoverageToleranceSigma * Math.Sqrt(Levels[level] * (1 - Levels[level]) / n);
                var ellipsoid = available.Count(f => f.Mahalanobis <= Ellipsoid[level]) / (double)n;
                var intervals = Enumerable.Range(0, 4).Select(p => available.Count(f => Math.Abs(f.Error![p]) <= Interval[level] * Math.Sqrt(f.Covariance![p * 5])) / (double)n).ToArray();
                if (Math.Abs(ellipsoid - Levels[level]) > bound) failures.Add($"{label}: ellipsoid coverage {ellipsoid:F3} at {Levels[level]} (bound {bound:F3}, n {n})");
                for (var p = 0; p < 4; p++)
                    if (Math.Abs(intervals[p] - Levels[level]) > bound) failures.Add($"{label}: parameter {p} interval coverage {intervals[p]:F3} at {Levels[level]} (bound {bound:F3}, n {n})");
                rows.Add(new { level = Levels[level], n, bound, ellipsoid, intervals });
            }
            coverage.Add(new
            {
                profile = label,
                frames = all.Length,
                withheld,
                meanChiSquarePerDegree = available.Length == 0 ? (double?)null : available.Average(f => f.ChiSquare!.Value / f.DegreesOfFreedom),
                meanHeldOutChiSquarePerDegree = available.Where(f => f.HeldOutChiSquare is not null).Select(f => f.HeldOutChiSquare!.Value / f.HeldOutDegreesOfFreedom).DefaultIfEmpty(double.NaN).Average(),
                rows
            });
            TestContext.WriteLine($"{label}: {available.Length}/{all.Length} available; {JsonSerializer.Serialize(rows)}");
        }
        foreach (var f in frames)
        {
            var limit = Math.Min(MaximumEstimatorMilliseconds, MaximumEstimatorToFrameRatio * f.Frame.WallMs);
            if (f.Estimator.WallMs > limit) failures.Add($"{f.Profile}-{f.Index}: estimator {f.Estimator.WallMs:F1} ms exceeds {limit:F1} ms");
            if (f.Estimator.AllocatedBytes > MaximumEstimatorAllocatedBytes) failures.Add($"{f.Profile}-{f.Index}: estimator allocated {f.Estimator.AllocatedBytes} bytes");
        }

        var root = Path.Combine(TestContext.TestRunDirectory!, partition); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "virtual-astrometric-uncertainty.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-astrometric-uncertainty-v1",
            projectionFamily = VirtualAstrometryFixture.Family.Name,
            exposureSeconds,
            floorSource,
            partition,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            snapshot.DatabaseSha256,
            catalog.IdentitySha256,
            catalog.SelectionIdentitySha256,
            estimator = AstrometricUncertaintyOptions.EstimatorVersion,
            tolerances = new
            {
                CoverageToleranceSigma,
                Levels,
                Ellipsoid,
                Interval,
                SessionEllipsoidLimit,
                MaximumFalseWithholdFraction,
                MaximumEstimatorMilliseconds,
                MaximumEstimatorAllocatedBytes,
                MaximumEstimatorToFrameRatio,
                DeclaredFloorPixels = floors
            },
            realizationsPerProfile = realizations,
            coverage,
            frameResources = new
            {
                frameMedianMs = Median(frames.Select(f => f.Frame.WallMs)),
                estimatorMedianMs = Median(frames.Select(f => f.Estimator.WallMs)),
                estimatorMaximumMs = frames.Max(f => f.Estimator.WallMs),
                estimatorMedianAllocatedBytes = Median(frames.Select(f => (double)f.Estimator.AllocatedBytes)),
                estimatorMaximumAllocatedBytes = frames.Max(f => f.Estimator.AllocatedBytes),
                peakWorkingSetBytes = frames.Max(f => f.Estimator.PeakWorkingSetBytes)
            },
            frames,
            sessions,
            failures
        }, EvidenceJsonOptions)).ConfigureAwait(false);
        TestContext.WriteLine(path);
        TestContext.AddResultFile(path);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Independent noise realizations of one profile, a minute apart so pixel phase changes too: a blind solve at the
    /// nominal optics, then warm refinement, each frame measured with covariance and estimated at the declared systematic budget.
    /// </summary>
    private static async Task<List<Realization>> ProfileAsync(CatalogSnapshotResult snapshot, AstrometricCatalogData catalog,
        IReadOnlyDictionary<string, double> floors, int month, int day, int seed, string name, int count, List<string> failures)
    {
        var results = new List<Realization>(); AstrometricFrameAssessment? prior = null;
        for (var index = 0; index < count; index++)
        {
            var profile = VirtualAstrometryFixture.Profiles(seed + 1000 * index).Single(p => p.Name == name);
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
            var captured = await module.CaptureAsync(VirtualAstrometryQualificationTests.Request(
                new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddMinutes(index)), CancellationToken.None).ConfigureAwait(false);
            var input = VirtualAstrometryPixels.FromCapture(captured.Frame!);
            var sceneUtc = captured.Frame!.Metadata.Scene!.SceneUtc!.Value;
            var nominal = VirtualAstrometryFixture.NominalCalibration(profile);
            var caseId = $"{month:D2}-{name}-{index}";
            var ((measured, solved), frame) = VirtualOpticalCalibrationQualificationTests.Sample(() =>
            {
                var m = VirtualAstrometryFixture.MeasureV2(input, nominal);
                return (m, VirtualAstrometryFixture.SolveV2(input, nominal, catalog, m, prior));
            });
            if (!solved.Assessment.HasMeasuredMapping) { failures.Add($"{caseId}: solve not accepted: {solved.Assessment.Reason}"); continue; }
            prior = solved.Assessment;
            var (uncertainty, estimator) = VirtualOpticalCalibrationQualificationTests.Sample(() => AstrometricUncertaintyEstimator.Estimate(nominal, catalog,
                VirtualAstrometryFixture.SolverOptions, solved, VirtualAstrometryFixture.Detections(measured), Covariances(measured),
                AstrometricClockFacts.NotSupplied, input.Layout.Readout, options: Budget(floors, name)));
            var repeat = AstrometricUncertaintyEstimator.Estimate(nominal, catalog, VirtualAstrometryFixture.SolverOptions, solved,
                VirtualAstrometryFixture.Detections(measured), Covariances(measured), AstrometricClockFacts.NotSupplied, input.Layout.Readout, options: Budget(floors, name));
            if (repeat.IdentitySha256 != uncertainty.IdentitySha256) failures.Add($"{caseId}: estimate is not deterministic");
            var required = RequiredFloor(sigma => AstrometricUncertaintyEstimator.Estimate(nominal, catalog, VirtualAstrometryFixture.SolverOptions, solved,
                VirtualAstrometryFixture.Detections(measured), Covariances(measured), AstrometricClockFacts.NotSupplied, input.Layout.Readout,
                options: new(SystematicPixelSigma: sigma)).Validity);
            double[]? error = null; double[]? covariance = null; double? distance = null;
            if (uncertainty.IsAvailable)
            {
                var rig = profile.Config.Rig;
                var truthScale = NativeFocal(rig) / rig.Readout!.BinX / nominal.Projection.FocalLengthXPixels;
                error = Error(rig, sceneUtc, solved.Assessment, uncertainty.Estimate!, truthScale);
                covariance = [.. uncertainty.ConditionalFit.Covariance!.Select((v, i) => v + uncertainty.Systematic.Covariance![i])];
                distance = Mahalanobis(error, covariance);
            }
            var v = uncertainty.Validity;
            results.Add(new(name, index, uncertainty.Status, uncertainty.ReasonCode, error, covariance, distance, v.ChiSquare, v.DegreesOfFreedom,
                v.HeldOutChiSquare, v.HeldOutDegreesOfFreedom, required, frame, estimator, uncertainty.IdentitySha256));
        }
        return results;
    }

    /// <summary>
    /// The full V4-V5-V6 chain: a #1104 session calibration on truth optics that differ from nominal, then each withheld
    /// frame's total under that calibration. Truth is the rendering lens itself, so the frame's true zoom is one and the
    /// calibration component must carry the calibrated lens's own error.
    /// </summary>
    private static async Task<object> SessionChainAsync(CatalogSnapshotResult snapshot, AstrometricCatalogData catalog,
        IReadOnlyDictionary<string, double> floors, int month, int day, int seed, List<string> failures)
    {
        var template = VirtualAstrometryFixture.Profiles(seed)[0];
        var nominalRig = template.Config.Rig;
        var nominalNative = RigProjectionContextFactory.CreateNative(nominalRig);
        var truthRig = nominalRig with
        {
            Orientation = new(80 + month % 5, 40 * month % 360, 7 * month - 30),
            Optics = nominalRig.Optics with
            {
                FocalLengthXPixels = nominalNative.FocalLengthXPixels * (1 + .002 * (month % 4 - 1.5)),
                FocalLengthYPixels = nominalNative.FocalLengthYPixels * (1 + .002 * (month % 4 - 1.5)),
                PrincipalPointX = nominalNative.PrincipalPointX + 3 - month % 3 * 2,
                PrincipalPointY = nominalNative.PrincipalPointY - 2 + month % 2 * 4,
                RadialDistortionK1 = -.006 + .002 * (month % 3),
                CalibrationVersion = "virtual-uncertainty-session-truth"
            }
        };
        var caseId = $"{month:D2}-session";
        var origin = new DateTimeOffset(2026, month, day, 0, 0, 0, TimeSpan.Zero);
        async Task<(OpticalCalibrationFrame Frame, VirtualAstrometryPixels Pixels, DateTimeOffset SceneUtc)> CaptureAsync(string readout, double hours)
        {
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(template.Config with { Rig = VirtualOpticalCalibrationQualificationTests.WithReadout(truthRig, readout) }, CancellationToken.None).ConfigureAwait(false);
            var captured = await module.CaptureAsync(VirtualAstrometryQualificationTests.Request(origin.AddHours(hours)), CancellationToken.None).ConfigureAwait(false);
            var input = VirtualAstrometryPixels.FromCapture(captured.Frame!);
            var nominal = VirtualOpticalCalibrationQualificationTests.Calibration(VirtualOpticalCalibrationQualificationTests.WithReadout(nominalRig, readout), nominalRig.Optics.CalibrationVersion!);
            var measured = VirtualAstrometryFixture.MeasureV2(input, nominal);
            var descriptor = input.Layout.Readout!;
            return (new OpticalCalibrationFrame(VirtualAstrometryFixture.FrameContextV2(input), descriptor, CaptureContractJson.ComputeCanonicalJsonSha256(descriptor),
                VirtualAstrometryFixture.Detections(measured)), input, captured.Frame!.Metadata.Scene!.SceneUtc!.Value);
        }
        var fit = new List<OpticalCalibrationFrame>();
        foreach (var (readout, hours) in new[] { ("native", 5d), ("roi", 6.5), ("native", 8), ("bin2", 9) })
            fit.Add((await CaptureAsync(readout, hours).ConfigureAwait(false)).Frame);
        var withheld = new List<(string Readout, OpticalCalibrationFrame Frame, VirtualAstrometryPixels Pixels, DateTimeOffset SceneUtc)>();
        foreach (var (readout, hours) in new[] { ("native", 7.25), ("roi", 7.25), ("roi-bin2", 9.5) })
        {
            var c = await CaptureAsync(readout, hours).ConfigureAwait(false);
            withheld.Add((readout, c.Frame, c.Pixels, c.SceneUtc));
        }
        var result = OpticalCalibrationSession.Fit(nominalNative, fit, [.. withheld.Select(w => w.Frame)], catalog,
            new(FitRadialDistortion: true), VirtualAstrometryFixture.SolverOptions);
        if (!result.IsAccepted || result.SharedCovariance is null)
        {
            failures.Add($"{caseId}: calibration {result.Status} {result.ReasonCode}");
            return new { caseId, result.Status, result.ReasonCode };
        }
        var shared = AstrometricCalibrationCovariance.FromSession(result);
        var calibrated = result.CalibratedNative!.Value;
        var version = $"{OpticalCalibrationOptions.FitterVersion}:{result.IdentitySha256}";
        var calibratedRig = nominalRig with { Optics = RigProjectionContextFactory.CreateCalibratedOptics(nominalRig, calibrated, version) };
        var rows = new List<object>(); var estimates = new List<AstrometricFrameUncertainty>();
        foreach (var (readout, frame, pixels, sceneUtc) in withheld)
        {
            var calibration = VirtualOpticalCalibrationQualificationTests.Calibration(VirtualOpticalCalibrationQualificationTests.WithReadout(calibratedRig, readout), version);
            var measured = VirtualAstrometryFixture.MeasureV2(pixels, calibration);
            var solved = VirtualAstrometryFixture.SolveV2(pixels, calibration, catalog, measured);
            if (!solved.Assessment.HasMeasuredMapping) { failures.Add($"{caseId}-{readout}: {solved.Assessment.Reason}"); continue; }
            var uncertainty = AstrometricUncertaintyEstimator.Estimate(calibration, catalog, VirtualAstrometryFixture.SolverOptions, solved,
                VirtualAstrometryFixture.Detections(measured), Covariances(measured), AstrometricClockFacts.NotSupplied, pixels.Layout.Readout, shared, Budget(floors, $"mono-{readout}"));
            if (uncertainty.Total.Status != AstrometricUncertaintyEstimator.Available)
            {
                failures.Add($"{caseId}-{readout}: total {uncertainty.Total.Status} {uncertainty.ReasonCode} {uncertainty.Total.ReasonCode}");
                rows.Add(new { readout, uncertainty.Status, uncertainty.ReasonCode, totalReason = uncertainty.Total.ReasonCode, uncertainty.Validity });
                continue;
            }
            estimates.Add(uncertainty);
            var truth = VirtualOpticalCalibrationQualificationTests.WithReadout(truthRig, readout);
            var error = Error(truth, sceneUtc, solved.Assessment, uncertainty.Estimate!, 1);
            var distance = Mahalanobis(error, [.. uncertainty.Total.Covariance!]);
            var conditionalDistance = Mahalanobis(error, [.. uncertainty.ConditionalFit.Covariance!.Select((v, i) => v + uncertainty.Systematic.Covariance![i])]);
            if (distance > SessionEllipsoidLimit) failures.Add($"{caseId}-{readout}: total Mahalanobis {distance:F2} exceeds {SessionEllipsoidLimit}");
            rows.Add(new
            {
                readout,
                frame.Frame.CaptureId,
                uncertainty.IdentitySha256,
                error,
                totalMahalanobis = distance,
                conditionalOnlyMahalanobis = conditionalDistance,
                uncertainty.Total,
                uncertainty.ConditionalFit,
                uncertainty.SharedCalibration,
                uncertainty.Validity
            });
        }
        var cross = estimates.Count >= 2 ? AstrometricUncertaintyEstimator.CrossCovariance(estimates[0], estimates[1], shared) : null;
        return new { caseId, result.IdentitySha256, sharedCalibrationIdentity = shared.IdentitySha256, shared.Covariance, rows, crossCovarianceFirstPair = cross };
    }

    /// <summary>
    /// Diagnostic only: the per-star floor at which this frame's fitting chi-square equals its degrees of freedom, by bisection.
    /// The tuning values set the declared budget; held-out values are reported as margin and never feed back into the decision.
    /// </summary>
    private static double? RequiredFloor(Func<double, AstrometricModelValidity> validity)
    {
        static double Excess(AstrometricModelValidity v) => v.ChiSquare is { } chi && v.DegreesOfFreedom > 0 ? chi / v.DegreesOfFreedom - 1 : double.NaN;
        var low = 0d; var high = 10d;
        if (!(Excess(validity(low)) > 0)) return 0;
        if (Excess(validity(high)) > 0) return null;
        for (var i = 0; i < 30; i++)
        {
            var mid = (low + high) / 2;
            if (Excess(validity(mid)) > 0) low = mid; else high = mid;
        }
        return (low + high) / 2;
    }

    private static AstrometricPixelCovariance[] Covariances(StellarMeasurementResult measured) =>
        [.. measured.Detections.Where(d => d.CentroidCovariance is not null).Select(d => new AstrometricPixelCovariance(d.Index,
            d.CentroidCovariance!.Value.XX, d.CentroidCovariance.Value.XY, d.CentroidCovariance.Value.YY))];

    /// <summary>
    /// Truth minus estimate in the estimator's parameterization, from reference bases only: the small-angle ENU rotation
    /// vector w with truth_k = est_k + w x est_k (so w = 1/2 sum est_k x truth_k), and the log focal-scale difference.
    /// </summary>
    private static double[] Error(CameraRigConfig truthRig, DateTimeOffset sceneUtc, AstrometricFrameAssessment assessment,
        AstrometricConditionalEstimate estimate, double truthScale)
    {
        var truth = Pose(truthRig); var site = assessment.Frame.Observer;
        Vector Transport(Vector value) => ToEnu(FromEnu(value, sceneUtc, site), assessment.Frame.MidpointUtc, site);
        var fitted = Pose(estimate.BoresightAltitudeDegrees, estimate.BoresightAzimuthDegrees, estimate.RollDegrees, truthRig.Optics.HorizontalFlip);
        var w = (Vector.Cross(fitted.Right, Transport(truth.Right)) + Vector.Cross(fitted.Up, Transport(truth.Up)) +
            Vector.Cross(fitted.Forward, Transport(truth.Forward))) * .5;
        return [w.X, w.Y, w.Z, Math.Log(truthScale) - Math.Log(estimate.FocalScale)];
    }

    /// <summary>Mahalanobis distance by an independent Gauss-Jordan solve.</summary>
    private static double Mahalanobis(double[] error, double[] covariance)
    {
        var a = Enumerable.Range(0, 4).Select(_ => new double[5]).ToArray();
        for (var i = 0; i < 4; i++) { for (var j = 0; j < 4; j++) a[i][j] = covariance[i * 4 + j]; a[i][4] = error[i]; }
        for (var c = 0; c < 4; c++)
        {
            var pivot = Enumerable.Range(c, 4 - c).MaxBy(r => Math.Abs(a[r][c]));
            (a[c], a[pivot]) = (a[pivot], a[c]);
            for (var r = 0; r < 4; r++)
            {
                if (r == c) continue;
                var factor = a[r][c] / a[c][c];
                for (var k = c; k < 5; k++) a[r][k] -= factor * a[c][k];
            }
        }
        return Enumerable.Range(0, 4).Sum(i => error[i] * a[i][4] / a[i][i]);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[sorted.Length / 2];
    }
}
