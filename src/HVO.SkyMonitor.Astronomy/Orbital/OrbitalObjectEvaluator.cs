using HVO.SkyMonitor.Astronomy.Orbital.Propagation;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>One selected record, one query; cached probes never escape or resolve external inputs.</summary>
internal sealed class OrbitalObjectEvaluator
{
    private readonly OrbitalTimeData time;
    private readonly OrbitalObserverFrame observer;
    private readonly OrbitalWorkBudget budget;
    private readonly Sgp4ObjectPropagator propagator;
    private readonly Dictionary<long, OrbitalTopocentricState> cache = [];
    private readonly OrbitalUtcInterval interval;
    internal OrbitalPredictionReason FailureReason { get; private set; }
    internal string FailureDetail { get; private set; } = "none";

    private OrbitalObjectEvaluator(OrbitalPredictionRequest request, OrbitalTimeData time,
        OrbitalWorkBudget budget, Sgp4ObjectPropagator propagator)
    {
        this.time = time; this.budget = budget; this.propagator = propagator;
        observer = OrbitalObserverFrame.Create(request.Observer);
        interval = request.Interval;
    }

    internal static OrbitalObjectEvaluator? Create(OrbitalPredictionRequest request, OrbitalRecordV1 record,
        OrbitalTimeData time, OrbitalWorkBudget budget, out OrbitalPredictionReason reason, out string detail)
    {
        reason = OrbitalPredictionReason.None; detail = "none";
        if (!budget.CanContinue()) { reason = budget.Reason; detail = "query-work-limit"; return null; }
        var e = record.Elements;
        var n = e.MeanMotionRevolutionsPerDay * (2 * Math.PI / 86400);
        var perigee = Math.Cbrt(398600.8 / (n * n)) * (1 - e.Eccentricity) - 6378.135;
        if (e.MeanMotionRevolutionsPerDay is < .5 or > 18 || e.Eccentricity > .9 ||
            Math.Abs(e.BStarInverseEarthRadii) > .01 || perigee < 100 ||
            request.Observer.ElevationMeters is < -1000 or > 10000)
        {
            reason = OrbitalPredictionReason.PropagationFailure; detail = "outside-numerical-support-profile-v1"; return null;
        }
        try { time.ValidatePropagationInterval(record.EpochUtc, request.Interval); }
        catch (ArgumentException exception)
        {
            reason = TimeFailure(exception); detail = exception.Message; return null;
        }
        if (!Sgp4ObjectPropagator.TryCreate(record, out var initialized, out var failure, budget.TryEvaluation))
        {
            reason = budget.Reason != OrbitalPredictionReason.None ? budget.Reason : OrbitalPredictionReason.PropagationFailure;
            detail = $"sgp4-{failure.Kind}-code-{failure.CoreErrorCode}"; return null;
        }
        return new(request, time, budget, initialized);
    }

    internal OrbitalTopocentricState? Evaluate(DateTimeOffset utc)
    {
        if (!budget.CanContinue()) return null;
        if (utc < interval.StartUtc || utc > interval.EndUtc)
            throw new ArgumentOutOfRangeException(nameof(utc), "Evaluation must stay in the query interval.");
        if (cache.TryGetValue(utc.UtcTicks, out var existing)) return existing;
        if (!propagator.TryPropagate(utc, out var state, out var failure, budget.TryEvaluation))
        {
            FailureReason = budget.Reason != OrbitalPredictionReason.None ? budget.Reason : OrbitalPredictionReason.PropagationFailure;
            FailureDetail = $"sgp4-{failure.Kind}-code-{failure.CoreErrorCode}"; return null;
        }
        try
        {
            var evaluated = observer.Evaluate(OrbitalCoordinateConverter.ToEarthFixed(state, time.Evaluate(utc)));
            if (evaluated.RangeKilometers < 50)
            {
                FailureReason = OrbitalPredictionReason.PropagationFailure;
                FailureDetail = "observer-range-outside-numerical-support-profile-v1"; return null;
            }
            cache.Add(utc.UtcTicks, evaluated);
            return evaluated;
        }
        catch (ArgumentException exception)
        {
            FailureReason = TimeFailure(exception); FailureDetail = exception.Message; return null;
        }
    }

    internal static OrbitalPredictionReason TimeFailure(ArgumentException exception)
        => exception.Message.Contains("leap", StringComparison.Ordinal)
            ? OrbitalPredictionReason.UnsupportedLeapCrossing : OrbitalPredictionReason.MissingEarthOrientation;
}
