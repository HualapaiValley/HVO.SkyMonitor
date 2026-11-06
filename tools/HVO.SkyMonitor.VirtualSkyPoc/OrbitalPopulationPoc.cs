using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.VirtualSkyPoc;

internal static partial class Program
{
    private static readonly int[] ResourceDurationsSeconds = [5, 20, 100];
    // Component engineering harness only: canonical local facts, never a provider parser or acquisition adapter.
    private static void OrbitalPopulationPoc(string[] args)
    {
        if (args.Length != 10)
            throw new ArgumentException("Usage: satellite-resources NEW_OUTPUT SETTINGS MANIFEST RECORDS EOP LEAPS UTC_START W1_SETTINGS W2_SETTINGS");
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new ArgumentException("Use a new output directory; retained evidence is immutable.");
        var start = DateTimeOffset.Parse(args[7], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (start.Offset != TimeSpan.Zero) throw new ArgumentException("An explicit UTC start is required.");
        Directory.CreateDirectory(output);
        WriteIdentity(output);
        using var process = Process.GetCurrentProcess();
        var preparation = new List<object>();
        var inputPaths = new[] { args[2], args[3], args[4], args[5], args[6], args[8], args[9] };
        var limits = new[] { 4 * 1024 * 1024, OrbitalContract.MaximumManifestBytes, OrbitalContract.MaximumPayloadBytes,
            8 * 1024 * 1024, 64 * 1024, 4 * 1024 * 1024, 4 * 1024 * 1024 };
        var inputs = ResourceMeasure(process, () => inputPaths.Select((path, i) => ReadOrbitalPocInput(path, limits[i])).ToArray());
        preparation.Add(new { stage = "bounded-local-input-read", resources = inputs.Resources });
        var parsed = ResourceMeasure(process, () => OrbitalSnapshot.Parse(inputs.Value[1], inputs.Value[2]));
        preparation.Add(new { stage = "validatedParse-and-object-index", resources = parsed.Resources });
        var snapshot = parsed.Value.Snapshot ?? throw new ArgumentException($"Invalid canonical snapshot: {parsed.Value.Reason}");
        var resolved = ResourceMeasure(process, () => OrbitalTimeData.FromFinals2000A(inputs.Value[3], inputs.Value[4]));
        preparation.Add(new { stage = "resolved-local-EOP-and-leaps", resources = resolved.Resources });
        var configured = ResourceMeasure(process, () => new[] { inputs.Value[0], inputs.Value[5], inputs.Value[6] }
            .Select(bytes => JsonSerializer.Deserialize<CameraModuleConfig>(bytes, Json)
                ?? throw new ArgumentException("Missing camera settings.")).ToArray());
        preparation.Add(new { stage = "three-rig-settings-deserialization", resources = configured.Resources });
        var setup = ResourceMeasure(process, () =>
        {
            var config = configured.Value[0];
            var observer = new ObserverLocation(config.Observatory.LatitudeDegrees, config.Observatory.LongitudeDegrees, config.Observatory.ElevationMeters);
            var policy = new OrbitalSelectionPolicy(snapshot.Manifest.Sources.Select(static s => new OrbitalSourcePriority(s.DatasetKey, 0)));
            return (Observer: observer, Policy: policy, Model: OrbitalPredictionModel.Create(resolved.Value, assumeEllipsoidalHeight: true),
                Engine: new OrbitalPredictionEngine(resolved.Value), Geometries: configured.Value.Select(ResourceGeometry).ToArray());
        });
        preparation.Add(new { stage = "policy-model-engine-and-three-geometries", resources = setup.Resources });
        var context = setup.Value;
        var populationBounds = new OrbitalQueryBounds(snapshot.ObjectIdentities.Count, 5, 150000, 256, 1, 20000, 1500, 1, .25);
        var singleBounds = new OrbitalQueryBounds(1, 100, 150000, 1, 1, 20000, 1500, 1, .25);
        var requestSetup = ResourceMeasure(process, () =>
        {
            var scenarios = new List<(string Name, OrbitalExposureQuery Query, bool RequireComplete)>();
            scenarios.Add(("population-supplied-5s", Query(context.Geometries[0], snapshot.ObjectIdentities, 5, populationBounds), false));
            foreach (var (name, identity) in new[] { ("iss", "25544"), ("goes18", "51850") })
            {
                var objectIdentity = new OrbitalObjectIdentity("norad", identity);
                if (snapshot.RecordsFor(objectIdentity).Count == 0) throw new ArgumentException($"Required reference object {identity} is absent.");
                foreach (var seconds in ResourceDurationsSeconds)
                    scenarios.Add(($"{name}-supplied-{seconds}s", Query(context.Geometries[0], [objectIdentity], seconds, singleBounds), true));
            }
            scenarios.Add(("population-W1-5s", Query(context.Geometries[1], snapshot.ObjectIdentities, 5, populationBounds), false));
            scenarios.Add(("population-W2-5s", Query(context.Geometries[2], snapshot.ObjectIdentities, 5, populationBounds), false));
            return scenarios;
        });
        preparation.Add(new { stage = "nine-immutable-query-constructions", resources = requestSetup.Resources });
        var scenarios = requestSetup.Value;
        WriteJson(Path.Combine(output, "workload.json"), new
        {
            schema = "orbital-component-resource-workload-v2",
            start,
            populationBounds,
            singleBounds,
            snapshot.Manifest,
            context.Observer,
            context.Model,
            policy = context.Policy.IdentitySha256,
            geometries = context.Geometries,
            scenarioOrder = scenarios.Select(s => new { s.Name, s.RequireComplete, requested = s.Query.Request.Objects.Count, s.Query.Request.Interval }),
            repetitions = 2,
            warmupOperations = 0,
            concurrency = 1,
            initialBacklog = 0,
            arrivals = 0,
            rasterAllocation = false,
            forcedCollections = false,
            measurementBoundary = "PredictExposureAsync through returned validated/frozen result; query creation, I/O, serialization and static scans excluded",
            order = "Preparation then first population pair; one-object pairs; W1/W2 population pairs; lookup and full static selection scan last"
        });
        WriteJson(Path.Combine(output, "preparation.json"), preparation);
        var measurements = new List<object>();
        var comparisons = new List<object>();
        var acceptanceFailures = new List<string>();
        foreach (var scenario in scenarios)
        {
            var completeProducts = new Dictionary<OrbitalObjectIdentity, string>();
            var seenProducts = new HashSet<OrbitalObjectIdentity>();
            for (var repetition = 0; repetition < 2; repetition++)
            {
                var measured = ResourceMeasure(process, () =>
                {
                    var pending = context.Engine.PredictExposureAsync(scenario.Query, CancellationToken.None);
                    return pending.IsCompleted ? pending.GetAwaiter().GetResult() : pending.AsTask().GetAwaiter().GetResult();
                });
                var result = measured.Value;
                var prefix = $"{scenario.Name}-{repetition}";
                WriteJson(Path.Combine(output, $"{prefix}-result.json"), result);
                var productHashes = new List<object>();
                var commonComplete = 0;
                var changedComplete = 0;
                foreach (var track in result.Products)
                {
                    var bytes = OrbitalPredictionJson.SerializeTrack(track);
                    var file = $"{prefix}-norad-{track.Lineage.ObjectIdentity.CatalogNumber}.json";
                    File.WriteAllBytes(Path.Combine(output, file), bytes);
                    var hash = SatelliteHash(bytes);
                    productHashes.Add(new { file, canonicalFileSha256 = hash, track.ProductSha256, track.Status, track.Reason, track.Lineage.ObjectIdentity });
                    if (track.Status != OrbitalExecutionStatus.Complete) continue;
                    if (repetition == 0) completeProducts.Add(track.Lineage.ObjectIdentity, hash);
                    else if (completeProducts.TryGetValue(track.Lineage.ObjectIdentity, out var original))
                    {
                        commonComplete++;
                        if (original != hash) changedComplete++;
                    }
                    if (repetition == 1) seenProducts.Add(track.Lineage.ObjectIdentity);
                }
                var failureIds = result.Failures.Select(f => f.ObjectIdentity).ToHashSet();
                var productIds = result.Products.Select(t => t.Lineage.ObjectIdentity).ToHashSet();
                var work = result.WorkDiagnostics ?? throw new InvalidOperationException("Engine omitted actual work diagnostics.");
                if (work.RequestedObjectSlots != result.RequestedObjects || work.VisitedObjectSlots != result.EvaluatedObjects ||
                    work.InitializationCoreCalls + work.PointPropagationCoreCalls != result.PropagationEvaluations ||
                    work.PointPropagationSuccesses + work.PointPropagationFailures != work.PointPropagationCoreCalls)
                    throw new InvalidOperationException("Actual work counters disagree with the query result.");
                // Successful no-product outcomes are resolved exclusions; explicit failures are not exclusions.
                var geometricExclusions = scenario.Query.Request.Objects.Take(result.EvaluatedObjects)
                    .Count(id => !failureIds.Contains(id) && !productIds.Contains(id));
                measurements.Add(new
                {
                    scenario = scenario.Name,
                    repetition,
                    resources = measured.Resources,
                    result.Status,
                    result.Reason,
                    result.RequestedObjects,
                    result.EvaluatedObjects,
                    result.PropagationEvaluations,
                    result.WorkDiagnostics,
                    enginePrefinalElapsedMilliseconds = result.ElapsedMilliseconds,
                    visitedObjects = result.EvaluatedObjects,
                    unvisitedObjects = scenario.Query.Request.Objects.Count - result.EvaluatedObjects,
                    actualSelectedAmongVisited = work.SelectedObjectSlots,
                    actualPolicyWithheld = result.Failures.Count(f => f.Reason == OrbitalPredictionReason.SelectionWithheld),
                    actualNumericalDomainRejected = work.StaticSupportRejectedObjects,
                    actualInitializationAttemptedObjects = work.InitializationAttemptedObjects,
                    actualInitializationCoreCalls = work.InitializationCoreCalls,
                    actualPointPropagatedObjects = work.PointPropagationAttemptedObjects,
                    actualPointPropagationCoreCalls = work.PointPropagationCoreCalls,
                    actualPointPropagationSuccesses = work.PointPropagationSuccesses,
                    actualPointPropagationFailures = work.PointPropagationFailures,
                    pointPropagatedObjectsLowerBound = productIds.Count + geometricExclusions,
                    actualPointPropagatedObjectsExplanation = "Actual query-time SGP4 attempt objects, including failed points. Initialization is separately counted; cached points do not add core attempts. The retained product/exclusion lower bound is a cross-check only.",
                    geometricallyExcludedObjects = geometricExclusions,
                    geometricallyExcludedDerivation = "Visited prefix minus distinct failed and returned objects; engine only has a successful no-product path for resolved geometric exclusion",
                    projectedObjects = work.ProjectionAttemptedObjects,
                    projectionAttempts = work.ProjectionAttempts,
                    projectionSuccesses = work.ProjectionSuccesses,
                    projectedObjectsScope = "Actual objects with bounded, unbounded or chart IImageProjector.Project attempts. Success is native coordinates, including later excluded/cropped probes; emitted products are separate.",
                    returnedObjectsWithEmittedPixels = result.Products.Count(t => t.Samples.Any(s => s.Pixel is not null)),
                    returnedObjects = productIds.Count,
                    returnedTracks = result.Products.Count,
                    emittedSamples = result.Products.Sum(t => t.Samples.Count),
                    emittedProjectedSamples = result.Products.Sum(t => t.Samples.Count(s => s.Pixel is not null)),
                    cacheHits = (int?)null,
                    cacheHitsScope = "Not collected; cached reuse does not increment actual core-call counters.",
                    failureGroups = result.Failures.GroupBy(f => new { f.Reason, f.Detail }).Select(g => new { g.Key, count = g.Count() }),
                    propagationEvaluationsPerSecond = result.PropagationEvaluations * 1000 / measured.Resources.ElapsedMilliseconds,
                    visitedObjectsPerSecond = result.EvaluatedObjects * 1000 / measured.Resources.ElapsedMilliseconds,
                    productHashes
                });
                WriteJson(Path.Combine(output, "measurements.json"), measurements);
                if (scenario.RequireComplete && (result.Status != OrbitalExecutionStatus.Complete || result.Products.Count != 1))
                    acceptanceFailures.Add($"{prefix}: expected one complete track, got {result.Status}/{result.Reason}/{result.Products.Count}");
                if (repetition == 1)
                {
                    comparisons.Add(new
                    {
                        scenario = scenario.Name,
                        commonComplete,
                        changedComplete,
                        firstCompleteProducts = completeProducts.Count,
                        repeatCompleteProducts = seenProducts.Count,
                        firstCompleteAbsentFromRepeat = completeProducts.Keys.Count(id => !seenProducts.Contains(id)),
                        partialPrefixEqualityRequired = false
                    });
                    if (changedComplete != 0) acceptanceFailures.Add($"{scenario.Name}: {changedComplete} complete products changed");
                }
                Console.WriteLine($"{prefix}: {result.Status}/{result.Reason}; visited={result.EvaluatedObjects}/{result.RequestedObjects}; evaluations={result.PropagationEvaluations}; tracks={result.Products.Count}; method={measured.Resources.ElapsedMilliseconds:F3}ms; allocated={measured.Resources.AllocatedBytes}");
            }
        }
        var lookups = ResourceMeasure(process, () => snapshot.ObjectIdentities.Sum(id => snapshot.RecordsFor(id).Count));
        preparation.Add(new { stage = "post-query-full-population-index-lookup", recordsFound = lookups.Value, resources = lookups.Resources });
        var scan = ResourceMeasure(process, () => ResourceSelectionScan(snapshot, context.Policy, scenarios[0].Query.Request.Interval, context.Observer));
        preparation.Add(new { stage = "post-query-static-selection-and-domain-scan", resources = scan.Resources });
        WriteJson(Path.Combine(output, "selection-scan.json"), scan.Value);
        WriteJson(Path.Combine(output, "preparation.json"), preparation);
        WriteJson(Path.Combine(output, "comparisons.json"), comparisons);
        WriteJson(Path.Combine(output, "environment.json"), new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            serverGC = System.Runtime.GCSettings.IsServerGC,
            latencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
            stopwatchFrequency = Stopwatch.Frequency,
            processId = Environment.ProcessId,
            inputs = inputPaths.Select((path, i) => new { path = Path.GetFullPath(path), bytes = inputs.Value[i].Length, sha256 = SatelliteHash(inputs.Value[i]) }),
            limitations = "Research-mapped canonical snapshot, not #528 production data. First/repeat order and JIT/GC are confounded; no OS-cache cold assertion. Process CPU/allocation include runtime background work. Working set is boundary-sampled and peak is process lifetime, not per-stage peak. No raster/full-host/cadence/#1395 qualification.",
            acceptanceFailures
        });
        if (acceptanceFailures.Count > 0) throw new InvalidOperationException(string.Join("; ", acceptanceFailures));

        OrbitalExposureQuery Query(OrbitalGeometryFacts geometry, IEnumerable<OrbitalObjectIdentity> objects, double seconds, OrbitalQueryBounds bounds)
        {
            var interval = new OrbitalUtcInterval(start, start.AddSeconds(seconds));
            var timing = new OrbitalTimingFacts(interval, OrbitalTimingBasis.Estimated, null, OrbitalTimingBasis.Unknown,
                "component-resource-logical-window-not-sensor-actual", AstrometricClockFacts.NotSupplied);
            return new(new(OrbitalPredictionUse.Diagnostic, snapshot, context.Policy, interval, objects, context.Observer, context.Model, bounds), geometry, timing);
        }
    }

    private static OrbitalGeometryFacts ResourceGeometry(CameraModuleConfig config)
    {
        var readout = config.Rig.Readout is { } configuredReadout ? SensorReadoutResolver.Resolve(config.Rig.Sensor, configuredReadout).Geometry :
            new FrameReadoutDescriptor(config.Rig.Sensor.WidthPixels, config.Rig.Sensor.HeightPixels, 0, 0,
                config.Rig.Sensor.WidthPixels, config.Rig.Sensor.HeightPixels, 1, 1, FrameBinningAlgorithm.IdentityV1,
                config.Rig.Sensor.ColorMode == SensorColorMode.Color ? 0 : null, config.Rig.Sensor.ColorMode == SensorColorMode.Color ? 0 : null);
        var readoutHash = CaptureContractJson.ComputeCanonicalJsonSha256(readout);
        var projection = RigProjectionContextFactory.Create(config.Rig);
        var calibration = new AstrometricCalibration(projection, config.Rig.Optics.CalibrationVersion, readoutHash);
        return new(OrbitalGeometryBasis.ConfiguredRig, RigProjectionContextFactory.CreateProfileHashSha256(config.Rig).ToUpperInvariant(),
            readoutHash, readout, calibration.IdentitySha256.ToUpperInvariant(), null, null, OrbitalProjectionCoordinates.ReadoutView,
            projection, ProjectedSceneImageTransformV1.Identity(projection.WidthPixels, projection.HeightPixels), HorizonPolicy.GeometricHorizon);
    }

    private static object ResourceSelectionScan(OrbitalSnapshot snapshot, OrbitalSelectionPolicy policy, OrbitalUtcInterval interval, ObserverLocation observer)
    {
        var selected = 0;
        var rejected = 0;
        var eligibility = new Dictionary<OrbitalSelectionEligibility, int>();
        foreach (var identity in snapshot.ObjectIdentities)
        {
            var choice = OrbitalRecordSelector.Select(snapshot, identity, interval, policy);
            eligibility[choice.Eligibility] = eligibility.GetValueOrDefault(choice.Eligibility) + 1;
            if (choice.SelectedRecord is not { } record) continue;
            selected++;
            // Descriptive mirror of the engine's static domain gate; no initializer/point propagation, EOP validation or geometric exclusion.
            var e = record.Elements;
            var n = e.MeanMotionRevolutionsPerDay * (2 * Math.PI / 86400);
            var perigee = Math.Cbrt(398600.8 / (n * n)) * (1 - e.Eccentricity) - 6378.135;
            if (e.MeanMotionRevolutionsPerDay is < .5 or > 18 || e.Eccentricity > .9 ||
                Math.Abs(e.BStarInverseEarthRadii) > .01 || perigee < 100 || observer.ElevationMeters is < -1000 or > 10000) rejected++;
        }
        return new
        {
            requestedObjects = snapshot.ObjectIdentities.Count,
            selectedObjects = selected,
            policyWithheldObjects = snapshot.ObjectIdentities.Count - selected,
            numericalDomainRejectedAmongSelected = rejected,
            staticallySupportedSelected = selected - rejected,
            eligibility,
            scope = "Full-population static scan outside frame/query timing. Static eligibility is not evidence that an object was initialized, point-propagated, projected or geometrically excluded by a bounded query."
        };
    }

    private static (T Value, ResourceSample Resources) ResourceMeasure<T>(Process process, Func<T> operation)
    {
        process.Refresh();
        var workingSet = process.WorkingSet64;
        var cpu = process.TotalProcessorTime;
        var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var timer = Stopwatch.StartNew();
        var value = operation();
        timer.Stop();
        var allocationDelta = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        var cpuDelta = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        process.Refresh();
        return (value, new(timer.Elapsed.TotalMilliseconds, cpuDelta, allocationDelta, workingSet, process.WorkingSet64,
            process.PeakWorkingSet64, Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray()));
    }

    private sealed record ResourceSample(double ElapsedMilliseconds, double CpuMilliseconds, long AllocatedBytes,
        long WorkingSetBeforeBytes, long WorkingSetAfterBytes, long ProcessLifetimePeakWorkingSetBytes, int[] CollectionsByGeneration);
}
