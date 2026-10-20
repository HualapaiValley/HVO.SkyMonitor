using HVO.SkyMonitor.Astronomy.Orbital.Propagation;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalObjectEvaluatorTests
{
    [TestMethod]
    public void ActualCoreCallsIncludeBothInitializationEvaluationsAndCacheHitsAreFree()
    {
        var time = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        var query = Request(time);
        time.ValidateModel(query.Model, query.Interval, query.Use, OrbitalGeometryBasis.ConfiguredRig);
        var budget = new OrbitalWorkBudget(query.Bounds with { MaximumEvaluations = 4 }, default);
        var evaluator = OrbitalObjectEvaluator.Create(query, query.Snapshot.Records[0], time, budget, out var reason, out var detail);
        Assert.IsNotNull(evaluator, detail); Assert.AreEqual(OrbitalPredictionReason.None, reason);
        Assert.AreEqual(2, budget.Evaluations);
        var first = evaluator.Evaluate(query.Interval.StartUtc);
        Assert.IsNotNull(first); Assert.AreEqual(3, budget.Evaluations);
        Assert.AreSame(first, evaluator.Evaluate(query.Interval.StartUtc)); Assert.AreEqual(3, budget.Evaluations);
        Assert.IsNotNull(evaluator.Evaluate(query.Interval.EndUtc)); Assert.AreEqual(4, budget.Evaluations);
        Assert.IsNull(evaluator.Evaluate(query.Interval.StartUtc.AddTicks(1)));
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, evaluator.FailureReason);
        Assert.AreEqual(4, budget.Evaluations);
    }

    [TestMethod]
    public void InitializationCannotExceedOneCallBudgetAndCancellationIsObservedBetweenCoreCalls()
    {
        var record = OrbitalContractExamples.Record();
        var budget = new OrbitalWorkBudget(OrbitalContractExamples.Bounds with { MaximumEvaluations = 1 }, default);
        Assert.IsFalse(Sgp4ObjectPropagator.TryCreate(record, out var result, out var failure, budget.TryEvaluation));
        Assert.IsNull(result); Assert.AreEqual(Sgp4FailureKind.WorkLimit, failure.Kind);
        Assert.AreEqual(1, budget.Evaluations); Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, budget.Reason);
        using var cancellation = new CancellationTokenSource();
        var canceledBudget = new OrbitalWorkBudget(OrbitalContractExamples.Bounds, cancellation.Token);
        var count = 0;
        bool Before()
        {
            if (++count == 2) cancellation.Cancel();
            return canceledBudget.TryEvaluation();
        }
        Assert.ThrowsExactly<OperationCanceledException>(() => Sgp4ObjectPropagator.TryCreate(record, out _, out _, Before));
        Assert.AreEqual(1, canceledBudget.Evaluations);
    }

    [TestMethod]
    public void UnsupportedNumericalDomainIsExplicitAndDoesNotConsumePropagationWork()
    {
        var time = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        var request = Request(time);
        var record = request.Snapshot.Records[0];
        record = OrbitalContract.CreateRecord(record with { Elements = record.Elements with { Eccentricity = .99 } });
        var budget = new OrbitalWorkBudget(request.Bounds, default);
        Assert.IsNull(OrbitalObjectEvaluator.Create(request, record, time, budget, out var reason, out var detail));
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, reason);
        Assert.AreEqual("outside-numerical-support-profile-v1", detail); Assert.AreEqual(0, budget.Evaluations);
        var model = OrbitalPredictionModel.Create(time);
        model.Validate(); Assert.AreEqual(OrbitalPredictionModel.Propagator, model.Propagator);
        Assert.IsNull(model.SolarShadowModelSha256);
        Assert.ThrowsExactly<ArgumentException>(() => time.ValidateModel(model with { EarthOrientationSha256 = new('A', 64) },
            request.Interval, request.Use, OrbitalGeometryBasis.ConfiguredRig));
    }

    private static OrbitalPredictionRequest Request(OrbitalTimeData time)
    {
        var snapshot = OrbitalContractExamples.Snapshot(OrbitalContractExamples.Record());
        return new(OrbitalPredictionUse.Diagnostic, snapshot, OrbitalContractExamples.Policy(), OrbitalContractExamples.Exposure,
            [snapshot.Records[0].ObjectIdentity], OrbitalContractExamples.Observer, OrbitalPredictionModel.Create(time),
            OrbitalContractExamples.Bounds with { MaximumElapsedMilliseconds = 600000 });
    }
}
