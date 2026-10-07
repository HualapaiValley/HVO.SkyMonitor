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

    /// <summary>
    /// A measured source farther than this from its associated star's truth trail, and nearer another rendered star, is an
    /// unmodeled-source association: a fainter rendered star matched to a catalog star.
    /// </summary>
    internal const double UnmodeledSourceRadiusPixels = 1.5;

    private sealed record CaseOutcome(string View, int Index, AstrometricSolveMode Mode, string Disposition, int Failures,
        IReadOnlyDictionary<string, int>? ExclusionCounts);

    private async Task RunAsync(bool tuning)
    {
        var snapshot = Snapshot();
        var catalog = await snapshot.Catalog.ReadAsync(5, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        Assert.HasCount(1637, catalog.Stars); Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        if (!tuning) RequireRevision();
        var family = VirtualAstrometryFixture.Family; var variant = VirtualAstrometryFixture.Variant;
        var exposureSeconds = VirtualAstrometryFixture.ExposureSeconds; var exposure = VirtualAstrometryFixture.Exposure;
        var measurer = VirtualAstrometryFixture.Measurer;
        // Truth trails at the rendered depth: product depth renders stars fainter than the solver catalog holds.
        var rendered = snapshot.Catalog.Query(new(variant.Depth.MaximumMagnitude, variant.Depth.MaximumResults));
        var reports = new List<object>(); var failures = new List<string>();
        var cases = new List<object>(); var outcomes = new List<CaseOutcome>();
        var unsupported = new List<object>(); var negativeControl = new List<object>();
        var mapCases = new List<MapCase>();
        var partitions = tuning ? new[] { (Month: 2, Day: 10, Seed: 110220) }
            : [(Month: 1, Day: 15, Seed: 110201), (Month: 5, Day: 15, Seed: 110205), (Month: 9, Day: 15, Seed: 110209)];
        var partition = tuning ? "tuning-not-final" : "held-out-stationary-baseline";
        // Views per capture: one per profile plus three derived phases for each CFA profile. A declared refusal runs no case.
        var plan = VirtualAstrometryFixture.Profiles(0, family, variant).Select(p => (p.Name,
            Views: p.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 4 : 1,
            Refusal: VirtualAstrometryFixture.DeclaredRefusal(family, p, exposureSeconds, variant))).ToArray();
        var resultRoot = Path.Combine(TestContext.TestRunDirectory!, partition); Directory.CreateDirectory(resultRoot);
        foreach (var (month, day, seed) in partitions)
        {
            foreach (var profile in VirtualAstrometryFixture.Profiles(VirtualAstrometryFixture.Seed(seed, variant), family, variant))
            {
                var refusal = VirtualAstrometryFixture.DeclaredRefusal(family, profile, exposureSeconds, variant);
                var cell = $"{month:D2}-{profile.Name}";
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                if (await RenderStage(async () =>
                    {
                        await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
                        return module;
                    }, refusal, "initialize", cell, null, unsupported, failures).ConfigureAwait(false) is null) continue;
                var priors = new Dictionary<string, AstrometricFrameAssessment>(StringComparer.Ordinal);
                for (var index = 0; index < 3; index++)
                {
                    var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddSeconds(60 * index);
                    var clock = Stopwatch.StartNew();
                    var captured = await RenderStage(() => module.CaptureAsync(Request(utc), CancellationToken.None),
                        refusal, "capture", cell, index, unsupported, failures).ConfigureAwait(false);
                    if (captured is null) continue;
                    var captureMs = clock.Elapsed.TotalMilliseconds;
                    var native = VirtualAstrometryPixels.FromCapture(captured.Frame!);
                    var views = new List<(VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels)> { (profile, native) };
                    if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
                        foreach (var (x, y) in new[] { (1, 0), (0, 1), (1, 1) })
                            views.Add(VirtualAstrometryFixture.DeriveCfaPhase(profile, native, x, y));
                    foreach (var (view, input) in views)
                    {
                        var caseId = $"{month:D2}-{view.Name}-{index}";
                        var firstFailure = failures.Count;
                        var nominal = VirtualAstrometryFixture.NominalCalibration(view);
                        clock.Restart(); var measured = VirtualAstrometryFixture.Measure(input, nominal, measurer);
                        var measurementMs = clock.Elapsed.TotalMilliseconds;
                        priors.TryGetValue(view.Name, out var prior);
                        var solved = VirtualAstrometryFixture.Solve(measured, nominal, catalog, prior);
                        var mustAccept = variant.ExpectedOutcome == VirtualLongExposureVariant.Accepted;
                        // An accept-only variant keeps the fixed blind-then-warm schedule; a fail-closed variant re-enters blind after a rejection.
                        var expectedMode = (mustAccept ? index == 0 : prior is null) ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm;
                        if (solved.Assessment.Mode != expectedMode) failures.Add($"{caseId}: expected {expectedMode} solve");
                        if (solved.Metrics.ElapsedMilliseconds > (expectedMode == AstrometricSolveMode.Blind ? 15000 : 500))
                            failures.Add($"{caseId}: solve exceeded declared cold/warm time budget");
                        var sceneUtc = captured.Frame!.Metadata.Scene!.SceneUtc!.Value;
                        // The renderer integrates about the scene time; the solver evaluates the sky at the frame-context midpoint.
                        var midpointDeltaTicks = (measured.Frame.MidpointUtc - sceneUtc).Ticks;
                        if (midpointDeltaTicks != 0) failures.Add($"{caseId}: frame midpoint is {midpointDeltaTicks} ticks from the rendered scene time");
                        var accepted = solved.Assessment.HasMeasuredMapping;
                        var score = accepted ? VirtualAstrometryReference.Score(view.Config.Rig, sceneUtc, exposure, nominal, catalog,
                            [.. measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux))], solved, failures, caseId) : null;
                        if (!accepted && mustAccept) failures.Add($"{caseId}: {solved.Assessment.Reason}");
                        if (accepted && variant.ExpectedOutcome == VirtualLongExposureVariant.Rejected)
                            failures.Add($"{caseId}: the {variant.Name} variant must reject without a measured mapping");
                        var renderTrails = VirtualAstrometryReference.Trails(view.Config.Rig, sceneUtc, exposure, rendered);
                        var sources = accepted ? Sources(renderTrails, measured, solved, failures, caseId) : null;
                        if (accepted)
                        {
                            priors[view.Name] = solved.Assessment;
                            mapCases.Add(new(caseId, view.Config.Rig, sceneUtc, nominal, solved.Assessment));
                            // A rejected blind solve leaves no prior to probe; skipping the probe keeps the remaining cases reporting.
                            if (index == 1 && prior is not null) CheckIncompatiblePriors(nominal, catalog, measured, prior);
                            if (index == 0 && view.Name is "mono-native" or "cfa-native" or "mono-roi")
                                negativeControl.Add(NegativeControl(caseId, view.Config.Rig, sceneUtc, exposure, nominal, catalog, measured, solved));
                        }
                        var payloadFile = Path.Combine(resultRoot, caseId + ".raw16");
                        await File.WriteAllBytesAsync(payloadFile, input.Payload.ToArray()).ConfigureAwait(false);
                        TestContext.AddResultFile(payloadFile);
                        if (!tuning && index == 0 && view.Name is "mono-native" or "cfa-native")
                            await CheckReproducibility(profile, snapshot, input, nominal, catalog, measured, solved, utc).ConfigureAwait(false);
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
                        var disposition = accepted ? "accepted"
                            : solved.Assessment.ReasonCode == "insufficient-detections" ? "rejected-underconstrained" : $"rejected-{solved.Assessment.ReasonCode}";
                        var candidate = measured.Candidate;
                        var trailLengths = candidate?.Detections.Where(d => d.TrailLengthPixels is not null).Select(d => d.TrailLengthPixels!.Value).Order().ToArray();
                        var whole = renderTrails.Where(trail => !trail.Truncated).ToArray();
                        var truthLengths = whole.Select(trail => trail.LengthPixels).Order().ToArray();
                        cases.Add(new
                        {
                            caseId,
                            view = view.Name,
                            index,
                            sceneUtc,
                            frameMidpointUtc = measured.Frame.MidpointUtc,
                            midpointDeltaTicks,
                            measured.Frame.DetectionAlgorithmVersion,
                            measurement = candidate is null ? null : new
                            {
                                candidate.Status,
                                candidate.ExclusionCounts,
                                saturatedDetections = candidate.Detections.Count(d => d.Conditions.HasFlag(StellarSourceConditions.Saturated)),
                                trailedDetections = candidate.Detections.Count(d => d.Conditions.HasFlag(StellarSourceConditions.Trailed)),
                                medianTrailPixels = Percentile(trailLengths!, .5),
                                maximumTrailPixels = trailLengths!.Length == 0 ? (double?)null : trailLengths[^1]
                            },
                            assessment = new { solved.Assessment.Status, solved.Assessment.ReasonCode, solved.Assessment.Mode, solved.Assessment.HasMeasuredMapping },
                            disposition,
                            truth = new
                            {
                                renderedVisible = renderTrails.Length,
                                truncated = renderTrails.Length - whole.Length,
                                medianTrailPixels = Percentile(truthLengths, .5),
                                maximumTrailPixels = truthLengths.Length == 0 ? (double?)null : truthLengths[^1],
                                maximumMidpointBiasPixels = whole.Select(trail => trail.MidpointBiasPixels).DefaultIfEmpty().Max()
                            },
                            sources,
                            render = captured.Frame.Metadata.Extra?.Where(entry => entry.Key.StartsWith("stellar", StringComparison.Ordinal))
                                .OrderBy(entry => entry.Key, StringComparer.Ordinal).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                            failures = failures.Skip(firstFailure).ToArray()
                        });
                        outcomes.Add(new(view.Name, index, solved.Assessment.Mode, disposition, failures.Count - firstFailure, candidate?.ExclusionCounts));
                        TestContext.WriteLine($"{caseId}: {solved.Assessment.Status}, {measured.Detections.Count} detected, {solved.Metrics.ElapsedMilliseconds:F2}ms solve");
                    }
                }
            }
        }
        var mappings = ScoreMappings(mapCases, failures);
        var cells = Cells(outcomes, plan.Where(p => p.Refusal is not null).Select(p => (p.Name, p.Refusal!)), variant);
        var path = Path.Combine(resultRoot, "virtual-astrometry-pixels.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-astrometry-pixels-v3",
            partition,
            projectionFamily = family.Name,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            snapshot.DatabaseSha256,
            snapshot.RowCount,
            catalog.IdentitySha256,
            catalog.SelectionIdentitySha256,
            logicalExposurePolicy = "requested-start-plus-applied-exposure-v1; reported callbacks retained separately",
            detectorValidity = "stored saturation and declared nominal aperture, no projected-star mask",
            mappingInteriorMarginPixels = 12,
            finalExposureQualification = "virtual-astrometry-long-exposure-v1",
            exposureSeconds,
            measurer,
            variant = new { variant.Name, variant.ExpectedOutcome, variant.Depth, variant.BackgroundElectronsPerSecond, cloudScenario = variant.CloudScenario?.ScenarioId },
            renderedTruthStars = rendered.Count,
            reports,
            grids = mappings.Grids,
            sourceGeometry = mappings.SourceGeometry,
            sourceCoverage = mappings.SourceCoverage,
            mappingCoverage = mappings.MappingCoverage,
            roundTrips = mappings.RoundTrips,
            cases,
            cells,
            unsupported,
            negativeControl,
            failures
        }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        Assert.AreEqual(partitions.Length * plan.Where(p => p.Refusal is null).Sum(p => p.Views) * 3, reports.Count, "Every supported camera/derived-phase case must run.");
        Assert.AreEqual(partitions.Length * plan.Where(p => p.Refusal is not null).Sum(p => p.Refusal!.Stage == "initialize" ? 1 : 3), unsupported.Count,
            "Every declared refusal, and only a declared refusal, must be recorded.");
        Assert.AreEqual(mapCases.Count * 25, mappings.SourceGeometry.Count, "Every accepted blind/warm source requires its own grid.");
        Assert.AreEqual(mapCases.Count, mappings.SourceCoverage.Count);
        var targets = mapCases.Sum(source => mapCases.Count(target => target.Id[..2] == source.Id[..2] && target.Id.EndsWith("-2", StringComparison.Ordinal)));
        Assert.AreEqual(targets * 25, mappings.Grids.Count, "Every source maps to every later warm view in its season.");
        Assert.AreEqual(targets, mappings.MappingCoverage.Count);
        Assert.AreEqual(mapCases.Count + targets, mappings.RoundTrips.Count);
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Runs one render stage. Only the refusal predeclared for this cell and stage, matched by exact type and message, is
    /// recorded as unsupported; any other exception propagates, and a declared refusal that does not occur is a failure.
    /// </summary>
    private static async Task<T?> RenderStage<T>(Func<Task<T>> stage, VirtualRenderRefusal? refusal, string stageName,
        string cell, int? index, List<object> unsupported, List<string> failures) where T : class
    {
        T result;
        try
        {
            result = await stage().ConfigureAwait(false);
        }
        catch (Exception ex) when (refusal is { } declared && declared.Stage == stageName && declared.Matches(ex))
        {
            unsupported.Add(new { cell, index, stage = stageName, type = ex.GetType().FullName, ex.Message });
            return null;
        }
        if (refusal?.Stage == stageName)
        {
            failures.Add($"{cell}: declared {stageName} refusal did not occur");
            return null;
        }
        return result;
    }

    /// <summary>All-or-nothing cells per view: a cell is accepted only when every one of its blind and warm cases is.</summary>
    private static List<object> Cells(List<CaseOutcome> outcomes, IEnumerable<(string Name, VirtualRenderRefusal Refusal)> refused,
        VirtualLongExposureVariant variant)
    {
        var cells = new List<object>();
        foreach (var group in outcomes.GroupBy(outcome => outcome.View, StringComparer.Ordinal))
        {
            var accepted = group.Count(outcome => outcome.Disposition == "accepted");
            var failed = group.Count(outcome => outcome.Failures > 0);
            var status = failed > 0 ? "failed" : accepted == group.Count() ? "accepted" : "fail-closed";
            cells.Add(new
            {
                view = group.Key,
                status,
                expectedOutcomeMet = variant.ExpectedOutcome switch
                {
                    VirtualLongExposureVariant.Accepted => status == "accepted",
                    VirtualLongExposureVariant.Rejected => status == "fail-closed" && accepted == 0,
                    _ => status != "failed"
                },
                cases = group.Count(),
                accepted,
                failedCases = failed,
                blindCases = group.Count(outcome => outcome.Mode == AstrometricSolveMode.Blind),
                blindAccepted = group.Count(outcome => outcome.Mode == AstrometricSolveMode.Blind && outcome.Disposition == "accepted"),
                dispositions = group.GroupBy(outcome => outcome.Disposition, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal),
                exclusionCounts = group.All(outcome => outcome.ExclusionCounts is null) ? null
                    : group.SelectMany(outcome => outcome.ExclusionCounts ?? new Dictionary<string, int>()).GroupBy(entry => entry.Key, StringComparer.Ordinal)
                        .OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Sum(entry => entry.Value), StringComparer.Ordinal)
            });
        }
        foreach (var (name, refusal) in refused)
            cells.Add(new { view = name, status = "unsupported-render-refused", expectedOutcomeMet = true, refusal.Stage, refusal.ExceptionType, refusal.Message });
        return cells;
    }

    /// <summary>
    /// Every association against the rendered truth: its distance to the associated star's trail, and whether a different,
    /// nearer rendered star explains the source instead.
    /// </summary>
    private static object Sources(VirtualAstrometryReference.TruthTrail[] renderTrails, VirtualAstrometryMeasurement measured,
        AstrometricSolveResult solved, List<string> failures, string caseId)
    {
        var byId = renderTrails.ToDictionary(trail => trail.Star.Id, StringComparer.Ordinal);
        var rows = solved.Associations.Select(association =>
        {
            var detection = measured.Detections.Single(d => d.Index == association.DetectionIndex);
            var distance = byId.GetValueOrDefault(association.CatalogId)?.DistanceTo(detection.Pixel);
            var nearest = VirtualAstrometryReference.Nearest(renderTrails, detection.Pixel);
            return (Detection: detection, Distance: distance, Unmodeled: nearest?.Star.Id != association.CatalogId &&
                distance is null or > UnmodeledSourceRadiusPixels);
        }).ToArray();
        var unmodeled = rows.Count(row => row.Unmodeled);
        if (unmodeled > 0) failures.Add($"{caseId}: {unmodeled} unmodeled-source associations");
        var distances = rows.Where(row => row.Distance is not null).Select(row => row.Distance!.Value).Order().ToArray();
        return new
        {
            associations = rows.Length,
            unmodeledSourceAssociations = unmodeled,
            saturatedAssociations = rows.Count(row => row.Detection.Conditions.HasFlag(StellarSourceConditions.Saturated)),
            trailedAssociations = rows.Count(row => row.Detection.Conditions.HasFlag(StellarSourceConditions.Trailed)),
            medianTrailDistancePixels = Percentile(distances, .5),
            maximumTrailDistancePixels = distances.Length == 0 ? (double?)null : distances[^1]
        };
    }

    /// <summary>
    /// Diagnostic only: re-solves the frame bound to its exposure start instead of its midpoint, and maps start-time sky
    /// positions through the midpoint mapping. Both errors scale with the half-exposure sky rotation, which is why a
    /// consumer must bind the frame midpoint.
    /// </summary>
    private static object NegativeControl(string caseId, CameraRigConfig truth, DateTimeOffset sceneUtc, TimeSpan exposure,
        AstrometricCalibration nominal, AstrometricCatalogData catalog, VirtualAstrometryMeasurement measured, AstrometricSolveResult solved)
    {
        var frame = measured.Frame; var start = frame.ExposureStartUtc; var site = frame.Observer;
        var startBound = VirtualAstrometryFixture.Solve(measured, nominal, catalog, frame: frame with { ExposureEndUtc = start });
        var whole = VirtualAstrometryReference.Trails(truth, sceneUtc, exposure, catalog.Stars).Where(trail => !trail.Truncated).ToArray();
        var mapping = new AstrometricMapping(nominal, solved.Assessment);
        var misconverted = whole.Select(trail =>
        {
            var horizontal = CoordinateTransforms.EquatorialToHorizontal(VirtualAstrometryReference.MeanOfDate(trail.Star, start), start,
                site.LatitudeDegrees, site.LongitudeDegrees);
            var mapped = mapping.SkyToPixel(CoordinateTransforms.HorizontalToEquatorial(horizontal, frame.MidpointUtc, site.LatitudeDegrees, site.LongitudeDegrees));
            return mapped is null ? (double?)null : VirtualAstrometryReference.Distance(mapped.Value, trail.Mid!.Value);
        }).Where(error => error is not null).Select(error => error!.Value).Order().ToArray();
        var motion = whole.Select(trail => VirtualAstrometryReference.Distance(trail.Start!.Value, trail.Mid!.Value)).Order().ToArray();
        object? startBoundFit = null;
        if (startBound.Assessment.HasMeasuredMapping)
        {
            var startMapping = new AstrometricMapping(nominal, startBound.Assessment);
            var consistency = whole.Select(trail => startMapping.SkyToPixel(VirtualAstrometryReference.MeanOfDate(trail.Star, start)) is { } mapped
                ? VirtualAstrometryReference.Distance(mapped, trail.Mid!.Value) : (double?)null)
                .Where(error => error is not null).Select(error => error!.Value).Order().ToArray();
            startBoundFit = new
            {
                groundPoseErrorDegrees = VirtualAstrometryReference.GroundPoseError(truth, startBound.Assessment),
                skyPoseErrorDegrees = VirtualAstrometryReference.PoseError(truth, sceneUtc, start, startBound.Assessment),
                selfConsistencyStars = consistency.Length,
                selfConsistencyRmsPixels = consistency.Length == 0 ? (double?)null : Math.Sqrt(consistency.Sum(value => value * value) / consistency.Length),
                selfConsistencyP95Pixels = Percentile(consistency, .95)
            };
        }
        return new
        {
            caseId,
            predictedHalfExposureRotationDegrees = VirtualAstrometryReference.SkyRotationDegrees(truth, sceneUtc, start),
            midpointBound = new
            {
                groundPoseErrorDegrees = VirtualAstrometryReference.GroundPoseError(truth, solved.Assessment),
                skyPoseErrorDegrees = VirtualAstrometryReference.PoseError(truth, sceneUtc, solved.Assessment)
            },
            startBound = new { startBound.Assessment.Status, startBound.Assessment.ReasonCode, fit = startBoundFit },
            startTimeSkyThroughMidpointMapping = new
            {
                stars = misconverted.Length,
                rmsPixels = misconverted.Length == 0 ? (double?)null : Math.Sqrt(misconverted.Sum(value => value * value) / misconverted.Length),
                p95Pixels = Percentile(misconverted, .95),
                maximumPixels = misconverted.Length == 0 ? (double?)null : misconverted[^1],
                truthHalfExposureMotionMedianPixels = Percentile(motion, .5),
                truthHalfExposureMotionMaximumPixels = motion.Length == 0 ? (double?)null : motion[^1]
            }
        };
    }

    /// <summary>Nearest-rank percentile of an ascending sample; null when empty.</summary>
    private static double? Percentile(double[] sorted, double fraction) =>
        sorted.Length == 0 ? null : sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Length) - 1)];

    private static void CheckIncompatiblePriors(AstrometricCalibration nominal, AstrometricCatalogData catalog,
        VirtualAstrometryMeasurement measured, AstrometricFrameAssessment prior)
    {
        var wrongCalibration = new AstrometricCalibration(nominal.Projection, "incompatible-calibration", nominal.ReadoutIdentitySha256);
        Assert.AreEqual("warm-context-incompatible", VirtualAstrometryFixture.Solve(measured, wrongCalibration, catalog, prior).Assessment.ReasonCode);
        var wrongCatalog = new AstrometricCatalogData(catalog.Metadata with { Version = "incompatible-package" },
            catalog.Stars, true, 5, provenance: catalog.Provenance);
        Assert.AreEqual("warm-context-incompatible", VirtualAstrometryFixture.Solve(measured, nominal, wrongCatalog, prior).Assessment.ReasonCode);
        var detections = measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux)).ToArray();
        var frame = measured.Frame;
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
        VirtualAstrometryMeasurement measured, AstrometricSolveResult expected, DateTimeOffset utc)
    {
        var repeated = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
        await using var lifetime = repeated.ConfigureAwait(false);
        await repeated.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
        var capture = await repeated.CaptureAsync(Request(utc), CancellationToken.None).ConfigureAwait(false);
        var input = VirtualAstrometryPixels.FromCapture(capture.Frame!);
        CollectionAssert.AreEqual(original.Payload.ToArray(), input.Payload.ToArray());
        var remeasured = VirtualAstrometryFixture.Measure(input, nominal, measured.Measurer);
        Assert.AreEqual(measured.Frame, remeasured.Frame);
        var actual = VirtualAstrometryFixture.Solve(remeasured, nominal, catalog);
        Assert.AreEqual(expected.Assessment.IdentitySha256, actual.Assessment.IdentitySha256);
        CollectionAssert.AreEqual(AstrometricEvidenceJson.Serialize(expected.Assessment), AstrometricEvidenceJson.Serialize(actual.Assessment));
    }

    private sealed record MapCase(string Id, CameraRigConfig Truth, DateTimeOffset SceneUtc,
        AstrometricCalibration Nominal, AstrometricFrameAssessment Assessment);

    private sealed record MappingEvidence(List<object> Grids, List<object> SourceGeometry,
        List<object> SourceCoverage, List<object> MappingCoverage, List<object> RoundTrips);

    /// <summary>Numerical round-trip invariant: mapping a pixel out and back must reproduce it to this many pixels.</summary>
    internal const double RoundTripTolerancePixels = 1e-6;

    private static MappingEvidence ScoreMappings(List<MapCase> cases, List<string> failures)
    {
        var rows = new List<object>();
        var geometry = new List<object>();
        var sourceCoverage = new List<object>();
        var mappingCoverage = new List<object>();
        var roundTrips = new List<object>();
        void RoundTrip(string source, string? destination, int points, int excluded, double maximum)
        {
            if (maximum > RoundTripTolerancePixels)
                failures.Add($"{source}{(destination is null ? "" : "->" + destination)} round trip error {maximum:R}px exceeds {RoundTripTolerancePixels:R}px");
            roundTrips.Add(new { source, destination, points, excluded, maximumErrorPixels = maximum });
        }
        foreach (var source in cases)
        {
            var sourceMapping = new AstrometricMapping(source.Nominal, source.Assessment);
            var samples = new List<(PixelPoint Pixel, VirtualAstrometryReference.Vector? Ray)>();
            var supportedSourcePoints = 0;
            var sameFramePoints = 0; var sameFrameMaximum = 0d;
            foreach (var fy in new[] { .05, .2, .5, .8, .95 })
                foreach (var fx in new[] { .05, .2, .5, .8, .95 })
                {
                    var pixel = new PixelPoint(fx * source.Nominal.Projection.WidthPixels, fy * source.Nominal.Projection.HeightPixels);
                    var ray = VirtualAstrometryReference.Unproject(source.Truth, pixel);
                    samples.Add((pixel, ray));
                    if (ray is not null) supportedSourcePoints++;
                    var sky = sourceMapping.PixelToSky(pixel);
                    if ((sky is null) != (ray is null)) failures.Add($"{source.Id} pixel-to-sky support mismatch at {pixel}");
                    if (sky is not null)
                    {
                        // Same-frame inverse: the sky point the mapping reports must project back onto its own pixel.
                        sameFramePoints++;
                        if (sourceMapping.SkyToPixel(sky.EquatorialMeanOfDate) is { } back)
                            sameFrameMaximum = Math.Max(sameFrameMaximum, VirtualAstrometryReference.Distance(back, pixel));
                        else failures.Add($"{source.Id} same-frame round trip lost {pixel}");
                    }
                    var expectedRay = ray is null ? (VirtualAstrometryReference.Vector?)null
                        : VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.FromEnu(ray.Value,
                            source.SceneUtc, VirtualAstrometryFixture.Observer), source.Assessment.Frame.MidpointUtc, VirtualAstrometryFixture.Observer);
                    double? rayError = null;
                    if (expectedRay is not null && sky is not null)
                    {
                        var a = VirtualAstrometryReference.Radians(sky.Horizontal.AltitudeDegrees);
                        var z = VirtualAstrometryReference.Radians(sky.Horizontal.AzimuthDegrees);
                        var fitted = new VirtualAstrometryReference.Vector(Math.Cos(a) * Math.Sin(z), Math.Cos(a) * Math.Cos(z), Math.Sin(a));
                        rayError = VirtualAstrometryReference.SeparationDegrees(expectedRay.Value, fitted);
                        if (rayError > .06) failures.Add($"{source.Id} independent inverse ray error {rayError:R}deg");
                    }
                    double? ReferenceScale(PixelPoint p, PixelPoint q)
                    {
                        var first = VirtualAstrometryReference.Unproject(source.Truth, p);
                        var second = VirtualAstrometryReference.Unproject(source.Truth, q);
                        return first is null || second is null ? null : VirtualAstrometryReference.SeparationDegrees(first.Value, second.Value) * 3600;
                    }
                    var sx = ReferenceScale(new(pixel.X - .5, pixel.Y), new(pixel.X + .5, pixel.Y));
                    var sy = ReferenceScale(new(pixel.X, pixel.Y - .5), new(pixel.X, pixel.Y + .5));
                    var scale = sourceMapping.LocalPixelScale(pixel);
                    var errorX = sx is null || scale.XArcsecondsPerPixel is null ? (double?)null : Math.Abs(scale.XArcsecondsPerPixel.Value / sx.Value - 1);
                    var errorY = sy is null || scale.YArcsecondsPerPixel is null ? (double?)null : Math.Abs(scale.YArcsecondsPerPixel.Value / sy.Value - 1);
                    if ((sx is null) != (scale.XArcsecondsPerPixel is null) || errorX > .002 ||
                        (sy is null) != (scale.YArcsecondsPerPixel is null) || errorY > .002)
                        failures.Add($"{source.Id} independent local pixel scale mismatch at {pixel}");
                    geometry.Add(new
                    {
                        source = source.Id,
                        source.Assessment.Mode,
                        source.Assessment.IdentitySha256,
                        pixel,
                        expectedRayAtMidpoint = expectedRay,
                        actualSky = sky,
                        rayErrorDegrees = rayError,
                        expectedScaleXArcsecondsPerPixel = sx,
                        expectedScaleYArcsecondsPerPixel = sy,
                        actualScaleXArcsecondsPerPixel = scale.XArcsecondsPerPixel,
                        actualScaleYArcsecondsPerPixel = scale.YArcsecondsPerPixel,
                        scaleXRelativeError = errorX,
                        scaleYRelativeError = errorY
                    });
                }
            // A rectangular aperture fills the frame, so its unsupported inverse points are explicit probes just outside the readout.
            var circular = VirtualAstrometryReference.IsCircular(source.Truth);
            var width = source.Nominal.Projection.WidthPixels; var height = source.Nominal.Projection.HeightPixels;
            var outsideFrameProbes = circular ? Array.Empty<PixelPoint>() : new PixelPoint[] { new(-.5, height / 2d), new(width + .5, height / 2d), new(width / 2d, -.5), new(width / 2d, height + .5) };
            foreach (var probe in outsideFrameProbes)
                if (VirtualAstrometryReference.Unproject(source.Truth, probe) is not null || sourceMapping.PixelToSky(probe) is not null)
                    failures.Add($"{source.Id} outside-frame inverse probe {probe} was not rejected");
            RoundTrip(source.Id, null, sameFramePoints, samples.Count - sameFramePoints, sameFrameMaximum);
            sourceCoverage.Add(new
            {
                source = source.Id,
                source.Assessment.Mode,
                points = samples.Count,
                supportedPoints = supportedSourcePoints,
                unsupportedPoints = samples.Count - supportedSourcePoints,
                outsideFrameProbes = outsideFrameProbes.Length
            });
            if (circular ? supportedSourcePoints == 0 || supportedSourcePoints == samples.Count : supportedSourcePoints != samples.Count)
                failures.Add($"{source.Id} grid must exercise supported and unsupported inverse/scale points");
            foreach (var target in cases.Where(item => item.Id[..2] == source.Id[..2] && item.Id.EndsWith("-2", StringComparison.Ordinal)))
            {
                var targetMapping = new AstrometricMapping(target.Nominal, target.Assessment);
                var supportedMappings = 0;
                var crossFramePoints = 0; var crossFrameMaximum = 0d;
                foreach (var (pixel, ray) in samples)
                {
                    var destinationRay = ray is null ? (VirtualAstrometryReference.Vector?)null : VirtualAstrometryReference.ToEnu(
                        VirtualAstrometryReference.FromEnu(ray.Value, source.SceneUtc, VirtualAstrometryFixture.Observer), target.SceneUtc, VirtualAstrometryFixture.Observer);
                    var expected = ray is { Z: >= 0 } && destinationRay is { Z: >= 0 }
                        ? VirtualAstrometryReference.Project(target.Truth, destinationRay.Value) : null;
                    if (expected is not null) supportedMappings++;
                    var actual = sourceMapping.MapPixelTo(pixel, targetMapping);
                    var residual = actual is null || expected is null ? (double?)null : VirtualAstrometryReference.Distance(actual.Value, expected.Value);
                    if ((actual is null) != (expected is null) || residual > .75)
                        failures.Add($"{source.Id}->{target.Id} independent grid mapping failed at {pixel}: expected {expected}, actual {actual}, residual {residual:R}");
                    if (actual is not null)
                    {
                        // Cross-frame inverse: mapping the destination pixel back must recover the source pixel.
                        crossFramePoints++;
                        if (targetMapping.MapPixelTo(actual.Value, sourceMapping) is { } back)
                            crossFrameMaximum = Math.Max(crossFrameMaximum, VirtualAstrometryReference.Distance(back, pixel));
                        else failures.Add($"{source.Id}->{target.Id} cross-frame round trip lost {pixel}");
                    }
                    rows.Add(new { source = source.Id, destination = target.Id, pixel, expected, actual, residualPixels = residual });
                }
                RoundTrip(source.Id, target.Id, crossFramePoints, samples.Count - crossFramePoints, crossFrameMaximum);
                mappingCoverage.Add(new
                {
                    source = source.Id,
                    destination = target.Id,
                    points = samples.Count,
                    supportedMappings,
                    unsupportedMappings = samples.Count - supportedMappings
                });
                // Overlapping rectangular views can legitimately map every grid point; circular views always clip some.
                if (supportedMappings == 0 || circular && supportedMappings == samples.Count)
                    failures.Add($"{source.Id}->{target.Id} grid must exercise supported and unsupported mappings");
            }
        }
        return new(rows, geometry, sourceCoverage, mappingCoverage, roundTrips);
    }

    internal static CatalogSnapshotResult Snapshot()
    {
        var root = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_CATALOG_ROOT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "Approved full catalog installation is mandatory.");
        var snapshot = CatalogSnapshotResolver.Resolve(new(root, "hyg-v42-production"));
        Assert.AreEqual(119625L, snapshot.RowCount);
        Assert.AreEqual("B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2", snapshot.DatabaseSha256);
        return snapshot;
    }
    internal static CaptureRequest Request(DateTimeOffset utc) => VirtualAstrometryFixture.Request(utc, VirtualAstrometryFixture.Exposure);
    internal static void RequireRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        Assert.IsTrue(revision is { Length: 40 } && revision.All(Uri.IsHexDigit), "Final evidence requires the exact committed source SHA.");
        var built = typeof(VirtualAstrometryQualificationTests).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.IsTrue(built.Contains(revision, StringComparison.OrdinalIgnoreCase), $"Binary revision {built} does not contain requested source {revision}.");
    }
}
