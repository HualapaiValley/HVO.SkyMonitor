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
    ResidualExceeded
}

public sealed record RegistrationCentroidMatch(int SourceIndex, int ReferenceIndex, double ResidualPixels);

public sealed record DirectRegistrationCheck(DirectRegistrationStatus Status,
    IReadOnlyList<RegistrationCentroidMatch> Fit, IReadOnlyList<RegistrationCentroidMatch> Verification);

public sealed record DirectSimilarityRegistration(ImageSimilarity? Map, DirectRegistrationCheck Check);

/// <summary>Bounded pair fitting and independent point checks for direct registration.</summary>
public static class DirectCentroidRegistration
{
    public const string FitVersion = "direct-centroid-similarity-v1";
    public const string PlateCheckVersion = "direct-plate-centroid-check-v1";
    private const int FeatureLimit = 24;
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

        // Partition the reference detections before searching. The holdout never selects, tunes or ranks a fit.
        // Endpoints used to fit the winning map are also barred from the source holdout.
        var training = references.Where((_, index) => index % 3 != 1).ToArray();
        var heldOut = references.Where((_, index) => index % 3 == 1).ToArray();
        var referencePairs = Pairs(training, Math.Min(width, height) * .12);
        var sourcePairs = Pairs(sources, Math.Min(width, height) * .12);
        var hits = new Match[FeatureLimit];
        var refinedHits = new Match[FeatureLimit];
        Candidate? best = null;
        Candidate? alternate = null;
        foreach (var referencePair in referencePairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var sourcePair in sourcePairs)
            {
                var scale = referencePair.Distance / sourcePair.Distance;
                if (scale is < MinimumScale or > MaximumScale)
                {
                    continue;
                }
                Consider(Seed(sourcePair.First, sourcePair.Second, referencePair.First, referencePair.Second));
                Consider(Seed(sourcePair.Second, sourcePair.First, referencePair.First, referencePair.Second));
            }
        }

        if (best is null)
        {
            return Failure(DirectRegistrationStatus.NoDirectMatch);
        }
        if (alternate is not null && alternate.Fit.Length >= best.Fit.Length - 1)
        {
            return Failure(DirectRegistrationStatus.AmbiguousMatch, best.Fit);
        }

        var trainingStatus = Check(best.Fit, width, height, MinimumFitCount, .50);
        if (trainingStatus != DirectRegistrationStatus.Verified)
        {
            return Failure(trainingStatus, best.Fit);
        }
        var used = best.Fit.Select(match => match.Source.Index).ToHashSet();
        var remaining = sources.Where(point => !used.Contains(point.Index)).ToArray();
        var count = MatchPoints(best.Map, remaining, heldOut, hits);
        var verification = hits.AsSpan(0, count);
        var status = Check(verification, width, height, MinimumVerificationCount, .55);
        var report = Report(status, best.Fit, verification);
        cancellationToken.ThrowIfCancellationRequested();
        return new(status == DirectRegistrationStatus.Verified ? best.Map : null, report);

        void Consider(ImageSimilarity seed)
        {
            var count = MatchPoints(seed, sources, training, hits);
            if (count < MinimumFitCount)
            {
                return;
            }
            var refined = Refine(hits.AsSpan(0, count));
            var scaleSquared = refined.A * refined.A + refined.B * refined.B;
            if (!double.IsFinite(scaleSquared) || scaleSquared is < MinimumScale * MinimumScale or > MaximumScale * MaximumScale)
            {
                return;
            }
            var refinedCount = MatchPoints(refined, sources, training, refinedHits);
            if (refinedCount != count)
            {
                return;
            }
            for (var index = 0; index < count; index++)
            {
                if (refinedHits[index].Source.Index != hits[index].Source.Index ||
                    refinedHits[index].Reference.Index != hits[index].Reference.Index)
                {
                    return;
                }
            }
            var matches = refinedHits.AsSpan(0, count);
            var error = 0.0;
            foreach (var match in matches)
            {
                error += match.SquaredResidual;
            }
            if (Better(count, error, best))
            {
                var previous = best;
                best = new(refined, matches.ToArray(), error);
                if (previous is not null && Distinct(refined, previous.Map, width, height))
                {
                    if (alternate is null || !Distinct(refined, alternate.Map, width, height) ||
                        Better(previous.Fit.Length, previous.SquaredError, alternate))
                    {
                        alternate = previous;
                    }
                }
                else if (alternate is not null && !Distinct(refined, alternate.Map, width, height))
                {
                    alternate = null;
                }
            }
            else if (best is not null && Better(count, error, alternate) && Distinct(refined, best.Map, width, height))
            {
                alternate = new(refined, matches.ToArray(), error);
            }
        }
    }

    /// <summary>Check an already verified plate pair without adjusting either plate or selecting a fit.</summary>
    public static DirectRegistrationCheck VerifyPlatePair(IImageRegistrationMap map, IReadOnlyList<StellarDetection> source,
        IReadOnlyList<StellarDetection> reference, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        cancellationToken.ThrowIfCancellationRequested();
        var hits = new Match[FeatureLimit];
        var count = MatchPoints(map, Select(source, width, height), Select(reference, width, height), hits);
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
            .Take(FeatureLimit)
            .ToArray();
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
                    pairs.Add(new(a, b, distance));
                }
            }
        }
        return pairs;
    }

    private static int MatchPoints<TMap>(TMap map, StellarDetection[] sources, StellarDetection[] references, Span<Match> output)
        where TMap : IImageRegistrationMap
    {
        Span<int> claimed = stackalloc int[FeatureLimit];
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
            for (var index = 0; index < references.Length; index++)
            {
                var distance = DistanceSquared(mapped, references[index].Pixel);
                if (!(distance <= CandidateRadius * CandidateRadius))
                {
                    continue;
                }
                if (candidate >= 0)
                {
                    claimed[candidate] = -2;
                    candidate = -2;
                }
                if (candidate == -2)
                {
                    claimed[index] = -2;
                    continue;
                }
                candidate = index;
                squaredResidual = distance;
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

    private readonly record struct Pair(PixelPoint First, PixelPoint Second, double Distance);
    private readonly record struct Match(StellarDetection Source, StellarDetection Reference, double SquaredResidual, int ReferencePosition);
    private sealed record Candidate(ImageSimilarity Map, Match[] Fit, double SquaredError);
}
