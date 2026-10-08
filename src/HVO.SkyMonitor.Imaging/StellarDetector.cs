using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Bounded, host-neutral detection of isolated stellar centroids from linear monochrome or reconstructed luminance.</summary>
/// <remarks>
/// Raw CFA mosaics must first pass through <see cref="LinearBayerReconstruction"/>. Display-stretched or gamma-encoded
/// samples are not supported. A true mask entry denotes a valid sample. The entire 9-by-9 measurement aperture must
/// be valid; a six-pixel image border is excluded. Sources within the isolation radius are all rejected, not deblended.
/// Width and shape thresholds must be selected for the declared optical PSF, exposure, and reconstruction.
/// No caller buffers are retained or modified. Callers must not modify input buffers during a call.
/// </remarks>
public static class StellarDetector
{
    public const string AlgorithmVersion = "linear-stellar-moments-v1";
    public const int MaximumSupportedPixels = 16_000_000;
    public const int MaximumSupportedCandidates = 4096;

    /// <exception cref="ArgumentException">The layout, thresholds, samples, or representable dynamic range are invalid.</exception>
    /// <exception cref="InvalidOperationException">The candidate budget is exceeded; no partial successful result is returned.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static StellarDetectionResult Detect(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        int width,
        int height,
        StellarDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ValidateOptions(options);
        LinearStellarInput.Validate(pixels, validMask, width, height, 13, options.MaximumPixelCount, cancellationToken);

        var validCount = 0;
        for (var index = 0; index < validMask.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (validMask[index])
            {
                validCount++;
            }
        }
        if (validCount == 0)
        {
            return new([], 0, 0, 0);
        }

        var samples = new double[validCount];
        var sampleIndex = 0;
        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (validMask[index])
            {
                samples[sampleIndex++] = pixels[index];
            }
        }
        var background = Median(samples, cancellationToken);
        for (var index = 0; index < samples.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            samples[index] = Math.Abs(samples[index] - background);
            EnsureFinite(samples[index]);
        }
        var noiseSigma = 1.4826 * Median(samples, cancellationToken);
        var threshold = Math.Max(options.MinimumPeakAboveBackground, options.NoiseThresholdMultiplier * noiseSigma);
        EnsureFinite(noiseSigma);
        EnsureFinite(threshold);

        var candidates = new List<StellarDetection>();
        var candidateCount = 0;
        for (var y = 6; y < height - 6; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 6; x < width - 6; x++)
            {
                if ((x & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var index = y * width + x;
                if (!validMask[index] || pixels[index] - background < threshold ||
                    !IsPeak(pixels, width, x, y) || !HasValidAperture(validMask, width, x, y))
                {
                    continue;
                }
                if (++candidateCount > options.MaximumCandidateCount)
                {
                    throw new InvalidOperationException("The stellar detection candidate budget was exceeded.");
                }
                var detection = Measure(pixels, width, x, y, background, noiseSigma);
                if (detection.PsfMinorSigma < options.MinimumMinorSigma ||
                    detection.PsfMajorSigma > options.MaximumMajorSigma ||
                    detection.PsfMajorSigma / detection.PsfMinorSigma > Math.Sqrt(options.MaximumVarianceRatio))
                {
                    continue;
                }
                candidates.Add(detection);
            }
        }

        var accepted = new List<StellarDetection>();
        var radiusSquared = options.IsolationRadiusPixels * options.IsolationRadiusPixels;
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Flux))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isolated = true;
            foreach (var other in candidates)
            {
                if (ReferenceEquals(candidate, other))
                {
                    continue;
                }
                var dx = candidate.Pixel.X - other.Pixel.X;
                var dy = candidate.Pixel.Y - other.Pixel.Y;
                if (dx * dx + dy * dy <= radiusSquared)
                {
                    isolated = false;
                    break;
                }
            }
            if (isolated)
            {
                accepted.Add(candidate with { Index = accepted.Count });
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(accepted, background, noiseSigma, candidateCount);
    }

    private static bool IsPeak(ReadOnlySpan<double> pixels, int width, int x, int y)
    {
        var peak = pixels[y * width + x];
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                if ((dx != 0 || dy != 0) && pixels[(y + dy) * width + x + dx] >= peak)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool HasValidAperture(ReadOnlySpan<bool> validMask, int width, int x, int y)
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                if (!validMask[(y + dy) * width + x + dx])
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static StellarDetection Measure(ReadOnlySpan<double> pixels, int width, int x, int y, double background, double noiseSigma)
    {
        // Local coordinates avoid precision loss from multiplying moments by the absolute detector position.
        double sum = 0, sx = 0, sy = 0;
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var weight = Math.Max(0, pixels[(y + dy) * width + x + dx] - background);
                sum += weight;
                sx += weight * dx;
                sy += weight * dy;
            }
        }
        EnsureFinite(sum);
        EnsureFinite(sx);
        EnsureFinite(sy);
        var cx = sx / sum;
        var cy = sy / sum;
        double mxx = 0, myy = 0, mxy = 0;
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -4; dx <= 4; dx++)
            {
                var weight = Math.Max(0, pixels[(y + dy) * width + x + dx] - background) / sum;
                var rx = dx - cx;
                var ry = dy - cy;
                mxx += weight * rx * rx;
                myy += weight * ry * ry;
                mxy += weight * rx * ry;
            }
        }
        var trace = mxx + myy;
        var discriminant = Math.Sqrt((mxx - myy) * (mxx - myy) + 4 * mxy * mxy);
        var majorSigma = Math.Sqrt(Math.Max(0, (trace + discriminant) / 2));
        var minorSigma = Math.Sqrt(Math.Max(0, (trace - discriminant) / 2));
        EnsureFinite(cx);
        EnsureFinite(cy);
        EnsureFinite(majorSigma);
        EnsureFinite(minorSigma);
        return new(0, new PixelPoint(x + 0.5 + cx, y + 0.5 + cy), sum, pixels[y * width + x],
            majorSigma, minorSigma, background, noiseSigma);
    }

    private static double Median(double[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(samples);
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve the reference detector's upper median convention for even sample counts.
        return samples[samples.Length / 2];
    }

    private static void EnsureFinite(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("The input dynamic range cannot be represented by finite stellar measurements.");
        }
    }

    private static void ValidateOptions(StellarDetectionOptions options)
    {
        if (!double.IsFinite(options.MinimumPeakAboveBackground) || options.MinimumPeakAboveBackground <= 0 ||
            !double.IsFinite(options.NoiseThresholdMultiplier) || options.NoiseThresholdMultiplier < 0 ||
            !double.IsFinite(options.MinimumMinorSigma) || options.MinimumMinorSigma <= 0 ||
            !double.IsFinite(options.MaximumMajorSigma) || options.MaximumMajorSigma < options.MinimumMinorSigma ||
            !double.IsFinite(options.MaximumVarianceRatio) || options.MaximumVarianceRatio < 1 ||
            !double.IsFinite(options.IsolationRadiusPixels) || options.IsolationRadiusPixels < 0 ||
            options.IsolationRadiusPixels > MaximumSupportedPixels ||
            options.MaximumPixelCount is < 169 or > MaximumSupportedPixels ||
            options.MaximumCandidateCount is < 1 or > MaximumSupportedCandidates)
        {
            throw new ArgumentException("Stellar detection thresholds or resource limits are invalid.", nameof(options));
        }
    }
}
