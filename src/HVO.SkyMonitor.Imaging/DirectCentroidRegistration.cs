using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Orientation-preserving similarity in delivered-image pixel-edge coordinates.</summary>
public readonly record struct ImageSimilarity(double A, double B, double Tx, double Ty) : IImageRegistrationMap
{
    public PixelPoint? SourceToReference(PixelPoint source) =>
        new PixelPoint(A * source.X - B * source.Y + Tx, B * source.X + A * source.Y + Ty);

    public PixelPoint? ReferenceToSource(PixelPoint reference)
    {
        var x = reference.X - Tx;
        var y = reference.Y - Ty;
        var determinant = A * A + B * B;
        return determinant > 0 ? new PixelPoint((A * x + B * y) / determinant, (A * y - B * x) / determinant) : null;
    }
}

public enum DirectRegistrationStatus
{
    Verified,
    InsufficientCentroids,
    NoDirectMatch,
    AmbiguousMatch,
    PoorFieldCoverage,
    ResidualExceeded,
    AmbiguityCheckLimitExceeded
}

public sealed record RegistrationCentroidMatch(int SourceIndex, int ReferenceIndex, double ResidualPixels);

public sealed record DirectRegistrationCheck(DirectRegistrationStatus Status,
    IReadOnlyList<RegistrationCentroidMatch> Fit, IReadOnlyList<RegistrationCentroidMatch> Verification);

public sealed record DirectSimilarityRegistration(ImageSimilarity? Map, DirectRegistrationCheck Check);

/// <summary>Bounded pair fitting and independent point checks for direct registration.</summary>
public static class DirectCentroidRegistration
{
    public const string FitVersion = "direct-centroid-similarity-v3";
    public const string PlateCheckVersion = "direct-plate-centroid-check-v1";
    private const int FeatureLimit = 24;
    private const int AmbiguityFieldLimit = 192;
    private const int AmbiguityProbeLimit = 250_000_000;
    private const int MinimumFitCount = 5;
    private const int MinimumVerificationCount = 3;
    private const double CandidateRadius = 1.25;
    private const double MinimumScale = .97;
    private const double MaximumScale = 1.03;

    public static DirectSimilarityRegistration Fit(IReadOnlyList<StellarDetection> source,
        IReadOnlyList<StellarDetection> reference, int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = Select(source, width, height);
        var references = Select(reference, width, height);
        if (Math.Min(sources.Length, references.Length) < MinimumFitCount + MinimumVerificationCount)
        {
            return Failure(DirectRegistrationStatus.InsufficientCentroids);
        }

        if (Math.Max(sources.Length, references.Length) > AmbiguityFieldLimit)
        {
            return Failure(DirectRegistrationStatus.AmbiguityCheckLimitExceeded);
        }

        // Freeze both holdouts before generating or selecting the transform.
        // A coarse annulus keeps membership stable under ordinary small field drift when enough detections exist.
        // Sparse fields use a fixed rank split and fail closed if brightness changes leave too little shared support.
        var dense = Math.Min(sources.Length, references.Length) > FeatureLimit;
        var (training, heldOut) = Partition(references, width, height, dense);
        var (sourceTraining, sourceHeldOut) = Partition(sources, width, height, dense);
        var hits = new Match[FeatureLimit];
        var refinedHits = new Match[FeatureLimit];
        Candidate? best = null;
        _ = Search(sourceTraining, training, null);

        if (best is null)
        {
            return Failure(DirectRegistrationStatus.NoDirectMatch);
        }
        var trainingStatus = Check(best.Fit, width, height, MinimumFitCount, .50);
        if (trainingStatus != DirectRegistrationStatus.Verified)
        {
            return Failure(trainingStatus, best.Fit);
        }
        var count = MatchPoints(best.Map, sourceHeldOut, heldOut, hits);
        var verification = hits.AsSpan(0, count);
        var status = Check(verification, width, height, MinimumVerificationCount, .55);
        var report = Report(status, best.Fit, verification);
        if (status != DirectRegistrationStatus.Verified)
        {
            return new(null, report);
        }

        // Check for aliases against every eligible point. Truncating by flux here
        // could certify a brighter repeated field after excluding genuine points.
        // Only the independent training and holdout sets can select a map.
        hits = new Match[references.Length];
        refinedHits = new Match[references.Length];
        var veto = Search(sources, references, new ReferenceGrid(references, width, height));
        cancellationToken.ThrowIfCancellationRequested();
        return veto == DirectRegistrationStatus.Verified ? new(best.Map, report) : new(null, report with { Status = veto });

        DirectRegistrationStatus Search(StellarDetection[] sourceField, StellarDetection[] referenceField, ReferenceGrid? grid)
        {
            var minimumLength = Math.Min(width, height) * .12;
            var referencePairs = Pairs(referenceField, minimumLength);
            var sourcePairs = Pairs(sourceField, minimumLength);
            sourcePairs.Sort(static (first, second) => first.Distance.CompareTo(second.Distance));
            foreach (var referencePair in referencePairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var low = Math.BitDecrement(referencePair.Distance / MaximumScale);
                var high = Math.BitIncrement(referencePair.Distance / MinimumScale);
                for (var index = LowerBound(sourcePairs, low); index < sourcePairs.Count; index++)
                {
                    var sourcePair = sourcePairs[index];
                    if (sourcePair.Distance > high)
                    {
                        break;
                    }
                    if (grid is not null && (index & 63) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    var scale = referencePair.Distance / sourcePair.Distance;
                    if (scale is < MinimumScale or > MaximumScale)
                    {
                        continue;
                    }
                    var competes = Consider(sourcePair.First, sourcePair.Second, referencePair.First, referencePair.Second, sourceField, referenceField, grid) ||
                        Consider(sourcePair.Second, sourcePair.First, referencePair.First, referencePair.Second, sourceField, referenceField, grid);
                    if (grid?.Exhausted == true)
                    {
                        return DirectRegistrationStatus.AmbiguityCheckLimitExceeded;
                    }
                    if (competes)
                    {
                        return DirectRegistrationStatus.AmbiguousMatch;
                    }
                }
            }
            return DirectRegistrationStatus.Verified;
        }

        bool Consider(StellarDetection sourceFirst, StellarDetection sourceSecond,
            StellarDetection referenceFirst, StellarDetection referenceSecond,
            StellarDetection[] sourceField, StellarDetection[] referenceField, ReferenceGrid? grid)
        {
            var seed = Seed(sourceFirst.Pixel, sourceSecond.Pixel, referenceFirst.Pixel, referenceSecond.Pixel);
            var count = MatchPoints(seed, sourceField, referenceField, hits, grid);
            // An alternate must have enough total correspondences to support both verification sets.
            var minimum = grid is null ? MinimumFitCount : MinimumFitCount + MinimumVerificationCount;
            if (count < minimum)
            {
                return false;
            }
            var refined = Refine(hits.AsSpan(0, count));
            var scaleSquared = refined.A * refined.A + refined.B * refined.B;
            if (!double.IsFinite(scaleSquared) || scaleSquared is < MinimumScale * MinimumScale or > MaximumScale * MaximumScale)
            {
                return false;
            }
            var refinedCount = MatchPoints(refined, sourceField, referenceField, refinedHits, grid);
            if (refinedCount != count)
            {
                return false;
            }
            for (var index = 0; index < count; index++)
            {
                if (refinedHits[index].Source.Index != hits[index].Source.Index ||
                    refinedHits[index].Reference.Index != hits[index].Reference.Index)
                {
                    return false;
                }
            }
            var matches = refinedHits.AsSpan(0, count);
            var error = 0.0;
            var usedFirst = false;
            var usedSecond = false;
            foreach (var match in matches)
            {
                error += match.SquaredResidual;
                usedFirst |= match.Source.Index == sourceFirst.Index && match.Reference.Index == referenceFirst.Index;
                usedSecond |= match.Source.Index == sourceSecond.Index && match.Reference.Index == referenceSecond.Index;
            }
            if (!usedFirst || !usedSecond)
            {
                return false;
            }
            if (grid is not null)
            {
                return Distinct(refined, best!.Map, width, height) &&
                    Check(matches, width, height, minimum, .55) == DirectRegistrationStatus.Verified &&
                    Contradicts(best.Map, matches);
            }
            if (Better(count, error, best))
            {
                best = new(refined, matches.ToArray(), error);
            }
            return false;
        }
    }

    /// <summary>Check an already verified plate pair without adjusting either plate or selecting a fit.</summary>
    public static DirectRegistrationCheck VerifyPlatePair(IImageRegistrationMap map, IReadOnlyList<StellarDetection> source,
        IReadOnlyList<StellarDetection> reference, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        cancellationToken.ThrowIfCancellationRequested();
        var hits = new Match[FeatureLimit];
        var sources = Select(source, width, height).Take(FeatureLimit).ToArray();
        var references = Select(reference, width, height).Take(FeatureLimit).ToArray();
        var count = MatchPoints(map, sources, references, hits);
        var verification = hits.AsSpan(0, count);
        var status = Check(verification, width, height, MinimumFitCount + MinimumVerificationCount, .55);
        cancellationToken.ThrowIfCancellationRequested();
        return Report(status, [], verification);
    }

    private static StellarDetection[] Select(IReadOnlyList<StellarDetection> points, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (width <= 0 || height <= 0 || points.Count > StellarDetector.MaximumSupportedCandidates)
        {
            throw new ArgumentOutOfRangeException(nameof(points));
        }
        return points.Where(point =>
                (point.Conditions & (StellarSourceConditions.Saturated | StellarSourceConditions.Trailed)) == 0 &&
                point.SaturatedSampleCount == 0 && double.IsFinite(point.Flux) && point.Flux > 0 &&
                point.Pixel.X >= .5 && point.Pixel.X <= width - .5 && point.Pixel.Y >= .5 && point.Pixel.Y <= height - .5)
            .OrderByDescending(point => point.Flux)
            .ThenBy(point => point.Index)
            .ToArray();
    }

    private static (StellarDetection[] Training, StellarDetection[] Holdout) Partition(
        StellarDetection[] points, int width, int height, bool dense)
    {
        if (!dense)
        {
            var selected = points.Take(FeatureLimit).ToArray();
            return (selected.Where((_, index) => index % 3 != 1).ToArray(),
                selected.Where((_, index) => index % 3 == 1).ToArray());
        }
        var center = new PixelPoint(width / 2.0, height / 2.0);
        var radius = .33 * Math.Min(width, height);
        var inner = (radius - 1) * (radius - 1);
        var outer = (radius + 1) * (radius + 1);
        return (points.Where(point => DistanceSquared(point.Pixel, center) < inner).Take(FeatureLimit).ToArray(),
            points.Where(point => DistanceSquared(point.Pixel, center) > outer).Take(FeatureLimit).ToArray());
    }

    private static List<Pair> Pairs(StellarDetection[] points, double minimumLength)
    {
        var pairs = new List<Pair>();
        for (var first = 0; first < points.Length - 1; first++)
        {
            for (var second = first + 1; second < points.Length; second++)
            {
                var a = points[first].Pixel;
                var b = points[second].Pixel;
                var distance = Math.Sqrt(DistanceSquared(a, b));
                if (distance >= minimumLength)
                {
                    pairs.Add(new(points[first], points[second], distance));
                }
            }
        }
        return pairs;
    }

    private static bool Contradicts(ImageSimilarity incumbent, ReadOnlySpan<Match> matches)
    {
        // A local fit to a subset of the same correspondences can extrapolate differently at the image corners.
        // A competing field has at least one match that the certified map cannot explain within the match radius.
        foreach (var match in matches)
        {
            var expected = incumbent.SourceToReference(match.Source.Pixel)!.Value;
            if (DistanceSquared(expected, match.Reference.Pixel) > CandidateRadius * CandidateRadius)
            {
                return true;
            }
        }
        return false;
    }

    private static int LowerBound(List<Pair> pairs, double distance)
    {
        var left = 0;
        var right = pairs.Count;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (pairs[middle].Distance < distance)
            {
                left = middle + 1;
            }
            else
            {
                right = middle;
            }
        }
        return left;
    }

    private static int MatchPoints<TMap>(TMap map, StellarDetection[] sources, StellarDetection[] references,
        Span<Match> output, ReferenceGrid? grid = null)
        where TMap : IImageRegistrationMap
    {
        Span<int> claimed = stackalloc int[references.Length];
        claimed.Fill(-1);
        var count = 0;
        foreach (var source in sources)
        {
            if (map.SourceToReference(source.Pixel) is not { } mapped)
            {
                continue;
            }
            var candidate = -1;
            var squaredResidual = double.PositiveInfinity;
            if (grid is null)
            {
                for (var index = 0; index < references.Length; index++)
                {
                    Probe(mapped, references, index, claimed, ref candidate, ref squaredResidual);
                }
            }
            else
            {
                grid.Probe(mapped, claimed, ref candidate, ref squaredResidual);
            }
            if (candidate >= 0)
            {
                if (claimed[candidate] == -1)
                {
                    claimed[candidate] = count;
                    output[count++] = new(source, references[candidate], squaredResidual, candidate);
                }
                else
                {
                    claimed[candidate] = -2;
                }
            }
        }
        var write = 0;
        for (var read = 0; read < count; read++)
        {
            if (claimed[output[read].ReferencePosition] == read)
            {
                output[write++] = output[read];
            }
        }
        return write;
    }

    private static void Probe(PixelPoint mapped, StellarDetection[] references, int index, Span<int> claimed,
        ref int candidate, ref double squaredResidual)
    {
        var distance = DistanceSquared(mapped, references[index].Pixel);
        if (!(distance <= CandidateRadius * CandidateRadius))
        {
            return;
        }
        if (candidate >= 0)
        {
            claimed[candidate] = -2;
            candidate = -2;
        }
        if (candidate == -2)
        {
            claimed[index] = -2;
            return;
        }
        candidate = index;
        squaredResidual = distance;
    }

    private sealed class ReferenceGrid
    {
        private const int Edge = 64;
        private readonly int[] _head = new int[Edge * Edge];
        private readonly int[] _next;
        private readonly int[] _cellCounts = new int[Edge * Edge];
        private readonly StellarDetection[] _references;
        private readonly double _scaleX;
        private readonly double _scaleY;
        private readonly int _width;
        private readonly int _height;
        private int _workRemaining = AmbiguityProbeLimit;

        public bool Exhausted => _workRemaining < 0;

        public ReferenceGrid(StellarDetection[] references, int width, int height)
        {
            _references = references;
            _width = width;
            _height = height;
            _scaleX = Math.Min((double)Edge / width, 1 / (2 * CandidateRadius));
            _scaleY = Math.Min((double)Edge / height, 1 / (2 * CandidateRadius));
            Array.Fill(_head, -1);
            _next = new int[references.Length];
            for (var index = 0; index < references.Length; index++)
            {
                var point = references[index].Pixel;
                var cell = Cell(point.Y, _scaleY) * Edge + Cell(point.X, _scaleX);
                _next[index] = _head[cell];
                _head[cell] = index;
                _cellCounts[cell]++;
            }
        }

        public void Probe(PixelPoint mapped, Span<int> claimed, ref int candidate, ref double squaredResidual)
        {
            if (--_workRemaining < 0 || !(mapped.X >= -CandidateRadius && mapped.X <= _width + CandidateRadius &&
                mapped.Y >= -CandidateRadius && mapped.Y <= _height + CandidateRadius))
            {
                return;
            }
            var left = Cell(mapped.X - CandidateRadius, _scaleX);
            var right = Cell(mapped.X + CandidateRadius, _scaleX);
            var top = Cell(mapped.Y - CandidateRadius, _scaleY);
            var bottom = Cell(mapped.Y + CandidateRadius, _scaleY);
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var cell = y * Edge + x;
                    _workRemaining -= _cellCounts[cell];
                    for (var index = _head[cell]; index >= 0; index = _next[index])
                    {
                        DirectCentroidRegistration.Probe(mapped, _references, index, claimed, ref candidate, ref squaredResidual);
                    }
                }
            }
        }

        private static int Cell(double coordinate, double scale) => Math.Clamp((int)(coordinate * scale), 0, Edge - 1);
    }

    private static DirectRegistrationStatus Check(ReadOnlySpan<Match> matches, int width, int height, int minimum, double rmsLimit)
    {
        if (matches.Length < minimum)
        {
            return DirectRegistrationStatus.NoDirectMatch;
        }
        if (!HasSpread(matches, width, height, true) || !HasSpread(matches, width, height, false))
        {
            return DirectRegistrationStatus.PoorFieldCoverage;
        }
        var sum = 0.0;
        foreach (var match in matches)
        {
            if (match.SquaredResidual > .85 * .85)
            {
                return DirectRegistrationStatus.ResidualExceeded;
            }
            sum += match.SquaredResidual;
        }
        return sum <= matches.Length * rmsLimit * rmsLimit
            ? DirectRegistrationStatus.Verified
            : DirectRegistrationStatus.ResidualExceeded;
    }

    private static bool HasSpread(ReadOnlySpan<Match> matches, int width, int height, bool reference)
    {
        double x = 0, y = 0, xx = 0, xy = 0, yy = 0;
        foreach (var match in matches)
        {
            var point = reference ? match.Reference.Pixel : match.Source.Pixel;
            var u = point.X / width;
            var v = point.Y / height;
            x += u;
            y += v;
            xx += u * u;
            xy += u * v;
            yy += v * v;
        }
        var n = matches.Length;
        var vx = xx / n - x * x / (n * n);
        var vy = yy / n - y * y / (n * n);
        var covariance = xy / n - x * y / (n * n);
        // Positive-definite covariance after subtracting the minimum variance on both field axes.
        const double minimumVariance = .003;
        return vx > minimumVariance && vy > minimumVariance &&
            (vx - minimumVariance) * (vy - minimumVariance) > covariance * covariance;
    }

    private static ImageSimilarity Seed(PixelPoint a, PixelPoint b, PixelPoint c, PixelPoint d)
    {
        var u = b.X - a.X;
        var v = b.Y - a.Y;
        var x = d.X - c.X;
        var y = d.Y - c.Y;
        return FromCenters((x * u + y * v) / (u * u + v * v),
            (y * u - x * v) / (u * u + v * v), a, c);
    }

    private static ImageSimilarity Refine(ReadOnlySpan<Match> hits)
    {
        double sx = 0, sy = 0, rx = 0, ry = 0;
        foreach (var hit in hits)
        {
            sx += hit.Source.Pixel.X;
            sy += hit.Source.Pixel.Y;
            rx += hit.Reference.Pixel.X;
            ry += hit.Reference.Pixel.Y;
        }
        var source = new PixelPoint(sx / hits.Length, sy / hits.Length);
        var reference = new PixelPoint(rx / hits.Length, ry / hits.Length);
        double dot = 0, cross = 0, variance = 0;
        foreach (var hit in hits)
        {
            var u = hit.Source.Pixel.X - source.X;
            var v = hit.Source.Pixel.Y - source.Y;
            var x = hit.Reference.Pixel.X - reference.X;
            var y = hit.Reference.Pixel.Y - reference.Y;
            dot += x * u + y * v;
            cross += y * u - x * v;
            variance += u * u + v * v;
        }
        return FromCenters(dot / variance, cross / variance, source, reference);
    }

    private static ImageSimilarity FromCenters(double a, double b, PixelPoint source, PixelPoint reference) =>
        new(a, b, reference.X - a * source.X + b * source.Y, reference.Y - b * source.X - a * source.Y);

    private static bool Better(int count, double error, Candidate? candidate) =>
        candidate is null || count > candidate.Fit.Length || count == candidate.Fit.Length && error < candidate.SquaredError;

    private static bool Distinct(ImageSimilarity first, ImageSimilarity second, int width, int height)
    {
        return Displacement(default) || Displacement(new(width, 0)) ||
            Displacement(new(0, height)) || Displacement(new(width, height));

        bool Displacement(PixelPoint point) =>
            first.SourceToReference(point) is { } a && second.SourceToReference(point) is { } b &&
            DistanceSquared(a, b) > 1.5 * 1.5;
    }

    private static double DistanceSquared(PixelPoint a, PixelPoint b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    private static DirectSimilarityRegistration Failure(DirectRegistrationStatus status, ReadOnlySpan<Match> fit = default) =>
        new(null, Report(status, fit, []));

    private static DirectRegistrationCheck Report(DirectRegistrationStatus status, ReadOnlySpan<Match> fit, ReadOnlySpan<Match> verification) =>
        new(status, ReportMatches(fit), ReportMatches(verification));

    private static RegistrationCentroidMatch[] ReportMatches(ReadOnlySpan<Match> matches)
    {
        var output = new RegistrationCentroidMatch[matches.Length];
        for (var index = 0; index < matches.Length; index++)
        {
            var match = matches[index];
            output[index] = new(match.Source.Index, match.Reference.Index, Math.Sqrt(match.SquaredResidual));
        }
        return output;
    }

    private readonly record struct Pair(StellarDetection First, StellarDetection Second, double Distance);
    private readonly record struct Match(StellarDetection Source, StellarDetection Reference, double SquaredResidual, int ReferencePosition);
    private sealed record Candidate(ImageSimilarity Map, Match[] Fit, double SquaredError);
}
