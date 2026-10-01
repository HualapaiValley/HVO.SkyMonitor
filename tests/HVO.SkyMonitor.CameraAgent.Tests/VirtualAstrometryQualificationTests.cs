using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>Explicit actual-module evidence; unavailable full-snapshot inputs fail rather than skip.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualAstrometryQualificationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TuningStationaryPixels() => RunAsync(tuning: true);

    [TestMethod]
    public Task ActualPixelsBlindWarmReadouts() => RunAsync(tuning: false);

    private async Task RunAsync(bool tuning)
    {
        var snapshot = Snapshot();
        var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        Assert.HasCount(1637, catalog.Stars); Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        if (!tuning) RequireRevision();
        var reports = new List<object>(); var failures = new List<string>();
        var mapCases = new List<MapCase>();
        var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110220) }
            : [(Month: 1, Day: 15, Seed: 110201), (Month: 5, Day: 15, Seed: 110205), (Month: 9, Day: 15, Seed: 110209)];
        var partition = tuning ? "tuning-not-final" : "held-out-stationary-baseline";
        var resultRoot = Path.Combine(TestContext.TestRunDirectory!, partition); Directory.CreateDirectory(resultRoot);
        foreach (var (month, day, seed) in partitions)
        {
            foreach (var profile in VirtualAstrometryFixture.Profiles(seed))
            {
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
                var priors = new Dictionary<string, AstrometricFrameAssessment>(StringComparer.Ordinal);
                for (var index = 0; index < 3; index++)
                {
                    var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddSeconds(60 * index);
                    var clock = Stopwatch.StartNew();
                    var captured = await module.CaptureAsync(Request(utc), CancellationToken.None).ConfigureAwait(false);
                    var captureMs = clock.Elapsed.TotalMilliseconds;
                    var native = VirtualAstrometryPixels.FromCapture(captured.Frame!);
                    var views = new List<(VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels)> { (profile, native) };
                    if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
                        foreach (var (x, y) in new[] { (1, 0), (0, 1), (1, 1) })
                            views.Add(VirtualAstrometryFixture.DeriveCfaPhase(profile, native, x, y));
                    foreach (var (view, input) in views)
                    {
                        var caseId = $"{month:D2}-{view.Name}-{index}";
                        var nominal = VirtualAstrometryFixture.NominalCalibration(view);
                        clock.Restart(); var measured = VirtualAstrometryFixture.Measure(input, nominal);
                        var measurementMs = clock.Elapsed.TotalMilliseconds;
                        priors.TryGetValue(view.Name, out var prior);
                        var solved = VirtualAstrometryFixture.Solve(input, nominal, catalog, measured, prior);
                        var expectedMode = index == 0 ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm;
                        if (solved.Assessment.Mode != expectedMode) failures.Add($"{caseId}: expected {expectedMode} solve");
                        if (solved.Metrics.ElapsedMilliseconds > (index == 0 ? 15000 : 500))
                            failures.Add($"{caseId}: solve exceeded declared cold/warm time budget");
                        var sceneUtc = captured.Frame!.Metadata.Scene!.SceneUtc!.Value;
                        var score = solved.Assessment.HasMeasuredMapping ? VirtualAstrometryReference.Score(view.Config.Rig,
                            sceneUtc, nominal, catalog, measured, solved, failures, caseId) : null;
                        if (!solved.Assessment.HasMeasuredMapping) failures.Add($"{caseId}: {solved.Assessment.Reason}");
                        else
                        {
                            priors[view.Name] = solved.Assessment;
                            mapCases.Add(new(caseId, view.Config.Rig, sceneUtc, nominal, solved.Assessment));
                            if (index == 1) CheckIncompatiblePriors(input, nominal, catalog, measured, prior!);
                        }
                        var payloadFile = Path.Combine(resultRoot, caseId + ".raw16");
                        await File.WriteAllBytesAsync(payloadFile, input.Payload.ToArray()).ConfigureAwait(false);
                        TestContext.AddResultFile(payloadFile);
                        if (!tuning && index == 0 && view.Name is "mono-native" or "cfa-native")
                            await CheckReproducibility(profile, snapshot, input, nominal, catalog, solved, utc).ConfigureAwait(false);
                        var row = new
                        {
                            caseId,
                            view.Name,
                            index,
                            utc,
                            captureMs,
                            measurementMs,
                            detected = measured.Detections.Count,
                            measured.CandidateCount,
                            measured.Background,
                            measured.NoiseSigma,
                            solved.Assessment,
                            solved.Metrics,
                            score,
                            reportedAcquisition = captured.AcquisitionTiming,
                            sceneUtc,
                            layout = input.Layout,
                            input.ParentPayloadSha256,
                            configHash = CaptureContractJson.ComputeCanonicalJsonSha256(profile.Config),
                            nominal = new { nominal.IdentitySha256, nominal.CalibrationVersion, nominal.ReadoutIdentitySha256, nominal.Projection },
                            detections = measured.Detections,
                            payloadFile = Path.GetFileName(payloadFile)
                        };
                        reports.Add(row);
                        TestContext.WriteLine($"{caseId}: {solved.Assessment.Status}, {measured.Detections.Count} detected, {solved.Metrics.ElapsedMilliseconds:F2}ms solve");
                    }
                }
            }
        }
        var grids = ScoreMappings(mapCases, failures);
        var path = Path.Combine(resultRoot, "virtual-astrometry-pixels.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-astrometry-pixels-v1",
            partition,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            snapshot.DatabaseSha256,
            snapshot.RowCount,
            catalog.IdentitySha256,
            catalog.SelectionIdentitySha256,
            logicalExposurePolicy = "requested-start-plus-applied-exposure-v1; reported callbacks retained separately",
            detectorValidity = "stored saturation and declared nominal aperture, no projected-star mask",
            mappingInteriorMarginPixels = 12,
            finalExposureQualification = "pending-522-1106",
            reports,
            grids,
            failures
        }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        Assert.AreEqual(partitions.Length * 30, reports.Count, "Every supported camera/derived-phase case must run.");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static void CheckIncompatiblePriors(VirtualAstrometryPixels input, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, StellarDetectionResult measured, AstrometricFrameAssessment prior)
    {
        var wrongCalibration = new AstrometricCalibration(nominal.Projection, "incompatible-calibration", nominal.ReadoutIdentitySha256);
        Assert.AreEqual("warm-context-incompatible", VirtualAstrometryFixture.Solve(input, wrongCalibration, catalog, measured, prior).Assessment.ReasonCode);
        var wrongCatalog = new AstrometricCatalogData(catalog.Metadata with { Version = "incompatible-package" },
            catalog.Stars, true, 5, provenance: catalog.Provenance);
        Assert.AreEqual("warm-context-incompatible", VirtualAstrometryFixture.Solve(input, nominal, wrongCatalog, measured, prior).Assessment.ReasonCode);
        var detections = measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
        var frame = VirtualAstrometryFixture.FrameContext(input);
        var wrongSettings = AstrometricSolver.Refine(frame, nominal, catalog, detections, prior,
            VirtualAstrometryFixture.SolverOptions with { MaximumWarmAgeSeconds = 599 });
        Assert.AreEqual("warm-context-incompatible", wrongSettings.Assessment.ReasonCode);
        var wrongObserver = frame.Observer with { LongitudeDegrees = frame.Observer.LongitudeDegrees + .001 };
        var wrongSite = AstrometricSolver.Refine(frame with
        {
            Observer = wrongObserver,
            ObserverIdentitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(wrongObserver)
        }, nominal, catalog, detections, prior, VirtualAstrometryFixture.SolverOptions);
        Assert.AreEqual("warm-context-incompatible", wrongSite.Assessment.ReasonCode);
        Assert.ThrowsExactly<OperationCanceledException>(() => AstrometricSolver.Solve(frame, nominal, catalog,
            detections, VirtualAstrometryFixture.SolverOptions, new CancellationToken(canceled: true)));
        var limited = AstrometricSolver.Solve(frame, nominal, catalog, detections,
            VirtualAstrometryFixture.SolverOptions with { HypothesisLimit = 1 });
        Assert.AreEqual(AstrometricAssessmentStatus.BudgetExceeded, limited.Assessment.Status);
        Assert.IsFalse(limited.Assessment.HasMeasuredMapping);
    }

    private static async Task CheckReproducibility(VirtualAstrometryProfile profile, CatalogSnapshotResult snapshot,
        VirtualAstrometryPixels original, AstrometricCalibration nominal, AstrometricCatalogData catalog,
        AstrometricSolveResult expected, DateTimeOffset utc)
    {
        var repeated = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
        await using var lifetime = repeated.ConfigureAwait(false);
        await repeated.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
        var capture = await repeated.CaptureAsync(Request(utc), CancellationToken.None).ConfigureAwait(false);
        var input = VirtualAstrometryPixels.FromCapture(capture.Frame!);
        CollectionAssert.AreEqual(original.Payload.ToArray(), input.Payload.ToArray());
        Assert.AreEqual(VirtualAstrometryFixture.FrameContext(original), VirtualAstrometryFixture.FrameContext(input));
        var actual = VirtualAstrometryFixture.Solve(input, nominal, catalog, VirtualAstrometryFixture.Measure(input, nominal));
        Assert.AreEqual(expected.Assessment.IdentitySha256, actual.Assessment.IdentitySha256);
        CollectionAssert.AreEqual(AstrometricEvidenceJson.Serialize(expected.Assessment), AstrometricEvidenceJson.Serialize(actual.Assessment));
    }

    private sealed record MapCase(string Id, CameraRigConfig Truth, DateTimeOffset SceneUtc,
        AstrometricCalibration Nominal, AstrometricFrameAssessment Assessment);

    private static List<object> ScoreMappings(List<MapCase> cases, List<string> failures)
    {
        var rows = new List<object>();
        foreach (var source in cases.Where(item => item.Id.EndsWith("mono-native-0", StringComparison.Ordinal)))
        {
            var sourceMapping = new AstrometricMapping(source.Nominal, source.Assessment);
            foreach (var target in cases.Where(item => item.Id[..2] == source.Id[..2] && item.Id.EndsWith("-2", StringComparison.Ordinal)))
            {
                var targetMapping = new AstrometricMapping(target.Nominal, target.Assessment);
                foreach (var fy in new[] { .05, .2, .5, .8, .95 })
                    foreach (var fx in new[] { .05, .2, .5, .8, .95 })
                    {
                        var pixel = new PixelPoint(fx * source.Nominal.Projection.WidthPixels, fy * source.Nominal.Projection.HeightPixels);
                        var ray = VirtualAstrometryReference.Unproject(source.Truth, pixel);
                        var destinationRay = ray is null ? (VirtualAstrometryReference.Vector?)null : VirtualAstrometryReference.ToEnu(
                            VirtualAstrometryReference.FromEnu(ray.Value, source.SceneUtc, VirtualAstrometryFixture.Observer), target.SceneUtc, VirtualAstrometryFixture.Observer);
                        var expected = ray is { Z: >= 0 } && destinationRay is { Z: >= 0 }
                            ? VirtualAstrometryReference.Project(target.Truth, destinationRay.Value) : null;
                        var actual = sourceMapping.MapPixelTo(pixel, targetMapping);
                        var residual = actual is null || expected is null ? (double?)null : VirtualAstrometryReference.Distance(actual.Value, expected.Value);
                        if ((actual is null) != (expected is null) || residual > .75)
                            failures.Add($"{source.Id}->{target.Id} independent grid mapping failed at {pixel}: expected {expected}, actual {actual}, residual {residual:R}");
                        var sky = sourceMapping.PixelToSky(pixel);
                        if ((sky is null) != (ray is null)) failures.Add($"{source.Id} pixel-to-sky support mismatch at {pixel}");
                        double? rayError = null;
                        if (ray is not null && sky is not null)
                        {
                            var transported = VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.FromEnu(ray.Value,
                                source.SceneUtc, VirtualAstrometryFixture.Observer), source.Assessment.Frame.MidpointUtc, VirtualAstrometryFixture.Observer);
                            var a = VirtualAstrometryReference.Radians(sky.Horizontal.AltitudeDegrees);
                            var z = VirtualAstrometryReference.Radians(sky.Horizontal.AzimuthDegrees);
                            var fitted = new VirtualAstrometryReference.Vector(Math.Cos(a) * Math.Sin(z), Math.Cos(a) * Math.Cos(z), Math.Sin(a));
                            rayError = VirtualAstrometryReference.SeparationDegrees(transported, fitted);
                            if (rayError > .06) failures.Add($"{source.Id} independent inverse ray error {rayError:R}deg");
                            var scale = sourceMapping.LocalPixelScale(pixel);
                            double? ReferenceScale(PixelPoint p, PixelPoint q)
                            {
                                var first = VirtualAstrometryReference.Unproject(source.Truth, p); var second = VirtualAstrometryReference.Unproject(source.Truth, q);
                                return first is null || second is null ? null : VirtualAstrometryReference.SeparationDegrees(first.Value, second.Value) * 3600;
                            }
                            var sx = ReferenceScale(new(pixel.X - .5, pixel.Y), new(pixel.X + .5, pixel.Y));
                            var sy = ReferenceScale(new(pixel.X, pixel.Y - .5), new(pixel.X, pixel.Y + .5));
                            if (sx is not null && (scale.XArcsecondsPerPixel is null || Math.Abs(scale.XArcsecondsPerPixel.Value / sx.Value - 1) > .002) ||
                                sy is not null && (scale.YArcsecondsPerPixel is null || Math.Abs(scale.YArcsecondsPerPixel.Value / sy.Value - 1) > .002))
                                failures.Add($"{source.Id} independent local pixel scale mismatch");
                        }
                        rows.Add(new { source = source.Id, destination = target.Id, pixel, expected, actual, residualPixels = residual, rayErrorDegrees = rayError });
                    }
            }
        }
        Assert.IsTrue(rows.Count >= 250);
        return rows;
    }

    private static CatalogSnapshotResult Snapshot()
    {
        var root = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "Approved full catalog installation is mandatory.");
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v42-production"));
        Assert.AreEqual(119625L, snapshot.RowCount);
        Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);
        return snapshot;
    }
    private static CaptureRequest Request(DateTimeOffset utc) => new(utc, TimeSpan.FromSeconds(60), CaptureMode.Still,
        new(TimeSpan.FromSeconds(1), 150, null, null));
    private static void RequireRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        Assert.IsTrue(revision is { Length: 40 } && revision.All(Uri.IsHexDigit), "Final evidence requires the exact committed source SHA.");
        var built = typeof(VirtualAstrometryQualificationTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.IsTrue(built.Contains(revision, StringComparison.OrdinalIgnoreCase), $"Binary revision {built} does not contain requested source {revision}.");
    }
}
