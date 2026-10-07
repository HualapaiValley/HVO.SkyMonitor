using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Catalog.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Issue #1167 deep HYG selection qualification on actual VirtualSky pixels. One process measures one projection family
/// (<see cref="VirtualAstrometryFixture.FamilyVariable"/>) under one variant (<see cref="VariantVariable"/>). Scoring is the
/// unchanged #1102 pixel harness; the variants, pointings and decision rule are frozen in
/// docs/validation/issue-1167-qualification-manifest.json before any measurement.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class VirtualDeepAstrometryQualificationTests
{
    internal const string VariantVariable = "HVO_DEEP_VARIANT";
    private const string Deep = AstrometricCatalogSelectionProfile.HygDeepSelectionV1Name;

    /// <summary>Render depth; a private equivalent of issue #1168's VirtualRenderDepth, which owns render depth.</summary>
    private sealed record DeepRenderDepth(double MaximumMagnitude, int MaximumResults);

    /// <summary>One frozen variant: render depth, solve depth, selection profile, and whether the frozen dense pointing replaces the base pose.</summary>
    private sealed record DeepVariant(string Name, DeepRenderDepth Render, double SolveMagnitude, string? Profile, bool Dense);

    private static readonly DeepRenderDepth QualificationDepth = new(5, 2000);
    private static readonly DeepVariant[] Variants =
    [
        new("m50", QualificationDepth, 5, null, false),
        new("m55", new(5.5, 32768), 5.5, Deep, false),
        new("m60", new(6, 32768), 6, Deep, false),
        new("d55", new(6.5, 32768), 5.5, Deep, false),
        new("d60", new(6.5, 32768), 6, Deep, false),
        new("x50", QualificationDepth, 5, null, true),
        new("x55", new(6.5, 32768), 5.5, Deep, true),
        new("x60", new(6.5, 32768), 6, Deep, true)
    ];

    /// <summary>Complete production selection sizes; any other count is a different snapshot or selection rule.</summary>
    private static readonly Dictionary<double, int> SelectionCounts = new() { [5] = 1637, [5.5] = 2865, [6] = 5070 };

    private static readonly (int Month, int Day, int Seed)[] TuningPartitions = [(2, 10, 116710)];
    private static readonly (int Month, int Day, int Seed)[] HeldOutPartitions = [(1, 15, 116711), (5, 15, 116715), (9, 15, 116719)];
    private static readonly (int X, int Y)[] DerivedCfaPhases = [(1, 0), (0, 1), (1, 1)];

    /// <summary>Densest rectilinear 6 mm field at 08:00Z with every frame corner at least 10 degrees up at roll 13; each profile keeps its roll.</summary>
    private static readonly Dictionary<(int Month, int Day), (double Altitude, double Azimuth)> DensePointings = new()
    {
        [(2, 10)] = (52, 260),
        [(1, 15)] = (52, 235),
        [(5, 15)] = (56, 70),
        [(9, 15)] = (66, 35)
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TuningDeepSelection() => RunAsync(tuning: true);

    [TestMethod]
    public Task HeldOutDeepSelection() => RunAsync(tuning: false);

    private async Task RunAsync(bool tuning)
    {
        VirtualAstrometryQualificationTests.RequireRevision();
        var variant = SelectedVariant();
        var family = VirtualAstrometryFixture.Family;
        Assert.IsTrue(!variant.Dense || family.Model == ProjectionModel.Perspective, $"{variant.Name} is declared for rectilinear families only.");
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var options = (VirtualAstrometryFixture.SolverOptions with { MaximumCatalogMagnitude = variant.SolveMagnitude }).WithCatalogSelectionProfile(variant.Profile);
        var bound = AstrometricCatalogSelectionProfile.Find(variant.Profile)?.MaximumEntries ?? AstrometricCatalogData.MaximumEntries;
        var clock = Stopwatch.StartNew();
        var catalog = await snapshot.Catalog.ReadAsync(variant.SolveMagnitude, bound).ConfigureAwait(false);
        var firstSelectionMs = clock.Elapsed.TotalMilliseconds;
        Assert.HasCount(SelectionCounts[variant.SolveMagnitude], catalog.Stars);
        Assert.IsTrue(catalog.IsCompleteForRequestedMagnitude);
        var selectionMs = new List<double>();
        for (var sample = -5; sample < 30; sample++)
        {
            clock.Restart();
            var again = await snapshot.Catalog.ReadAsync(variant.SolveMagnitude, bound).ConfigureAwait(false);
            if (sample >= 0) selectionMs.Add(clock.Elapsed.TotalMilliseconds);
            Assert.AreEqual(catalog.SelectionIdentitySha256, again.SelectionIdentitySha256);
        }
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var initialPeakWorkingSet = process.PeakWorkingSet64;
        var reports = new List<object>(); var failures = new List<string>(); var failingCases = new SortedSet<string>(StringComparer.Ordinal);
        var mapCases = new List<VirtualAstrometryQualificationTests.MapCase>(); var cold = new List<double>();
        object? sourceProbe = null;
        var partitions = tuning ? TuningPartitions : HeldOutPartitions;
        var partition = tuning ? "tuning-not-final" : "held-out";
        var viewsPerCapture = VirtualAstrometryFixture.Profiles(0).Sum(p => p.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 4 : 1);
        foreach (var (month, day, seed) in partitions)
        {
            foreach (var fixtureProfile in VirtualAstrometryFixture.Profiles(seed))
            {
                var profile = Apply(fixtureProfile, variant.Render, variant.Dense ? DensePointings[(month, day)] : null);
                var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
                await using var lifetime = module.ConfigureAwait(false);
                await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
                var priors = new Dictionary<string, AstrometricFrameAssessment>(StringComparer.Ordinal);
                for (var index = 0; index < 3; index++)
                {
                    var utc = new DateTimeOffset(2026, month, day, 8, 0, 0, TimeSpan.Zero).AddSeconds(60 * index);
                    clock.Restart();
                    var captured = await module.CaptureAsync(VirtualAstrometryQualificationTests.Request(utc), CancellationToken.None).ConfigureAwait(false);
                    var captureMs = clock.Elapsed.TotalMilliseconds;
                    var capturedFrame = captured.Frame!; var native = VirtualAstrometryPixels.FromCapture(capturedFrame);
                    var views = new List<(VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels)> { (profile, native) };
                    if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
                        foreach (var (x, y) in DerivedCfaPhases)
                            views.Add(VirtualAstrometryFixture.DeriveCfaPhase(profile, native, x, y));
                    foreach (var (view, input) in views)
                    {
                        var caseId = $"{month:D2}-{view.Name}-{index}";
                        var caseFailures = new List<string>();
                        var nominal = VirtualAstrometryFixture.NominalCalibration(view);
                        clock.Restart();
                        var measured = VirtualAstrometryFixture.Measure(input, nominal);
                        var measurementMs = clock.Elapsed.TotalMilliseconds;
                        AstrometricDetection[] detections = [.. measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux))];
                        var frame = VirtualAstrometryFixture.FrameContext(input);
                        priors.TryGetValue(view.Name, out var prior);
                        var (solved, allocatedBytes, cpuMs) = Measured(process, () => prior is null
                            ? AstrometricSolver.Solve(frame, nominal, catalog, detections, options)
                            : AstrometricSolver.Refine(frame, nominal, catalog, detections, prior, options));
                        var expectedMode = prior is null ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm;
                        if (index > 0 && prior is null) caseFailures.Add($"{caseId}: no warm prior from the previous capture");
                        if (solved.Assessment.Mode != expectedMode) caseFailures.Add($"{caseId}: expected {expectedMode} solve");
                        if (solved.Metrics.ElapsedMilliseconds > (prior is null ? 15000 : 500))
                            caseFailures.Add($"{caseId}: solve exceeded declared cold/warm time budget");
                        var sceneUtc = capturedFrame.Metadata.Scene!.SceneUtc!.Value;
                        object? score = null; var falseAssociations = 0;
                        if (!solved.Assessment.HasMeasuredMapping) caseFailures.Add($"{caseId}: {solved.Assessment.ReasonCode}: {solved.Assessment.Reason}");
                        else
                        {
                            score = VirtualAstrometryReference.Score(view.Config.Rig, sceneUtc, nominal, catalog, detections, solved, caseFailures, caseId);
                            falseAssociations = FalseAssociations(view.Config.Rig, sceneUtc, catalog, detections, solved);
                            if (falseAssociations > .01 * solved.Associations.Count)
                                caseFailures.Add($"{caseId}: {falseAssociations} of {solved.Associations.Count} associations are more than 1.5 px from their truth pixel");
                            priors[view.Name] = solved.Assessment;
                            mapCases.Add(new(caseId, view.Config.Rig, sceneUtc, nominal, solved.Assessment));
                            if (index == 1 && prior is not null && FailClosedProbe(variant, frame, nominal, catalog, detections, prior, options) is { } probeFailure)
                                caseFailures.Add($"{caseId}: {probeFailure}");
                        }
                        if (sourceProbe is null && prior is null && view.Name == "mono-native")
                        {
                            // The production read path must choose the identical selection from the settings alone.
                            var viaSource = await AstrometricSolver.SolveAsync(frame, nominal, snapshot.Catalog, detections, options).ConfigureAwait(false);
                            sourceProbe = new { caseId, viaSource.Assessment.IdentitySha256, matches = viaSource.Assessment.IdentitySha256 == solved.Assessment.IdentitySha256 };
                            if (viaSource.Assessment.IdentitySha256 != solved.Assessment.IdentitySha256)
                                caseFailures.Add($"{caseId}: SolveAsync selected a different catalog or settings identity");
                        }
                        if (!tuning && index == 0 && view.Name is "mono-native" or "cfa-native" &&
                            await Reproduce(profile, snapshot.Catalog, input, nominal, catalog, options, solved, utc).ConfigureAwait(false) is { } reproduction)
                            caseFailures.Add($"{caseId}: {reproduction}");
                        var isolation = prior is null && variant.SolveMagnitude == 6
                            ? IsolationTiming(view.Config.Rig, sceneUtc, catalog, nominal.Projection.WidthPixels, nominal.Projection.HeightPixels, solved, caseFailures, caseId)
                            : null;
                        var indexPrefix = family.Model == ProjectionModel.Perspective && prior is null
                            ? IndexPrefixBound(catalog, frame.MidpointUtc, solved.Metrics.IndexStars) : null;
                        if (indexPrefix is { BoundBytes: > 16L * 1024 * 1024 }) caseFailures.Add($"{caseId}: IndexPrefix bound {indexPrefix.BoundBytes} bytes exceeds 16 MiB");
                        reports.Add(new
                        {
                            caseId,
                            view.Name,
                            index,
                            utc,
                            sceneUtc,
                            captureMs,
                            measurementMs,
                            solveAllocatedBytes = allocatedBytes,
                            solveProcessCpuMs = cpuMs,
                            indexPrefix,
                            renderQueryCeiling = capturedFrame.Metadata.Extra?.GetValueOrDefault("stellarQueryCeiling"),
                            renderAdmitted = capturedFrame.Metadata.Extra?.GetValueOrDefault("stellarAdmittedCount"),
                            detected = measured.Detections.Count,
                            measured.CandidateCount,
                            detectionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(detections),
                            payloadSha256 = Convert.ToHexString(SHA256.HashData(input.Payload.Span)),
                            orientation = view.Config.Rig.Orientation,
                            solved.Assessment,
                            solved.Metrics,
                            associationCount = solved.Associations.Count,
                            falseAssociations,
                            score,
                            isolation,
                            configHash = CaptureContractJson.ComputeCanonicalJsonSha256(profile.Config),
                            nominal = new { nominal.IdentitySha256, nominal.CalibrationVersion, nominal.ReadoutIdentitySha256 },
                            failures = caseFailures
                        });
                        if (index == 0) cold.Add(solved.Metrics.ElapsedMilliseconds);
                        if (caseFailures.Count > 0) failingCases.Add(caseId);
                        failures.AddRange(caseFailures);
                        TestContext.WriteLine($"{caseId}: {solved.Assessment.ReasonCode}, {measured.Detections.Count} detected, {solved.Metrics.ElapsedMilliseconds:F2}ms solve");
                    }
                }
            }
        }
        var mappingFailures = new List<string>();
        var mappings = VirtualAstrometryQualificationTests.ScoreMappings(mapCases, mappingFailures);
        // A mapping failure is charged to its source case and, for a cross-frame grid, to its destination as well.
        foreach (var failure in mappingFailures)
            foreach (var id in failure.Split(' ', 2)[0].Split("->"))
                failingCases.Add(id.TrimEnd(':'));
        failures.AddRange(mappingFailures);
        process.Refresh();
        var path = Path.Combine(TestContext.TestRunDirectory!, "virtual-deep-astrometry.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-deep-astrometry-v1",
            partition,
            projectionFamily = family.Name,
            variant = new { variant.Name, render = variant.Render, variant.SolveMagnitude, variant.Profile, variant.Dense },
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            host = Environment.MachineName,
            processors = Environment.ProcessorCount,
            runtime = Environment.Version.ToString(),
            snapshot.DatabaseSha256,
            snapshot.RowCount,
            catalog.IdentitySha256,
            catalog.SelectionIdentitySha256,
            selection = new { stars = catalog.Stars.Count, bound, firstReadMs = firstSelectionMs, warmup = 5, measured = selectionMs.Count, p95Ms = Percentile95(selectionMs), samplesMs = selectionMs },
            settingsIdentitySha256 = options.IdentitySha256,
            measurer = "StellarDetector v1 through VirtualAstrometryFixture.Measure, as the #1102 pixel harness",
            falseAssociationRule = "an association whose catalog star's truth pixel is more than 1.5 px from its detection",
            sourceProbe,
            process = new
            {
                initialPeakWorkingSetBytes = initialPeakWorkingSet,
                peakWorkingSetBytes = process.PeakWorkingSet64,
                workingSetBytes = process.WorkingSet64,
                meaning = "Process working set and cumulative process peak of this one family/variant process"
            },
            coldSolves = cold.Count,
            coldP95Ms = cold.Count == 0 ? (double?)null : Percentile95(cold),
            reports,
            grids = mappings.Grids.Count,
            sourceGeometry = mappings.SourceGeometry.Count,
            sourceCoverage = mappings.SourceCoverage,
            mappingCoverage = mappings.MappingCoverage,
            mappingFailures,
            failingCases,
            failures
        }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        Assert.AreEqual(partitions.Length * viewsPerCapture * 3, reports.Count, "Every supported camera/derived-phase case must run.");
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Evaluation only: the complete magnitude-7 HYG sky (15,598 rows) through the identical bounded blind search, by the
    /// internal evaluation overload. No declared profile reaches this depth and nothing here is advertised.
    /// </summary>
    [TestMethod]
    public async Task Magnitude7Evaluation()
    {
        VirtualAstrometryQualificationTests.RequireRevision();
        var snapshot = VirtualAstrometryQualificationTests.Snapshot();
        var stars = snapshot.Catalog.Query(new CatalogQuery(7, 100000));
        Assert.HasCount(15598, stars);
        var family = VirtualAstrometryFixture.Family;
        var defaults = new AstrometricSolverOptions();
        var search = new CoreSolverOptions(defaults.MinimumFocalScale, defaults.MaximumFocalScale, defaults.FocalScaleStep,
            defaults.TriangleDetectionCount, defaults.ImageTriangleLimit, defaults.HypothesisLimit, defaults.CandidateLimit, 7);
        using var process = Process.GetCurrentProcess();
        var reports = new List<object>();
        var utc = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        foreach (var fixtureProfile in VirtualAstrometryFixture.Profiles(116771))
        {
            var profile = Apply(fixtureProfile, new(7, 32768), null);
            var module = new VirtualSkyCameraModule(TimeProvider.System, snapshot.Catalog, new ProjectedSceneStore());
            await using var lifetime = module.ConfigureAwait(false);
            await module.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
            var captured = await module.CaptureAsync(VirtualAstrometryQualificationTests.Request(utc), CancellationToken.None).ConfigureAwait(false);
            var capturedFrame = captured.Frame!; var native = VirtualAstrometryPixels.FromCapture(capturedFrame);
            var views = new List<(VirtualAstrometryProfile Profile, VirtualAstrometryPixels Pixels)> { (profile, native) };
            if (native.Layout.PixelFormat == CameraPixelFormat.BayerRggb16)
                foreach (var (x, y) in DerivedCfaPhases)
                    views.Add(VirtualAstrometryFixture.DeriveCfaPhase(profile, native, x, y));
            foreach (var (view, input) in views)
            {
                var caseId = $"01-{view.Name}-0";
                var nominal = VirtualAstrometryFixture.NominalCalibration(view);
                var measured = VirtualAstrometryFixture.Measure(input, nominal);
                // Exactly the solver's mapping: index order, flux as peak, the frame's observer and midpoint.
                CoreDetection[] detections = [.. measured.Detections.OrderBy(d => d.Index).Select(d => new CoreDetection(d.Index, d.Pixel.X, d.Pixel.Y, d.Flux, d.Flux))];
                var frame = VirtualAstrometryFixture.FrameContext(input);
                var sceneUtc = capturedFrame.Metadata.Scene!.SceneUtc!.Value;
                var timedOut = false;
                var site = new CoreSite(frame.Observer.LatitudeDegrees, frame.Observer.LongitudeDegrees);
                var (core, allocatedBytes, cpuMs) = Measured<CoreResult?>(process, () =>
                {
                    try
                    {
                        return AstrometricSolverCore.Solve(detections, stars, SolverOptics.From(nominal.Projection), site, frame.MidpointUtc,
                            search, new AstrometricWorkControl(defaults.ColdBudgetMilliseconds, CancellationToken.None), stars.Count);
                    }
                    catch (TimeoutException)
                    {
                        timedOut = true;
                        return null;
                    }
                });
                double? poseError = null, focalError = null; var falseAssociations = 0;
                if (core is { Accepted: true, Solution: { } solution })
                {
                    var truth = VirtualAstrometryReference.Pose(view.Config.Rig);
                    var fitted = VirtualAstrometryReference.Pose(solution.BoresightAltitude, solution.BoresightAzimuth, solution.Roll, view.Config.Rig.Optics.HorizontalFlip);
                    VirtualAstrometryReference.Vector Transport(VirtualAstrometryReference.Vector value) => VirtualAstrometryReference.ToEnu(
                        VirtualAstrometryReference.FromEnu(value, sceneUtc, VirtualAstrometryFixture.Observer), frame.MidpointUtc, VirtualAstrometryFixture.Observer);
                    poseError = new[] { VirtualAstrometryReference.SeparationDegrees(Transport(truth.Right), fitted.Right),
                        VirtualAstrometryReference.SeparationDegrees(Transport(truth.Up), fitted.Up),
                        VirtualAstrometryReference.SeparationDegrees(Transport(truth.Forward), fitted.Forward) }.Max();
                    focalError = Math.Abs(nominal.Projection.FocalLengthXPixels * core.FocalScale!.Value /
                        (VirtualAstrometryReference.NativeFocal(view.Config.Rig) / view.Config.Rig.Readout!.BinX) - 1);
                    var byId = stars.ToDictionary(s => s.Id, StringComparer.Ordinal);
                    falseAssociations = core.Associations.Count(a => !TruthMatches(view.Config.Rig, sceneUtc, byId[a.CatalogId],
                        measured.Detections.Single(d => d.Index == a.DetectionIndex).Pixel));
                }
                process.Refresh();
                reports.Add(new
                {
                    caseId,
                    view.Name,
                    detected = measured.Detections.Count,
                    renderQueryCeiling = capturedFrame.Metadata.Extra?.GetValueOrDefault("stellarQueryCeiling"),
                    renderAdmitted = capturedFrame.Metadata.Extra?.GetValueOrDefault("stellarAdmittedCount"),
                    timedOut,
                    accepted = core?.Accepted,
                    status = core?.Status,
                    reason = core?.Reason,
                    elapsedMs = core?.ElapsedMilliseconds,
                    solveAllocatedBytes = allocatedBytes,
                    solveProcessCpuMs = cpuMs,
                    indexStars = core?.CatalogIndexStars,
                    catalogTriangles = core?.CatalogTriangles,
                    imageTriangles = core?.ImageTriangles,
                    hypotheses = core?.Hypotheses,
                    distinctCandidates = core?.DistinctCandidates,
                    budgetExhausted = core?.BudgetExhausted,
                    fittingStars = core?.Quality?.FittingStars,
                    verificationStars = core?.Quality?.VerificationStars,
                    expectedFittingStars = core?.Quality?.ExpectedFittingStars,
                    exceedsEvidenceBound = core?.Quality is { } q && AstrometricEvidenceJson.ExceedsEvidenceBound(q.ExpectedFittingStars, q.FittingStars, q.VerificationStars),
                    associations = core?.Associations.Length,
                    falseAssociations,
                    poseErrorDegrees = poseError,
                    focalRelativeError = focalError,
                    processWorkingSetBytes = process.WorkingSet64,
                    processPeakWorkingSetBytes = process.PeakWorkingSet64
                });
                TestContext.WriteLine($"{caseId}: {(timedOut ? "time-budget" : core!.Reason)}");
            }
        }
        var path = Path.Combine(TestContext.TestRunDirectory!, "virtual-deep-astrometry-mag7.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schema = "virtual-deep-astrometry-mag7-v1",
            disposition = "evaluation only; never advertised; no declared profile admits this selection",
            projectionFamily = family.Name,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            host = Environment.MachineName,
            snapshot.DatabaseSha256,
            selection = new { maximumMagnitude = 7, rows = stars.Count, route = "ICelestialCatalog.Query then the internal evaluation overload" },
            render = new DeepRenderDepth(7, 32768),
            seed = 116771,
            utc,
            budgetMs = defaults.ColdBudgetMilliseconds,
            reports
        }, VirtualAstrometryFixture.JsonOptions)).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        Assert.AreEqual(VirtualAstrometryFixture.Profiles(0).Sum(p => p.Config.Rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? 4 : 1), reports.Count);
    }

    private static DeepVariant SelectedVariant()
    {
        var name = Environment.GetEnvironmentVariable(VariantVariable);
        return Variants.SingleOrDefault(v => v.Name == name) ?? throw new InvalidOperationException($"{VariantVariable} must name a declared variant, not '{name}'.");
    }

    /// <summary>Rebuilds the module options with the variant's render depth and optionally replaces the pose, keeping each profile's roll.</summary>
    private static VirtualAstrometryProfile Apply(VirtualAstrometryProfile profile, DeepRenderDepth render, (double Altitude, double Azimuth)? pointing)
    {
        var config = profile.Config;
        if (render != QualificationDepth)
        {
            var options = JsonNode.Parse(config.Module.Options!.Value.GetRawText())!.AsObject();
            Assert.IsTrue(options.ContainsKey("maximumMagnitude") && options.ContainsKey("maximumResults"), "The fixture must declare its render depth.");
            options["maximumMagnitude"] = render.MaximumMagnitude;
            options["maximumResults"] = render.MaximumResults;
            config = config with { Module = config.Module with { Options = JsonSerializer.SerializeToElement(options, VirtualAstrometryFixture.JsonOptions) } };
        }
        if (pointing is { } p)
            config = config with { Rig = config.Rig with { Orientation = new(p.Altitude, p.Azimuth, config.Rig.Orientation.RollAdjustmentDegrees) } };
        return new(profile.Name, config);
    }

    private static (T Result, long AllocatedBytes, double ProcessCpuMs) Measured<T>(Process process, Func<T> solve)
    {
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var result = solve();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        process.Refresh();
        return (result, allocated, (process.TotalProcessorTime - cpu).TotalMilliseconds);
    }

    /// <summary>A deep variant without its profile, and the legacy variant with it, must fail closed on the same frame.</summary>
    private static string? FailClosedProbe(DeepVariant variant, AstrometricFrameContext frame, AstrometricCalibration nominal,
        AstrometricCatalogData catalog, AstrometricDetection[] detections, AstrometricFrameAssessment prior, AstrometricSolverOptions options)
    {
        var (probe, expected) = variant.Profile is null
            ? (options.WithCatalogSelectionProfile(Deep), "warm-context-incompatible")
            : (options.WithCatalogSelectionProfile(null), "catalog-selection-unsupported");
        var result = AstrometricSolver.Refine(frame, nominal, catalog, detections, prior, probe);
        return result.Assessment.ReasonCode == expected && !result.Assessment.HasMeasuredMapping ? null
            : $"fail-closed probe expected {expected}, got {result.Assessment.ReasonCode}";
    }

    private static async Task<string?> Reproduce(VirtualAstrometryProfile profile, SqliteCelestialCatalog source,
        VirtualAstrometryPixels original, AstrometricCalibration nominal, AstrometricCatalogData catalog, AstrometricSolverOptions options,
        AstrometricSolveResult expected, DateTimeOffset utc)
    {
        var repeated = new VirtualSkyCameraModule(TimeProvider.System, source, new ProjectedSceneStore());
        await using var lifetime = repeated.ConfigureAwait(false);
        await repeated.InitializeAsync(profile.Config, CancellationToken.None).ConfigureAwait(false);
        var capture = await repeated.CaptureAsync(VirtualAstrometryQualificationTests.Request(utc), CancellationToken.None).ConfigureAwait(false);
        var input = VirtualAstrometryPixels.FromCapture(capture.Frame!);
        if (!original.Payload.Span.SequenceEqual(input.Payload.Span)) return "re-capture changed the payload";
        var measured = VirtualAstrometryFixture.Measure(input, nominal);
        var actual = AstrometricSolver.Solve(VirtualAstrometryFixture.FrameContext(input), nominal, catalog,
            [.. measured.Detections.Select(d => new AstrometricDetection(d.Index, d.Pixel, d.Flux))], options);
        return actual.Assessment.IdentitySha256 == expected.Assessment.IdentitySha256 ? null : "re-solve changed the assessment identity";
    }

    private static int FalseAssociations(CameraRigConfig truth, DateTimeOffset sceneUtc, AstrometricCatalogData catalog,
        AstrometricDetection[] detections, AstrometricSolveResult solved)
    {
        var stars = catalog.Stars.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var pixels = detections.ToDictionary(d => d.Index);
        return solved.Associations.Count(a => !TruthMatches(truth, sceneUtc, stars[a.CatalogId], pixels[a.DetectionIndex].Pixel));
    }

    private static bool TruthMatches(CameraRigConfig truth, DateTimeOffset sceneUtc, CelestialCatalogObject star, PixelPoint detection) =>
        VirtualAstrometryReference.Project(truth, VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.J2000(star), sceneUtc,
            VirtualAstrometryFixture.Observer)) is { } pixel && VirtualAstrometryReference.Distance(pixel, detection) <= 1.5;

    /// <summary>
    /// Evaluate-grid evidence for the coordinator's conditional decision: the shipped grid and solver v2's pairwise rule on
    /// this frame's truth-projected selection, each the median of five runs, scaled by the number of evaluated candidates.
    /// </summary>
    private static object IsolationTiming(CameraRigConfig truth, DateTimeOffset sceneUtc, AstrometricCatalogData catalog, int width, int height,
        AstrometricSolveResult solved, List<string> failures, string caseId)
    {
        // Evaluate isolates the above-horizon selection, so the timing input is that set at its truth pixels.
        (string Id, PixelPoint? Pixel)[] projected = [.. catalog.Stars
            .Select(s => (s.Id, Ray: VirtualAstrometryReference.ToEnu(VirtualAstrometryReference.J2000(s), sceneUtc, VirtualAstrometryFixture.Observer)))
            .Where(s => s.Ray.Z > 0).Select(s => (s.Id, VirtualAstrometryReference.Project(truth, s.Ray)))];
        static double Median(Func<HashSet<string>> run, out HashSet<string> result)
        {
            var samples = new double[5]; result = [];
            for (var i = 0; i < samples.Length; i++)
            {
                var clock = Stopwatch.StartNew(); result = run(); samples[i] = clock.Elapsed.TotalMilliseconds;
            }
            return samples.Order().ElementAt(2);
        }
        var gridMs = Median(() => AstrometricSolverCore.IsolatedIds(projected, width, height), out var grid);
        var pairwiseMs = Median(() => PairwiseIsolatedIds(projected, width, height), out var pairwise);
        if (!grid.SetEquals(pairwise)) failures.Add($"{caseId}: isolation grid disagrees with the pairwise rule");
        var candidates = solved.Metrics.DistinctCandidates; var coldMs = solved.Metrics.ElapsedMilliseconds;
        return new
        {
            projectedStars = projected.Count(p => p.Pixel is not null),
            isolated = grid.Count,
            gridMs,
            pairwiseMs,
            distinctCandidates = candidates,
            coldMs,
            estimatedGridShare = coldMs > 0 ? gridMs * candidates / coldMs : (double?)null,
            estimatedPairwiseShare = coldMs > 0 ? pairwiseMs * candidates / coldMs : (double?)null
        };
    }

    /// <summary>Evaluate's isolation exactly as solver v2 shipped it, the oracle the Astronomy tests also keep.</summary>
    private static HashSet<string> PairwiseIsolatedIds((string Id, PixelPoint? Pixel)[] stars, int width, int height)
    {
        var projected = stars.Where(s => s.Pixel is { } p && p.X > 6 && p.Y > 6 && p.X < width - 6 && p.Y < height - 6).ToArray();
        return projected.Where(s => projected.All(t => t.Id == s.Id || VirtualAstrometryReference.Distance(t.Pixel!.Value, s.Pixel!.Value) > 12))
            .Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
    }

    private sealed record IndexPrefixEvidence(int TrainingStars, int AdmittedIndexStars, long BoundBytes);

    /// <summary>
    /// Upper bound on IndexPrefix's live arrays for a perspective solve. Its rays are every above-horizon training star, and
    /// it holds at most 1,501 triangular rows of doubles: their payload and array headers, the row table and the ray array.
    /// </summary>
    private static IndexPrefixEvidence IndexPrefixBound(AstrometricCatalogData catalog, DateTimeOffset midpointUtc, int admitted)
    {
        var site = new CoreSite(VirtualAstrometryFixture.Observer.LatitudeDegrees, VirtualAstrometryFixture.Observer.LongitudeDegrees);
        var training = catalog.Stars.Count(s => !AstrometricSolverCore.IsVerification(s.Id) &&
            CameraBasis.FromHorizontal(AstrometricMath.Horizontal(s, midpointUtc, site)).Up > 0);
        long rows = Math.Min(training, 1501);
        return new(training, admitted, 8 * rows * (rows - 1) / 2 + 24 * rows + 8L * training + 24L * training + 48);
    }

    private static double Percentile95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(.95 * values.Count) - 1);
}
