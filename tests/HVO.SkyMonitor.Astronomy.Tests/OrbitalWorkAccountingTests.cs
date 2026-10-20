using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalWorkAccountingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CompleteInstantCountsActualOperationsAndLegacyConstructionLeavesDiagnosticsUnknown()
    {
        var (query, time) = Exposure(instant: true);
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        var work = Work(result);
        AssertSlots(work, 1, 1, 1);
        Assert.AreEqual(1, work.InitializationAttemptedObjects); Assert.AreEqual(2, work.InitializationCoreCalls);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.PointPropagationCoreCalls);
        Assert.AreEqual(1, work.PointPropagationSuccesses); Assert.AreEqual(0, work.PointPropagationFailures);
        Assert.AreEqual(1, work.ProjectionAttemptedObjects); Assert.AreEqual(1L, work.ProjectionAttempts);
        Assert.AreEqual(1L, work.ProjectionSuccesses); Assert.AreEqual(1, result.Products.Single().Samples.Count);
        var legacy = new OrbitalPredictionResult<OrbitalExposureTrackV1>(query, result.Status, result.Reason,
            result.EvaluatedObjects, result.PropagationEvaluations, result.ElapsedMilliseconds, result.Products, result.Failures);
        Assert.IsNull(legacy.WorkDiagnostics);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(result.Products.Single()),
            OrbitalPredictionJson.SerializeTrack(legacy.Products.Single()));
        Assert.ThrowsExactly<ArgumentException>(() => new OrbitalPredictionResult<OrbitalExposureTrackV1>(query,
            result.Status, result.Reason, 0, result.PropagationEvaluations, result.ElapsedMilliseconds, result.Products, result.Failures));
    }

    [TestMethod]
    public async Task SelectionAndStaticRejectionAreVisitsWithoutInitializationOrPointWork()
    {
        var (query, time) = Exposure(instant: true);
        var unsupported = OrbitalContract.CreateRecord(OrbitalContractExamples.Record("2") with
        { Elements = OrbitalContractExamples.Record().Elements with { Eccentricity = .99 } });
        var stale = OrbitalContractExamples.Record("3", epochHours: -72);
        var snapshot = OrbitalContractExamples.Snapshot(unsupported, stale);
        query = Change(query, snapshot: snapshot, objects: [new("norad", "1"), unsupported.ObjectIdentity, stale.ObjectIdentity]);
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        var work = Work(result); AssertSlots(work, 3, 3, 1);
        Assert.AreEqual(1, work.StaticSupportRejectedObjects);
        AssertNoCoreOrProjection(work);
        CollectionAssert.AreEquivalent(new[] { OrbitalPredictionReason.ObjectMissing, OrbitalPredictionReason.PropagationFailure,
            OrbitalPredictionReason.SelectionWithheld }, result.Failures.Select(static f => f.Reason).ToArray());
    }

    [TestMethod]
    [DataRow(1, 1, 0, OrbitalExecutionStatus.Unavailable)]
    [DataRow(2, 2, 0, OrbitalExecutionStatus.Unavailable)]
    [DataRow(3, 2, 1, OrbitalExecutionStatus.Partial)]
    public async Task InitializationAndFirstPointLimitsCountOnlyCoreCallsActuallyStarted(int limit, int initialization,
        int points, OrbitalExecutionStatus status)
    {
        var (query, time) = Exposure();
        query = Change(query, bounds: query.Request.Bounds with { MaximumEvaluations = limit });
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(status, result.Status); Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, result.Reason);
        var work = Work(result); AssertSlots(work, 1, 1, 1);
        Assert.AreEqual(1, work.InitializationAttemptedObjects); Assert.AreEqual(initialization, work.InitializationCoreCalls);
        Assert.AreEqual(points, work.PointPropagationAttemptedObjects); Assert.AreEqual(points, work.PointPropagationCoreCalls);
        Assert.AreEqual(points, work.PointPropagationSuccesses); Assert.AreEqual(0, work.PointPropagationFailures);
        Assert.AreEqual(points, work.ProjectionAttemptedObjects); Assert.AreEqual((long)points, work.ProjectionAttempts);
    }

    [TestMethod]
    public async Task CoefficientRejectionBeforeInitializationDoesNotInventCoreCalls()
    {
        var (query, time) = Exposure();
        var record = OrbitalContractExamples.Record();
        record = OrbitalContract.CreateRecord(record with { Elements = record.Elements with { MeanMotionDot = double.MaxValue } });
        query = Change(query, snapshot: OrbitalContractExamples.Snapshot(record));
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        StringAssert.Contains(result.Failures.Single().Detail, "NonFiniteCoefficients", StringComparison.Ordinal);
        var work = Work(result); AssertSlots(work, 1, 1, 1);
        Assert.AreEqual(0, work.StaticSupportRejectedObjects); AssertNoCoreOrProjection(work);
    }

    [TestMethod]
    public async Task FailedPointAttemptCountsEvenWhenNoProductOrProjectionExists()
    {
        var (query, time) = Exposure(instant: true);
        var record = OrbitalContractExamples.Record();
        record = OrbitalContract.CreateRecord(record with
        {
            Elements = record.Elements with
            { MeanMotionRevolutionsPerDay = 16.2, BStarInverseEarthRadii = .01 }
        });
        var instant = record.EpochUtc.AddHours(120);
        var snapshot = OrbitalContractExamples.Snapshot(record);
        query = OrbitalPredictionTestData.Query(snapshot, time, new(instant, instant), query.Request.Observer, query.Geometry, stale: true);
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, result.Reason);
        var work = Work(result); AssertSlots(work, 1, 1, 1);
        Assert.AreEqual(1, work.InitializationAttemptedObjects); Assert.AreEqual(2, work.InitializationCoreCalls);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.PointPropagationCoreCalls);
        Assert.AreEqual(0, work.PointPropagationSuccesses); Assert.AreEqual(1, work.PointPropagationFailures);
        Assert.AreEqual(0, work.ProjectionAttemptedObjects); Assert.AreEqual(0L, work.ProjectionAttempts);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ModelOrResolvedTimeRejectionFreezesZeroVisitedWork(bool unsupportedModel)
    {
        var (query, time) = Exposure();
        var model = unsupportedModel ? query.Request.Model with { Propagator = new("unsupported", "1", new('A', 64)) } :
            query.Request.Model with { EarthOrientationSha256 = new('A', 64) };
        var result = await Predict(Change(query, model: model), time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Unavailable, result.Status);
        var work = Work(result); AssertSlots(work, 1, 0, 0); AssertNoCoreOrProjection(work);
    }

    [TestMethod]
    public async Task BudgetPrefixDoesNotCountUnvisitedPopulationOrResetPreviouslyFinishedObjects()
    {
        var (query, time) = Exposure(instant: true);
        var snapshot = OrbitalContractExamples.Snapshot(OrbitalContractExamples.Record("1"), OrbitalContractExamples.Record("2"),
            OrbitalContractExamples.Record("3"), OrbitalContractExamples.Record("4"));
        query = Change(query, snapshot: snapshot, bounds: query.Request.Bounds with { MaximumEvaluations = 5 });
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, result.Status); Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, result.Reason);
        var work = Work(result); AssertSlots(work, 4, 2, 2);
        Assert.AreEqual(2, work.InitializationAttemptedObjects); Assert.AreEqual(4, work.InitializationCoreCalls);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.PointPropagationCoreCalls);
        Assert.AreEqual(1, work.ProjectionAttemptedObjects); Assert.AreEqual(1, result.ReturnedProducts);
    }

    [TestMethod]
    public async Task BelowHorizonObjectsHavePointWorkAndNoActualImageProjection()
    {
        var (query, time) = Exposure(instant: true);
        // Existing synthetic fixture is below the geometric horizon at this site/UTC.
        query = new(query.Request, query.Geometry with { HorizonPolicy = HorizonPolicy.GeometricHorizon }, query.Timing);
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status); Assert.AreEqual(0, result.ReturnedProducts);
        var work = Work(result);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.PointPropagationSuccesses);
        Assert.AreEqual(0, work.ProjectionAttemptedObjects); Assert.AreEqual(0L, work.ProjectionAttempts);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ApertureSensorAndCropExclusionStillCountActualCalls(int clipping)
    {
        var (query, time) = Exposure(instant: true);
        var geometry = clipping == 2 ? query.Geometry with
        {
            Projection = query.Geometry.Projection with { PrincipalPointX = 800 }
        } : clipping == 1 ? query.Geometry with
        {
            Readout = query.Geometry.Readout with { RoiWidth = 100, RoiHeight = 100 },
            ImageTransform = new(ProjectedSceneImageTransformV1.CurrentSchemaVersion, 640, 640,
                0, 0, 100, 100, 1, 1, false, false, ProjectedSceneQuarterRotation.Degrees0, 100, 100)
        } : query.Geometry with
        {
            Projection = query.Geometry.Projection with
            {
                Model = ProjectionModel.Perspective,
                BoresightAltitudeDegrees = -query.Geometry.Projection.BoresightAltitudeDegrees,
                BoresightAzimuthDegrees = (query.Geometry.Projection.BoresightAzimuthDegrees + 180) % 360,
                Aperture = ProjectionAperture.Rectangular,
                ImageCircleRadiusPixels = null
            }
        };
        var result = await Predict(new(query.Request, geometry, query.Timing), time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status); Assert.AreEqual(0, result.ReturnedProducts);
        var work = Work(result);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.ProjectionAttemptedObjects);
        Assert.AreEqual(clipping == 1 ? 1L : 2L, work.ProjectionAttempts);
        Assert.AreEqual(clipping == 0 ? 0L : 1L, work.ProjectionSuccesses);
    }

    [TestMethod]
    public async Task InternalProbesAndClassificationExceedEmittedPixelsWithoutCountingObjectTwice()
    {
        var (query, time) = Exposure();
        var result = await Predict(query, time).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        var work = Work(result);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.ProjectionAttemptedObjects);
        Assert.IsGreaterThan((long)result.Products.Single().Samples.Count, work.ProjectionAttempts);
        Assert.IsGreaterThan((long)work.PointPropagationCoreCalls, work.ProjectionAttempts);
    }

    [TestMethod]
    public async Task PassSearchAndArcRemainUnprojectedAndArcCountsOnlyItsOneRequestedObject()
    {
        var (original, time) = OrbitalPassArcEngineTests.Query();
        var old = original.SearchQuery.Request;
        var snapshot = OrbitalContractExamples.Snapshot(original.SelectedRecord,
            OrbitalContract.CreateRecord(original.SelectedRecord with { ObjectIdentity = new("norad", "999999999") }));
        var interval = original.Pass.PassInterval;
        var request = new OrbitalPredictionRequest(old.Use, snapshot, old.SelectionPolicy, interval, snapshot.ObjectIdentities,
            old.Observer, old.Model, old.Bounds with { MaximumElapsedMilliseconds = 600000 });
        var search = new OrbitalPassQuery(request, 0);
        var engine = new OrbitalPredictionEngine(time);
        var passes = await engine.FindPassesAsync(search, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, passes.Status);
        var passWork = Work(passes); AssertSlots(passWork, 2, 2, 2);
        Assert.AreEqual(2, passWork.InitializationAttemptedObjects); Assert.AreEqual(4, passWork.InitializationCoreCalls);
        Assert.AreEqual(2, passWork.PointPropagationAttemptedObjects);
        Assert.AreEqual(0, passWork.ProjectionAttemptedObjects); Assert.AreEqual(0L, passWork.ProjectionAttempts);
        var pass = passes.Products.First(p => p.Lineage.ObjectIdentity == original.SelectedRecord.ObjectIdentity);
        var arc = await engine.PredictPassArcAsync(new(search, pass, original.Bounds), TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, arc.Status);
        var arcWork = Work(arc); AssertSlots(arcWork, 1, 1, 1);
        Assert.AreEqual(1, arcWork.PointPropagationAttemptedObjects);
        Assert.AreEqual(0, arcWork.ProjectionAttemptedObjects); Assert.AreEqual(0L, arcWork.ProjectionAttempts);
    }

    [TestMethod]
    public async Task ArcPeakCacheReuseDoesNotAddASecondPointCoreCall()
    {
        var (query, time) = OrbitalPassArcEngineTests.Query();
        var instant = query.Pass.PeakUtc;
        var pass = OrbitalPredictionJson.CreatePass(query.Pass with { PassInterval = new(instant, instant) });
        query = new(query.SearchQuery, pass, query.Bounds with { MaximumEvaluations = 3, MaximumSamples = 1 });
        var result = await new OrbitalPredictionEngine(time).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, result.Status);
        var work = Work(result); AssertSlots(work, 1, 1, 1);
        Assert.AreEqual(2, work.InitializationCoreCalls); Assert.AreEqual(1, work.PointPropagationCoreCalls);
        Assert.AreEqual(1, work.PointPropagationAttemptedObjects); Assert.AreEqual(1, work.PointPropagationSuccesses);
        Assert.AreEqual(0L, work.ProjectionAttempts);
    }

    [TestMethod]
    public async Task FinalArcDeadlineRebuildPreservesActualWorkWithoutRecountingOrResetting()
    {
        var (query, time) = OrbitalPassArcEngineTests.Query(); var clock = new AccountingClock();
        var complete = await new OrbitalPredictionEngine(time, clock).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Complete, complete.Status);
        var lateClock = new AccountingClock { DeadlineAt = clock.Reads };
        var late = await new OrbitalPredictionEngine(time, lateClock).PredictPassArcAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, late.Status); Assert.AreEqual(OrbitalPredictionReason.Deadline, late.Reason);
        Assert.AreEqual(OrbitalExecutionStatus.Partial, late.Products.Single().Status);
        var first = Work(complete); var second = Work(late);
        Assert.AreEqual(first.InitializationCoreCalls, second.InitializationCoreCalls);
        Assert.AreEqual(first.PointPropagationCoreCalls, second.PointPropagationCoreCalls);
        Assert.AreEqual(first.PointPropagationSuccesses, second.PointPropagationSuccesses);
        Assert.AreEqual(first.PointPropagationAttemptedObjects, second.PointPropagationAttemptedObjects);
        Assert.AreEqual(0L, second.ProjectionAttempts);
    }

    [TestMethod]
    public async Task RepeatedQueriesFreezeIndependentDiagnosticsAndPreserveCanonicalProducts()
    {
        var (query, time) = Exposure(); var engine = new OrbitalPredictionEngine(time);
        var first = await engine.PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        var firstWork = Work(first);
        var repeated = await engine.PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);
        var repeatedWork = Work(repeated);
        Assert.AreNotSame(firstWork, repeatedWork);
        Assert.AreEqual(firstWork.PointPropagationCoreCalls, repeatedWork.PointPropagationCoreCalls);
        Assert.AreEqual(firstWork.ProjectionAttempts, repeatedWork.ProjectionAttempts);
        CollectionAssert.AreEqual(OrbitalPredictionJson.SerializeTrack(first.Products.Single()),
            OrbitalPredictionJson.SerializeTrack(repeated.Products.Single()));
        _ = await engine.PredictExposureAsync(Change(query, bounds: query.Request.Bounds with { MaximumEvaluations = 1 }),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(repeatedWork.ProjectionAttempts, firstWork.ProjectionAttempts);
        Assert.AreEqual(repeatedWork.PointPropagationCoreCalls, firstWork.PointPropagationCoreCalls);
        Assert.IsTrue(typeof(OrbitalWorkDiagnostics).GetProperties().All(static p => p.SetMethod is null));
    }

    private async Task<OrbitalPredictionResult<OrbitalExposureTrackV1>> Predict(OrbitalExposureQuery query, OrbitalTimeData time)
        => await new OrbitalPredictionEngine(time).PredictExposureAsync(query, TestContext.CancellationToken).ConfigureAwait(false);

    private static OrbitalWorkDiagnostics Work<T>(OrbitalPredictionResult<T> result)
    {
        var work = result.WorkDiagnostics; Assert.IsNotNull(work);
        Assert.AreEqual(result.RequestedObjects, work.RequestedObjectSlots);
        Assert.AreEqual(result.EvaluatedObjects, work.VisitedObjectSlots);
        Assert.AreEqual(result.PropagationEvaluations, work.InitializationCoreCalls + work.PointPropagationCoreCalls);
        Assert.AreEqual(work.PointPropagationCoreCalls, work.PointPropagationSuccesses + work.PointPropagationFailures);
        Assert.IsLessThanOrEqualTo(work.VisitedObjectSlots, work.SelectedObjectSlots);
        Assert.IsLessThanOrEqualTo(work.SelectedObjectSlots, work.InitializationAttemptedObjects);
        Assert.IsLessThanOrEqualTo(work.InitializationAttemptedObjects, work.PointPropagationAttemptedObjects);
        Assert.IsLessThanOrEqualTo(work.PointPropagationAttemptedObjects, work.ProjectionAttemptedObjects);
        Assert.IsLessThanOrEqualTo(work.ProjectionAttempts, work.ProjectionSuccesses);
        return work;
    }

    private static void AssertSlots(OrbitalWorkDiagnostics work, int requested, int visited, int selected)
    {
        Assert.AreEqual(requested, work.RequestedObjectSlots); Assert.AreEqual(visited, work.VisitedObjectSlots);
        Assert.AreEqual(selected, work.SelectedObjectSlots);
    }

    private static void AssertNoCoreOrProjection(OrbitalWorkDiagnostics work)
    {
        Assert.AreEqual(0, work.InitializationAttemptedObjects); Assert.AreEqual(0, work.InitializationCoreCalls);
        Assert.AreEqual(0, work.PointPropagationAttemptedObjects); Assert.AreEqual(0, work.PointPropagationCoreCalls);
        Assert.AreEqual(0, work.PointPropagationSuccesses); Assert.AreEqual(0, work.PointPropagationFailures);
        Assert.AreEqual(0, work.ProjectionAttemptedObjects); Assert.AreEqual(0L, work.ProjectionAttempts);
        Assert.AreEqual(0L, work.ProjectionSuccesses);
    }

    private static OrbitalExposureQuery Change(OrbitalExposureQuery query, OrbitalSnapshot? snapshot = null,
        IEnumerable<OrbitalObjectIdentity>? objects = null, OrbitalQueryBounds? bounds = null, OrbitalPredictionModelFacts? model = null)
    {
        var r = query.Request;
        return new(new(r.Use, snapshot ?? r.Snapshot, r.SelectionPolicy, r.Interval, objects ?? snapshot?.ObjectIdentities ?? r.Objects,
            r.Observer, model ?? r.Model, bounds ?? r.Bounds), query.Geometry, query.Timing);
    }

    private static (OrbitalExposureQuery Query, OrbitalTimeData Time) Exposure(bool instant = false)
    {
        var time = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        var snapshot = OrbitalContractExamples.Snapshot(OrbitalContractExamples.Record());
        var interval = instant ? new(OrbitalContractExamples.Exposure.StartUtc, OrbitalContractExamples.Exposure.StartUtc) : OrbitalContractExamples.Exposure;
        var geometry = OrbitalContractExamples.Geometry with { HorizonPolicy = HorizonPolicy.ProjectionOnly };
        var query = OrbitalPredictionTestData.Query(snapshot, time, interval, OrbitalContractExamples.Observer, geometry);
        var evaluator = OrbitalObjectEvaluator.Create(query.Request, snapshot.Records[0], time,
            new(query.Request.Bounds, default), out _, out _)!;
        var center = evaluator.Evaluate(interval.StartUtc)!.Horizontal;
        geometry = geometry with
        {
            Projection = geometry.Projection with
            { BoresightAltitudeDegrees = center.AltitudeDegrees, BoresightAzimuthDegrees = center.AzimuthDegrees }
        };
        return (new(query.Request, geometry, query.Timing), time);
    }

    private sealed class AccountingClock : TimeProvider
    {
        internal int Reads { get; private set; }
        internal int DeadlineAt { get; init; } = int.MaxValue;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ++Reads >= DeadlineAt ? TimeSpan.FromMinutes(11).Ticks : 0;
    }
}
