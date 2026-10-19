using System.Globalization;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy.Orbital.Propagation;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class OrbitalPassSearchTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void QuarticHullFindsTwoCrossingsBetweenAllNineInitialProbes()
    {
        var run = Synthetic(60, t => .0009 - (t - 3.2) * (t - 3.2));
        Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason, run.Result.Detail);
        var pass = run.Result.Passes.Single();
        AssertTime(3.17, pass.PassInterval.StartUtc, run.Query, .01);
        AssertTime(3.23, pass.PassInterval.EndUtc, run.Query, .01);
        AssertTime(3.2, pass.PeakUtc, run.Query, .01);
        Assert.AreEqual(OrbitalPassBoundary.ResolvedCrossing, pass.StartBoundary);
        Assert.AreEqual(OrbitalPassBoundary.ResolvedCrossing, pass.EndBoundary);
        Assert.AreEqual(0, run.Budget.Samples);
        Assert.AreEqual(1, run.Budget.Passes);
        Assert.IsLessThan(2000, run.Budget.Evaluations);
    }

    [TestMethod]
    public void TouchAndSubRemainderExcursionRemainExplicitlyUnresolved()
    {
        foreach (var clearance in new[] { 0d, 1e-9, -1e-9 })
        {
            var run = Synthetic(10, t => clearance - (t - 3.2) * (t - 3.2));
            Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, run.Result.Reason, clearance.ToString("R", CultureInfo.InvariantCulture));
            Assert.IsTrue(run.Result.Passes.All(p => p.Status == OrbitalExecutionStatus.Partial));
            Assert.IsFalse(run.Result.Passes.Any(p => p.PassInterval.EndUtc <= p.PassInterval.StartUtc));
        }
    }

    [TestMethod]
    public void SingularAndDiscontinuousCellsReturnBoundedIncompleteIntervals()
    {
        var discontinuity = Synthetic(10, t => t < 3.2 ? 1 : -1);
        Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, discontinuity.Result.Reason);
        Assert.IsLessThan(1000, discontinuity.Budget.Evaluations);
        Assert.AreEqual(OrbitalPassBoundary.WorkLimit, discontinuity.Result.Passes.Single().EndBoundary);
        var interval = new OrbitalUtcInterval(OrbitalContractExamples.Epoch, OrbitalContractExamples.Epoch.AddSeconds(10));
        var record = OrbitalContractExamples.Record();
        var (query, lineage) = Query(record, interval, OrbitalContractExamples.Observer, OrbitalContractExamples.Model, 0, Bounds(10));
        var budget = new OrbitalWorkBudget(query.Request.Bounds, default);
        OrbitalTopocentricState? Evaluate(DateTimeOffset _) => budget.TryEvaluation() ? new(new(0, 90), 1e-9, 0, 0, new(1, 0, 0)) : null;
        var singular = OrbitalPassSearch.Search(query, lineage, record.Elements, Evaluate, budget);
        Assert.AreEqual(OrbitalPredictionReason.IncompleteInterval, singular.Reason);
        Assert.AreEqual(0, singular.Passes.Count);
        Assert.IsLessThan(1000, budget.Evaluations);
    }

    [TestMethod]
    public void WindowWithoutCulminationRetainsEndpointsAndPeakAtWindowEnd()
    {
        var run = Synthetic(30, t => 1 + t);
        Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason);
        var pass = run.Result.Passes.Single();
        Assert.AreEqual(run.Query.Request.Interval, pass.PassInterval);
        Assert.AreEqual(OrbitalPassBoundary.SearchWindow, pass.StartBoundary);
        Assert.AreEqual(OrbitalPassBoundary.SearchWindow, pass.EndBoundary);
        Assert.AreEqual(run.Query.Request.Interval.EndUtc, pass.PeakUtc);
    }

    [TestMethod]
    public void AllLocalMaximaCompeteAndEqualPeaksUseEarliestTime()
    {
        static double Shape(double t) => 10 - Math.Pow((t - 20) * (t - 40) / 100, 2);
        var equal = Synthetic(40, t => Shape(t + 10));
        Assert.AreEqual(OrbitalPredictionReason.None, equal.Result.Reason);
        var pass = equal.Result.Passes.Single();
        AssertTime(10, pass.PeakUtc, equal.Query, .01);
        var unequal = Synthetic(40, t => Shape(t + 10) + .001 * t);
        Assert.AreEqual(OrbitalPredictionReason.None, unequal.Result.Reason);
        AssertTime(30, unequal.Result.Passes.Single().PeakUtc, unequal.Query, .1);
        var flat = Synthetic(3600, _ => 1000);
        Assert.AreEqual(OrbitalPredictionReason.None, flat.Result.Reason);
        Assert.AreEqual(flat.Query.Request.Interval.StartUtc, flat.Result.Passes.Single().PeakUtc);
    }

    [TestMethod]
    public void ContinuousAndNeverVisibleGeoRemainUsefulAndBounded()
    {
        foreach (var up in new[] { 5000d, -5000d })
        {
            var run = Synthetic(86400, _ => up, elements: OrbitalContractExamples.Record().Elements with { MeanMotionRevolutionsPerDay = 1 });
            Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason);
            Assert.AreEqual(up > 0 ? 1 : 0, run.Result.Passes.Count);
            Assert.IsLessThan(3000, run.Budget.Evaluations);
        }
    }

    [TestMethod]
    public void PositiveThresholdUsesCartesianDirectionTube()
    {
        var run = Synthetic(40, t => 200 - (t - 20) * (t - 20), threshold: 10);
        Assert.AreEqual(OrbitalPredictionReason.None, run.Result.Reason, run.Result.Detail);
        var pass = run.Result.Passes.Single();
        var halfWidth = Math.Sqrt(200 - 1000 * Math.Tan(10 * Math.PI / 180));
        AssertTime(20 - halfWidth, pass.PassInterval.StartUtc, run.Query, .01);
        AssertTime(20 + halfWidth, pass.PassInterval.EndUtc, run.Query, .01);
    }

    [TestMethod]
    public void WindowCanStartOrEndInPassWithoutInventingCrossings()
    {
        var starts = Synthetic(40, t => 100 - t * t);
        Assert.AreEqual(OrbitalPredictionReason.None, starts.Result.Reason);
        Assert.AreEqual(OrbitalPassBoundary.SearchWindow, starts.Result.Passes.Single().StartBoundary);
        AssertTime(10, starts.Result.Passes.Single().PassInterval.EndUtc, starts.Query, .01);
        var ends = Synthetic(40, t => 100 - (t - 40) * (t - 40));
        Assert.AreEqual(OrbitalPredictionReason.None, ends.Result.Reason);
        Assert.AreEqual(OrbitalPassBoundary.SearchWindow, ends.Result.Passes.Single().EndBoundary);
        AssertTime(30, ends.Result.Passes.Single().PassInterval.StartUtc, ends.Query, .01);
    }

    [TestMethod]
    public void EvaluationLimitsDuringFitCrossingAndPeakNeverBecomeCompleteEmpty()
    {
        var complete = Synthetic(60, t => 100 - (t - 20.123) * (t - 20.123) / 100);
        Assert.AreEqual(OrbitalPredictionReason.None, complete.Result.Reason);
        foreach (var limit in new[] { 1, 9, 30, complete.Budget.Evaluations - 1 })
        {
            var run = Synthetic(60, t => 100 - (t - 20.123) * (t - 20.123) / 100,
                bounds: Bounds(60) with { MaximumEvaluations = limit });
            Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, run.Result.Reason, $"limit={limit}");
            Assert.AreEqual(limit, run.Budget.Evaluations);
            Assert.IsTrue(run.Result.Passes.All(p => p.Status == OrbitalExecutionStatus.Partial && p.EndBoundary == OrbitalPassBoundary.WorkLimit));
            if (limit == complete.Budget.Evaluations - 1) Assert.AreEqual(1, run.Result.Passes.Count);
        }
        var crossings = Synthetic(60, t => .0009 - (t - 3.2) * (t - 3.2), bounds: Bounds(60) with { MaximumEvaluations = 50 });
        Assert.AreEqual(OrbitalPredictionReason.EvaluationBudget, crossings.Result.Reason);
    }

    [TestMethod]
    public void FutureFailurePreservesCompletedPassAndOpenWorkLimitPass()
    {
        var run = Synthetic(120, t => 10 * Math.Cos(t * Math.PI / 20), failAfter: 90);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, run.Result.Reason);
        Assert.IsTrue(run.Result.Passes.Any(p => p.Status == OrbitalExecutionStatus.Complete));
        var open = run.Result.Passes[^1];
        Assert.AreEqual(OrbitalExecutionStatus.Partial, open.Status);
        Assert.AreEqual(OrbitalPassBoundary.WorkLimit, open.EndBoundary);
        Assert.AreEqual(OrbitalPredictionReason.PropagationFailure, open.Reason);
    }

    [TestMethod]
    public void ResultBudgetAndDeadlinePreservePriorEvidence()
    {
        var limited = Synthetic(120, t => 10 * Math.Cos(t * Math.PI / 20), bounds: Bounds(120) with { MaximumPasses = 1 });
        Assert.AreEqual(OrbitalPredictionReason.ResultBudget, limited.Result.Reason);
        Assert.AreEqual(1, limited.Result.Passes.Count);
        Assert.AreEqual(1, limited.Budget.Passes);
        var clock = new SearchClock();
        var deadline = Synthetic(120, t => 10 * Math.Cos(t * Math.PI / 20), clock: clock,
            onEvaluation: count => { if (count == 50) clock.Ticks = TimeSpan.TicksPerSecond * 61; });
        Assert.AreEqual(OrbitalPredictionReason.Deadline, deadline.Result.Reason);
        Assert.IsLessThanOrEqualTo(50, deadline.Budget.Evaluations);
    }

    [TestMethod]
    public void CancellationThrowsDuringBothEventAndPeakRefinement()
    {
        foreach (var peakPhase in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var evaluations = 0;
            Assert.ThrowsExactly<OperationCanceledException>(() => Synthetic(60,
                t => peakPhase ? 100 - Math.Pow(t - 20.123, 2) / 100 : .0009 - Math.Pow(t - 3.2, 2),
                cancellation: cancellation.Token,
                onPoint: t =>
                {
                    if (++evaluations > 17 && Math.Abs(t - (peakPhase ? 20.123 : 3.17)) < .001) cancellation.Cancel();
                }));
        }
    }

    [TestMethod]
    public void IndependentModeAPassPackMeetsUnmodifiedCrossingAndPeakGates()
    {
        using var fixture = JsonDocument.Parse(OrbitalCoordinateTestData.Bytes("coordinates", "pass-references.json"));
        using var facts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "orbital-reference-v1", "gp-source-facts.json")));
        var time = OrbitalCoordinateTestData.Load("predicted-2026.finals2000A");
        double worstEvent = 0, worstPeak = 0, worstAltitude = 0;
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var record = FactRecord(facts.RootElement.EnumerateArray().Single(f => f.GetProperty("NORAD_CAT_ID").GetInt32() == (name.StartsWith("LES", StringComparison.Ordinal) ? 2866 : 25544)));
            var site = item.GetProperty("observer");
            var observer = new ObserverLocation(site[0].GetDouble(), site[1].GetDouble(), site[2].GetDouble());
            var interval = new OrbitalUtcInterval(Utc(item.GetProperty("windowStartUtc")), Utc(item.GetProperty("windowEndUtc")));
            var (query, lineage) = Query(record, interval, observer, OrbitalCoordinateTestData.Model(time), 0, Bounds(216000));
            time.ValidateModel(query.Request.Model, interval, OrbitalPredictionUse.Diagnostic, OrbitalGeometryBasis.ConfiguredRig);
            time.ValidatePropagationInterval(record.EpochUtc, interval); // Before the first SGP4 call.
            Assert.IsTrue(Sgp4ObjectPropagator.TryCreate(record, out var propagator, out _));
            var frame = OrbitalObserverFrame.Create(observer);
            var budget = new OrbitalWorkBudget(query.Request.Bounds, default);
            OrbitalTopocentricState? Evaluate(DateTimeOffset utc)
            {
                if (!budget.TryEvaluation()) return null;
                Assert.IsTrue(propagator!.TryPropagate(utc, out var teme, out _));
                return frame.Evaluate(OrbitalCoordinateConverter.ToEarthFixed(teme!, time.Evaluate(utc)));
            }
            var result = OrbitalPassSearch.Search(query, lineage, record.Elements, Evaluate, budget);
            var searchEvaluations = budget.Evaluations;
            var searchElapsed = budget.ElapsedMilliseconds;
            Assert.AreEqual(OrbitalPredictionReason.None, result.Reason, $"{name}: {result.Detail}, evaluations={budget.Evaluations}, passes={result.Passes.Count}");
            var events = result.Passes.SelectMany(p => new[]
            {
                (p.StartBoundary, Utc: p.PassInterval.StartUtc), (p.EndBoundary, Utc: p.PassInterval.EndUtc)
            }).Where(p => p.Item1 == OrbitalPassBoundary.ResolvedCrossing).ToArray();
            var expectedEvents = item.GetProperty("crossings").EnumerateArray().ToArray();
            Assert.AreEqual(expectedEvents.Length, events.Length, name);
            for (var i = 0; i < events.Length; i++)
            {
                var reference = expectedEvents[i];
                var bracket = reference.GetProperty("bracketSeconds");
                Assert.IsLessThanOrEqualTo(.001, bracket[1].GetDouble() - bracket[0].GetDouble());
                var error = Math.Abs((events[i].Utc - Utc(reference.GetProperty("utc"))).TotalSeconds);
                worstEvent = Math.Max(worstEvent, error);
                Assert.IsLessThanOrEqualTo(.02, error, $"{name} event {i}");
                var before = Evaluate(events[i].Utc.AddSeconds(-OrbitalPassSearch.CrossingBracketSeconds / 2))!.Horizontal.AltitudeDegrees;
                var after = Evaluate(events[i].Utc.AddSeconds(OrbitalPassSearch.CrossingBracketSeconds / 2))!.Horizontal.AltitudeDegrees;
                Assert.IsTrue(before * after <= 0, $"{name} event {i}: HVO .01 s bracket [{before:R}, {after:R}]");
            }
            var expectedPeaks = item.GetProperty("peaks").EnumerateArray().ToArray();
            foreach (var expected in expectedPeaks)
            {
                var utc = Utc(expected.GetProperty("utc"));
                var pass = result.Passes.Single(p => utc >= p.PassInterval.StartUtc && utc <= p.PassInterval.EndUtc);
                var error = Math.Abs((pass.PeakUtc - utc).TotalSeconds);
                var altitudeError = Math.Abs(pass.MaximumElevationDegrees - expected.GetProperty("altitudeDegrees").GetDouble());
                worstPeak = Math.Max(worstPeak, error); worstAltitude = Math.Max(worstAltitude, altitudeError);
                Assert.IsLessThanOrEqualTo(.02, error, $"{name} peak");
                Assert.IsLessThanOrEqualTo(1e-6, altitudeError, $"{name} altitude");
            }
            TestContext.WriteLine($"{name}: {result.Passes.Count} passes, {events.Length} crossings, {searchEvaluations} search evaluations, {searchElapsed:R} ms");
            Assert.AreEqual(result.Passes.Count, budget.Passes);
            Assert.AreEqual(0, budget.Samples);
            foreach (var pass in result.Passes)
            {
                Assert.AreEqual(interval, pass.SearchInterval);
                CollectionAssert.AreEqual(OrbitalContract.SerializeCanonical(lineage), OrbitalContract.SerializeCanonical(pass.Lineage));
                Assert.AreEqual(pass.ProductSha256, OrbitalPredictionJson.CreatePass(pass).ProductSha256);
            }
            if (item.TryGetProperty("nearTangent", out var tangent))
            {
                var peak = tangent.GetProperty("peak");
                var expectedPeak = Utc(peak.GetProperty("utc"));
                var nearInterval = new OrbitalUtcInterval(expectedPeak.AddSeconds(-1), expectedPeak.AddSeconds(1));
                var (nearQuery, nearLineage) = Query(record, nearInterval, observer, query.Request.Model,
                    tangent.GetProperty("thresholdDegrees").GetDouble(), Bounds(2));
                time.ValidatePropagationInterval(record.EpochUtc, nearInterval);
                budget = new(nearQuery.Request.Bounds, default);
                var near = OrbitalPassSearch.Search(nearQuery, nearLineage, record.Elements, Evaluate, budget);
                Assert.IsTrue(near.Reason is OrbitalPredictionReason.None or OrbitalPredictionReason.IncompleteInterval, near.Detail);
                if (near.Reason == OrbitalPredictionReason.None)
                {
                    var pass = near.Passes.Single();
                    Assert.AreEqual(peak.GetProperty("altitudeDegrees").GetDouble(), pass.MaximumElevationDegrees, 1e-6);
                    Assert.IsTrue(pass.PassInterval.EndUtc > pass.PassInterval.StartUtc);
                    TestContext.WriteLine($"{name} near tangent: rise residual={(pass.PassInterval.StartUtc - Utc(tangent.GetProperty("rise").GetProperty("utc"))).TotalSeconds:R} s; set residual={(pass.PassInterval.EndUtc - Utc(tangent.GetProperty("set").GetProperty("utc"))).TotalSeconds:R} s; peak altitude={pass.MaximumElevationDegrees:R} deg; evaluations={budget.Evaluations}");
                }
                else TestContext.WriteLine($"{name} near tangent: explicit IncompleteInterval, {budget.Evaluations} evaluations");
            }
        }
        TestContext.WriteLine($"Worst external residual: crossing={worstEvent:R} s, peak={worstPeak:R} s, peakAltitude={worstAltitude:R} deg");
    }

    private static (OrbitalPassSearchResult Result, OrbitalPassQuery Query, OrbitalWorkBudget Budget) Synthetic(
        double seconds, Func<double, double> up, double threshold = 0, OrbitalMeanElements? elements = null,
        OrbitalQueryBounds? bounds = null, double? failAfter = null,
        SearchClock? clock = null, Action<int>? onEvaluation = null, Action<double>? onPoint = null, CancellationToken cancellation = default)
    {
        var interval = new OrbitalUtcInterval(OrbitalContractExamples.Epoch, OrbitalContractExamples.Epoch.AddSeconds(seconds));
        var record = OrbitalContractExamples.Record();
        var (query, lineage) = Query(record, interval, OrbitalContractExamples.Observer, OrbitalContractExamples.Model, threshold, bounds ?? Bounds(seconds));
        var budget = new OrbitalWorkBudget(query.Request.Bounds, cancellation, clock);
        OrbitalTopocentricState? Evaluate(DateTimeOffset utc)
        {
            if (!budget.TryEvaluation()) return null;
            onEvaluation?.Invoke(budget.Evaluations);
            var t = (utc - interval.StartUtc).TotalSeconds;
            onPoint?.Invoke(t);
            if (failAfter is { } after && t > after) return null;
            var position = new EnuVector(1000, 0, up(t));
            var range = position.Length;
            var direction = position / range;
            return new(CameraBasis.ToHorizontal(direction), range, 0, 0, direction);
        }
        return (OrbitalPassSearch.Search(query, lineage, elements ?? record.Elements, Evaluate, budget), query, budget);
    }

    private static OrbitalQueryBounds Bounds(double seconds) => OrbitalContractExamples.Bounds with
    { MaximumWindowSeconds = seconds, MaximumElapsedMilliseconds = 60000, MaximumEvaluations = 100000, MaximumPasses = 100 };

    private static (OrbitalPassQuery Query, OrbitalProductLineage Lineage) Query(OrbitalRecordV1 record, OrbitalUtcInterval interval,
        ObserverLocation observer, OrbitalPredictionModelFacts model, double threshold, OrbitalQueryBounds bounds)
    {
        var snapshot = OrbitalContractExamples.Snapshot(record);
        var policy = OrbitalContractExamples.Policy(stale: true);
        var selection = OrbitalRecordSelector.Select(snapshot, record.ObjectIdentity, interval, policy);
        Assert.IsNotNull(selection.SelectedRecord);
        var candidate = selection.Candidates.Single();
        var lineage = new OrbitalProductLineage(snapshot.Manifest.ManifestSha256, snapshot.Manifest.NormalizedPayloadSha256,
            record.RecordSha256, record.ObjectIdentity, record.SourceDataset, policy.IdentitySha256, record.EpochUtc,
            (interval.StartUtc - record.EpochUtc).TotalHours, (interval.EndUtc - record.EpochUtc).TotalHours,
            candidate.Freshness, candidate.SourceValidity, snapshot.Manifest.Coverage);
        return (new(new(OrbitalPredictionUse.Diagnostic, snapshot, policy, interval, [record.ObjectIdentity], observer, model, bounds), threshold), lineage);
    }

    private static OrbitalRecordV1 FactRecord(JsonElement fact)
    {
        double D(string name) => fact.GetProperty(name).GetDouble();
        return OrbitalContract.CreateRecord(OrbitalContractExamples.Record(fact.GetProperty("NORAD_CAT_ID").GetInt32().ToString(CultureInfo.InvariantCulture)) with
        {
            EpochUtc = DateTimeOffset.Parse(fact.GetProperty("EPOCH").GetString()! + "Z", CultureInfo.InvariantCulture),
            Elements = new(D("MEAN_MOTION"), D("ECCENTRICITY"), D("INCLINATION"), D("RA_OF_ASC_NODE"), D("ARG_OF_PERICENTER"),
                D("MEAN_ANOMALY"), D("BSTAR"), D("MEAN_MOTION_DOT"), D("MEAN_MOTION_DDOT"), 0, "U", 999, fact.GetProperty("REV_AT_EPOCH").GetInt64())
        });
    }

    private static DateTimeOffset Utc(JsonElement value) => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    private static void AssertTime(double expected, DateTimeOffset actual, OrbitalPassQuery query, double tolerance)
        => Assert.AreEqual(expected, (actual - query.Request.Interval.StartUtc).TotalSeconds, tolerance);

    private sealed class SearchClock : TimeProvider
    {
        internal long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }
}
