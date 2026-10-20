namespace HVO.SkyMonitor.Astronomy;

internal sealed record OrbitalPassArcSamplingResult(OrbitalPassArcV1? Arc, OrbitalPredictionReason Reason, string Detail);

/// <summary>Deterministic useful prefix. An unresolved cell stops the run, so no emitted edge can bridge a gap.</summary>
internal static class OrbitalPassArcSampler
{
    internal static OrbitalPassArcSamplingResult Sample(OrbitalPassArcQuery query,
        Func<DateTimeOffset, OrbitalTopocentricState?> evaluate, OrbitalWorkBudget budget)
        => new Sampling(query, evaluate, budget).Run();

    private sealed class Sampling(OrbitalPassArcQuery query,
        Func<DateTimeOffset, OrbitalTopocentricState?> evaluate, OrbitalWorkBudget budget)
    {
        private readonly List<OrbitalTrackSample> samples = [];
        private double estimatedError;
        private OrbitalPredictionReason failure;

        internal OrbitalPassArcSamplingResult Run()
        {
            var interval = query.Pass.PassInterval;
            var start = interval.StartUtc;
            if (Get(start) is { } first) Emit(start, first);
            if (start < interval.EndUtc && query.Bounds.MaximumSampleStepSeconds < TimeSpan.FromTicks(1).TotalSeconds)
                failure = OrbitalPredictionReason.IncompleteInterval;
            var stepTicks = Math.Max(1, (long)(query.Bounds.MaximumSampleStepSeconds * TimeSpan.TicksPerSecond));
            while (start < interval.EndUtc && failure == OrbitalPredictionReason.None && budget.CanContinue())
            {
                var end = start.AddTicks(Math.Min(stepTicks, interval.EndUtc.UtcTicks - start.UtcTicks));
                if (query.Pass.PeakUtc > start && query.Pass.PeakUtc < end) end = query.Pass.PeakUtc;
                // finals2000A interpolation knots are integer MJD, i.e. UTC midnight.
                var midnight = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
                if (midnight < end) end = midnight;
                if (!Cell(start, end, 0)) break;
                start = end;
            }
            budget.CanContinue();
            var reason = budget.Reason != OrbitalPredictionReason.None ? budget.Reason : failure;
            if (samples.Count == 0)
                return new(null, reason == OrbitalPredictionReason.None ? OrbitalPredictionReason.PropagationFailure : reason, "arc-no-useful-samples");
            if (reason == OrbitalPredictionReason.None && samples[^1].Utc != interval.EndUtc) reason = OrbitalPredictionReason.IncompleteInterval;
            if (reason == OrbitalPredictionReason.None && query.Pass.Status != OrbitalExecutionStatus.Complete) reason = query.Pass.Reason;
            var step = 0d;
            for (var i = 1; i < samples.Count; i++)
            {
                budget.CanContinue();
                step = Math.Max(step, (samples[i].Utc - samples[i - 1].Utc).TotalSeconds);
            }
            var anchors = new OrbitalPassArcAnchorsV1(Index(interval.StartUtc), Index(query.Pass.PeakUtc), Index(interval.EndUtc));
            var arc = OrbitalPassArcJson.Create(new(OrbitalPassArcJson.SchemaVersion, "", query.IdentitySha256, query.SearchQuerySha256,
                query.Pass, new(samples[0].Utc, samples[^1].Utc), samples, [new(0, samples.Count)], anchors,
                new(query.Bounds.MaximumSampleStepSeconds, query.Bounds.MaximumAngularErrorDegrees, step, estimatedError,
                    OrbitalPassArcJson.InterpolationVersion, OrbitalPassArcJson.EstimatorVersion),
                new(null, "degrees", "physical-orbit-error-unqualified"),
                reason == OrbitalPredictionReason.None ? OrbitalExecutionStatus.Complete : OrbitalExecutionStatus.Partial, reason),
                () => budget.CanContinue());
            budget.CanContinue();
            if (budget.Reason != OrbitalPredictionReason.None && budget.Reason != reason)
            {
                reason = budget.Reason;
                arc = OrbitalPassArcJson.Create(arc with { Status = OrbitalExecutionStatus.Partial, Reason = reason }, () => budget.CanContinue());
            }
            budget.CanContinue();
            return new(arc, reason, reason == OrbitalPredictionReason.None ? "arc-resolved-empirical-angular-estimate" : "arc-useful-prefix-retained");
        }

        private OrbitalTopocentricState? Get(DateTimeOffset utc)
        {
            if (!budget.CanContinue()) return null;
            var point = evaluate(utc);
            budget.CanContinue();
            if (point is null) failure = OrbitalPredictionReason.PropagationFailure;
            return point;
        }

        private bool Cell(DateTimeOffset start, DateTimeOffset end, int depth)
        {
            if (!budget.CanContinue()) return false;
            var probes = new OrbitalTopocentricState[9];
            for (var i = 0; i < probes.Length; i++)
            {
                var point = Get(OrbitalNumericalPath.At(start, end, i, 8));
                if (point is null || !budget.CanContinue()) return false;
                probes[i] = point;
            }
            var error = OrbitalAngularPath.Estimate(probes);
            if (error <= query.Bounds.MaximumAngularErrorDegrees)
            {
                if (!Emit(end, probes[8])) return false;
                estimatedError = Math.Max(estimatedError, error);
                return true;
            }
            if (depth >= 24 || end.UtcTicks - start.UtcTicks < 16)
            {
                failure = OrbitalPredictionReason.IncompleteInterval;
                return false;
            }
            var middle = OrbitalNumericalPath.At(start, end, 1, 2);
            return Cell(start, middle, depth + 1) && Cell(middle, end, depth + 1);
        }

        private bool Emit(DateTimeOffset utc, OrbitalTopocentricState point)
        {
            if (!budget.TrySample()) return false;
            samples.Add(new(utc, point.Horizontal, null, point.RangeKilometers, point.RangeRateKilometersPerSecond,
                point.AngularSpeedDegreesPerSecond, OrbitalIllumination.Unknown));
            return true;
        }

        private int? Index(DateTimeOffset utc)
        {
            var index = samples.FindIndex(s => s.Utc == utc);
            return index < 0 ? null : index;
        }
    }
}
