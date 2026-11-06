namespace HVO.SkyMonitor.Astronomy;

internal sealed record OrbitalPassSearchResult(IReadOnlyList<OrbitalPassSummaryV1> Passes,
    OrbitalPredictionReason Reason, string Detail);

/// <summary>
/// Bounded numerical event search. Bernstein hulls enclose the fitted Cartesian polynomial;
/// the independently probed SGP4 remainder is an estimate, not an all-function certificate.
/// </summary>
internal static class OrbitalPassSearch
{
    internal const double CrossingBracketSeconds = .01;
    internal const double PeakBracketSeconds = .005;
    private const double MinimumCellSeconds = .00001;
    private const double PeakTieDegrees = 1e-12;
    private const int MaximumDepth = 32;

    internal static OrbitalPassSearchResult Search(OrbitalPassQuery query, OrbitalProductLineage lineage,
        OrbitalMeanElements elements, Func<DateTimeOffset, OrbitalTopocentricState?> evaluate, OrbitalWorkBudget budget)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(elements); ArgumentNullException.ThrowIfNull(evaluate);
        ArgumentNullException.ThrowIfNull(budget);
        query.Validate();
        return new SearchState(query, lineage, elements, evaluate, budget).Run();
    }

    private sealed record Cell(DateTimeOffset Start, DateTimeOffset End, OrbitalTopocentricState[] Samples,
        EnuVector[] Controls, OrbitalDirectionTube Tube)
    {
        internal double Seconds => (End - Start).TotalSeconds;
    }

    private sealed class SearchState(OrbitalPassQuery query, OrbitalProductLineage lineage,
        OrbitalMeanElements elements, Func<DateTimeOffset, OrbitalTopocentricState?> evaluate, OrbitalWorkBudget budget)
    {
        private readonly List<OrbitalPassSummaryV1> passes = [];
        private readonly Dictionary<long, OrbitalTopocentricState> cache = [];
        private readonly List<Cell> peakCells = [];
        private OrbitalPredictionReason reason;
        private string failureDetail = "pass-unresolved-tangent-singularity-or-convergence";
        private DateTimeOffset? passStart;
        private OrbitalPassBoundary startBoundary;
        private DateTimeOffset coveredEnd;
        private DateTimeOffset peakUtc;
        private DateTimeOffset? lastCrossing;
        private double peakAltitude = double.NegativeInfinity;

        internal OrbitalPassSearchResult Run()
        {
            if (!Continue()) return new(passes.AsReadOnly(), reason, "pass-work-limit");
            // Perigee timescale is a seed heuristic only: every seed still receives a tube test.
            var seed = Math.Min(300, 86400 / elements.MeanMotionRevolutionsPerDay *
                Math.Pow(1 - elements.Eccentricity, 1.5) / (64 * Math.Sqrt(1 + elements.Eccentricity)));
            if (!double.IsFinite(seed) || seed < MinimumCellSeconds)
                return new(passes.AsReadOnly(), OrbitalPredictionReason.IncompleteInterval, "pass-seed-outside-numerical-profile");
            var seedTicks = Math.Max(1, (long)(seed * TimeSpan.TicksPerSecond));
            var start = query.Request.Interval.StartUtc;
            var finish = query.Request.Interval.EndUtc;
            while (start < finish && Continue())
            {
                var end = start.AddTicks(Math.Min(seedTicks, finish.UtcTicks - start.UtcTicks));
                // finals2000A interpolation changes slope at UTC midnight. Do not fit across it.
                var midnight = new DateTimeOffset(start.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
                if (end > midnight) end = midnight;
                if (!Visit(start, end, null, 0)) break;
                start = end;
            }
            if (reason == OrbitalPredictionReason.None && Continue() && passStart is not null)
                Finish(finish, OrbitalPassBoundary.SearchWindow);
            Continue();
            if (reason != OrbitalPredictionReason.None && passStart is not null)
                Retain(coveredEnd, OrbitalPassBoundary.WorkLimit, reason);
            return new(passes.AsReadOnly(), reason, reason == OrbitalPredictionReason.None
                ? "pass-search-quartic-bernstein-numerical-remainder-v1"
                : reason == OrbitalPredictionReason.IncompleteInterval ? failureDetail
                : reason == OrbitalPredictionReason.PropagationFailure ? "pass-point-evaluation-failed" : "pass-work-limit");
        }

        private bool Continue()
        {
            // Always call CanContinue, including after a numerical failure, so cancellation throws.
            if (!budget.CanContinue()) { reason = budget.Reason; return false; }
            return reason == OrbitalPredictionReason.None;
        }

        private OrbitalTopocentricState? Point(DateTimeOffset utc)
        {
            if (!Continue()) return null;
            if (cache.TryGetValue(utc.UtcTicks, out var existing)) return existing;
            // The owning evaluator charges TryEvaluation immediately before the actual SGP4 call.
            var value = evaluate(utc);
            if (!budget.CanContinue()) { reason = budget.Reason; return null; }
            if (value is null) { reason = OrbitalPredictionReason.PropagationFailure; return null; }
            if (cache.Count == query.Request.Bounds.MaximumEvaluations)
            {
                reason = OrbitalPredictionReason.IncompleteInterval;
                return null;
            }
            cache.Add(utc.UtcTicks, value);
            return value;
        }

        private Cell? Fit(DateTimeOffset start, DateTimeOffset end)
        {
            var samples = new OrbitalTopocentricState[9];
            for (var i = 0; i <= 8; i++)
            {
                var point = Point(OrbitalNumericalPath.At(start, end, i, 8));
                if (point is null) return null;
                samples[i] = point;
            }
            var tube = OrbitalNumericalPath.Enclose(samples);
            var controls = OrbitalNumericalPath.Fit([Position(samples[0]), Position(samples[2]),
                Position(samples[4]), Position(samples[6]), Position(samples[8])]);
            return new(start, end, samples, controls, tube);
        }

        private bool Visit(DateTimeOffset start, DateTimeOffset end, double? parentRemainder, int depth)
        {
            if (!Continue()) return false;
            var cell = Fit(start, end);
            if (cell is null) return false;
            var remainder = cell.Tube.CartesianRemainderKilometers;
            // Residuals at/below the coordinate comparison resolution need not decrease under
            // halving (UTC nodes are rounded to ticks). Keep their full inflated remainder.
            var converged = Converged(remainder, parentRemainder);
            if (converged && cell.Tube.MinimumRangeKilometers > remainder)
            {
                var (lower, upper) = ElevationBounds(cell);
                if (upper < query.MinimumElevationDegrees) { coveredEnd = end; return true; }
                if (lower > query.MinimumElevationDegrees) return Above(cell);
                var a = cell.Samples[0].Horizontal.AltitudeDegrees - query.MinimumElevationDegrees;
                var b = cell.Samples[8].Horizontal.AltitudeDegrees - query.MinimumElevationDegrees;
                var errorDegrees = Math.Asin(Math.Min(1, remainder / cell.Tube.MinimumRangeKilometers)) * (180 / Math.PI);
                if (cell.Seconds <= CrossingBracketSeconds && a * b <= 0 && a != b &&
                    Math.Abs(b - a) > errorDegrees * 4 && Monotone(cell, b > a))
                {
                    if (a == 0 && lastCrossing == start)
                    {
                        if (b > 0) return Above(cell);
                        coveredEnd = end;
                        return true;
                    }
                    var crossing = a == 0 ? start : b == 0 ? end : OrbitalNumericalPath.At(start, end, 1, 2);
                    lastCrossing = crossing;
                    if (b > a)
                    {
                        if (!Begin(crossing, crossing == query.Request.Interval.StartUtc
                            ? OrbitalPassBoundary.SearchWindow : OrbitalPassBoundary.ResolvedCrossing)) return false;
                        peakCells.Add(cell);
                        Consider(end, cell.Samples[8]);
                        coveredEnd = end;
                        return true;
                    }
                    if (passStart is null) return Unresolved();
                    peakCells.Add(cell);
                    Consider(start, cell.Samples[0]);
                    coveredEnd = crossing;
                    return Finish(crossing, crossing == query.Request.Interval.EndUtc
                        ? OrbitalPassBoundary.SearchWindow : OrbitalPassBoundary.ResolvedCrossing);
                }
            }
            if (depth == MaximumDepth || cell.Seconds <= MinimumCellSeconds)
                return Unresolved(FormattableString.Invariant($"pass-unresolved-cell:{start:O}/{end:O};remainder={remainder:R};parent={parentRemainder:R};range={cell.Tube.MinimumRangeKilometers:R}"));
            var middle = OrbitalNumericalPath.At(start, end, 1, 2);
            return Visit(start, middle, remainder, depth + 1) && Visit(middle, end, remainder, depth + 1);
        }

        private bool Above(Cell cell)
        {
            if (passStart is null && !Begin(cell.Start, cell.Start == query.Request.Interval.StartUtc
                ? OrbitalPassBoundary.SearchWindow : OrbitalPassBoundary.WorkLimit)) return false;
            peakCells.Add(cell);
            for (var i = 0; i <= 8; i++) Consider(OrbitalNumericalPath.At(cell.Start, cell.End, i, 8), cell.Samples[i]);
            coveredEnd = cell.End;
            return true;
        }

        private bool Begin(DateTimeOffset start, OrbitalPassBoundary boundary)
        {
            if (passStart is not null || boundary == OrbitalPassBoundary.WorkLimit) return Unresolved("pass-unresolved-boundary-state");
            // Reserve while the query is live so an eventual work limit can retain this open pass.
            if (!budget.TryPass()) { reason = budget.Reason; return false; }
            passStart = start; startBoundary = boundary; coveredEnd = start;
            peakAltitude = double.NegativeInfinity; peakUtc = start;
            peakCells.Clear();
            return true;
        }

        private void Consider(DateTimeOffset utc, OrbitalTopocentricState point)
        {
            if (passStart is null || utc < passStart || point.Horizontal.AltitudeDegrees < query.MinimumElevationDegrees) return;
            var altitude = point.Horizontal.AltitudeDegrees;
            if (altitude > peakAltitude + PeakTieDegrees ||
                Math.Abs(altitude - peakAltitude) <= PeakTieDegrees && utc < peakUtc)
            {
                peakAltitude = altitude;
                peakUtc = utc;
            }
        }

        private bool Finish(DateTimeOffset end, OrbitalPassBoundary boundary)
        {
            foreach (var cell in peakCells)
            {
                if (!Continue()) return false;
                // Crossing cells include a small outside portion; keep the peak in the pass interval.
                var a = cell.Start < passStart!.Value ? passStart.Value : cell.Start;
                var b = cell.End > end ? end : cell.End;
                if (a >= b) continue;
                var clipped = a != cell.Start || b != cell.End ? Fit(a, b) : cell;
                if (clipped is null || !Peak(clipped, 0)) return false;
            }
            Retain(end, boundary, OrbitalPredictionReason.None);
            return true;
        }

        private bool Peak(Cell cell, int depth, double? parentRemainder = null)
        {
            if (!Continue()) return false;
            for (var i = 0; i <= 8; i++) Consider(OrbitalNumericalPath.At(cell.Start, cell.End, i, 8), cell.Samples[i]);
            var (_, upper) = ElevationBounds(cell);
            var remainder = cell.Tube.CartesianRemainderKilometers;
            var converged = parentRemainder is null || Converged(remainder, parentRemainder);
            var regular = converged && cell.Tube.MinimumRangeKilometers > remainder;
            if (regular && upper < peakAltitude - PeakTieDegrees) return true;
            var derivative = ElevationDerivative(cell.Controls);
            var scale = derivative.Max(Math.Abs);
            var flat = cell.Samples.Max(p => p.Horizontal.AltitudeDegrees) -
                cell.Samples.Min(p => p.Horizontal.AltitudeDegrees) <= PeakTieDegrees && scale <= 1e-12;
            // A definite Bernstein derivative sign excludes every fitted interior extremum,
            // including multiple maxima; no unimodality assumption or scalar sampled speed.
            if (regular && (flat || Monotone(cell, true) || Monotone(cell, false))) return true;
            if (regular && cell.Seconds <= PeakBracketSeconds) return true;
            if (depth == MaximumDepth || cell.Seconds <= MinimumCellSeconds) return Unresolved();
            var midpoint = OrbitalNumericalPath.At(cell.Start, cell.End, 1, 2);
            var left = Fit(cell.Start, midpoint);
            if (left is null || !Peak(left, depth + 1, remainder)) return false;
            var right = Fit(midpoint, cell.End);
            return right is not null && Peak(right, depth + 1, remainder);
        }

        private void Retain(DateTimeOffset end, OrbitalPassBoundary boundary, OrbitalPredictionReason failure)
        {
            if (passStart is { } start && end > start && double.IsFinite(peakAltitude) && peakUtc <= end)
                passes.Add(OrbitalPredictionJson.CreatePass(new(OrbitalPredictionJson.PassSchemaVersion, "", lineage,
                    query.Request.Interval, new(start, end), peakUtc, peakAltitude, query.MinimumElevationDegrees,
                    startBoundary, boundary, query.Request.Observer, query.Request.Model,
                    failure == OrbitalPredictionReason.None ? OrbitalExecutionStatus.Complete : OrbitalExecutionStatus.Partial, failure)));
            passStart = null;
            peakCells.Clear();
        }

        private bool Unresolved(string detail = "pass-unresolved-tangent-singularity-or-convergence")
        { reason = OrbitalPredictionReason.IncompleteInterval; failureDetail = detail; return false; }

        private static bool Converged(double remainder, double? parentRemainder)
            => remainder <= OrbitalNumericalPath.CartesianFloorKilometers * OrbitalNumericalPath.RemainderMultiplier ||
                parentRemainder is { } parent && remainder <= parent * .9;

        private (double Lower, double Upper) ElevationBounds(Cell cell)
        {
            var tube = cell.Tube;
            if (query.MinimumElevationDegrees == 0)
            {
                // Up is a scalar Bernstein polynomial: tighter than a spherical direction cap.
                var minimum = cell.Controls.Min(static p => p.Up) - tube.CartesianRemainderKilometers;
                var maximum = cell.Controls.Max(static p => p.Up) + tube.CartesianRemainderKilometers;
                if (maximum < 0) return (-90, -double.Epsilon);
                if (minimum > 0) return (double.Epsilon, UpperAltitude());
            }
            return (Math.Max(-90, tube.Center.AltitudeDegrees - tube.RadiusRadians * (180 / Math.PI)), UpperAltitude());

            double UpperAltitude() => Math.Min(90, tube.Center.AltitudeDegrees + tube.RadiusRadians * (180 / Math.PI));
        }

        private static bool Monotone(Cell cell, bool increasing)
        {
            var derivative = ElevationDerivative(cell.Controls);
            if (!derivative.All(v => increasing ? v > 0 : v < 0)) return false;
            for (var i = 1; i <= 8; i++)
                if (increasing ? cell.Samples[i].Horizontal.AltitudeDegrees <= cell.Samples[i - 1].Horizontal.AltitudeDegrees
                    : cell.Samples[i].Horizontal.AltitudeDegrees >= cell.Samples[i - 1].Horizontal.AltitudeDegrees) return false;
            return true;
        }
    }

    private static EnuVector Position(OrbitalTopocentricState point) => point.UnitDirection * point.RangeKilometers;

    // Sign of d altitude / ds = sign[u'(e²+n²) - u(ee'+nn')]. Products remain
    // Bernstein polynomials (degree eleven); their coefficient hull includes every derivative value.
    private static double[] ElevationDerivative(EnuVector[] controls)
    {
        var scale = controls.Max(static p => p.Length);
        var e = controls.Select(p => p.East / scale).ToArray();
        var n = controls.Select(p => p.North / scale).ToArray();
        var u = controls.Select(p => p.Up / scale).ToArray();
        var horizontalSquared = Add(Product(e, e), Product(n, n));
        var horizontalRate = Add(Product(e, Derivative(e)), Product(n, Derivative(n)));
        return Add(Product(Derivative(u), horizontalSquared), Product(u, horizontalRate), -1);
    }

    private static double[] Derivative(double[] values) => Enumerable.Range(0, values.Length - 1)
        .Select(i => (values[i + 1] - values[i]) * (values.Length - 1)).ToArray();

    private static double[] Add(double[] a, double[] b, double factor = 1) => a.Select((v, i) => v + b[i] * factor).ToArray();

    private static double[] Product(double[] a, double[] b)
    {
        var m = a.Length - 1; var n = b.Length - 1;
        var result = new double[m + n + 1];
        for (var k = 0; k <= m + n; k++)
            for (var i = Math.Max(0, k - n); i <= Math.Min(m, k); i++)
                result[k] += a[i] * b[k - i] * Choose(m, i) * Choose(n, k - i) / Choose(m + n, k);
        return result;
    }

    private static double Choose(int n, int k)
    {
        var value = 1d;
        for (var i = 1; i <= k; i++) value = value * (n - i + 1) / i;
        return value;
    }
}
