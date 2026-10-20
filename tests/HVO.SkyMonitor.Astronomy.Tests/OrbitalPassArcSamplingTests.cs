namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPassArcSamplingTests
{
    [TestMethod]
    public void NorthWrapAndZenithUseUnitVectorsRatherThanScalarAzimuth()
    {
        var wrapped = Synthetic(2, t => CameraBasis.FromHorizontal(new(60, (359 + t) % 360)));
        Assert.AreEqual(OrbitalPredictionReason.None, wrapped.Result.Reason);
        Assert.IsTrue(wrapped.Result.Arc!.Samples.Any(s => s.Horizontal.AzimuthDegrees < 1));
        var zenith = Synthetic(2, t => new EnuVector(Math.Sin((t - 1) * .1), 0, Math.Cos((t - 1) * .1)));
        Assert.AreEqual(OrbitalPredictionReason.None, zenith.Result.Reason);
        var arc = zenith.Result.Arc!;
        Assert.AreEqual(90, arc.Samples[arc.Anchors.PeakSampleIndex!.Value].Horizontal.AltitudeDegrees, 1e-12);
        for (var i = 1; i < arc.Samples.Count; i++)
        {
            var from = CameraBasis.FromHorizontal(arc.Samples[i - 1].Horizontal); var to = CameraBasis.FromHorizontal(arc.Samples[i].Horizontal);
            var midpoint = EnuVector.SphericalInterpolate(from, to, .5);
            Assert.IsTrue(double.IsFinite(midpoint.Length));
        }
    }

    [TestMethod]
    public void AntipodalEndpointsSubdivideAndCoincidentDirectionsRemainResolved()
    {
        var antipodal = Synthetic(1, t => new(Math.Cos(Math.PI * t), 0, Math.Sin(Math.PI * t)), angularSpeed: 180);
        Assert.AreEqual(OrbitalPredictionReason.None, antipodal.Result.Reason);
        Assert.IsTrue(antipodal.Result.Arc!.Samples.Count > 3);
        Assert.IsTrue(antipodal.Budget.Evaluations < 100000);
        var stationary = Synthetic(2, _ => CameraBasis.FromHorizontal(new(60, 0)));
        Assert.AreEqual(OrbitalPredictionReason.None, stationary.Result.Reason);
        Assert.AreEqual(3, stationary.Result.Arc!.Samples.Count);
        Assert.IsTrue(stationary.Result.Arc.Sampling.AchievedEstimatedAngularErrorDegrees > 0);
        Assert.ThrowsExactly<ArgumentException>(() => EnuVector.SphericalInterpolate(new(1, 0, 0), new(-1, 0, 0), .5));
    }

    [TestMethod]
    public void MissingInteriorProbeStopsPrefixBeforeTheUnresolvedGap()
    {
        var result = Synthetic(4, _ => CameraBasis.FromHorizontal(new(60, 0)), failAfter: 1.1);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, result.Result.Reason);
        var arc = result.Result.Arc!; Assert.AreEqual(OrbitalExecutionStatus.Partial, arc.Status);
        Assert.AreEqual(2, arc.Samples.Count); Assert.AreEqual(new OrbitalSkyArcSegmentV1(0, 2), arc.Segments.Single());
        Assert.IsNull(arc.Anchors.PeakSampleIndex); Assert.IsNull(arc.Anchors.EndSampleIndex);
        Assert.IsLessThanOrEqualTo(1.1, (arc.CoveredInterval.EndUtc - arc.Pass.PassInterval.StartUtc).TotalSeconds);
    }

    [TestMethod]
    public void EopMidnightKnotAndExactPeakAreBothSamplingBoundaries()
    {
        var start = new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.Zero).AddMilliseconds(750);
        var sampled = Synthetic(1.5, _ => CameraBasis.FromHorizontal(new(60, 0)), start: start);
        Assert.AreEqual(OrbitalPredictionReason.None, sampled.Result.Reason);
        var arc = sampled.Result.Arc!;
        Assert.IsTrue(arc.Samples.Any(s => s.Utc == start.AddMilliseconds(250)));
        Assert.AreEqual(start.AddSeconds(.75), arc.Samples[arc.Anchors.PeakSampleIndex!.Value].Utc);
    }

    [TestMethod]
    public void ConservativeSerializedAllowanceStopsAsSampleBudgetBeforeOversizeAllocation()
    {
        var limit = OrbitalPassArcJson.MaximumMaterializedSamples;
        var sampled = Synthetic(limit + 2, _ => CameraBasis.FromHorizontal(new(60, 0)), maximumSamples: 1_000_000);
        Assert.AreEqual(OrbitalPredictionReason.SampleBudget, sampled.Result.Reason);
        Assert.AreEqual(limit, sampled.Result.Arc!.Samples.Count);
        Assert.AreEqual(limit, sampled.Budget.Samples);
        Assert.IsTrue(OrbitalPassArcJson.Serialize(sampled.Result.Arc).Length < OrbitalPredictionJson.MaximumProductBytes);
    }

    [TestMethod]
    public void CancellationBetweenUncachedProbesDoesNotPublishAPartialProduct()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => Synthetic(2, _ => CameraBasis.FromHorizontal(new(60, 0)),
            cancellation: cancellation.Token, onEvaluation: count => { if (count == 5) cancellation.Cancel(); }));
    }

    private static (OrbitalPassArcSamplingResult Result, OrbitalWorkBudget Budget) Synthetic(double seconds,
        Func<double, EnuVector> direction, double? failAfter = null, DateTimeOffset? start = null,
        int maximumSamples = 10000, double angularSpeed = 0, Action<int>? onEvaluation = null, CancellationToken cancellation = default)
    {
        var original = OrbitalPassArcContractTests.ExampleQuery(); var r = original.SearchQuery.Request;
        var first = start ?? original.Pass.PassInterval.StartUtc;
        var interval = new OrbitalUtcInterval(first, first.AddSeconds(seconds));
        var searchInterval = new OrbitalUtcInterval(first.AddSeconds(-1), interval.EndUtc.AddSeconds(1));
        var peakUtc = first.AddTicks((interval.EndUtc.UtcTicks - first.UtcTicks) / 2);
        var peak = CameraBasis.ToHorizontal(direction(seconds / 2).Normalize());
        var request = new OrbitalPredictionRequest(r.Use, r.Snapshot, r.SelectionPolicy, searchInterval, r.Objects, r.Observer, r.Model, r.Bounds);
        var pass = OrbitalPredictionJson.CreatePass(original.Pass with
        {
            SearchInterval = searchInterval,
            PassInterval = interval,
            PeakUtc = peakUtc,
            MaximumElevationDegrees = peak.AltitudeDegrees,
            Lineage = original.Pass.Lineage with
            {
                StartEpochAgeHours = (searchInterval.StartUtc - original.Pass.Lineage.EpochUtc).TotalHours,
                EndEpochAgeHours = (searchInterval.EndUtc - original.Pass.Lineage.EpochUtc).TotalHours
            }
        });
        var query = new OrbitalPassArcQuery(new(request, 0), pass, original.Bounds with { MaximumSamples = maximumSamples });
        var budget = new OrbitalWorkBudget(query.Bounds, cancellation); var cache = new Dictionary<long, OrbitalTopocentricState>();
        OrbitalTopocentricState? Evaluate(DateTimeOffset utc)
        {
            if (cache.TryGetValue(utc.UtcTicks, out var existing)) return existing;
            if (!budget.TryEvaluation()) return null;
            onEvaluation?.Invoke(budget.Evaluations);
            var t = (utc - first).TotalSeconds;
            if (failAfter is { } after && t > after) return null;
            var unit = direction(t).Normalize();
            var state = new OrbitalTopocentricState(CameraBasis.ToHorizontal(unit), 1000, 0, angularSpeed, unit);
            cache.Add(utc.UtcTicks, state); return state;
        }
        return (OrbitalPassArcSampler.Sample(query, Evaluate, budget), budget);
    }
}
