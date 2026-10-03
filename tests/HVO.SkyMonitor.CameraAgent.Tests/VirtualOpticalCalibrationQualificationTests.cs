using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Explicit HYG evidence for session optical calibration on actual VirtualSky frames. The module renders a truth rig
/// whose focal scale, principal point and radial k1 differ from the nominal optics; only final pixels and the nominal
/// rig reach the fitter. Withheld frames are scored by the independent reference equations, never by production projectors.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualOpticalCalibrationQualificationTests
{
    // Predeclared on #1104 before held-out evidence; tuning results may not change them.
    private const double MaximumFocalScaleError = .001;
    private const double MaximumPrincipalPointErrorPixels = 1;
    private const double MaximumRadialDistortionError = .0015;
    private const double MaximumNativeMappingErrorPixels = .5;
    private const double MappingDomainZenithDegrees = 85;
    private const double MaximumValidationVerificationRmsPixels = .5;
    private const double MaximumFitMilliseconds = 60000;
    // A session fit may cost at most its constituent ordinary single-frame cold solves, with this margin and overhead.
    private const double MaximumFitToSingleSolveRatioMargin = 1.25;
    private const double FitOverheadMilliseconds = 2000;
    private const long FitOverheadAllocatedBytes = 256L << 20;
    private const long MaximumPeakWorkingSetBytes = 2L << 30;

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(VirtualAstrometryFixture.JsonOptions)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true
    };

    // Fit frames span four hours of sky rotation over three readouts of the one native sensor.
    private static readonly (string Readout, double Hours)[] FitPlan = [("native", 5), ("roi", 6.5), ("native", 8), ("bin2", 9)];
    private static readonly (string Readout, double Hours)[] ValidationPlan = [("native", 7.25), ("roi", 7.25), ("roi-bin2", 9.5)];

    private sealed record Truth(double FocalScale, double PrincipalOffsetX, double PrincipalOffsetY, double K1, RigOrientation Pose);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TuningOpticalCalibration() => RunAsync(tuning: true);

    [TestMethod]
    public Task HeldOutOpticalCalibration() => RunAsync(tuning: false);

    private async Task RunAsync(bool tuning)
    {
        Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), "The declared measurement disables tiered compilation.");
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        Assert.HasCount(1637, catalog.Stars); Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        if (!tuning) VirtualAstrometryQualificationTests.RequireRevision();
        var partitions = tuning
            ? new[] { (Month: 2, Day: 10, Seed: 110420, Truth: new Truth(1.010, 5, -4, -.007, new(82, 137, 13))) }
            : [(Month: 1, Day: 15, Seed: 110401, Truth: new Truth(1.008, 4.5, -3.5, -.008, new(78, 205, -21))),
               (Month: 5, Day: 15, Seed: 110405, Truth: new Truth(.992, -6, 2.5, .006, new(85, 40, 33))),
               (Month: 9, Day: 15, Seed: 110409, Truth: new Truth(1.004, 3, 5, 0, new(80, 300, 5)))];
        var partition = tuning ? "optical-calibration-tuning-not-final" : "optical-calibration-held-out";
        var reports = new List<object>(); var failures = new List<string>();
        var fitIndex = 0;
        foreach (var (month, day, seed, truth) in partitions)
        {
            var caseId = $"{month:D2}";
            var template = VirtualAstrometryFixture.Profiles(seed)[0];
            var nominalRig = template.Config.Rig;
            var nominalNative = RigProjectionContextFactory.CreateNative(nominalRig);
            var truthRig = nominalRig with
            {
                Orientation = truth.Pose,
                Optics = nominalRig.Optics with
                {
                    FocalLengthXPixels = nominalNative.FocalLengthXPixels * truth.FocalScale,
                    FocalLengthYPixels = nominalNative.FocalLengthYPixels * truth.FocalScale,
                    PrincipalPointX = nominalNative.PrincipalPointX + truth.PrincipalOffsetX,
                    PrincipalPointY = nominalNative.PrincipalPointY + truth.PrincipalOffsetY,
                    RadialDistortionK1 = truth.K1,
                    CalibrationVersion = "virtual-optical-calibration-truth"
                }
            };
            var truthNative = RigProjectionContextFactory.CreateNative(truthRig);
            var origin = new DateTimeOffset(2026, month, day, 0, 0, 0, TimeSpan.Zero);
            var measurements = new List<object>();
            async Task<(OpticalCalibrationFrame Frame, VirtualAstrometryPixels Pixels, StellarMeasurementSummary Summary, DateTimeOffset SceneUtc)> CaptureAsync(string readout, double hours)
            {
                var rig = WithReadout(truthRig, readout);
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(template.Config with { Rig = rig }, CancellationToken.None).ConfigureAwait(false);
                var captured = await module.CaptureAsync(VirtualAstrometryQualificationTests.Request(origin.AddHours(hours)), CancellationToken.None).ConfigureAwait(false);
                var input = VirtualAstrometryPixels.FromCapture(captured.Frame!);
                var nominal = Calibration(WithReadout(nominalRig, readout), nominalRig.Optics.CalibrationVersion!);
                var (measured, resources) = Sample(() => VirtualAstrometryFixture.MeasureV2(input, nominal));
                var descriptor = input.Layout.Readout ?? throw new InvalidOperationException("The capture must declare its readout.");
                var summary = new StellarMeasurementSummary(readout, hours, measured.Status.ToString(), measured.Detections.Count,
                    measured.CandidateCount, resources);
                measurements.Add(summary);
                return (new OpticalCalibrationFrame(VirtualAstrometryFixture.FrameContextV2(input), descriptor,
                    CaptureContractJson.ComputeCanonicalJsonSha256(descriptor), VirtualAstrometryFixture.Detections(measured)),
                    input, summary, captured.Frame!.Metadata.Scene!.SceneUtc!.Value);
            }

            var fit = new List<OpticalCalibrationFrame>(); VirtualAstrometryPixels? firstNative = null;
            foreach (var (readout, hours) in FitPlan)
            {
                var captured = await CaptureAsync(readout, hours).ConfigureAwait(false);
                fit.Add(captured.Frame); firstNative ??= captured.Pixels;
            }
            var withheld = new List<(string Readout, OpticalCalibrationFrame Frame, VirtualAstrometryPixels Pixels, DateTimeOffset SceneUtc)>();
            foreach (var (readout, hours) in ValidationPlan)
            {
                var captured = await CaptureAsync(readout, hours).ConfigureAwait(false);
                withheld.Add((readout, captured.Frame, captured.Pixels, captured.SceneUtc));
            }
            var validation = withheld.Select(w => w.Frame).ToArray();

            var (result, fitResources) = Sample(() => OpticalCalibrationSession.Fit(nominalNative, fit, validation, catalog,
                new(FitRadialDistortion: true), VirtualAstrometryFixture.SolverOptions));
            var fitKind = fitIndex++ == 0 ? "cold" : "session";
            var (omitted, omittedResources) = Sample(() => OpticalCalibrationSession.Fit(nominalNative, fit, validation, catalog,
                new(FitRadialDistortion: false), VirtualAstrometryFixture.SolverOptions));
            fitIndex++;

            // Baseline: one ordinary cold single-frame solve of the first native fit frame at nominal optics.
            var baselineCalibration = Calibration(WithReadout(nominalRig, "native"), nominalRig.Optics.CalibrationVersion!);
            var baselineMeasured = VirtualAstrometryFixture.MeasureV2(firstNative!, baselineCalibration);
            var (_, baseline) = Sample(() => VirtualAstrometryFixture.SolveV2(firstNative!, baselineCalibration, catalog, baselineMeasured));
            var solves = FitPlan.Length + ValidationPlan.Length;
            CheckResources($"{caseId} {fitKind} fit", fitResources, baseline, solves, failures);
            CheckResources($"{caseId} omitted-distortion fit", omittedResources, baseline, solves, failures);
            if (truth.K1 != 0 && omitted.IsAccepted) failures.Add($"{caseId}: omitted distortion was accepted for k1 {truth.K1}");
            if (truth.K1 == 0 && !omitted.IsAccepted)
                failures.Add($"{caseId}: undistorted truth rejected without k1: {string.Join(",", omitted.Rejections)}");
            if (omitted.CalibratedNative is not null && !omitted.IsAccepted) failures.Add($"{caseId}: rejected result proposes optics");

            object? errors = null; var withheldScores = new List<object>();
            if (!result.IsAccepted)
            {
                failures.Add($"{caseId}: calibration {result.Status} {result.ReasonCode}: {string.Join(",", result.Rejections)}");
            }
            else
            {
                var calibrated = result.CalibratedNative!.Value;
                var focalError = Math.Abs(calibrated.FocalLengthXPixels / truthNative.FocalLengthXPixels - 1);
                var principalError = Math.Sqrt(Math.Pow(calibrated.PrincipalPointX - truthNative.PrincipalPointX, 2) +
                    Math.Pow(calibrated.PrincipalPointY - truthNative.PrincipalPointY, 2));
                var k1Error = Math.Abs(calibrated.RadialDistortionK1 - truth.K1);
                var mappingError = MappingError(truthNative, calibrated);
                errors = new { focalError, principalError, k1Error, mappingError };
                if (focalError > MaximumFocalScaleError || principalError > MaximumPrincipalPointErrorPixels ||
                    k1Error > MaximumRadialDistortionError || mappingError > MaximumNativeMappingErrorPixels)
                    failures.Add($"{caseId}: focal {focalError:R}, principal {principalError:R} px, k1 {k1Error:R}, mapping {mappingError:R} px");
                foreach (var check in result.Validations)
                {
                    if (check.Status != AstrometricAssessmentStatus.Accepted || check.VerificationRmsPixels is null or > MaximumValidationVerificationRmsPixels)
                        failures.Add($"{caseId}: validation {check.CaptureId} {check.Status} {check.ReasonCode} rms {check.VerificationRmsPixels:R}");
                }

                // Hosts activate only through the declared optics profile; it must reproduce the calibration exactly.
                var version = $"{OpticalCalibrationOptions.FitterVersion}:{result.IdentitySha256}";
                var calibratedOptics = RigProjectionContextFactory.CreateCalibratedOptics(nominalRig, calibrated, version);
                var calibratedRig = nominalRig with { Optics = calibratedOptics };
                var activation = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using (activation.ConfigureAwait(false))
                    await activation.InitializeAsync(template.Config with { Rig = calibratedRig }, CancellationToken.None).ConfigureAwait(false);
                foreach (var (readout, frame, pixels, sceneUtc) in withheld)
                {
                    var calibration = Calibration(WithReadout(calibratedRig, readout), version);
                    var measured = VirtualAstrometryFixture.MeasureV2(pixels, calibration);
                    var solved = VirtualAstrometryFixture.SolveV2(pixels, calibration, catalog, measured);
                    var scoreId = $"{caseId}-{readout}-withheld";
                    if (!solved.Assessment.HasMeasuredMapping) { failures.Add($"{scoreId}: {solved.Assessment.Reason}"); continue; }
                    withheldScores.Add(new
                    {
                        readout,
                        frame.Frame.CaptureId,
                        score = VirtualAstrometryReference.Score(WithReadout(truthRig, readout), sceneUtc, calibration, catalog,
                            VirtualAstrometryFixture.Detections(measured), solved, failures, scoreId)
                    });
                }
            }
            TestContext.WriteLine($"{caseId}: {result.Status} {result.ReasonCode} {fitKind} {fitResources.WallMs:F0} ms cpu {fitResources.CpuMs:F0} ms " +
                $"alloc {fitResources.AllocatedBytes / 1048576.0:F0} MiB peak {fitResources.PeakWorkingSetBytes / 1048576.0:F0} MiB; " +
                $"errors {JsonSerializer.Serialize(errors)}; omitted {omitted.Status} {string.Join(",", omitted.Rejections)}");
            reports.Add(new
            {
                caseId,
                truth,
                truthNative,
                nominalNative,
                measurements,
                fit = new
                {
                    kind = fitKind,
                    resources = fitResources,
                    singleFrameColdSolveBaseline = baseline,
                    result.Status,
                    result.ReasonCode,
                    result.Rejections,
                    result.IdentitySha256,
                    result.CalibratedNative,
                    result.Parameters,
                    result.Frames,
                    result.Validations,
                    result.Diagnostics,
                    result.Metrics
                },
                errors,
                withheldScores,
                omittedDistortion = new
                {
                    resources = omittedResources,
                    omitted.Status,
                    omitted.ReasonCode,
                    omitted.Rejections,
                    omitted.Diagnostics
                }
            });
        }
        var root = Path.Combine(TestContext.TestRunDirectory!, partition); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "virtual-optical-calibration.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-optical-calibration-v1",
            partition,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            snapshot.DatabaseSha256,
            catalog.IdentitySha256,
            catalog.SelectionIdentitySha256,
            fitter = OpticalCalibrationOptions.FitterVersion,
            tolerances = new
            {
                MaximumFocalScaleError,
                MaximumPrincipalPointErrorPixels,
                MaximumRadialDistortionError,
                MaximumNativeMappingErrorPixels,
                MappingDomainZenithDegrees,
                MaximumValidationVerificationRmsPixels,
                MaximumFitMilliseconds,
                MaximumFitToSingleSolveRatioMargin,
                FitOverheadMilliseconds,
                FitOverheadAllocatedBytes,
                MaximumPeakWorkingSetBytes
            },
            fitPlan = FitPlan.Select(p => new { p.Readout, p.Hours }),
            validationPlan = ValidationPlan.Select(p => new { p.Readout, p.Hours }),
            reports,
            failures
        }, EvidenceJsonOptions)).ConfigureAwait(false);
        TestContext.WriteLine(path);
        TestContext.AddResultFile(path);
        Assert.HasCount(partitions.Length, reports);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private sealed record StellarMeasurementSummary(string Readout, double Hours, string Status, int Detections, int Candidates,
        VirtualMeasuredStarQualificationTests.ResourceSample Resources);

    internal static CameraRigConfig WithReadout(CameraRigConfig rig, string readout)
    {
        var native = rig.Readout!;
        var roi = native with { Roi = new(240, 96, 1440, 1024) };
        var bin = new Func<SensorReadoutProfile, SensorReadoutProfile>(r => r with { BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalAverageV1 });
        return rig with
        {
            Readout = readout switch
            {
                "native" => native,
                "roi" => roi,
                "bin2" => bin(native),
                "roi-bin2" => bin(roi),
                _ => throw new ArgumentOutOfRangeException(nameof(readout))
            }
        };
    }

    internal static AstrometricCalibration Calibration(CameraRigConfig rig, string version)
    {
        var readout = SensorReadoutResolver.Resolve(rig.Sensor, rig.Readout!);
        return new(RigProjectionContextFactory.Create(rig), version, CaptureContractJson.ComputeCanonicalJsonSha256(readout.Layout));
    }

    /// <summary>
    /// Largest native-pixel disagreement between the truth and calibrated intrinsic maps over the supported field,
    /// evaluated with the reference equidistant form r = f theta (1 + k1 theta^2) rather than a production projector.
    /// </summary>
    private static double MappingError(ProjectionContext truth, ProjectionContext calibrated)
    {
        var worst = 0d;
        for (var zenith = 0d; zenith <= MappingDomainZenithDegrees; zenith += 2.5)
        {
            var theta = VirtualAstrometryReference.Radians(zenith);
            var truthRadius = truth.FocalLengthXPixels * theta * (1 + truth.RadialDistortionK1 * theta * theta);
            var calibratedRadius = calibrated.FocalLengthXPixels * theta * (1 + calibrated.RadialDistortionK1 * theta * theta);
            for (var azimuth = 0d; azimuth < 360; azimuth += 10)
            {
                var phi = VirtualAstrometryReference.Radians(azimuth);
                var dx = truth.PrincipalPointX + truthRadius * Math.Cos(phi) - calibrated.PrincipalPointX - calibratedRadius * Math.Cos(phi);
                var dy = truth.PrincipalPointY - truthRadius * Math.Sin(phi) - calibrated.PrincipalPointY + calibratedRadius * Math.Sin(phi);
                worst = Math.Max(worst, Math.Sqrt(dx * dx + dy * dy));
            }
        }
        return worst;
    }

    private static void CheckResources(string label, VirtualMeasuredStarQualificationTests.ResourceSample resources,
        VirtualMeasuredStarQualificationTests.ResourceSample singleSolve, int solves, List<string> failures)
    {
        var wallLimit = Math.Min(MaximumFitMilliseconds, MaximumFitToSingleSolveRatioMargin * solves * singleSolve.WallMs + FitOverheadMilliseconds);
        var allocationLimit = MaximumFitToSingleSolveRatioMargin * solves * singleSolve.AllocatedBytes + FitOverheadAllocatedBytes;
        if (resources.WallMs > wallLimit) failures.Add($"{label}: {resources.WallMs:F0} ms exceeds {wallLimit:F0} ms");
        if (resources.AllocatedBytes > allocationLimit)
            failures.Add($"{label}: allocated {resources.AllocatedBytes / 1048576.0:F0} MiB exceeds {allocationLimit / 1048576.0:F0} MiB");
        if (resources.PeakWorkingSetBytes > MaximumPeakWorkingSetBytes) failures.Add($"{label}: peak working set {resources.PeakWorkingSetBytes / 1048576.0:F0} MiB");
    }

    internal static (T Value, VirtualMeasuredStarQualificationTests.ResourceSample Resources) Sample<T>(Func<T> measure)
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
}
