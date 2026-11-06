namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalWorkBudgetTests
{
    [TestMethod]
    public void WorkAndResultLimitsCountSuccessfulReservationsWithoutOverrun()
    {
        var limits = OrbitalContractExamples.Bounds with { MaximumEvaluations = 2, MaximumSamples = 1, MaximumTracks = 1, MaximumPasses = 1 };
        var clock = new BudgetClock();
        var evaluations = new OrbitalWorkBudget(limits, default, clock);
        Assert.IsTrue(evaluations.TryEvaluation()); Assert.IsTrue(evaluations.TryEvaluation());
        Assert.IsFalse(evaluations.TryEvaluation()); Assert.AreEqual(2, evaluations.Evaluations);
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, evaluations.Reason);
        Assert.IsFalse(evaluations.TrySample()); Assert.AreEqual(0, evaluations.Samples);
        var samples = new OrbitalWorkBudget(limits, default, clock);
        Assert.IsTrue(samples.TrySample()); Assert.IsFalse(samples.TrySample()); Assert.AreEqual(1, samples.Samples);
        Assert.AreEqual(OrbitalPredictionReason.SampleBudget, samples.Reason);
        var tracks = new OrbitalWorkBudget(limits, default, clock);
        Assert.IsTrue(tracks.TryTrack()); Assert.IsFalse(tracks.TryTrack()); Assert.AreEqual(1, tracks.Tracks);
        Assert.AreEqual(OrbitalPredictionReason.ResultBudget, tracks.Reason);
        var passes = new OrbitalWorkBudget(limits, default, clock);
        Assert.IsTrue(passes.TryPass()); Assert.IsFalse(passes.TryPass()); Assert.AreEqual(1, passes.Passes);
    }

    [TestMethod]
    public void DeadlineEqualityIsAllowedAndElapsedOverrunTakesPrecedence()
    {
        var clock = new BudgetClock();
        var budget = new OrbitalWorkBudget(OrbitalContractExamples.Bounds with { MaximumElapsedMilliseconds = 10, MaximumSamples = 1 }, default, clock);
        clock.Ticks = TimeSpan.TicksPerMillisecond * 10;
        Assert.IsTrue(budget.TrySample()); Assert.AreEqual(10, budget.ElapsedMilliseconds);
        Assert.IsFalse(budget.TrySample()); Assert.AreEqual(OrbitalPredictionReason.SampleBudget, budget.Reason);
        clock.Ticks++;
        Assert.AreEqual(OrbitalPredictionReason.Deadline, budget.Reason);
        Assert.IsFalse(budget.CanContinue());
    }

    [TestMethod]
    public void CancellationThrowsEvenAfterAnotherBudgetWasExhausted()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new OrbitalWorkBudget(OrbitalContractExamples.Bounds with { MaximumSamples = 1 }, cancellation.Token, new BudgetClock());
        Assert.IsTrue(budget.TrySample()); Assert.IsFalse(budget.TrySample());
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => budget.TryEvaluation());
        Assert.ThrowsExactly<OperationCanceledException>(() => budget.TryTrack());
    }

    private sealed class BudgetClock : TimeProvider
    {
        internal long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
