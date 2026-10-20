using System.Text.Json;
using HVO.SkyMonitor.Astronomy.Orbital.Propagation;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPassArcEngineTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FullOffRigArcRetainsOriginalRecordWhenNarrowingWouldSelectAnotherEpoch()
    {
        var (query, time) = Query();
        var selected = query.SelectedRecord;
        var narrow = OrbitalRecordSelector.Select(query.SearchQuery.Request.Snapshot, selected.ObjectIdentity,
            query.Pass.PassInterval, query.SearchQuery.Request.SelectionPolicy).SelectedRecord!;
        Assert.AreNotEqual(selected.RecordSha256, narrow.RecordSha256);
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status, string.Join(';', result.Failures.Select(f => f.Detail)));
        var arc = result.Products.Single();
        Assert.AreEqual(selected.RecordSha256, arc.Pass.Lineage.RecordSha256);
        Assert.AreEqual(query.Pass.ProductSha256, arc.Pass.ProductSha256);
        Assert.AreEqual(query.Pass.SearchInterval, arc.Pass.SearchInterval);
        Assert.AreEqual(query.Pass.PassInterval, result.RequestedInterval);
        Assert.IsTrue(arc.Samples.All(static s => s.Pixel is null && s.Illumination == OrbitalIllumination.Unknown));
        Assert.IsNull(arc.OrbitUncertainty.Value);
        Assert.AreEqual(query.Pass.PassInterval.StartUtc, arc.Samples[arc.Anchors.StartSampleIndex!.Value].Utc);
        Assert.AreEqual(query.Pass.PeakUtc, arc.Samples[arc.Anchors.PeakSampleIndex!.Value].Utc);
        Assert.AreEqual(query.Pass.PassInterval.EndUtc, arc.Samples[arc.Anchors.EndSampleIndex!.Value].Utc);
        Assert.AreEqual(query.Pass.MaximumElevationDegrees, arc.Samples[arc.Anchors.PeakSampleIndex.Value].Horizontal.AltitudeDegrees, 1e-8);
        Assert.IsTrue(result.PropagationEvaluations > arc.Samples.Count);
        Assert.AreEqual(1, result.RequestedObjects);
        var geometry = OrbitalContractExamples.Geometry with
        {
            HorizonPolicy = HorizonPolicy.ProjectionOnly,
            Projection = OrbitalContractExamples.Geometry.Projection with
            {
                Model = ProjectionModel.Perspective,
                BoresightAltitudeDegrees = -90,
                FocalLengthXPixels = 10000,
                FocalLengthYPixels = 10000,
                Aperture = ProjectionAperture.Rectangular,
                ImageCircleRadiusPixels = null
            }
        };
        Assert.IsTrue(arc.Samples.All(s => new OrbitalSampleProjector(geometry).Project(s.Utc,
            new(s.Horizontal, s.RangeKilometers!.Value, s.RangeRateKilometersPerSecond!.Value,
                s.AngularSpeedDegreesPerSecond!.Value, CameraBasis.FromHorizontal(s.Horizontal))).Sample.Pixel is null));
    }

    [TestMethod]
    public async Task InitializationAndEveryUncachedCoreCallConsumeTheArcBudget()
    {
        var (query, time) = Query();
        for (var limit = 1; limit <= 4; limit++)
        {
            var q = new OrbitalPassArcQuery(query.SearchQuery, query.Pass, query.Bounds with { MaximumEvaluations = limit });
            var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(q, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(limit, result.PropagationEvaluations);
            Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, result.Reason);
            Assert.AreEqual(limit == 4 ? OrbitalExecutionStatus.Partial : OrbitalExecutionStatus.Unavailable, result.Status);
            if (limit == 4) Assert.AreEqual(1, result.Products.Single().Samples.Count);
        }
    }

    [TestMethod]
    public async Task SampleLimitPreservesUsefulPrefixAndPartialParentCannotBecomeComplete()
    {
        var (query, time) = Query();
        var small = new OrbitalPassArcQuery(query.SearchQuery, query.Pass, query.Bounds with { MaximumSamples = 2 });
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(small, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Status); Assert.AreEqual(OrbitalPredictionReason.SampleBudget, result.Reason);
        var arc = result.Products.Single(); Assert.AreEqual(2, arc.Samples.Count);
        Assert.AreEqual(new OrbitalSkyArcSegmentV1(0, 2), arc.Segments.Single());
        Assert.IsNull(arc.Anchors.EndSampleIndex);
        var pass = OrbitalPredictionJson.CreatePass(query.Pass with
        {
            Status = OrbitalExecutionStatus.Partial,
            Reason = OrbitalPredictionReason.EvaluationBudget,
            EndBoundary = OrbitalPassBoundary.WorkLimit
        });
        result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(new(query.SearchQuery, pass, query.Bounds),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Status); Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, result.Reason);
        Assert.AreEqual(query.Pass.PassInterval, result.Products.Single().CoveredInterval);
        Assert.AreEqual(OrbitalPassBoundary.WorkLimit, result.Products.Single().Pass.EndBoundary);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnrepresentableTimeStepAndUnresolvedAngularFloorReturnExplicitPartialPrefix(bool subTickStep)
    {
        var (query, time) = Query();
        var bounds = subTickStep ? query.Bounds with { MaximumSampleStepSeconds = .00000005 } :
            query.Bounds with { MaximumAngularErrorDegrees = 1e-20 };
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(new(query.SearchQuery, query.Pass, bounds),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Status);
        Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, result.Reason);
        Assert.AreEqual(1, result.Products.Single().Samples.Count);
        Assert.IsNull(result.Products.Single().Anchors.EndSampleIndex);
        Assert.IsLessThanOrEqualTo(300, result.PropagationEvaluations);
    }

    [TestMethod]
    public async Task UnknownNominalTimeWrongResolvedInputsAndPeakMismatchAreExplicitlyUnavailable()
    {
        var (query, time) = Query();
        var nominal = OrbitalTimeData.CreateNominalZeroDiagnostic(OrbitalCoordinateTestData.Leaps);
        var r = query.SearchQuery.Request; var model = OrbitalPredictionModel.Create(nominal);
        var search = new OrbitalPassQuery(new(r.Use, r.Snapshot, r.SelectionPolicy, r.Interval, r.Objects, r.Observer, model, r.Bounds), 0);
        var pass = OrbitalPredictionJson.CreatePass(query.Pass with { Model = model });
        var result = await new OrbitalPredictionEngine(nominal).PredictPassArcAsync(new(search, pass, query.Bounds), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.MissingEarthOrientation, result.Reason); Assert.AreEqual(0, result.PropagationEvaluations);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        result = await new OrbitalPredictionEngine(nominal).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        pass = OrbitalPredictionJson.CreatePass(query.Pass with { MaximumElevationDegrees = query.Pass.MaximumElevationDegrees - .001 });
        result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(new(query.SearchQuery, pass, query.Bounds), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, result.Reason);
        Assert.AreEqual("arc-parent-peak-altitude-mismatch", result.Failures.Single().Detail);
        Assert.AreEqual(0, result.ReturnedProducts);
    }

    [TestMethod]
    public async Task SingleInstantDeduplicatesAllAnchorsAndReusesThePeakEvaluation()
    {
        var (query, time) = Query(); var instant = query.Pass.PeakUtc;
        var pass = OrbitalPredictionJson.CreatePass(query.Pass with { PassInterval = new(instant, instant) });
        var q = new OrbitalPassArcQuery(query.SearchQuery, pass, query.Bounds with { MaximumEvaluations = 3, MaximumSamples = 1 });
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(q, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        Assert.AreEqual(3, result.PropagationEvaluations); var arc = result.Products.Single();
        Assert.AreEqual(1, arc.Samples.Count); Assert.AreEqual(new OrbitalPassArcAnchorsV1(0, 0, 0), arc.Anchors);
        Assert.AreEqual(0, arc.Sampling.AchievedMaximumStepSeconds); Assert.AreEqual(0, arc.Sampling.AchievedEstimatedAngularErrorDegrees);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task WindowClippedPeakAtEitherEndpointRetainsExactDeduplicatedAnchors(bool peakAtStart)
    {
        var (original, time) = Query(); var old = original.SearchQuery.Request;
        var snapshot = OrbitalContractExamples.Snapshot(original.SelectedRecord);
        var peak = original.Pass.PeakUtc;
        var interval = peakAtStart ? new OrbitalUtcInterval(peak, peak.AddSeconds(2)) : new(peak.AddSeconds(-2), peak);
        var request = new OrbitalPredictionRequest(old.Use, snapshot, old.SelectionPolicy, interval, snapshot.ObjectIdentities,
            old.Observer, old.Model, old.Bounds);
        var pass = OrbitalPredictionJson.CreatePass(original.Pass with
        {
            SearchInterval = interval,
            PassInterval = interval,
            StartBoundary = OrbitalPassBoundary.SearchWindow,
            EndBoundary = OrbitalPassBoundary.SearchWindow,
            Lineage = original.Pass.Lineage with
            {
                SnapshotManifestSha256 = snapshot.Manifest.ManifestSha256,
                NormalizedPayloadSha256 = snapshot.Manifest.NormalizedPayloadSha256,
                StartEpochAgeHours = (interval.StartUtc - original.SelectedRecord.EpochUtc).TotalHours,
                EndEpochAgeHours = (interval.EndUtc - original.SelectedRecord.EpochUtc).TotalHours
            }
        });
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(new(new(request, 0), pass, original.Bounds),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        var arc = result.Products.Single();
        Assert.AreEqual(peakAtStart ? arc.Anchors.StartSampleIndex : arc.Anchors.EndSampleIndex, arc.Anchors.PeakSampleIndex);
        Assert.AreEqual(interval, arc.CoveredInterval);
        Assert.AreEqual(OrbitalPassBoundary.SearchWindow, arc.Pass.StartBoundary);
        Assert.AreEqual(OrbitalPassBoundary.SearchWindow, arc.Pass.EndBoundary);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ResolvedEopAndLeapArtifactIdentityCannotBeReplacedByFactsWithMatchingCoverage(bool replaceEop)
    {
        var (original, time) = Query(); var r = original.SearchQuery.Request;
        var model = replaceEop ? r.Model with { EarthOrientationSha256 = new('A', 64) } : r.Model with { LeapSecondsSha256 = new('A', 64) };
        var request = new OrbitalPredictionRequest(r.Use, r.Snapshot, r.SelectionPolicy, r.Interval, r.Objects, r.Observer, model, r.Bounds);
        var pass = OrbitalPredictionJson.CreatePass(original.Pass with { Model = model });
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(new(new(request, 0), pass, original.Bounds),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        Assert.AreEqual(0, result.PropagationEvaluations); Assert.AreEqual(0, result.ReturnedProducts);
        StringAssert.Contains(result.Failures.Single().Detail, "resolved-time-model-mismatch", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task CancellationIsCheckedBeforeInitializationAndDuringFinalFreezing()
    {
        var (query, time) = Query(); using var canceled = new CancellationTokenSource(); await canceled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new OrbitalPredictionEngine(time)
            .PredictPassArcAsync(query, canceled.Token).AsTask()).ConfigureAwait(false);
        var clock = new CountingClock();
        var completed = await new OrbitalPredictionEngine(time, clock).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, completed.Status);
        using var during = new CancellationTokenSource();
        var cancelClock = new CountingClock { OnRead = reads => { if (reads >= clock.Reads - 2) during.Cancel(); } };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new OrbitalPredictionEngine(time, cancelClock)
            .PredictPassArcAsync(query, during.Token).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DeadlineDuringResultPreparationCannotReturnComplete()
    {
        var (query, time) = Query(); var clock = new CountingClock();
        _ = await new OrbitalPredictionEngine(time, clock).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        var deadline = new CountingClock { DeadlineAt = clock.Reads - 3 };
        var result = await new OrbitalPredictionEngine(time, deadline).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Status); Assert.AreEqual(OrbitalPredictionReason.Deadline, result.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Products.Single().Status);
    }

    internal static (OrbitalPassArcQuery Query, OrbitalTimeData Time) Query()
    {
        using var json = JsonDocument.Parse(OrbitalCoordinateTestData.Bytes("sampling", "fast-directions.json"));
        var item = json.RootElement.GetProperty("cases")[0]; var first = OrbitalPredictionTestData.Record(item.GetProperty("facts"));
        var second = OrbitalContract.CreateRecord(first with { EpochUtc = first.EpochUtc.AddHours(1) });
        var snapshot = OrbitalContractExamples.Snapshot(first, second); var policy = OrbitalContractExamples.Policy();
        var time = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A"); var model = OrbitalPredictionModel.Create(time);
        var searchInterval = new OrbitalUtcInterval(first.EpochUtc.AddHours(-1), first.EpochUtc.AddHours(3));
        var peakUtc = first.EpochUtc.AddMinutes(3);
        Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(second, out var propagator, out _));
        Assert.IsTrue(propagator!.TryPropagate(peakUtc, out var teme, out _));
        var fixedState = OrbitalCoordinateConverter.ToEarthFixed(teme!, time.Evaluate(peakUtc)); var p = fixedState.PositionKilometers;
        var observer = new ObserverLocation(Math.Atan2(p.Z, double.Hypot(p.X, p.Y)) * (180 / Math.PI),
            Math.Atan2(p.Y, p.X) * (180 / Math.PI), 50);
        var request = new OrbitalPredictionRequest(OrbitalPredictionUse.Diagnostic, snapshot, policy, searchInterval,
            snapshot.ObjectIdentities, observer, model, OrbitalContractExamples.Bounds);
        var budget = new OrbitalWorkBudget(request.Bounds, default);
        var evaluator = OrbitalObjectEvaluator.Create(request, second, time, budget, out _, out _)!;
        var peak = evaluator.Evaluate(peakUtc)!;
        var lineage = new OrbitalProductLineage(snapshot.Manifest.ManifestSha256, snapshot.Manifest.NormalizedPayloadSha256,
            second.RecordSha256, second.ObjectIdentity, second.SourceDataset, policy.IdentitySha256, second.EpochUtc,
            (searchInterval.StartUtc - second.EpochUtc).TotalHours, (searchInterval.EndUtc - second.EpochUtc).TotalHours,
            OrbitalElementFreshness.Fresh, OrbitalSourceValidity.Unknown, snapshot.Manifest.Coverage);
        var pass = OrbitalPredictionJson.CreatePass(new(OrbitalPredictionJson.PassSchemaVersion, "", lineage, searchInterval,
            new(peakUtc.AddSeconds(-2), peakUtc.AddSeconds(2)), peakUtc, peak.Horizontal.AltitudeDegrees, 0,
            OrbitalPassBoundary.ResolvedCrossing, OrbitalPassBoundary.ResolvedCrossing, observer, model,
            OrbitalExecutionStatus.Complete, OrbitalPredictionReason.None));
        return (new(new(request, 0), pass, OrbitalPassArcContractTests.ExampleBounds), time);
    }

    private sealed class CountingClock : TimeProvider
    {
        internal int Reads { get; private set; }
        internal Action<int>? OnRead { get; init; }
        internal int DeadlineAt { get; init; } = int.MaxValue;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            Reads++; OnRead?.Invoke(Reads);
            return Reads >= DeadlineAt ? TimeSpan.FromMinutes(11).Ticks : 0;
        }
    }
}
