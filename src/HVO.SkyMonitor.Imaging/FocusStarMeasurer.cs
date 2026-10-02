using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Outcome of one single-star focus measurement. Only <see cref="Valid"/> carries a width.</summary>
public enum FocusStarStatus
{
    /// <summary>The star was measured: centroid, half-flux radius/diameter and moment FWHM are reported.</summary>
    Valid,

    /// <summary>No significant star signal is present in the aperture.</summary>
    NoStar,

    /// <summary>The aperture contains clipped samples, so the profile and half-flux radius are not trustworthy.</summary>
    Saturated,

    /// <summary>The aperture or background annulus leaves the supplied pixels or contains masked samples.</summary>
    ApertureTruncated,

    /// <summary>The star is too broad for the declared aperture: its half-flux radius cannot be bounded.</summary>
    NotContained,

    /// <summary>Too few usable annulus samples remain for a local background estimate.</summary>
    BackgroundUnavailable
}

/// <summary>Stable reason codes carried with every <see cref="FocusStarMeasurement"/>.</summary>
public static class FocusStarReasonCodes
{
    public const string Valid = "valid";
    public const string NoStar = "no-star";
    public const string Saturated = "saturated";
    public const string ApertureTruncated = "aperture-truncated";
    public const string ApertureMasked = "aperture-masked";
    public const string NotContained = "not-contained";
    public const string BackgroundUnavailable = "background-unavailable";
    public const string NoCandidate = "no-candidate";
    public const string NoCandidateNearSelection = "no-candidate-near-selection";
}

/// <summary>
/// Declared geometry and gates for <see cref="FocusStarMeasurer"/>, in linear sample units and source-frame pixels.
/// The aperture holds the star; the annulus beyond it estimates the local background and noise.
/// <see cref="ApertureRadiusPixels"/> is the largest aperture; each star is measured in an aperture sized from its own
/// profile, never smaller than <see cref="MinimumApertureRadiusPixels"/>.
/// </summary>
public sealed record FocusStarMeasurementOptions(
    double ApertureRadiusPixels = 32,
    double AnnulusInnerRadiusPixels = 36,
    double AnnulusOuterRadiusPixels = 48,
    double BackgroundClipSigma = 3,
    int MinimumAnnulusSamples = 64,
    double CentroidThresholdSigma = 2,
    int MaximumCentroidIterations = 20,
    double CentroidConvergencePixels = 0.01,
    double MinimumPeakSigma = 5,
    double MinimumSignalToNoise = 10,
    int MaximumSaturatedSamples = 0,
    double MaximumHalfFluxApertureFraction = 0.5,
    double MaximumOuterFluxFraction = 0.05,
    int MaximumTargetCandidates = 4096,
    double MinimumApertureRadiusPixels = 8);

/// <summary>
/// One immutable single-star measurement. Coordinates are source-frame pixel-edge coordinates (sample centers at
/// +0.5). <see cref="Centroid"/> is reported whenever it was measured so that a caller can keep tracking a clipped
/// star, but the widths are reported only for <see cref="FocusStarStatus.Valid"/>; no invalid state carries a value.
/// </summary>
public sealed record FocusStarMeasurement(
    FocusStarStatus Status,
    string ReasonCode,
    PixelPoint? Centroid,
    double? HalfFluxRadiusPixels,
    double? FwhmPixels,
    double? TotalFlux,
    double? PeakAboveBackground,
    double? Background,
    double? NoiseSigma,
    double? SignalToNoise,
    int SaturatedSampleCount,
    int ApertureSampleCount,
    string SettingsIdentitySha256)
{
    /// <summary>Canonical primary metric definition: N.I.N.A.-style half-flux diameter, lower is sharper.</summary>
    public const string MetricDefinition = "half-flux-diameter-px";

    public const string Units = "pixels";

    /// <summary>Half-flux diameter, exactly twice <see cref="HalfFluxRadiusPixels"/>.</summary>
    public double? HalfFluxDiameterPixels => HalfFluxRadiusPixels * 2;

    public string AlgorithmVersion { get; init; } = FocusStarMeasurer.AlgorithmVersion;

    public bool HasMeasurement => Status == FocusStarStatus.Valid;
}

/// <summary>Result of selecting a focus star from frame pixels.</summary>
/// <param name="Position">Peak sample center in source-frame pixel-edge coordinates, or null when none qualified.</param>
public sealed record FocusStarTarget(PixelPoint? Position, double? PeakAboveBackground, int CandidateCount, string ReasonCode)
{
    public bool Selected => Position is not null;
}

/// <summary>
/// Host-neutral single-star focus measurement following the N.I.N.A. half-flux convention. The local sky is a plane
/// fitted to an annulus by sigma-clipped least squares, extended to a quadratic surface when an F-test finds curvature
/// such as vignetting, so gradients are subtracted per sample; the noise is the residual standard deviation. The centroid is the iterated
/// thresholded first moment of background-subtracted samples, refined by a Gaussian-windowed moment sized from the star,
/// so it is always measured from image pixels. The aperture is sized from the star: twice the radius at which the
/// median background excess of one-pixel rings becomes consistent with the sky, clamped to the declared range. A fixed
/// wide aperture would integrate the many faint stars of a crowded wide field, each below detection but together
/// comparable to the target, and report a broad but valid width for a sharp star; the ring median ignores both those and
/// a compact neighbour. A neighbour inside the sized aperture still adds its flux, so an isolated star should be chosen.
/// The half-flux
/// radius is the radius at which cumulative background-subtracted aperture flux reaches half the total, evaluated on a
/// four-by-four sub-sample grid in fine radial bins; the half-flux diameter is twice it. FWHM is reported separately as
/// the Gaussian-equivalent width of the background-subtracted second moments within three half-flux radii, corrected
/// for the pixel box. Every invalid state is explicit and carries no width.
/// <para>
/// Known accuracy: at high signal-to-noise (about 200 and above) the half-flux diameter of a pixel-integrated Gaussian
/// reads within about 3% low of truth across sigma 1 to 8. Near the <see cref="FocusStarMeasurementOptions.MinimumSignalToNoise"/>
/// floor of 10, noise in the outer aperture is clipped asymmetrically by the background fit, so HFD and total flux read
/// about 8 to 10% low. Samples are comparable within one session at fixed exposure and gain; they are not an absolute
/// optical calibration.
/// </para>
/// </summary>
public static class FocusStarMeasurer
{
    public const string AlgorithmVersion = "focus-star-hfr-v3";

    private const double FwhmPerSigma = 2.3548200450309493;
    private const double MadToSigma = 1.4826;
    private const int SubSamples = 4;
    private const double RadialBinPixels = 0.02;
    private const double PixelBoxVariance = 1.0 / 12.0;

    /// <summary>Half-flux radius of a Gaussian in units of its sigma: sqrt(2 ln 2).</summary>
    private const double HalfFluxRadiusPerSigma = 1.1774100225154747;
    private const double MinimumWindowSigmaPixels = 0.5;
    private const double WindowSupportSigmas = 4;

    /// <summary>A maximum whose ridge to a brighter one stays above this fraction of its own excess is the same object.</summary>
    public const double SaddleFraction = 0.6;

    /// <summary>A confirmed object at least this fraction as bright within the aperture radius makes a candidate crowded.</summary>
    public const double CrowdingPeakFraction = 0.1;

    /// <summary>F statistic above which annulus curvature is significant (p below about 1e-6 for three terms).</summary>
    private const double CurvatureFStatistic = 10;

    private const double OuterRingFraction = 0.85;
    private const double OuterRingNoiseSigmas = 3;

    /// <summary>
    /// A ring whose median excess is within this many standard errors of the sky holds no star light. At two, about one
    /// ring in forty read as star light by chance, which grew the aperture of a compact star over its neighbours.
    /// </summary>
    private const double QuietRingSigmas = 3;

    /// <summary>
    /// Consecutive quiet rings that mark where the star meets the sky, so that one noisy ring median in a faint wing does
    /// not truncate the aperture. Longer runs measured no better on faint isolated stars and admit more neighbours.
    /// </summary>
    private const int QuietRingsAtEdge = 2;

    /// <summary>Standard error of a median relative to that of a mean, for Gaussian noise: sqrt(pi / 2).</summary>
    private const double MedianStandardErrorFactor = 1.2533141373155003;

    /// <summary>The sized aperture is this multiple of the radius where the star meets the sky.</summary>
    private const double ApertureExtentFactor = 2;

    /// <summary>Bound on per-object annulus confirmations in one selection.</summary>
    private const int MaximumConfirmations = 256;

    /// <summary>
    /// Measures the star nearest <paramref name="initial"/>. The supplied pixels are a window whose top-left sample is
    /// at (<paramref name="originX"/>, <paramref name="originY"/>) in the source frame; coordinates in and out are
    /// source-frame pixel-edge coordinates.
    /// </summary>
    /// <exception cref="ArgumentException">The layout, masks or options are invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static FocusStarMeasurement Measure(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        PixelPoint initial,
        int originX = 0,
        int originY = 0,
        FocusStarMeasurementOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        Validate(pixels, validMask, saturatedMask, width, height, options);
        if (!double.IsFinite(initial.X) || !double.IsFinite(initial.Y))
        {
            throw new ArgumentException("The initial position must be finite.", nameof(initial));
        }
        var identity = SettingsIdentity(options);
        var cx = initial.X - originX;
        var cy = initial.Y - originY;
        var sky = default(LocalSky);
        double noise = 0;
        var radius = options.ApertureRadiusPixels;
        var converged = false;
        for (var iteration = 0; iteration < options.MaximumCentroidIterations && !converged; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Contained(cx, cy, width, height, options.AnnulusOuterRadiusPixels))
            {
                return Invalid(FocusStarStatus.ApertureTruncated, FocusStarReasonCodes.ApertureTruncated,
                    Frame(cx, cy, originX, originY), identity);
            }
            if (!TryFitSky(pixels, validMask, saturatedMask, width, cx, cy, options, out sky, out noise))
            {
                return Invalid(FocusStarStatus.BackgroundUnavailable, FocusStarReasonCodes.BackgroundUnavailable,
                    Frame(cx, cy, originX, originY), identity);
            }
            var threshold = options.CentroidThresholdSigma * noise;
            // Sized about the current position each pass so that neighbours never pull the thresholded moment.
            radius = StarApertureRadius(pixels, validMask, width, height, cx, cy, sky, noise, options);
            if (iteration == 0 && noise > 0 &&
                PeakExcess(pixels, validMask, width, height, cx, cy, radius, sky) < options.MinimumPeakSigma * noise)
            {
                // Without a significant peak the thresholded moment would only chase noise.
                return Invalid(FocusStarStatus.NoStar, FocusStarReasonCodes.NoStar, null, identity) with
                {
                    Background = sky.Level,
                    NoiseSigma = noise
                };
            }
            double sum = 0, sumX = 0, sumY = 0;
            var centroidAperture = new Aperture(cx, cy, radius, width, height);
            for (var y = centroidAperture.MinimumY; y <= centroidAperture.MaximumY; y++)
            {
                for (var x = centroidAperture.MinimumX; x <= centroidAperture.MaximumX; x++)
                {
                    var index = y * width + x;
                    if (!centroidAperture.Contains(x, y) || !validMask[index])
                    {
                        continue;
                    }
                    var excess = pixels[index] - sky.At(x, y);
                    if (excess > threshold)
                    {
                        sum += excess;
                        sumX += excess * (x + 0.5);
                        sumY += excess * (y + 0.5);
                    }
                }
            }
            if (sum <= 0)
            {
                return Invalid(FocusStarStatus.NoStar, FocusStarReasonCodes.NoStar, null, identity) with
                {
                    Background = sky.Level,
                    NoiseSigma = noise
                };
            }
            var nx = sumX / sum;
            var ny = sumY / sum;
            converged = Math.Abs(nx - cx) <= options.CentroidConvergencePixels &&
                Math.Abs(ny - cy) <= options.CentroidConvergencePixels;
            cx = nx;
            cy = ny;
        }
        if (!Contained(cx, cy, width, height, options.AnnulusOuterRadiusPixels))
        {
            return Invalid(FocusStarStatus.ApertureTruncated, FocusStarReasonCodes.ApertureTruncated,
                Frame(cx, cy, originX, originY), identity);
        }
        if (!TryFitSky(pixels, validMask, saturatedMask, width, cx, cy, options, out sky, out noise))
        {
            return Invalid(FocusStarStatus.BackgroundUnavailable, FocusStarReasonCodes.BackgroundUnavailable,
                Frame(cx, cy, originX, originY), identity);
        }

        // The thresholded moment above admits background noise from the whole aperture, which biases a faint star toward
        // the aperture centre and any sky gradient. Refine it with a Gaussian-windowed moment sized from the star itself.
        var coarseHalfFlux = HalfFluxRadius(pixels, width, height, cx, cy, radius, sky, out _, out _);
        if (double.IsFinite(coarseHalfFlux) && coarseHalfFlux > 0)
        {
            (cx, cy) = RefineWindowedCentroid(pixels, validMask, width, height, cx, cy, sky,
                coarseHalfFlux / HalfFluxRadiusPerSigma, options, cancellationToken);
            if (!Contained(cx, cy, width, height, options.AnnulusOuterRadiusPixels))
            {
                return Invalid(FocusStarStatus.ApertureTruncated, FocusStarReasonCodes.ApertureTruncated,
                    Frame(cx, cy, originX, originY), identity);
            }
            if (!TryFitSky(pixels, validMask, saturatedMask, width, cx, cy, options, out sky, out noise))
            {
                return Invalid(FocusStarStatus.BackgroundUnavailable, FocusStarReasonCodes.BackgroundUnavailable,
                    Frame(cx, cy, originX, originY), identity);
            }
        }

        var centroid = Frame(cx, cy, originX, originY);
        radius = StarApertureRadius(pixels, validMask, width, height, cx, cy, sky, noise, options);
        double total = 0, peak = double.NegativeInfinity;
        int samples = 0, saturated = 0, masked = 0;
        var aperture = new Aperture(cx, cy, radius, width, height);
        for (var y = aperture.MinimumY; y <= aperture.MaximumY; y++)
        {
            for (var x = aperture.MinimumX; x <= aperture.MaximumX; x++)
            {
                var index = y * width + x;
                if (!aperture.Contains(x, y))
                {
                    continue;
                }
                if (!validMask[index])
                {
                    masked++;
                    continue;
                }
                if (!saturatedMask.IsEmpty && saturatedMask[index])
                {
                    saturated++;
                }
                var excess = pixels[index] - sky.At(x, y);
                total += excess;
                peak = Math.Max(peak, excess);
                samples++;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var signalToNoise = noise > 0 ? total / (noise * Math.Sqrt(samples)) : double.PositiveInfinity;
        var measured = new FocusStarMeasurement(FocusStarStatus.Valid, FocusStarReasonCodes.Valid, centroid, null,
            null, total, peak, sky.Level, noise, signalToNoise, saturated, samples, identity);
        if (masked > 0)
        {
            return measured with { Status = FocusStarStatus.ApertureTruncated, ReasonCode = FocusStarReasonCodes.ApertureMasked };
        }
        if (saturated > options.MaximumSaturatedSamples)
        {
            return measured with { Status = FocusStarStatus.Saturated, ReasonCode = FocusStarReasonCodes.Saturated };
        }
        if (total <= 0 || signalToNoise < options.MinimumSignalToNoise ||
            noise > 0 && peak / noise < options.MinimumPeakSigma)
        {
            return measured with { Status = FocusStarStatus.NoStar, ReasonCode = FocusStarReasonCodes.NoStar };
        }

        var halfFluxRadius = HalfFluxRadius(pixels, width, height, cx, cy, radius, sky,
            out var outerFlux, out var outerSamples);
        // Flux in the outer ring beyond the declared fraction means the star overflows the aperture; the allowance for
        // sky noise summed over the ring keeps a faint, compact star from failing on noise alone.
        if (!double.IsFinite(halfFluxRadius) ||
            halfFluxRadius > options.MaximumHalfFluxApertureFraction * radius ||
            outerFlux > options.MaximumOuterFluxFraction * total + OuterRingNoiseSigmas * noise * Math.Sqrt(outerSamples))
        {
            return measured with { Status = FocusStarStatus.NotContained, ReasonCode = FocusStarReasonCodes.NotContained };
        }

        var momentRadius = Math.Min(radius, 3 * halfFluxRadius);
        double momentSum = 0, momentXx = 0, momentYy = 0;
        var momentAperture = new Aperture(cx, cy, momentRadius, width, height);
        for (var y = momentAperture.MinimumY; y <= momentAperture.MaximumY; y++)
        {
            for (var x = momentAperture.MinimumX; x <= momentAperture.MaximumX; x++)
            {
                if (!momentAperture.Contains(x, y))
                {
                    continue;
                }
                var excess = pixels[y * width + x] - sky.At(x, y);
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                momentSum += excess;
                momentXx += excess * dx * dx;
                momentYy += excess * dy * dy;
            }
        }
        var variance = momentSum > 0 ? (momentXx + momentYy) / (2 * momentSum) - PixelBoxVariance : double.NaN;
        double? fwhm = variance > 0 ? FwhmPerSigma * Math.Sqrt(variance) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return measured with { HalfFluxRadiusPixels = halfFluxRadius, FwhmPixels = fwhm };
    }

    /// <summary>
    /// Selects a focus star from frame pixels. Candidates are local maxima at least
    /// <see cref="FocusStarMeasurementOptions.MinimumPeakSigma"/> above a robust global background, with at least three
    /// significant neighbours (rejecting hot pixels) and a full annulus inside the frame. Maxima joined to a brighter one by
    /// a ridge that never falls below <see cref="SaddleFraction"/> of their own excess are the same object, so the noise
    /// on a broad defocused profile neither splits it nor crowds it. Each object is then confirmed against its own
    /// annulus with the gate <see cref="Measure"/> applies, which rejects steps such as a fisheye image-circle edge that only
    /// look significant against the global statistics. Without <paramref name="near"/> the brightest isolated unclipped
    /// object whose aperture holds no masked sample wins; when only clipped objects remain the brightest of them is returned so that measurement reports
    /// <see cref="FocusStarStatus.Saturated"/> rather than nothing. With <paramref name="near"/> the confirmed object
    /// nearest that position within <paramref name="searchRadiusPixels"/> wins, clipped or not. The target is never taken
    /// from a catalog. When too little unclipped sky remains to estimate the background, the reason is
    /// <see cref="FocusStarReasonCodes.Saturated"/> if clipped samples outnumber the unclipped ones and
    /// <see cref="FocusStarReasonCodes.BackgroundUnavailable"/> otherwise.
    /// </summary>
    public static FocusStarTarget SelectTarget(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        FocusStarMeasurementOptions? options = null,
        PixelPoint? near = null,
        double searchRadiusPixels = 24,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        Validate(pixels, validMask, saturatedMask, width, height, options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(searchRadiusPixels);
        var stride = Math.Max(1, (int)Math.Sqrt(pixels.Length / 65536.0));
        var sample = new List<double>(65536 + 1024);
        var clippedSky = 0;
        for (var y = 0; y < height; y += stride)
        {
            for (var x = 0; x < width; x += stride)
            {
                var index = y * width + x;
                if (!validMask[index])
                {
                    continue;
                }
                if (saturatedMask.IsEmpty || !saturatedMask[index])
                {
                    sample.Add(pixels[index]);
                }
                else
                {
                    clippedSky++;
                }
            }
        }
        if (sample.Count < options.MinimumAnnulusSamples)
        {
            // A clipped sky (daylight, or far too much exposure) is a saturation fault, not a missing background.
            return new(null, null, 0, clippedSky > sample.Count
                ? FocusStarReasonCodes.Saturated
                : FocusStarReasonCodes.BackgroundUnavailable);
        }
        var (background, noise) = RobustStatistics(sample, options.BackgroundClipSigma);
        var peakGate = background + options.MinimumPeakSigma * Math.Max(noise, double.Epsilon);
        var neighbourGate = background + 2 * noise;
        var margin = (int)Math.Ceiling(options.AnnulusOuterRadiusPixels);
        var candidates = new List<Candidate>();
        for (var y = Math.Max(1, margin); y < Math.Min(height - 1, height - margin); y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            for (var x = Math.Max(1, margin); x < Math.Min(width - 1, width - margin); x++)
            {
                var index = y * width + x;
                var value = pixels[index];
                if (value <= peakGate || !validMask[index])
                {
                    continue;
                }
                var isPeak = true;
                var clipped = !saturatedMask.IsEmpty && saturatedMask[index];
                var significant = 0;
                for (var dy = -1; dy <= 1 && isPeak; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }
                        var neighbour = index + dy * width + dx;
                        var other = pixels[neighbour];
                        // Ties resolve to the first sample in scan order so a flat-topped peak yields one candidate.
                        if (other > value || other == value && (dy < 0 || dy == 0 && dx < 0))
                        {
                            isPeak = false;
                            break;
                        }
                        clipped |= !saturatedMask.IsEmpty && saturatedMask[neighbour];
                        if (validMask[neighbour] && other > neighbourGate)
                        {
                            significant++;
                        }
                    }
                }
                if (isPeak && significant >= 3)
                {
                    candidates.Add(new(x, y, value - background, clipped));
                    if (candidates.Count > options.MaximumTargetCandidates)
                    {
                        candidates.Sort(static (left, right) => right.Peak.CompareTo(left.Peak));
                        candidates.RemoveRange(options.MaximumTargetCandidates / 2, candidates.Count - options.MaximumTargetCandidates / 2);
                    }
                }
            }
        }
        if (candidates.Count == 0)
        {
            return new(null, null, 0, FocusStarReasonCodes.NoCandidate);
        }

        // Brightest first, so every maximum is compared only with brighter objects it could belong to.
        candidates.Sort(static (left, right) => right.Peak.CompareTo(left.Peak));
        var objects = new List<Candidate>();
        var mergeRadius = options.ApertureRadiusPixels * options.ApertureRadiusPixels;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var merged = false;
            foreach (var brighter in objects)
            {
                if (candidate.DistanceSquared(brighter) <= mergeRadius &&
                    RidgeMinimum(pixels, validMask, width, height, brighter, candidate, background) >=
                    SaddleFraction * candidate.Peak)
                {
                    merged = true;
                    break;
                }
            }
            if (!merged)
            {
                objects.Add(candidate);
            }
        }

        var confirmed = new Dictionary<Candidate, bool>();
        var budget = MaximumConfirmations;
        bool Confirm(Candidate candidate, ReadOnlySpan<double> source, ReadOnlySpan<bool> valid, ReadOnlySpan<bool> clippedMask)
        {
            if (confirmed.TryGetValue(candidate, out var known))
            {
                return known;
            }
            if (budget-- <= 0)
            {
                return false;
            }
            var significant = TryFitSky(source, valid, clippedMask, width, candidate.X + 0.5, candidate.Y + 0.5,
                    options, out var localSky, out var localNoise) &&
                source[candidate.Y * width + candidate.X] - localSky.Level >= options.MinimumPeakSigma * localNoise &&
                source[candidate.Y * width + candidate.X] > localSky.Level;
            confirmed[candidate] = significant;
            return significant;
        }

        if (near is { } preferred)
        {
            var nearest = objects
                .Select(candidate => (Candidate: candidate,
                    Distance: Math.Sqrt(Math.Pow(candidate.X + 0.5 - preferred.X, 2) + Math.Pow(candidate.Y + 0.5 - preferred.Y, 2))))
                .Where(entry => entry.Distance <= searchRadiusPixels)
                .OrderBy(entry => entry.Distance)
                .ThenByDescending(entry => entry.Candidate.Peak)
                .Select(entry => entry.Candidate)
                .ToList();
            foreach (var candidate in nearest)
            {
                if (Confirm(candidate, pixels, validMask, saturatedMask))
                {
                    return Selected(candidate, objects.Count);
                }
            }
            return new(null, null, objects.Count, FocusStarReasonCodes.NoCandidateNearSelection);
        }

        Candidate? crowdedFallback = null, clippedFallback = null;
        foreach (var candidate in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Automatic selection only offers objects measurement can complete; an operator-chosen one near a masked edge
            // is still returned above so that measurement reports the truncation explicitly.
            if (!Confirm(candidate, pixels, validMask, saturatedMask) ||
                !ApertureUnmasked(validMask, width, height, candidate, options.ApertureRadiusPixels))
            {
                continue;
            }
            var crowded = false;
            foreach (var other in objects)
            {
                if (other != candidate && other.Peak >= CrowdingPeakFraction * candidate.Peak &&
                    other.DistanceSquared(candidate) <= mergeRadius && Confirm(other, pixels, validMask, saturatedMask))
                {
                    crowded = true;
                    break;
                }
            }
            if (candidate.Clipped)
            {
                clippedFallback ??= candidate;
            }
            else if (crowded)
            {
                crowdedFallback ??= candidate;
            }
            else
            {
                return Selected(candidate, objects.Count);
            }
        }
        return (crowdedFallback ?? clippedFallback) is { } fallback
            ? Selected(fallback, objects.Count)
            : new(null, null, objects.Count, FocusStarReasonCodes.NoCandidate);
    }

    private static bool ApertureUnmasked(ReadOnlySpan<bool> validMask, int width, int height, Candidate candidate, double radius)
    {
        var aperture = new Aperture(candidate.X + 0.5, candidate.Y + 0.5, radius, width, height);
        for (var y = aperture.MinimumY; y <= aperture.MaximumY; y++)
        {
            for (var x = aperture.MinimumX; x <= aperture.MaximumX; x++)
            {
                if (aperture.Contains(x, y) && !validMask[y * width + x])
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static FocusStarTarget Selected(Candidate candidate, int count)
        => new(new(candidate.X + 0.5, candidate.Y + 0.5), candidate.Peak, count, FocusStarReasonCodes.Valid);

    /// <summary>
    /// Lowest three-by-three mean background excess on the straight path between two maxima, excluding the endpoints'
    /// own boxes; the box mean keeps one noisy sample from splitting a smooth profile.
    /// </summary>
    private static double RidgeMinimum(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        int width,
        int height,
        Candidate from,
        Candidate to,
        double background)
    {
        var length = Math.Sqrt(from.DistanceSquared(to));
        var steps = Math.Max(1, (int)Math.Ceiling(length * 2));
        var minimum = double.PositiveInfinity;
        for (var step = 1; step < steps; step++)
        {
            var t = (double)step / steps;
            var x = (int)Math.Round(from.X + (to.X - from.X) * t);
            var y = (int)Math.Round(from.Y + (to.Y - from.Y) * t);
            double sum = 0;
            var count = 0;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var sx = x + dx;
                    var sy = y + dy;
                    if (sx >= 0 && sy >= 0 && sx < width && sy < height && validMask[sy * width + sx])
                    {
                        sum += pixels[sy * width + sx];
                        count++;
                    }
                }
            }
            if (count == 0)
            {
                return double.NegativeInfinity;
            }
            minimum = Math.Min(minimum, sum / count - background);
        }
        return double.IsPositiveInfinity(minimum) ? Math.Min(from.Peak, to.Peak) : minimum;
    }

    private readonly record struct Candidate(int X, int Y, double Peak, bool Clipped)
    {
        public double DistanceSquared(Candidate other)
            => (double)(X - other.X) * (X - other.X) + (double)(Y - other.Y) * (Y - other.Y);
    }

    /// <summary>Content identity of the algorithm version and every declared threshold.</summary>
    public static string SettingsIdentity(FocusStarMeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "focus-star-settings-v1",
            algorithm = AlgorithmVersion,
            options
        })));
    }

    /// <summary>
    /// Radius at which cumulative background-subtracted flux on a sub-sampled grid reaches half the aperture total, and
    /// the flux and sample count beyond <see cref="OuterRingFraction"/> of the aperture radius. NaN when the total is not
    /// positive.
    /// </summary>
    private static double HalfFluxRadius(
        ReadOnlySpan<double> pixels,
        int width,
        int height,
        double cx,
        double cy,
        double radius,
        in LocalSky sky,
        out double outerFlux,
        out int outerSamples)
    {
        outerFlux = double.NaN;
        outerSamples = 0;
        var aperture = new Aperture(cx, cy, radius, width, height);
        var bins = new double[(int)Math.Ceiling(radius / RadialBinPixels) + 2];
        const double subWeight = 1.0 / (SubSamples * SubSamples);
        double total = 0;
        for (var y = aperture.MinimumY; y <= aperture.MaximumY; y++)
        {
            for (var x = aperture.MinimumX; x <= aperture.MaximumX; x++)
            {
                if (!aperture.Contains(x, y))
                {
                    continue;
                }
                var excess = pixels[y * width + x] - sky.At(x, y);
                total += excess;
                var share = excess * subWeight;
                for (var sy = 0; sy < SubSamples; sy++)
                {
                    var dy = y + (sy + 0.5) / SubSamples - cy;
                    for (var sx = 0; sx < SubSamples; sx++)
                    {
                        var dx = x + (sx + 0.5) / SubSamples - cx;
                        var bin = Math.Min(bins.Length - 1, (int)(Math.Sqrt(dx * dx + dy * dy) / RadialBinPixels));
                        bins[bin] += share;
                    }
                }
            }
        }
        if (total <= 0)
        {
            return double.NaN;
        }
        var half = total / 2;
        double cumulative = 0, halfFluxRadius = double.NaN;
        for (var bin = 0; bin < bins.Length; bin++)
        {
            var next = cumulative + bins[bin];
            if (next >= half && bins[bin] > 0)
            {
                halfFluxRadius = (bin + (half - cumulative) / bins[bin]) * RadialBinPixels;
                break;
            }
            cumulative = next;
        }
        double outer = 0;
        for (var bin = (int)(OuterRingFraction * radius / RadialBinPixels); bin < bins.Length; bin++)
        {
            outer += bins[bin];
        }
        var ring = OuterRingFraction * radius;
        for (var y = aperture.MinimumY; y <= aperture.MaximumY; y++)
        {
            for (var x = aperture.MinimumX; x <= aperture.MaximumX; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                if (aperture.Contains(x, y) && dx * dx + dy * dy >= ring * ring)
                {
                    outerSamples++;
                }
            }
        }
        outerFlux = outer;
        return halfFluxRadius;
    }

    /// <summary>
    /// Gaussian-windowed first moment (the SExtractor XWIN iteration): each step moves the estimate by twice the
    /// window-weighted mean offset of background-subtracted samples, which converges on the centre of a symmetric
    /// profile without a threshold and with little weight on distant noise. Returns the input when the windowed flux is
    /// not positive or the iteration leaves the aperture.
    /// </summary>
    private static (double X, double Y) RefineWindowedCentroid(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        int width,
        int height,
        double cx,
        double cy,
        in LocalSky sky,
        double windowSigma,
        FocusStarMeasurementOptions options,
        CancellationToken cancellationToken)
    {
        windowSigma = Math.Max(windowSigma, MinimumWindowSigmaPixels);
        var radius = Math.Min(options.ApertureRadiusPixels, WindowSupportSigmas * windowSigma);
        var inverseTwoVariance = 1 / (2 * windowSigma * windowSigma);
        double x0 = cx, y0 = cy;
        for (var iteration = 0; iteration < options.MaximumCentroidIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double weighted = 0, sumX = 0, sumY = 0;
            var window = new Aperture(x0, y0, radius, width, height);
            for (var y = window.MinimumY; y <= window.MaximumY; y++)
            {
                for (var x = window.MinimumX; x <= window.MaximumX; x++)
                {
                    var index = y * width + x;
                    if (!window.Contains(x, y) || !validMask[index])
                    {
                        continue;
                    }
                    var dx = x + 0.5 - x0;
                    var dy = y + 0.5 - y0;
                    var weight = Math.Exp(-(dx * dx + dy * dy) * inverseTwoVariance) * (pixels[index] - sky.At(x, y));
                    weighted += weight;
                    sumX += weight * dx;
                    sumY += weight * dy;
                }
            }
            if (weighted <= 0)
            {
                return (cx, cy);
            }
            var stepX = 2 * sumX / weighted;
            var stepY = 2 * sumY / weighted;
            x0 += stepX;
            y0 += stepY;
            if ((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy) > windowSigma * windowSigma * WindowSupportSigmas * WindowSupportSigmas)
            {
                return (cx, cy);
            }
            if (Math.Abs(stepX) <= options.CentroidConvergencePixels && Math.Abs(stepY) <= options.CentroidConvergencePixels)
            {
                break;
            }
        }
        return (x0, y0);
    }

    private static FocusStarMeasurement Invalid(FocusStarStatus status, string reason, PixelPoint? centroid, string identity)
        => new(status, reason, centroid, null, null, null, null, null, null, null, 0, 0, identity);

    private static PixelPoint Frame(double x, double y, int originX, int originY) => new(x + originX, y + originY);

    private static bool Contained(double cx, double cy, int width, int height, double radius)
        => cx - radius >= 0 && cy - radius >= 0 && cx + radius <= width && cy + radius <= height;

    private static double PeakExcess(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        int width,
        int height,
        double cx,
        double cy,
        double radius,
        in LocalSky sky)
    {
        var peak = double.NegativeInfinity;
        var aperture = new Aperture(cx, cy, radius, width, height);
        for (var y = aperture.MinimumY; y <= aperture.MaximumY; y++)
        {
            for (var x = aperture.MinimumX; x <= aperture.MaximumX; x++)
            {
                var index = y * width + x;
                if (aperture.Contains(x, y) && validMask[index])
                {
                    peak = Math.Max(peak, pixels[index] - sky.At(x, y));
                }
            }
        }
        return peak;
    }

    /// <summary>
    /// Sizes the aperture to the star at (<paramref name="cx"/>, <paramref name="cy"/>): twice the outer edge of the first
    /// of <see cref="QuietRingsAtEdge"/> consecutive one-pixel rings whose median sky excess is consistent with zero, clamped to the declared limits.
    /// A ring median ignores a neighbour that covers less than half of the ring, so a crowded field cannot widen it.
    /// </summary>
    private static double StarApertureRadius(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        int width,
        int height,
        double cx,
        double cy,
        in LocalSky sky,
        double noise,
        FocusStarMeasurementOptions options)
    {
        var maximum = options.ApertureRadiusPixels;
        if (noise <= 0)
        {
            return maximum;
        }
        var rings = new List<double>[(int)maximum + 1];
        for (var ring = 0; ring < rings.Length; ring++)
        {
            rings[ring] = [];
        }
        var aperture = new Aperture(cx, cy, maximum, width, height);
        for (var y = aperture.MinimumY; y <= aperture.MaximumY; y++)
        {
            for (var x = aperture.MinimumX; x <= aperture.MaximumX; x++)
            {
                var index = y * width + x;
                if (!aperture.Contains(x, y) || !validMask[index])
                {
                    continue;
                }
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                rings[(int)Math.Sqrt(dx * dx + dy * dy)].Add(pixels[index] - sky.At(x, y));
            }
        }
        var quietRun = 0;
        for (var ring = 0; ring < rings.Length; ring++)
        {
            var values = rings[ring];
            if (values.Count == 0)
            {
                quietRun = 0;
                continue;
            }
            var sorted = values.ToArray();
            Array.Sort(sorted);
            quietRun = Median(sorted) <= QuietRingSigmas * MedianStandardErrorFactor * noise / Math.Sqrt(sorted.Length)
                ? quietRun + 1
                : 0;
            if (quietRun == QuietRingsAtEdge)
            {
                // The star meets the sky at the outer edge of the first quiet ring of the run.
                var edge = ring - QuietRingsAtEdge + 2;
                return Math.Clamp(ApertureExtentFactor * edge, options.MinimumApertureRadiusPixels, maximum);
            }
        }
        return maximum;
    }

    /// <summary>Sample-center circle bounds clipped to the supplied pixels.</summary>
    private readonly struct Aperture
    {
        private readonly double _cx;
        private readonly double _cy;
        private readonly double _limit;

        public Aperture(double cx, double cy, double radius, int width, int height)
        {
            _cx = cx;
            _cy = cy;
            _limit = radius * radius;
            MinimumY = Math.Max(0, (int)Math.Floor(cy - radius));
            MaximumY = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
            MinimumX = Math.Max(0, (int)Math.Floor(cx - radius));
            MaximumX = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
        }

        public int MinimumX { get; }
        public int MaximumX { get; }
        public int MinimumY { get; }
        public int MaximumY { get; }

        public bool Contains(int x, int y)
        {
            var dx = x + 0.5 - _cx;
            var dy = y + 0.5 - _cy;
            return dx * dx + dy * dy <= _limit;
        }
    }

    /// <summary>
    /// Fits the local sky to the annulus by iterated sigma-clipped least squares and returns the residual standard deviation
    /// as the noise. A flat level cannot represent vignetting or a sky gradient, whose slope and curvature over the annulus
    /// would otherwise be summed over every aperture sample and move the flux, the half-flux radius and even the
    /// star/no-star decision. Clipping uses a quadratic surface; the first pass clips about the median using the scaled
    /// median absolute deviation, which resists a neighbour in the annulus. The reported surface is that quadratic only
    /// when its curvature is significant against a plane on the same samples, and otherwise the plane.
    /// </summary>
    private static bool TryFitSky(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        double cx,
        double cy,
        FocusStarMeasurementOptions options,
        out LocalSky sky,
        out double noise)
    {
        var inner = options.AnnulusInnerRadiusPixels * options.AnnulusInnerRadiusPixels;
        var outer = options.AnnulusOuterRadiusPixels * options.AnnulusOuterRadiusPixels;
        var scale = options.AnnulusOuterRadiusPixels;
        var us = new List<double>();
        var vs = new List<double>();
        var values = new List<double>();
        var minimumY = (int)Math.Floor(cy - options.AnnulusOuterRadiusPixels);
        var maximumY = (int)Math.Ceiling(cy + options.AnnulusOuterRadiusPixels);
        var minimumX = (int)Math.Floor(cx - options.AnnulusOuterRadiusPixels);
        var maximumX = (int)Math.Ceiling(cx + options.AnnulusOuterRadiusPixels);
        var height = pixels.Length / width;
        for (var y = Math.Max(0, minimumY); y <= Math.Min(height - 1, maximumY); y++)
        {
            var dy = y + 0.5 - cy;
            for (var x = Math.Max(0, minimumX); x <= Math.Min(width - 1, maximumX); x++)
            {
                var dx = x + 0.5 - cx;
                var squared = dx * dx + dy * dy;
                var index = y * width + x;
                if (squared >= inner && squared <= outer && validMask[index] &&
                    (saturatedMask.IsEmpty || !saturatedMask[index]))
                {
                    us.Add(dx / scale);
                    vs.Add(dy / scale);
                    values.Add(pixels[index]);
                }
            }
        }
        sky = default;
        noise = 0;
        if (values.Count < options.MinimumAnnulusSamples)
        {
            return false;
        }

        var count = values.Count;
        var (center, sigma) = RobustStatistics(values, options.BackgroundClipSigma);
        var kept = new bool[count];
        var residuals = new double[count];
        for (var index = 0; index < count; index++)
        {
            residuals[index] = values[index] - center;
        }
        var coefficients = new double[LocalSky.Terms];
        coefficients[0] = center;
        for (var pass = 0; pass < 10; pass++)
        {
            var limit = options.BackgroundClipSigma * sigma;
            var keptCount = 0;
            var changed = false;
            for (var index = 0; index < count; index++)
            {
                var keep = !(sigma > 0) || Math.Abs(residuals[index]) <= limit;
                changed |= pass == 0 || keep != kept[index];
                kept[index] = keep;
                keptCount += keep ? 1 : 0;
            }
            if (keptCount < Math.Max(8, 4 * LocalSky.Terms) || pass > 0 && !changed)
            {
                break;
            }
            if (!TrySolveSky(LocalSky.Terms, us, vs, values, kept, coefficients))
            {
                // A degenerate annulus, such as one confined to a thin masked edge, falls back to a level.
                Array.Clear(coefficients);
                double sum = 0;
                for (var index = 0; index < count; index++)
                {
                    sum += kept[index] ? values[index] : 0;
                }
                coefficients[0] = sum / keptCount;
            }
            var model = new LocalSky(cx, cy, scale, coefficients);
            double squares = 0;
            for (var index = 0; index < count; index++)
            {
                residuals[index] = values[index] - model.AtOffset(us[index], vs[index]);
                squares += kept[index] ? residuals[index] * residuals[index] : 0;
            }
            sigma = Math.Sqrt(squares / Math.Max(1, keptCount - LocalSky.Terms));
        }

        // Over an annulus the radial curvature is nearly degenerate with the level, so extrapolating it inward multiplies
        // the level's noise. Keep the curvature only when an F-test against a plane on the same samples finds it.
        var planar = new double[LocalSky.Terms];
        var keptSamples = kept.Count(static keep => keep);
        if (keptSamples > 2 * LocalSky.Terms && TrySolveSky(LocalSky.PlaneTerms, us, vs, values, kept, planar))
        {
            var quadraticModel = new LocalSky(cx, cy, scale, coefficients);
            var planarModel = new LocalSky(cx, cy, scale, planar);
            double quadraticSquares = 0, planarSquares = 0;
            for (var index = 0; index < count; index++)
            {
                if (kept[index])
                {
                    var quadraticResidual = values[index] - quadraticModel.AtOffset(us[index], vs[index]);
                    var planarResidual = values[index] - planarModel.AtOffset(us[index], vs[index]);
                    quadraticSquares += quadraticResidual * quadraticResidual;
                    planarSquares += planarResidual * planarResidual;
                }
            }
            var freedom = keptSamples - LocalSky.Terms;
            var statistic = quadraticSquares > 0
                ? (planarSquares - quadraticSquares) / (LocalSky.Terms - LocalSky.PlaneTerms) / (quadraticSquares / freedom)
                : double.PositiveInfinity;
            if (statistic < CurvatureFStatistic)
            {
                coefficients = planar;
                sigma = Math.Sqrt(planarSquares / Math.Max(1, keptSamples - LocalSky.PlaneTerms));
            }
        }
        sky = new LocalSky(cx, cy, scale, coefficients);
        noise = sigma;
        return true;
    }

    /// <summary>
    /// Least squares for the first <paramref name="n"/> sky terms by normal equations with partial pivoting; the remaining
    /// coefficients are zeroed.
    /// </summary>
    private static bool TrySolveSky(
        int n,
        List<double> us,
        List<double> vs,
        List<double> values,
        bool[] kept,
        double[] solution)
    {
        var columns = n + 1;
        var matrix = new double[n * columns];
        Span<double> basis = stackalloc double[LocalSky.Terms];
        for (var index = 0; index < values.Count; index++)
        {
            if (!kept[index])
            {
                continue;
            }
            LocalSky.Basis(us[index], vs[index], basis);
            for (var row = 0; row < n; row++)
            {
                for (var column = 0; column < n; column++)
                {
                    matrix[row * columns + column] += basis[row] * basis[column];
                }
                matrix[row * columns + n] += basis[row] * values[index];
            }
        }
        var scale = Math.Abs(matrix[0]);
        for (var pivot = 0; pivot < n; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < n; row++)
            {
                if (Math.Abs(matrix[row * columns + pivot]) > Math.Abs(matrix[best * columns + pivot]))
                {
                    best = row;
                }
            }
            if (!(Math.Abs(matrix[best * columns + pivot]) > 1e-10 * scale))
            {
                return false;
            }
            for (var column = 0; column <= n; column++)
            {
                (matrix[pivot * columns + column], matrix[best * columns + column]) =
                    (matrix[best * columns + column], matrix[pivot * columns + column]);
            }
            for (var row = 0; row < n; row++)
            {
                if (row == pivot)
                {
                    continue;
                }
                var factor = matrix[row * columns + pivot] / matrix[pivot * columns + pivot];
                for (var column = pivot; column <= n; column++)
                {
                    matrix[row * columns + column] -= factor * matrix[pivot * columns + column];
                }
            }
        }
        Array.Clear(solution);
        for (var row = 0; row < n; row++)
        {
            solution[row] = matrix[row * columns + n] / matrix[row * columns + row];
        }
        return true;
    }

    /// <summary>
    /// Median and scaled median absolute deviation after iterated clipping about the median, falling back to the standard
    /// deviation when quantized data makes the median absolute deviation zero.
    /// </summary>
    private static (double Median, double Sigma) RobustStatistics(List<double> values, double clipSigma = 3)
    {
        var current = values.ToArray();
        double median = 0, sigma = 0;
        for (var pass = 0; pass < 5; pass++)
        {
            Array.Sort(current);
            median = Median(current);
            var deviations = new double[current.Length];
            double squares = 0;
            for (var index = 0; index < current.Length; index++)
            {
                deviations[index] = Math.Abs(current[index] - median);
                squares += deviations[index] * deviations[index];
            }
            Array.Sort(deviations);
            sigma = MadToSigma * Median(deviations);
            if (!(sigma > 0))
            {
                sigma = Math.Sqrt(squares / Math.Max(1, current.Length - 1));
            }
            if (!(sigma > 0))
            {
                break;
            }
            var limit = clipSigma * sigma;
            var kept = current.Where(value => Math.Abs(value - median) <= limit).ToArray();
            if (kept.Length == current.Length || kept.Length < 8)
            {
                break;
            }
            current = kept;
        }
        return (median, sigma);
    }

    /// <summary>Quadratic sky surface about the annulus centre in coordinates scaled by the annulus outer radius.</summary>
    private readonly struct LocalSky
    {
        public const int Terms = 6;
        public const int PlaneTerms = 3;
        private readonly double _cx;
        private readonly double _cy;
        private readonly double _inverseScale;
        private readonly double _c0, _c1, _c2, _c3, _c4, _c5;

        public LocalSky(double cx, double cy, double scale, double[] coefficients)
        {
            _cx = cx;
            _cy = cy;
            _inverseScale = 1 / scale;
            (_c0, _c1, _c2, _c3, _c4, _c5) =
                (coefficients[0], coefficients[1], coefficients[2], coefficients[3], coefficients[4], coefficients[5]);
        }

        /// <summary>Sky at the fitted centre, reported as the measurement's background.</summary>
        public double Level => _c0;

        /// <summary>Sky at the centre of sample (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public double At(int x, int y) => AtOffset((x + 0.5 - _cx) * _inverseScale, (y + 0.5 - _cy) * _inverseScale);

        public double AtOffset(double u, double v) => _c0 + _c1 * u + _c2 * v + _c3 * u * u + _c4 * u * v + _c5 * v * v;

        public static void Basis(double u, double v, Span<double> basis)
        {
            basis[0] = 1;
            basis[1] = u;
            basis[2] = v;
            basis[3] = u * u;
            basis[4] = u * v;
            basis[5] = v * v;
        }
    }

    private static double Median(double[] sorted)
    {
        var count = sorted.Length;
        return (sorted[(count - 1) / 2] + sorted[count / 2]) / 2;
    }

    private static void Validate(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        FocusStarMeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height != pixels.Length || validMask.Length != pixels.Length)
        {
            throw new ArgumentException("The pixels and valid mask must match the declared layout.", nameof(pixels));
        }
        if (!saturatedMask.IsEmpty && saturatedMask.Length != pixels.Length)
        {
            throw new ArgumentException("The saturation mask must be empty or match the image.", nameof(saturatedMask));
        }
        if (!(options.ApertureRadiusPixels >= 2) ||
            !(options.AnnulusInnerRadiusPixels >= options.ApertureRadiusPixels) ||
            !(options.AnnulusOuterRadiusPixels > options.AnnulusInnerRadiusPixels + 1) ||
            options.AnnulusOuterRadiusPixels > 256 ||
            !(options.BackgroundClipSigma > 0) || options.MinimumAnnulusSamples < 8 ||
            !(options.CentroidThresholdSigma >= 0) || options.MaximumCentroidIterations < 1 ||
            !(options.CentroidConvergencePixels > 0) || !(options.MinimumPeakSigma >= 0) ||
            !(options.MinimumSignalToNoise >= 0) || options.MaximumSaturatedSamples < 0 ||
            !(options.MaximumHalfFluxApertureFraction is > 0 and <= 1) ||
            !(options.MaximumOuterFluxFraction is > 0 and <= 1) || options.MaximumTargetCandidates < 2 ||
            !(options.MinimumApertureRadiusPixels >= 2) ||
            !(options.MinimumApertureRadiusPixels <= options.ApertureRadiusPixels))
        {
            throw new ArgumentException("The focus star measurement options are invalid.", nameof(options));
        }
    }
}
