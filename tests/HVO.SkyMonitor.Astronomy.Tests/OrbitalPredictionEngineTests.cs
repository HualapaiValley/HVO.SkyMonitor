using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPredictionEngineTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(AstrometricClockSynchronization.NotSupplied)]
    [DataRow(AstrometricClockSynchronization.Unknown)]
    [DataRow(AstrometricClockSynchronization.Unsynchronized)]
    [DataRow(AstrometricClockSynchronization.Synchronized)]
    public async Task ClockUncertaintyRequiresSynchronizationAndReportedMaximum(AstrometricClockSynchronization synchronization)
    {
        var (query, time) = SimpleQuery();
        foreach (var maximum in new double?[] { .25, null })
        {
            var clock = new AstrometricClockFacts("capture-host-clock", synchronization, maximum, .1);
            var timing = query.Timing with { Clock = clock };
            var result = await new OrbitalPredictionEngine(time).PredictExposureAsync(new(query.Request, query.Geometry, timing),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
            var track = result.Products.Single();
            var expected = synchronization == AstrometricClockSynchronization.Synchronized ? maximum : null;
            Assert.AreEqual(expected, track.Uncertainty.Clock.Value);
            Assert.AreEqual(expected is null ? "clock-error-unknown; capture-host-clock" : "reported-clock-maximum-error; capture-host-clock",
                track.Uncertainty.Clock.Reason);
            Assert.AreEqual(clock, track.Timing.Clock);
        }
    }

    [TestMethod]
    public async Task NominalZeroEarthOrientationRetainsExplicitDiagnosticDegradation()
    {
        var (query, time) = SimpleQuery(OrbitalTimeData.CreateNominalZeroDiagnostic(OrbitalCoordinateTestData.Leaps));
        var result = await new OrbitalPredictionEngine(time).PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        var track = result.Products.Single();
        Assert.IsTrue(track.Model.NominalZeroEarthOrientation);
        Assert.IsNull(track.Uncertainty.Orbit.Value);
        Assert.AreEqual("physical-orbit-error-unknown; zero-eop-diagnostic-approximation", track.Uncertainty.Orbit.Reason);
    }

    [TestMethod]
    public async Task IndependentConsumersAndConcurrentCallsReproduceCanonicalProducts()
    {
        var (query, time) = SimpleQuery();
        var edge = new OrbitalPredictionEngine(time);
        var central = new OrbitalPredictionEngine(time);
        var first = await edge.PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        var other = await central.PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, first.Status);
        Assert.AreEqual(1, first.Products.Count);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(first.Products[0]), OrbitalPredictionJson.SerializeTrack(other.Products[0]));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            await edge.PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false), TestContext.CancellationToken))).ConfigureAwait(false);
        foreach (var result in results)
            CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(first.Products[0]), OrbitalPredictionJson.SerializeTrack(result.Products[0]));
        var passQuery = new OrbitalPassQuery(query.Request, 0);
        var a = await edge.FindPassesAsync(passQuery, TestContext.CancellationToken).ConfigureAwait(false);
        var b = await central.FindPassesAsync(passQuery, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, a.Status);
        CollectionAssert.AreEqual(a.Products.Select(static p => p.ProductSha256).ToArray(), b.Products.Select(static p => p.ProductSha256).ToArray());
    }

    [TestMethod]
    public async Task MissingObjectsUnsupportedModelsTimeMismatchAndInitializationLimitsStayExplicit()
    {
        var (query, time) = SimpleQuery();
        var engine = new OrbitalPredictionEngine(time);
        OrbitalExposureQuery Changed(OrbitalPredictionRequest request) => new(request, query.Geometry, query.Timing);
        OrbitalPredictionRequest Request(OrbitalPredictionModelFacts? model = null, OrbitalQueryBounds? bounds = null,
            OrbitalObjectIdentity[]? objects = null) => new(OrbitalPredictionUse.Diagnostic, query.Request.Snapshot,
                query.Request.SelectionPolicy, query.Request.Interval, objects ?? query.Request.Objects.ToArray(), query.Request.Observer,
                model ?? query.Request.Model, bounds ?? query.Request.Bounds);
        var missing = await engine.PredictExposureAsync(Changed(Request(objects: [new("norad", "999999999")])), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, missing.Status); Assert.AreEqual(OrbitalPredictionReason.ObjectMissing, missing.Reason);
        var model = query.Request.Model with { Propagator = new("unrecognized", "1", new('A', 64)) };
        var unsupported = await engine.PredictExposureAsync(Changed(Request(model)), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, unsupported.Reason); Assert.AreEqual(0, unsupported.PropagationEvaluations);
        var wrongTime = await engine.PredictExposureAsync(Changed(Request(query.Request.Model with { EarthOrientationSha256 = new('A', 64) })), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.MissingEarthOrientation, wrongTime.Reason); Assert.AreEqual(0, wrongTime.PropagationEvaluations);
        var bounded = await engine.PredictExposureAsync(Changed(Request(bounds: query.Request.Bounds with { MaximumEvaluations = 1 })), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, bounded.Reason); Assert.AreEqual(1, bounded.PropagationEvaluations);
        Assert.AreEqual(0, bounded.ReturnedProducts);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await engine.PredictExposureAsync(query, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DeadlineReachedDuringFinalizationCannotPublishCompleteStatus()
    {
        var (query, time) = SimpleQuery();
        var baselineClock = new FinalizationClock(int.MaxValue, query.Request.Bounds.MaximumElapsedMilliseconds);
        var baseline = await new OrbitalPredictionEngine(time, baselineClock).PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, baseline.Status);
        var lateClock = new FinalizationClock(baselineClock.Calls - 1, query.Request.Bounds.MaximumElapsedMilliseconds);
        var late = await new OrbitalPredictionEngine(time, lateClock).PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, late.Status); Assert.AreEqual(OrbitalPredictionReason.Deadline, late.Reason);
        Assert.IsGreaterThan(query.Request.Bounds.MaximumElapsedMilliseconds, late.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task ExhaustedSamplesPreservePrefixAndExcludedObjectsReleaseUnusedTrackSlots()
    {
        var (query, time) = SimpleQuery();
        var r = query.Request;
        var limitedRequest = new OrbitalPredictionRequest(r.Use, r.Snapshot, r.SelectionPolicy, r.Interval, r.Objects,
            r.Observer, r.Model, r.Bounds with { MaximumSamples = 2 });
        var limited = await new OrbitalPredictionEngine(time).PredictExposureAsync(new(limitedRequest, query.Geometry, query.Timing), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, limited.Status);
        Assert.AreEqual(OrbitalPredictionReason.SampleBudget, limited.Reason);
        Assert.AreEqual(1, limited.Products.Count); Assert.AreEqual(2, limited.Products[0].Samples.Count);
        Assert.IsLessThan(r.Interval.EndUtc, limited.Products[0].CoveredInterval.EndUtc);

        var record = r.Snapshot.Records[0];
        var excluded = OrbitalContract.CreateRecord(record with
        {
            ObjectIdentity = new("norad", "1"),
            Elements = record.Elements with { MeanAnomalyDegrees = record.Elements.MeanAnomalyDegrees + 180 }
        });
        var snapshot = OrbitalContractExamples.Snapshot(record, excluded);
        var request = new OrbitalPredictionRequest(r.Use, snapshot, r.SelectionPolicy, r.Interval, snapshot.ObjectIdentities,
            r.Observer, r.Model, r.Bounds with { MaximumTracks = 1 });
        var result = await new OrbitalPredictionEngine(time).PredictExposureAsync(new(request, query.Geometry, query.Timing), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        Assert.AreEqual(2, result.EvaluatedObjects); Assert.AreEqual(1, result.Products.Count);
        Assert.AreEqual(record.ObjectIdentity, result.Products[0].Lineage.ObjectIdentity);
    }

    [TestMethod]
    [DataRow(ProjectionModel.Perspective)]
    [DataRow(ProjectionModel.EquidistantFisheye)]
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    public async Task IndependentOffPhaseDirectionsMeetReturnedChordEstimatesAcrossOptics(ProjectionModel opticalModel)
    {
        var bytes = OrbitalCoordinateTestData.Bytes("sampling", "dense-directions.json");
        Assert.AreEqual("8B9B12052455E06947AE44226151329D0480A895102CBF67025BF32210F22EBC", Convert.ToHexString(SHA256.HashData(bytes)));
        using var document = JsonDocument.Parse(bytes);
        var fastBytes = OrbitalCoordinateTestData.Bytes("sampling", "fast-directions.json");
        Assert.AreEqual("397235B677EDED5BD08D6EA9C85E5E47184D727D758B2A642C8B513B1252BDFA", Convert.ToHexString(SHA256.HashData(fastBytes)));
        using var fast = JsonDocument.Parse(fastBytes);
        double worstError = 0, worstFraction = 0;
        var checkedPoints = 0;
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray().Concat(fast.RootElement.GetProperty("cases").EnumerateArray()))
        {
            var name = item.GetProperty("name").GetString();
            var record = OrbitalPredictionTestData.Record(item.GetProperty("facts"));
            var snapshot = OrbitalContractExamples.Snapshot(record);
            var time = item.GetProperty("nominalZeroEop").GetBoolean()
                ? OrbitalTimeData.CreateNominalZeroDiagnostic(OrbitalCoordinateTestData.Leaps)
                : OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
            var start = OrbitalCoordinateTestData.Utc(item.GetProperty("startUtc").GetString()!);
            var interval = new OrbitalUtcInterval(start, start.AddSeconds(item.GetProperty("durationSeconds").GetDouble()));
            var site = item.GetProperty("observer");
            var observer = new ObserverLocation(site[0].GetDouble(), site[1].GetDouble(), site[2].GetDouble());
            var midpoint = item.GetProperty("samples").EnumerateArray().Single(s =>
                OrbitalCoordinateTestData.Utc(s.GetProperty("utc").GetString()!) == OrbitalNumericalPath.At(interval.StartUtc, interval.EndUtc, 1, 2));
            var original = OrbitalContractExamples.Geometry;
            var projection = original.Projection with
            {
                Model = opticalModel,
                BoresightAltitudeDegrees = midpoint.GetProperty("altitude").GetDouble(),
                BoresightAzimuthDegrees = midpoint.GetProperty("azimuth").GetDouble(),
                FocalLengthXPixels = opticalModel == ProjectionModel.OrthographicFisheye ? 320 : 200,
                FocalLengthYPixels = opticalModel == ProjectionModel.Perspective ? 210 : opticalModel == ProjectionModel.OrthographicFisheye ? 320 : 200,
                Aperture = opticalModel == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
                ImageCircleRadiusPixels = opticalModel == ProjectionModel.Perspective ? null : 314,
                RadialDistortionK1 = .01
            };
            var geometry = original with
            {
                Projection = projection,
                HorizonPolicy = HorizonPolicy.ProjectionOnly,
                Readout = original.Readout with { RoiX = 80, RoiY = 80, RoiWidth = 480, RoiHeight = 480, BinX = 2, BinY = 2, BinningAlgorithm = FrameBinningAlgorithm.DigitalSumV1 },
                ImageTransform = new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 640, 640,
                    80, 80, 480, 480, 2, 2, true, true, ProjectedSceneQuarterRotation.Degrees270, 240, 240)
            };
            var bounds = OrbitalContractExamples.Bounds with
            {
                MaximumWindowSeconds = 100,
                MaximumSampleStepSeconds = .37,
                MaximumChordErrorPixels = .01,
                MaximumElapsedMilliseconds = 600000,
                MaximumEvaluations = 1000000,
                MaximumSamples = 100000
            };
            var query = OrbitalPredictionTestData.Query(snapshot, time, interval, observer, geometry, bounds, stale: true);
            var result = await new OrbitalPredictionEngine(time).PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status, $"{opticalModel}/{name}: {result.Reason} {string.Join(';', result.Failures.Select(static f => f.Detail))}");
            Assert.AreEqual(1, result.Products.Count, name);
            var track = result.Products[0];
            Assert.IsLessThanOrEqualTo(.37, track.Sampling.AchievedMaximumStepSeconds);
            Assert.IsLessThanOrEqualTo(.01, track.Sampling.AchievedChordErrorPixels!.Value);
            Assert.IsNull(track.Uncertainty.Orbit.Value); Assert.IsNull(track.Uncertainty.Clock.Value);
            Assert.IsTrue(track.Samples.All(static p => p.Illumination == OrbitalIllumination.Unknown));
            var projector = new OrbitalSampleProjector(geometry);
            foreach (var point in item.GetProperty("samples").EnumerateArray())
            {
                var utc = OrbitalCoordinateTestData.Utc(point.GetProperty("utc").GetString()!);
                var d = point.GetProperty("unitEnu");
                var direction = new EnuVector(d[0].GetDouble(), d[1].GetDouble(), d[2].GetDouble());
                var actual = projector.Project(utc, new(new(point.GetProperty("altitude").GetDouble(), point.GetProperty("azimuth").GetDouble()),
                    point.GetProperty("rangeKilometers").GetDouble(), 0, 0, direction)).Sample.Pixel;
                if (actual is null) continue;
                var pair = Bracket(track, utc);
                Assert.IsNotNull(pair, $"Drawable independent point omitted: {opticalModel}/{name} {utc:O}");
                var (a, b) = pair.Value;
                var t = a.Utc == b.Utc ? 0 : (double)(utc.UtcTicks - a.Utc.UtcTicks) / (b.Utc.UtcTicks - a.Utc.UtcTicks);
                var x = a.Pixel!.Value.X * (1 - t) + b.Pixel!.Value.X * t;
                var y = a.Pixel.Value.Y * (1 - t) + b.Pixel.Value.Y * t;
                var error = double.Hypot(actual.Value.X - x, actual.Value.Y - y);
                // Independent SGP4/coordinate residual plus microsecond oracle UTC quantization.
                const double referenceAllowancePixels = 1e-5;
                Assert.IsLessThanOrEqualTo(.01 + referenceAllowancePixels, error, $"{opticalModel}/{name}/{utc:O}");
                Assert.IsLessThanOrEqualTo(track.Sampling.AchievedChordErrorPixels.Value + referenceAllowancePixels, error,
                    $"Underestimated chord error: {opticalModel}/{name}/{utc:O}");
                worstError = Math.Max(worstError, error);
                worstFraction = Math.Max(worstFraction, error / (track.Sampling.AchievedChordErrorPixels.Value + referenceAllowancePixels));
                checkedPoints++;
            }
        }
        TestContext.WriteLine($"{opticalModel}: 36 frozen calibration/held-out cases plus 2 supplemental fast passes; {checkedPoints} independent phases; worst chord={worstError:R}px; worst error/(estimate+reference allowance)={worstFraction:R}.");
        Assert.IsGreaterThan(9800, checkedPoints);
    }

    private static (OrbitalTrackSample A, OrbitalTrackSample B)? Bracket(OrbitalExposureTrackV1 track, DateTimeOffset utc)
    {
        foreach (var segment in track.Segments)
        {
            var start = segment.StartSampleIndex;
            var end = start + segment.SampleCount - 1;
            if (utc < track.Samples[start].Utc || utc > track.Samples[end].Utc) continue;
            if (start == end) return (track.Samples[start], track.Samples[end]);
            for (var i = start; i < end; i++)
                if (utc >= track.Samples[i].Utc && utc <= track.Samples[i + 1].Utc) return (track.Samples[i], track.Samples[i + 1]);
        }
        return null;
    }

    private static (OrbitalExposureQuery Query, OrbitalTimeData Time) SimpleQuery(OrbitalTimeData? inputTime = null)
    {
        var time = inputTime ?? OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        var snapshot = OrbitalContractExamples.Snapshot(OrbitalContractExamples.Record());
        var geometry = OrbitalContractExamples.Geometry with { HorizonPolicy = HorizonPolicy.ProjectionOnly };
        var interval = OrbitalContractExamples.Exposure;
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(), interval,
            snapshot.ObjectIdentities, OrbitalContractExamples.Observer, OrbitalPredictionModel.Create(time),
            OrbitalContractExamples.Bounds with { MaximumElapsedMilliseconds = 600000 });
        var budget = new OrbitalWorkBudget(request.Bounds, default);
        var evaluator = OrbitalObjectEvaluator.Create(request, snapshot.Records[0], time, budget, out _, out _)!;
        var center = evaluator.Evaluate(interval.StartUtc)!.Horizontal;
        geometry = geometry with { Projection = geometry.Projection with { BoresightAltitudeDegrees = center.AltitudeDegrees, BoresightAzimuthDegrees = center.AzimuthDegrees } };
        return (OrbitalPredictionTestData.Query(snapshot, time, interval, request.Observer, geometry), time);
    }

    private sealed class FinalizationClock(int allowedCalls, double maximumMilliseconds) : TimeProvider
    {
        internal int Calls { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ++Calls <= allowedCalls ? 0 : (long)((maximumMilliseconds + 1) * TimeSpan.TicksPerMillisecond);
    }
}

internal static class OrbitalPredictionTestData
{
    // Fixture mapping only; production acquisition/normalization belongs to #528.
    internal static OrbitalRecordV1 Record(JsonElement f)
    {
        var original = OrbitalContractExamples.Record(f.GetProperty("NORAD_CAT_ID").GetInt32().ToString(CultureInfo.InvariantCulture));
        return OrbitalContract.CreateRecord(original with
        {
            ObjectName = f.GetProperty("OBJECT_NAME").GetString(),
            EpochUtc = OrbitalCoordinateTestData.Utc(f.GetProperty("EPOCH").GetString()! + "Z"),
            Elements = new(f.GetProperty("MEAN_MOTION").GetDouble(), f.GetProperty("ECCENTRICITY").GetDouble(),
                f.GetProperty("INCLINATION").GetDouble(), f.GetProperty("RA_OF_ASC_NODE").GetDouble(),
                f.GetProperty("ARG_OF_PERICENTER").GetDouble(), f.GetProperty("MEAN_ANOMALY").GetDouble(),
                f.GetProperty("BSTAR").GetDouble(), f.GetProperty("MEAN_MOTION_DOT").GetDouble(), f.GetProperty("MEAN_MOTION_DDOT").GetDouble(),
                0, "U", null, null)
        });
    }

    internal static OrbitalExposureQuery Query(OrbitalSnapshot snapshot, OrbitalTimeData time, OrbitalUtcInterval interval,
        ObserverLocation observer, OrbitalGeometryFacts geometry, OrbitalQueryBounds? bounds = null, bool stale = false)
        => new(new(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(stale), interval, snapshot.ObjectIdentities,
            observer, OrbitalPredictionModel.Create(time), bounds ?? OrbitalContractExamples.Bounds with { MaximumElapsedMilliseconds = 600000 }),
            geometry, new(interval, OrbitalTimingBasis.Estimated, null, OrbitalTimingBasis.Unknown,
                "independent-numerical-fixture; estimated shutter", AstrometricClockFacts.NotSupplied));
}
