namespace HVO.SkyMonitor.Astronomy;

internal sealed record OrbitalExposureSamplingResult(OrbitalExposureTrackV1? Track, OrbitalPredictionReason Reason, string Detail);

/// <summary>Bounded numerical exposure sampling; the remainder estimate is not a physical orbit-error bound.</summary>
internal static class OrbitalExposureSampler
{
    internal const int MaximumDepth = 24;
    internal const double TemporalFloorSeconds = .0000008;
    private const double BoundaryBracketSeconds = .0001;
    private const double PixelFloor = 1e-8;
    // ASCII numeric sample/segment fields fit within 768 bytes per point, with 256 KiB
    // reserved for validated metadata (including UTF-8 population exclusion text).
    private const int MaximumProductSamples = (OrbitalPredictionJson.MaximumProductBytes - 256 * 1024) / 768;

    internal static OrbitalExposureSamplingResult Sample(OrbitalExposureQuery query, OrbitalProductLineage lineage,
        Func<DateTimeOffset, OrbitalTopocentricState?> evaluate, OrbitalWorkBudget budget)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(evaluate); ArgumentNullException.ThrowIfNull(budget);
        query.Validate();
        return new Sampling(query, lineage, evaluate, budget).Run();
    }

    private sealed record Probe(OrbitalTopocentricState State, OrbitalProjectedSample Projected);
    private sealed record Edge(long Start, double Error);

    private sealed class Sampling(OrbitalExposureQuery query, OrbitalProductLineage lineage,
        Func<DateTimeOffset, OrbitalTopocentricState?> evaluate, OrbitalWorkBudget budget)
    {
        private readonly OrbitalSampleProjector projector = new(query.Geometry);
        private readonly Dictionary<long, Probe?> cache = [];
        private readonly SortedDictionary<long, OrbitalProjectedSample> output = [];
        private readonly SortedDictionary<long, OrbitalProjectedSample> staged = [];
        private readonly Dictionary<long, Edge> edges = [];
        private readonly HashSet<long> unresolved = [];
        private bool materialized;
        private bool productLimit;
        private OrbitalPredictionReason failure;
        private string? failureDetail;
        private double achievedError;

        internal OrbitalExposureSamplingResult Run()
        {
            var interval = query.Request.Interval;
            var stepTicks = Math.Max(1, (long)(query.Request.Bounds.MaximumSampleStepSeconds * TimeSpan.TicksPerSecond));
            var start = interval.StartUtc;
            if (start == interval.EndUtc)
            {
                if (Get(start) is { } point) Emit(point);
            }
            while (start < interval.EndUtc && budget.CanContinue() && !productLimit)
            {
                var end = start.AddTicks(Math.Min(stepTicks, interval.EndUtc.UtcTicks - start.UtcTicks));
                Cell(start, end, 0, null);
                start = end;
            }
            // CanContinue is also the cancellation checkpoint during product preparation, even on exhausted work.
            budget.CanContinue();
            var reason = budget.Reason != OrbitalPredictionReason.None ? budget.Reason : failure;
            if (!materialized && reason == OrbitalPredictionReason.None && staged.Count > 0)
                return new(null, OrbitalPredictionReason.None, "interval-geometrically-excluded");
            if (output.Count == 0)
                return new(null, reason == OrbitalPredictionReason.None ? OrbitalPredictionReason.PropagationFailure : reason,
                    "No useful topocentric sample was available within the work bounds.");
            var points = output.Values.ToArray();
            var samples = points.Select(static p => p.Sample).ToArray();
            var maximumStep = 0d;
            for (var i = 1; i < samples.Length; i++)
            {
                budget.CanContinue();
                maximumStep = Math.Max(maximumStep, (samples[i].Utc - samples[i - 1].Utc).TotalSeconds);
            }
            reason = budget.Reason != OrbitalPredictionReason.None ? budget.Reason : reason;
            if (reason == OrbitalPredictionReason.None &&
                (samples[0].Utc != interval.StartUtc || samples[^1].Utc != interval.EndUtc ||
                    maximumStep > query.Request.Bounds.MaximumSampleStepSeconds))
                reason = OrbitalPredictionReason.IncompleteInterval;
            var segments = Segments(points, reason);
            var clock = query.Timing.Clock;
            var uncertainty = new OrbitalPredictionUncertainty(new(null, "degrees", "physical-orbit-error-unknown"),
                new(clock.MaximumErrorSeconds ?? clock.EstimatedErrorSeconds, "seconds",
                    clock.MaximumErrorSeconds is not null ? "reported-clock-maximum-error; " + clock.Source :
                    clock.EstimatedErrorSeconds is not null ? "reported-clock-estimated-error; " + clock.Source : "clock-error-unknown; " + clock.Source),
                new(null, "pixels", query.Geometry.Basis == OrbitalGeometryBasis.AcceptedMeasured
                    ? "accepted-measured-geometry; pixel-calibration-error-not-supplied" : "configured-rig; pixel-calibration-error-unknown"),
                new(achievedError, "pixels", OrbitalNumericalPath.EstimatorVersion + "; numerical estimate, not a global proof"));
            var track = OrbitalPredictionJson.CreateTrack(new(OrbitalPredictionJson.TrackSchemaVersion, "", lineage,
                interval, new(samples[0].Utc, samples[^1].Utc), query.Request.Observer, query.Geometry, query.Timing, query.Request.Model,
                samples, segments, uncertainty, new(query.Request.Bounds.MaximumSampleStepSeconds,
                    query.Request.Bounds.MaximumChordErrorPixels, maximumStep, achievedError),
                reason == OrbitalPredictionReason.None ? OrbitalExecutionStatus.Complete : OrbitalExecutionStatus.Partial, reason));
            budget.CanContinue();
            if (budget.Reason != OrbitalPredictionReason.None && budget.Reason != reason)
            {
                reason = budget.Reason;
                track = OrbitalPredictionJson.CreateTrack(track with { Status = OrbitalExecutionStatus.Partial, Reason = reason });
            }
            return new(track, reason, reason == OrbitalPredictionReason.None
                ? "Requested interval resolved with " + OrbitalNumericalPath.EstimatorVersion + "; illumination remains unknown."
                : "Useful samples retained; unresolved or work-limited intervals are not joined. " + failureDetail);
        }

        private Probe? Get(DateTimeOffset utc)
        {
            if (!budget.CanContinue()) return null;
            if (cache.TryGetValue(utc.UtcTicks, out var retained)) return retained;
            // The caller charges actual propagation inside evaluate. Probes never reserve output samples.
            var state = evaluate(utc);
            budget.CanContinue();
            var probe = state is null ? null : new Probe(state, projector.Project(utc, state));
            cache.Add(utc.UtcTicks, probe);
            return probe;
        }

        private bool Emit(Probe probe)
        {
            var ticks = probe.Projected.Sample.Utc.UtcTicks;
            if (output.ContainsKey(ticks)) return true;
            if (!materialized)
            {
                staged.TryAdd(ticks, probe.Projected);
                if (probe.Projected.Sample.Pixel is null) return true;
                // Excluded objects cost evaluations only. Once useful geometry appears, materialize
                // preceding coverage points as well, so the v1 maximum-step fact includes every gap.
                materialized = true;
                foreach (var (utc, point) in staged)
                {
                    if (!ReserveSample()) { staged.Clear(); return false; }
                    output.Add(utc, point);
                }
                staged.Clear();
                return true;
            }
            if (!ReserveSample()) return false;
            output.Add(ticks, probe.Projected);
            return true;
        }

        private bool ReserveSample()
        {
            if (output.Count < MaximumProductSamples) return budget.TrySample();
            budget.CanContinue();
            productLimit = true;
            failure = OrbitalPredictionReason.IncompleteInterval;
            failureDetail = "Conservative v1 serialized-product byte allowance reached.";
            return false;
        }

        private void Cell(DateTimeOffset start, DateTimeOffset end, int depth, double? parentRemainder)
        {
            if (!budget.CanContinue() || productLimit) return;
            var first = Get(start);
            if (first is not null && !Emit(first)) return;
            var probes = new Probe?[9];
            probes[0] = first;
            for (var i = 1; i < 9 && budget.CanContinue(); i++)
                probes[i] = Get(OrbitalNumericalPath.At(start, end, i, 8));
            if (!budget.CanContinue()) return;
            var canSplit = depth < MaximumDepth && (end - start).TotalSeconds > TemporalFloorSeconds && end.UtcTicks - start.UtcTicks >= 16;
            if (probes.Any(static p => p is null))
            {
                if (canSplit && probes.Any(static p => p is not null)) Split(start, end, depth, null);
                else Unresolved(probes, OrbitalPredictionReason.PropagationFailure);
                return;
            }
            var actual = probes.Select(static p => p!).ToArray();
            var tube = OrbitalNumericalPath.Enclose(actual.Select(static p => p.State).ToArray());
            var classification = projector.Classify(tube);
            // At() rounds to UTC ticks. Nonuniform one-tick offsets stop residuals converging at
            // high rates; retain that residual in the tube, and recognize its numerical plateau.
            var tickPlateau = 4 * OrbitalNumericalPath.CartesianFloorKilometers;
            for (var i = 1; i < 9; i++)
            {
                var ticks = actual[i].Projected.Sample.Utc.UtcTicks - actual[i - 1].Projected.Sample.Utc.UtcTicks;
                if (ticks > 0) tickPlateau = Math.Max(tickPlateau, 16 *
                    (actual[i].State.UnitDirection * actual[i].State.RangeKilometers -
                        actual[i - 1].State.UnitDirection * actual[i - 1].State.RangeKilometers).Length / ticks);
            }
            var converged = parentRemainder is { } parent && tube.CartesianRemainderKilometers <=
                Math.Max(tickPlateau, parent * .8) || !canSplit && tube.CartesianRemainderKilometers <= tickPlateau;
            if (actual.All(static p => p.State.RangeKilometers <= 4 * OrbitalNumericalPath.CartesianFloorKilometers))
            {
                Unresolved(actual, OrbitalPredictionReason.IncompleteInterval);
                return;
            }
            if (converged && classification.Excluded is not null)
            {
                Emit(actual[^1]);
                return;
            }
            if (converged && classification.Drawable && actual.All(static p => p.Projected.Sample.Pixel is not null))
            {
                var error = ChordError(actual, tube, classification.PixelsPerRadian);
                // Keep qualification headroom rather than filling the request with an empirical remainder.
                if (error <= query.Request.Bounds.MaximumChordErrorPixels / 2)
                {
                    if (Emit(actual[^1])) AddEdge(actual[0], actual[^1], error);
                    return;
                }
            }
            if (converged && (end - start).TotalSeconds <= BoundaryBracketSeconds &&
                ResolveBoundary(actual, tube, classification.PixelsPerRadian)) return;
            if (!canSplit)
            {
                failureDetail ??= $"Unresolved {start:O}..{end:O}; radius={tube.RadiusRadians:R}, range={tube.MinimumRangeKilometers:R}, remainder={tube.CartesianRemainderKilometers:R}, drawable={classification.Drawable}, excluded={classification.Excluded}, k={classification.PixelsPerRadian:R}.";
                Unresolved(actual, OrbitalPredictionReason.IncompleteInterval);
                return;
            }
            // An effectively stationary tube on a boundary cannot be made decisive by repeated subdivision.
            if (converged && tube.RadiusRadians <= 4 * OrbitalNumericalPath.CartesianFloorKilometers / tube.MinimumRangeKilometers &&
                !classification.Drawable && classification.Excluded is null)
            {
                Unresolved(actual, OrbitalPredictionReason.IncompleteInterval);
                return;
            }
            Split(start, end, depth, tube.CartesianRemainderKilometers);
        }

        private void Split(DateTimeOffset start, DateTimeOffset end, int depth, double? remainder)
        {
            var ticks = end.UtcTicks - start.UtcTicks;
            // Keep all nine nodes exact when the incoming cell has an eight-tick stencil.
            var half = ticks % 8 == 0 ? Math.Max(8, ticks / 16 * 8) : ticks / 2;
            var middle = start.AddTicks(half);
            Cell(start, middle, depth + 1, remainder);
            if (budget.CanContinue() && !productLimit) Cell(middle, end, depth + 1, remainder);
        }

        private double ChordError(Probe[] probes, OrbitalDirectionTube tube, double k)
        {
            var pixels = probes.Select(static p => p.Projected.Sample.Pixel!.Value).Select(static p => new EnuVector(p.X, p.Y, 0)).ToArray();
            var controls = OrbitalNumericalPath.Fit([pixels[0], pixels[2], pixels[4], pixels[6], pixels[8]]);
            var hull = 0d;
            for (var i = 0; i < 5; i++)
                hull = Math.Max(hull, (controls[i] - (pixels[0] * (1 - i / 4d) + pixels[8] * (i / 4d))).Length);
            var residual = 0d;
            for (var i = 1; i < 8; i += 2)
                residual = Math.Max(residual, (pixels[i] - OrbitalNumericalPath.Evaluate(controls, i / 8d)).Length);
            var remainder = k * Math.Asin(Math.Min(1, tube.CartesianRemainderKilometers / tube.MinimumRangeKilometers)) * projector.RemainingPixelScaleBound;
            return hull + Math.Max(OrbitalNumericalPath.RemainderMultiplier * residual, remainder) + PixelFloor;
        }

        private bool ResolveBoundary(Probe[] probes, OrbitalDirectionTube tube, double k)
        {
            if (!double.IsFinite(k) || !(tube.MinimumRangeKilometers > 0)) return false;
            var clips = probes.Select(static p => p.Projected.ClipReason).ToArray();
            var reasons = clips.Where(static r => r is not null).Distinct().ToArray();
            if (reasons.Length == 0)
            {
                // A well-conditioned crossing may fall exactly on a dyadic cell endpoint.
                reasons = new[] { OrbitalClipReason.Horizon, OrbitalClipReason.Sensor, OrbitalClipReason.Aperture }
                    .Where(r => r != OrbitalClipReason.Horizon || query.Geometry.HorizonPolicy == HorizonPolicy.GeometricHorizon)
                    .Where(r => new[] { probes[0], probes[^1] }.Any(p =>
                        projector.BoundaryCoordinate(p.State, r) is { } v && Math.Abs(v) <=
                        (r == OrbitalClipReason.Horizon ? 4 * tube.CartesianRemainderKilometers :
                            4 * k * tube.CartesianRemainderKilometers / tube.MinimumRangeKilometers + PixelFloor)))
                    .Select(static r => (OrbitalClipReason?)r).ToArray();
            }
            if (reasons.Length != 1) return false;
            var transitions = 0;
            for (var i = 1; i < 9; i++) if (clips[i] != clips[i - 1]) transitions++;
            if (transitions > 1) return false;
            var values = probes.Select(p => projector.BoundaryCoordinate(p.State, reasons[0]!.Value)).ToArray();
            if (values.Any(static v => v is null || !double.IsFinite(v.Value))) return false;
            var controls = OrbitalNumericalPath.Fit([new(values[0]!.Value, 0, 0), new(values[2]!.Value, 0, 0),
                new(values[4]!.Value, 0, 0), new(values[6]!.Value, 0, 0), new(values[8]!.Value, 0, 0)]);
            var residual = 0d;
            for (var i = 1; i < 8; i += 2)
                residual = Math.Max(residual, Math.Abs(values[i]!.Value - OrbitalNumericalPath.Evaluate(controls, i / 8d).East));
            var floor = reasons[0] == OrbitalClipReason.Horizon ? tube.CartesianRemainderKilometers :
                k * tube.CartesianRemainderKilometers / tube.MinimumRangeKilometers + PixelFloor;
            if (transitions == 0 && Math.Min(Math.Abs(values[0]!.Value), Math.Abs(values[8]!.Value)) > 4 * floor) return false;
            var direction = Math.Sign(controls[4].East - controls[0].East);
            for (var i = 0; i < 4; i++)
                if (direction * 4 * (controls[i + 1].East - controls[i].East) <= 32 * residual + 4 * floor) return false;
            // The whole tube's image diameter also bounds each retained short drawable chord.
            var error = 2 * k * tube.RadiusRadians * projector.RemainingPixelScaleBound + PixelFloor;
            if (error > query.Request.Bounds.MaximumChordErrorPixels / 2) return false;
            for (var i = 0; i < probes.Length; i++)
            {
                if (!Emit(probes[i])) return true;
                if (i > 0 && clips[i] is null && clips[i - 1] is null) AddEdge(probes[i - 1], probes[i], error);
            }
            return true;
        }

        private void Unresolved(IEnumerable<Probe?> probes, OrbitalPredictionReason reason)
        {
            if (failure == OrbitalPredictionReason.None) failure = reason;
            foreach (var probe in probes)
            {
                if (!budget.CanContinue()) break;
                if (probe is not null)
                {
                    unresolved.Add(probe.Projected.Sample.Utc.UtcTicks);
                    if (!Emit(probe)) break;
                }
            }
        }

        private void AddEdge(Probe start, Probe end, double error)
        {
            var a = start.Projected.Sample.Utc.UtcTicks;
            var b = end.Projected.Sample.Utc.UtcTicks;
            if (a == b) return;
            edges[b] = new(a, error);
            achievedError = Math.Max(achievedError, error);
        }

        private OrbitalTrackSegment[] Segments(OrbitalProjectedSample[] points, OrbitalPredictionReason reason)
        {
            var result = new List<OrbitalTrackSegment>();
            var workLimited = reason is OrbitalPredictionReason.SampleBudget or OrbitalPredictionReason.EvaluationBudget or OrbitalPredictionReason.Deadline;
            var i = 0;
            while (i < points.Length)
            {
                budget.CanContinue();
                if (points[i].Sample.Pixel is null) { i++; continue; }
                var first = i;
                while (i + 1 < points.Length && points[i + 1].Sample.Pixel is not null &&
                    edges.TryGetValue(points[i + 1].Sample.Utc.UtcTicks, out var edge) && edge.Start == points[i].Sample.Utc.UtcTicks) i++;
                var startReason = first == 0 && points[first].Sample.Utc == query.Request.Interval.StartUtc ? OrbitalClipReason.WindowBoundary :
                    unresolved.Contains(points[first].Sample.Utc.UtcTicks) ? OrbitalClipReason.ProjectionFailure :
                    first > 0 && points[first - 1].ClipReason is { } before ? before : OrbitalClipReason.ProjectionFailure;
                var endReason = points[i].Sample.Utc == query.Request.Interval.EndUtc ? OrbitalClipReason.WindowBoundary :
                    unresolved.Contains(points[i].Sample.Utc.UtcTicks) ? OrbitalClipReason.ProjectionFailure :
                    i + 1 < points.Length && points[i + 1].ClipReason is { } after ? after :
                    workLimited && i == points.Length - 1 ? OrbitalClipReason.SamplingBudget : OrbitalClipReason.ProjectionFailure;
                result.Add(new(first, i - first + 1, startReason, endReason));
                i++;
            }
            return result.ToArray();
        }
    }
}
