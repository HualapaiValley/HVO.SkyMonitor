using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Declared thresholds for <see cref="StellarSourceMeasurer"/> in linear, un-stretched sample units.</summary>
/// <remarks>
/// Width, trail, and sharpness bounds must describe the declared optical PSF, exposure, and reconstruction. They are
/// engineering gates, not fitted optical parameters. <see cref="ElectronsPerSampleUnit"/> adds source shot noise to the
/// propagated variance; when null only the locally measured background noise is propagated.
/// <see cref="NoiseEstimator"/> must match the image's spatial noise correlation: interpolated CFA luminance correlates
/// neighboring samples, so adjacent differences understate its noise and it must use the clipped spread.
/// </remarks>
public sealed record StellarMeasurementOptions(
    int BackgroundTileSizePixels = 64,
    int MinimumBackgroundTileSamples = 256,
    double BackgroundClipSigma = 3,
    double SegmentationSigma = 3,
    double PeakSigma = 5,
    double MinimumPeakAboveBackground = 8,
    double WindowSigmaPixels = 1,
    double MinimumMinorSigma = 0.35,
    double MaximumMinorSigma = 2.5,
    double MaximumSharpness = 0.7,
    double MinimumTrailLengthPixels = 3,
    double MaximumTrailLengthPixels = 16,
    int MaximumComponentSamples = 600,
    int MaximumSaturatedSamples = 16,
    int SaturationDilationPixels = 0,
    double BlendMinimumFraction = 0.15,
    double BlendSaddleFraction = 0.7,
    double CrowdingPeakRatio = 0.05,
    double MinimumSignalToNoise = 5,
    double? ElectronsPerSampleUnit = null,
    int MaximumPixelCount = StellarDetector.MaximumSupportedPixels,
    int MaximumCandidateCount = StellarDetector.MaximumSupportedCandidates,
    StellarNoiseEstimator NoiseEstimator = StellarNoiseEstimator.AdjacentDifferences);

/// <summary>How each background tile's per-sample noise is estimated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StellarNoiseEstimator>))]
public enum StellarNoiseEstimator
{
    /// <summary>Clipped spread of adjacent horizontal differences over the square root of two; immune to smooth
    /// gradients but valid only when neighboring samples carry independent noise.</summary>
    AdjacentDifferences,

    /// <summary>Clipped spread of the tile's samples; valid for spatially correlated noise, inflated by steep gradients.</summary>
    ClippedSpread
}

/// <summary>Stable reason codes for candidates that passed the peak gate but were not accepted as measured stars.</summary>
public static class StellarExclusionReasons
{
    public const string BackgroundUnavailable = "background-unavailable";
    public const string ImageEdge = "image-edge";
    public const string ExtendedRegion = "extended-region";
    public const string MaskedAperture = "masked-aperture";
    public const string SaturatedExcessive = "saturated-excessive";
    public const string HotPixelOrCosmicRay = "hot-pixel-or-cosmic-ray";
    public const string TooBroad = "too-broad";
    public const string TrailTooLong = "trail-too-long";
    public const string Blended = "blended";
    public const string Crowded = "crowded";
    public const string LowSignalToNoise = "low-snr";

    /// <summary>Every code in gate order. The first failing gate is reported; a windowed centroid that cannot converge
    /// on positive signal is also reported as <see cref="LowSignalToNoise"/>.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        ImageEdge, ExtendedRegion, BackgroundUnavailable, MaskedAperture, SaturatedExcessive,
        HotPixelOrCosmicRay, TooBroad, TrailTooLong, Blended, Crowded, LowSignalToNoise
    });
}

public enum StellarMeasurementStatus
{
    Completed,
    CandidateBudgetExceeded
}

/// <summary>A gated candidate that was not accepted. The pixel is the peak sample center, not a measured centroid.</summary>
public sealed record StellarMeasurementExclusion(PixelPoint Peak, double PeakAboveBackground, int SampleCount, string ReasonCode);

/// <summary>Owned, immutable measurement output. A budget failure carries no partial detections or exclusions.</summary>
public sealed class StellarMeasurementResult
{
    internal StellarMeasurementResult(StellarMeasurementStatus status, List<StellarDetection> detections,
        List<StellarMeasurementExclusion> exclusions, int candidateCount, int tiles, int availableTiles, int filledTiles,
        double? medianBackground, double? medianNoiseSigma, string settingsIdentitySha256)
    {
        Status = status;
        Detections = detections.AsReadOnly();
        Exclusions = exclusions.AsReadOnly();
        ExclusionCounts = StellarExclusionReasons.All
            .Select(code => (Code: code, Count: exclusions.Count(exclusion => exclusion.ReasonCode == code)))
            .Where(entry => entry.Count > 0)
            .ToDictionary(entry => entry.Code, entry => entry.Count, StringComparer.Ordinal)
            .AsReadOnly();
        CandidateCount = candidateCount;
        BackgroundTileCount = tiles;
        AvailableBackgroundTileCount = availableTiles;
        FilledBackgroundTileCount = filledTiles;
        MedianBackground = medianBackground;
        MedianNoiseSigma = medianNoiseSigma;
        SettingsIdentitySha256 = settingsIdentitySha256;
    }

    public StellarMeasurementStatus Status { get; }

    /// <summary>Accepted sources ordered by decreasing flux with stable peak scan-order ties.</summary>
    public IReadOnlyList<StellarDetection> Detections { get; }

    /// <summary>Excluded candidates in peak scan order.</summary>
    public IReadOnlyList<StellarMeasurementExclusion> Exclusions { get; }

    public IReadOnlyDictionary<string, int> ExclusionCounts { get; }

    /// <summary>Connected components whose peak passed the local significance gate.</summary>
    public int CandidateCount { get; }
    public int BackgroundTileCount { get; }

    /// <summary>Tiles with enough valid unsaturated samples for an independent local estimate.</summary>
    public int AvailableBackgroundTileCount { get; }

    /// <summary>Tiles without an estimate that were filled from available neighbors.</summary>
    public int FilledBackgroundTileCount { get; }
    /// <summary>Median of independently estimated tiles, or null when no tile had enough usable samples.</summary>
    public double? MedianBackground { get; }
    public double? MedianNoiseSigma { get; }
    public string AlgorithmVersion { get; } = StellarSourceMeasurer.AlgorithmVersion;
    public string SettingsIdentitySha256 { get; }
}

/// <summary>Bounded, host-neutral measurement of stellar sources from linear monochrome or reconstructed luminance.</summary>
/// <remarks>
/// <para>Background is the sigma-clipped tile median. Noise is the clipped, scaled median absolute deviation of adjacent
/// horizontal differences divided by the square root of two, so smooth gradients do not inflate it. Both are bilinearly
/// interpolated between tile centers. Pixels more than the segmentation threshold above the local background form
/// eight-connected components. A component is a candidate when its peak exceeds the local peak gate.</para>
/// <para>A true valid-mask entry denotes a usable sample. Saturated samples must remain valid and be marked in the
/// separate saturation mask so that they can be counted rather than silently discarded; they never enter the
/// background estimate. Raw CFA mosaics must first pass through <see cref="LinearBayerReconstruction"/>; set
/// <see cref="StellarMeasurementOptions.SaturationDilationPixels"/> to one so that interpolated neighbors of a saturated
/// photosite are also counted, and select <see cref="StellarNoiseEstimator.ClippedSpread"/> because interpolation
/// correlates neighboring samples.</para>
/// <para>Centroids are iterative Gaussian-windowed first moments. The centroid covariance propagates the declared
/// per-sample noise model through the window's fixed-point equation. It ignores interpolation-correlated noise, PSF
/// model error, and saturation clipping, so it is reported as unvalidated and must not be treated as calibrated
/// coverage. No caller buffers are retained or modified.</para>
/// </remarks>
public static class StellarSourceMeasurer
{
    public const string AlgorithmVersion = "linear-stellar-local-v2";

    private const int MinimumDimension = 16;

    /// <exception cref="ArgumentException">The layout, options, masks, or samples are invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static StellarMeasurementResult Measure(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        StellarMeasurementOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ValidateOptions(options);
        LinearStellarInput.Validate(pixels, validMask, width, height, MinimumDimension, options.MaximumPixelCount, cancellationToken);
        if (!saturatedMask.IsEmpty && saturatedMask.Length != pixels.Length)
        {
            throw new ArgumentException("The saturation mask must be empty or match the image.", nameof(saturatedMask));
        }

        var identity = SettingsIdentity(options);
        var saturated = Dilate(saturatedMask, width, height, options.SaturationDilationPixels, cancellationToken);
        var mesh = BackgroundMesh.Create(pixels, validMask, saturated, width, height, options, cancellationToken);
        var labels = new int[pixels.Length];
        var components = Segment(pixels, validMask, saturated, width, height, mesh, options, labels, cancellationToken);
        var candidates = components.Where(component => component.IsCandidate).ToList();
        if (candidates.Count > options.MaximumCandidateCount)
        {
            return new(StellarMeasurementStatus.CandidateBudgetExceeded, [], [], candidates.Count,
                mesh.TileCount, mesh.AvailableCount, mesh.FilledCount, mesh.MedianBackground, mesh.MedianNoise, identity);
        }

        var outcomes = new Dictionary<int, Outcome>(candidates.Count);
        foreach (var component in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes[component.Label] = Evaluate(pixels, validMask, saturated, width, height, mesh, options, labels, components, component);
        }

        // Crowding is resolved after every candidate is classified: a neighbor that is itself a noise artifact cannot
        // contaminate the window, because its samples are already withheld from the measurement.
        var accepted = new List<(StellarDetection Detection, int PeakIndex)>();
        var exclusions = new List<StellarMeasurementExclusion>();
        foreach (var component in candidates)
        {
            var outcome = outcomes[component.Label];
            if (outcome.CrowdingNeighbors?.Any(label => outcomes[label].Reason is not
                    (StellarExclusionReasons.HotPixelOrCosmicRay or StellarExclusionReasons.LowSignalToNoise)) == true)
            {
                outcome = new(null, StellarExclusionReasons.Crowded);
            }
            if (outcome.Detection is { } detection)
            {
                accepted.Add((detection, component.PeakIndex));
            }
            else
            {
                exclusions.Add(new(new(component.PeakIndex % width + 0.5, component.PeakIndex / width + 0.5),
                    component.PeakExcess, component.Count, outcome.Reason!));
            }
        }
        var ordered = accepted
            .OrderByDescending(entry => entry.Detection.Flux)
            .ThenBy(entry => entry.PeakIndex)
            .Select((entry, index) => entry.Detection with { Index = index })
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return new(StellarMeasurementStatus.Completed, ordered, exclusions, candidates.Count,
            mesh.TileCount, mesh.AvailableCount, mesh.FilledCount, mesh.MedianBackground, mesh.MedianNoise, identity);
    }

    /// <summary>Content identity of the algorithm version and every declared threshold.</summary>
    public static string SettingsIdentity(StellarMeasurementOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "stellar-measurement-settings-v1",
            algorithm = AlgorithmVersion,
            options
        })));
    }

    private sealed class Component
    {
        public int Label;
        public int Count;
        public int PeakIndex;
        public double PeakExcess;
        public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
        public int SaturatedCount;
        public bool IsCandidate;
    }

    private static List<Component> Segment(ReadOnlySpan<double> pixels, ReadOnlySpan<bool> valid, bool[] saturated,
        int width, int height, BackgroundMesh mesh, StellarMeasurementOptions options, int[] labels,
        CancellationToken cancellationToken)
    {
        var components = new List<Component> { new() };
        var stack = new Stack<int>();
        var floor = options.MinimumPeakAboveBackground * options.SegmentationSigma / options.PeakSigma;
        bool Member(ReadOnlySpan<double> samples, ReadOnlySpan<bool> mask, int index, out double excess)
        {
            excess = 0;
            if (!mask[index] || !mesh.TrySample(index % width, index / width, out var background, out var noise))
            {
                return false;
            }
            excess = samples[index] - background;
            return excess > Math.Max(options.SegmentationSigma * noise, floor);
        }

        for (var start = 0; start < pixels.Length; start++)
        {
            if ((start & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (labels[start] != 0 || !Member(pixels, valid, start, out _))
            {
                continue;
            }
            var component = new Component { Label = components.Count, PeakExcess = double.NegativeInfinity };
            components.Add(component);
            labels[start] = component.Label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var x = index % width;
                var y = index / width;
                Member(pixels, valid, index, out var excess);
                component.Count++;
                if ((component.Count & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                // Scan-order tie breaking keeps the reported peak independent of stack traversal order.
                if (excess > component.PeakExcess || excess == component.PeakExcess && index < component.PeakIndex)
                {
                    component.PeakExcess = excess;
                    component.PeakIndex = index;
                }
                component.MinX = Math.Min(component.MinX, x);
                component.MaxX = Math.Max(component.MaxX, x);
                component.MinY = Math.Min(component.MinY, y);
                component.MaxY = Math.Max(component.MaxY, y);
                if (saturated.Length > 0 && saturated[index])
                {
                    component.SaturatedCount++;
                }
                for (var dy = -1; dy <= 1; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width || dx == 0 && dy == 0)
                        {
                            continue;
                        }
                        var neighbor = ny * width + nx;
                        if (labels[neighbor] == 0 && Member(pixels, valid, neighbor, out _))
                        {
                            labels[neighbor] = component.Label;
                            stack.Push(neighbor);
                        }
                    }
                }
            }
            mesh.TrySample(component.PeakIndex % width, component.PeakIndex / width, out _, out var peakNoise);
            component.IsCandidate = component.PeakExcess >= Math.Max(options.MinimumPeakAboveBackground, options.PeakSigma * peakNoise);
        }
        return components;
    }

    /// <summary>A provisional result; <paramref name="CrowdingNeighbors"/> lists brighter-than-ratio neighbor candidates
    /// whose classification decides whether the result is replaced by <see cref="StellarExclusionReasons.Crowded"/>.</summary>
    private readonly record struct Outcome(StellarDetection? Detection, string? Reason, IReadOnlyCollection<int>? CrowdingNeighbors = null);

    private static Outcome Evaluate(ReadOnlySpan<double> pixels, ReadOnlySpan<bool> valid, bool[] saturated,
        int width, int height, BackgroundMesh mesh, StellarMeasurementOptions options, int[] labels,
        List<Component> components, Component component)
    {
        var margin = Math.Max(3, (int)Math.Ceiling(4 * options.WindowSigmaPixels));
        var x0 = component.MinX - margin;
        var y0 = component.MinY - margin;
        var x1 = component.MaxX + margin;
        var y1 = component.MaxY + margin;
        if (x0 < 0 || y0 < 0 || x1 >= width || y1 >= height)
        {
            return new(null, StellarExclusionReasons.ImageEdge);
        }
        if (component.Count > options.MaximumComponentSamples)
        {
            return new(null, StellarExclusionReasons.ExtendedRegion);
        }
        var windowWidth = x1 - x0 + 1;
        var windowHeight = y1 - y0 + 1;
        var excess = new double[windowWidth * windowHeight];
        var variance = new double[excess.Length];
        var usable = new bool[excess.Length];
        var own = new bool[excess.Length];
        var crowding = new HashSet<int>();
        var missingBackground = false;
        var masked = false;
        for (var wy = 0; wy < windowHeight; wy++)
        {
            for (var wx = 0; wx < windowWidth; wx++)
            {
                var index = (y0 + wy) * width + x0 + wx;
                var local = wy * windowWidth + wx;
                if (!mesh.TrySample(x0 + wx, y0 + wy, out var background, out var noise))
                {
                    missingBackground = true;
                    continue;
                }
                if (!valid[index])
                {
                    masked = true;
                    continue;
                }
                var label = labels[index];
                own[local] = label == component.Label;
                if (label != 0 && label != component.Label && components[label].IsCandidate)
                {
                    if (components[label].PeakExcess >= options.CrowdingPeakRatio * component.PeakExcess)
                    {
                        crowding.Add(label);
                    }
                    continue;
                }
                excess[local] = pixels[index] - background;
                variance[local] = noise * noise +
                    (options.ElectronsPerSampleUnit is { } gain ? Math.Max(0, excess[local]) / gain : 0);
                usable[local] = true;
            }
        }
        if (missingBackground)
        {
            return new(null, StellarExclusionReasons.BackgroundUnavailable);
        }
        if (masked)
        {
            return new(null, StellarExclusionReasons.MaskedAperture);
        }
        if (component.SaturatedCount > options.MaximumSaturatedSamples)
        {
            return new(null, StellarExclusionReasons.SaturatedExcessive);
        }

        // Isophotal start, then the Gaussian-windowed fixed point used for centroid and covariance.
        double sum = 0, sx = 0, sy = 0;
        for (var local = 0; local < excess.Length; local++)
        {
            if (own[local])
            {
                sum += excess[local];
                sx += excess[local] * (local % windowWidth + 0.5);
                sy += excess[local] * (local / windowWidth + 0.5);
            }
        }
        var cx = sx / sum;
        var cy = sy / sum;
        var sigmaSquared = options.WindowSigmaPixels * options.WindowSigmaPixels;
        for (var iteration = 0; iteration < 60; iteration++)
        {
            double weighted = 0, mx = 0, my = 0;
            for (var local = 0; local < excess.Length; local++)
            {
                if (!usable[local])
                {
                    continue;
                }
                var dx = local % windowWidth + 0.5 - cx;
                var dy = local / windowWidth + 0.5 - cy;
                var weight = Math.Exp(-(dx * dx + dy * dy) / (2 * sigmaSquared)) * excess[local];
                weighted += weight;
                mx += weight * dx;
                my += weight * dy;
            }
            if (!(weighted > 0) || !double.IsFinite(weighted))
            {
                return new(null, StellarExclusionReasons.LowSignalToNoise);
            }
            var stepX = mx / weighted;
            var stepY = my / weighted;
            cx += stepX;
            cy += stepY;
            if (cx < 0 || cy < 0 || cx > windowWidth || cy > windowHeight)
            {
                return new(null, StellarExclusionReasons.LowSignalToNoise);
            }
            if (Math.Abs(stepX) + Math.Abs(stepY) < 1e-7)
            {
                break;
            }
        }

        // Flux and shape use the own footprint grown by the window radius, excluding other candidates' samples.
        var grow = Math.Max(2, (int)Math.Ceiling(2 * options.WindowSigmaPixels));
        var aperture = Grow(own, windowWidth, windowHeight, grow);
        double flux = 0, fluxVariance = 0, positive = 0, mxx = 0, myy = 0, mxy = 0;
        for (var local = 0; local < excess.Length; local++)
        {
            if (!aperture[local] || !usable[local])
            {
                continue;
            }
            flux += excess[local];
            fluxVariance += variance[local];
            var weight = Math.Max(0, excess[local]);
            var dx = local % windowWidth + 0.5 - cx;
            var dy = local / windowWidth + 0.5 - cy;
            positive += weight;
            mxx += weight * dx * dx;
            myy += weight * dy * dy;
            mxy += weight * dx * dy;
        }
        if (!(flux > 0) || !(positive > 0))
        {
            return new(null, StellarExclusionReasons.LowSignalToNoise);
        }
        mxx /= positive;
        myy /= positive;
        mxy /= positive;
        var trace = mxx + myy;
        var discriminant = Math.Sqrt((mxx - myy) * (mxx - myy) + 4 * mxy * mxy);
        var major = Math.Sqrt(Math.Max(0, (trace + discriminant) / 2));
        var minor = Math.Sqrt(Math.Max(0, (trace - discriminant) / 2));
        var trail = Math.Sqrt(12 * Math.Max(0, major * major - minor * minor));
        var sharpness = component.PeakExcess / flux;
        if (component.Count == 1 || minor < options.MinimumMinorSigma || sharpness > options.MaximumSharpness)
        {
            return new(null, StellarExclusionReasons.HotPixelOrCosmicRay);
        }
        if (minor > options.MaximumMinorSigma)
        {
            return new(null, StellarExclusionReasons.TooBroad);
        }
        if (trail > options.MaximumTrailLengthPixels)
        {
            return new(null, StellarExclusionReasons.TrailTooLong);
        }
        mesh.TrySample(component.PeakIndex % width, component.PeakIndex / width, out _, out var peakNoise);
        if (IsBlended(excess, own, windowWidth, windowHeight, component, x0, y0, width, options, peakNoise))
        {
            return new(null, StellarExclusionReasons.Blended);
        }
        IReadOnlyCollection<int>? neighbors = crowding.Count > 0 ? crowding : null;
        double? signalToNoise = fluxVariance > 0 ? flux / Math.Sqrt(fluxVariance) : null;
        if (signalToNoise < options.MinimumSignalToNoise)
        {
            return new(null, StellarExclusionReasons.LowSignalToNoise, neighbors);
        }

        var (covariance, covarianceStatus) = Covariance(excess, variance, usable, windowWidth, cx, cy, sigmaSquared,
            component.SaturatedCount);
        var flags = StellarSourceConditions.None;
        if (component.SaturatedCount > 0)
        {
            flags |= StellarSourceConditions.Saturated;
        }
        var trailed = trail >= options.MinimumTrailLengthPixels;
        if (trailed)
        {
            flags |= StellarSourceConditions.Trailed;
        }
        if (covariance is null)
        {
            flags |= StellarSourceConditions.CovarianceUnavailable;
        }
        mesh.TrySample(component.PeakIndex % width, component.PeakIndex / width, out var peakBackground, out _);
        var angle = 0.5 * Math.Atan2(2 * mxy, mxx - myy) * 180 / Math.PI;
        if (angle >= 90)
        {
            angle -= 180;
        }
        return new(new StellarDetection(0, new(x0 + cx, y0 + cy), flux, pixels[component.PeakIndex], major, minor,
            peakBackground, peakNoise)
        {
            CentroidCovariance = covariance,
            CentroidCovarianceStatus = covarianceStatus,
            SignalToNoise = signalToNoise,
            Conditions = flags,
            SaturatedSampleCount = component.SaturatedCount,
            TrailLengthPixels = trail,
            TrailAngleDegrees = trailed ? angle : null
        }, null, neighbors);
    }

    private static (StellarCentroidCovariance? Covariance, string Status) Covariance(double[] excess, double[] variance,
        bool[] usable, int windowWidth, double cx, double cy, double sigmaSquared, int saturatedCount)
    {
        // The windowed centroid solves F(c) = sum w(r) I r = 0. Linear propagation gives
        // Cov(c) = J^-1 S J^-T with J = dF/dc and S = sum w^2 r r^T var(I).
        double jxx = 0, jxy = 0, jyy = 0, sxx = 0, sxy = 0, syy = 0;
        for (var local = 0; local < excess.Length; local++)
        {
            if (!usable[local])
            {
                continue;
            }
            var dx = local % windowWidth + 0.5 - cx;
            var dy = local / windowWidth + 0.5 - cy;
            var weight = Math.Exp(-(dx * dx + dy * dy) / (2 * sigmaSquared));
            var wi = weight * excess[local];
            jxx += wi * (1 - dx * dx / sigmaSquared);
            jyy += wi * (1 - dy * dy / sigmaSquared);
            jxy -= wi * dx * dy / sigmaSquared;
            var w2 = weight * weight * variance[local];
            sxx += w2 * dx * dx;
            syy += w2 * dy * dy;
            sxy += w2 * dx * dy;
        }
        var determinant = jxx * jyy - jxy * jxy;
        if (!(determinant > 0) || !double.IsFinite(determinant) || jxx <= 0)
        {
            return (null, "unavailable-singular-window");
        }
        // Inverse of the symmetric Jacobian.
        var ixx = jyy / determinant;
        var iyy = jxx / determinant;
        var ixy = -jxy / determinant;
        var cxx = ixx * (ixx * sxx + ixy * sxy) + ixy * (ixx * sxy + ixy * syy);
        var cxy = ixx * (ixy * sxx + iyy * sxy) + ixy * (ixy * sxy + iyy * syy);
        var cyy = ixy * (ixy * sxx + iyy * sxy) + iyy * (ixy * sxy + iyy * syy);
        var inflation = saturatedCount > 0 ? 1 + saturatedCount : 1;
        var covariance = new StellarCentroidCovariance(cxx * inflation, cxy * inflation, cyy * inflation);
        if (!double.IsFinite(covariance.XX) || !double.IsFinite(covariance.XY) || !double.IsFinite(covariance.YY))
        {
            return (null, "unavailable-singular-window");
        }
        return (covariance, saturatedCount > 0
            ? "propagated-noise-saturation-inflated-unvalidated"
            : "propagated-noise-unvalidated");
    }

    private static bool IsBlended(double[] excess, bool[] own, int windowWidth, int windowHeight, Component component,
        int x0, int y0, int width, StellarMeasurementOptions options, double peakNoise)
    {
        var primaryX = component.PeakIndex % width - x0;
        var primaryY = component.PeakIndex / width - y0;
        var significant = Math.Max(Math.Max(options.MinimumPeakAboveBackground, options.PeakSigma * peakNoise),
            options.BlendMinimumFraction * component.PeakExcess);
        for (var wy = 1; wy < windowHeight - 1; wy++)
        {
            for (var wx = 1; wx < windowWidth - 1; wx++)
            {
                var local = wy * windowWidth + wx;
                if (!own[local] || excess[local] < significant ||
                    Math.Max(Math.Abs(wx - primaryX), Math.Abs(wy - primaryY)) < 2)
                {
                    continue;
                }
                var isMaximum = true;
                for (var dy = -1; dy <= 1 && isMaximum; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var neighbor = (wy + dy) * windowWidth + wx + dx;
                        if ((dx != 0 || dy != 0) && own[neighbor] && excess[neighbor] > excess[local])
                        {
                            isMaximum = false;
                            break;
                        }
                    }
                }
                if (!isMaximum)
                {
                    continue;
                }
                // A real second source leaves a saddle between the peaks; a trail or saturated plateau does not.
                var steps = Math.Max(Math.Abs(wx - primaryX), Math.Abs(wy - primaryY));
                var saddle = double.PositiveInfinity;
                for (var step = 1; step < steps; step++)
                {
                    var sx = (int)Math.Round(primaryX + (wx - primaryX) * step / (double)steps);
                    var sy = (int)Math.Round(primaryY + (wy - primaryY) * step / (double)steps);
                    saddle = Math.Min(saddle, excess[sy * windowWidth + sx]);
                }
                if (saddle <= options.BlendSaddleFraction * excess[local])
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool[] Grow(bool[] mask, int width, int height, int radius)
    {
        var horizontal = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var dx = -radius; dx <= radius && !horizontal[y * width + x]; dx++)
                {
                    var nx = x + dx;
                    horizontal[y * width + x] = nx >= 0 && nx < width && mask[y * width + nx];
                }
            }
        }
        var result = new bool[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var dy = -radius; dy <= radius && !result[y * width + x]; dy++)
                {
                    var ny = y + dy;
                    result[y * width + x] = ny >= 0 && ny < height && horizontal[ny * width + x];
                }
            }
        }
        return result;
    }

    private static bool[] Dilate(ReadOnlySpan<bool> mask, int width, int height, int radius, CancellationToken cancellationToken)
    {
        if (mask.IsEmpty)
        {
            return [];
        }
        var source = mask.ToArray();
        if (radius == 0)
        {
            return source;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = Grow(source, width, height, radius);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void ValidateOptions(StellarMeasurementOptions options)
    {
        static bool Positive(double value) => double.IsFinite(value) && value > 0;
        if (options.BackgroundTileSizePixels is < 16 or > 1024 ||
            options.MinimumBackgroundTileSamples < 16 ||
            options.MinimumBackgroundTileSamples > options.BackgroundTileSizePixels * options.BackgroundTileSizePixels ||
            !Positive(options.BackgroundClipSigma) || !Positive(options.SegmentationSigma) || !Positive(options.PeakSigma) ||
            options.PeakSigma < options.SegmentationSigma || !Positive(options.MinimumPeakAboveBackground) ||
            !Positive(options.WindowSigmaPixels) || options.WindowSigmaPixels > 8 ||
            !Positive(options.MinimumMinorSigma) || !double.IsFinite(options.MaximumMinorSigma) ||
            options.MaximumMinorSigma < options.MinimumMinorSigma ||
            !Positive(options.MaximumSharpness) || options.MaximumSharpness > 1 ||
            !double.IsFinite(options.MinimumTrailLengthPixels) || options.MinimumTrailLengthPixels < 0 ||
            !double.IsFinite(options.MaximumTrailLengthPixels) || options.MaximumTrailLengthPixels < options.MinimumTrailLengthPixels ||
            options.MaximumTrailLengthPixels > 256 ||
            options.MaximumComponentSamples is < 1 or > 65536 ||
            options.MaximumSaturatedSamples < 0 || options.MaximumSaturatedSamples > options.MaximumComponentSamples ||
            options.SaturationDilationPixels is < 0 or > 2 ||
            !Positive(options.BlendMinimumFraction) || options.BlendMinimumFraction > 1 ||
            !Positive(options.BlendSaddleFraction) || options.BlendSaddleFraction > 1 ||
            !Positive(options.CrowdingPeakRatio) ||
            !double.IsFinite(options.MinimumSignalToNoise) || options.MinimumSignalToNoise < 0 ||
            options.ElectronsPerSampleUnit is { } gain && !Positive(gain) ||
            options.MaximumPixelCount is < 256 or > StellarDetector.MaximumSupportedPixels ||
            options.MaximumCandidateCount is < 1 or > StellarDetector.MaximumSupportedCandidates ||
            !Enum.IsDefined(options.NoiseEstimator))
        {
            throw new ArgumentException("Stellar measurement thresholds or resource limits are invalid.", nameof(options));
        }
    }

    /// <summary>Sigma-clipped tile estimates, filled one tile from available neighbors, bilinear between centers.</summary>
    private sealed class BackgroundMesh
    {
        private readonly int tileSize;
        private readonly int tilesX;
        private readonly int tilesY;
        private readonly int width;
        private readonly int height;
        private readonly double[] background;
        private readonly double[] noise;

        private BackgroundMesh(int tileSize, int tilesX, int tilesY, int width, int height, double[] background, double[] noise)
        {
            this.tileSize = tileSize;
            this.tilesX = tilesX;
            this.tilesY = tilesY;
            this.width = width;
            this.height = height;
            this.background = background;
            this.noise = noise;
        }

        public int TileCount => tilesX * tilesY;
        public int AvailableCount { get; private init; }
        public int FilledCount { get; private init; }
        public double? MedianBackground { get; private init; }
        public double? MedianNoise { get; private init; }

        public static BackgroundMesh Create(ReadOnlySpan<double> pixels, ReadOnlySpan<bool> valid, bool[] saturated,
            int width, int height, StellarMeasurementOptions options, CancellationToken cancellationToken)
        {
            var size = options.BackgroundTileSizePixels;
            var tilesX = (width + size - 1) / size;
            var tilesY = (height + size - 1) / size;
            var background = new double[tilesX * tilesY];
            var noise = new double[background.Length];
            var buffer = new double[size * size];
            var differences = new double[size * size];
            var deviations = new double[size * size];
            var available = 0;
            for (var ty = 0; ty < tilesY; ty++)
            {
                for (var tx = 0; tx < tilesX; tx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = 0;
                    var differenceCount = 0;
                    var right = Math.Min(width, (tx + 1) * size);
                    for (var y = ty * size; y < Math.Min(height, (ty + 1) * size); y++)
                    {
                        for (var x = tx * size; x < right; x++)
                        {
                            var index = y * width + x;
                            if (valid[index] && (saturated.Length == 0 || !saturated[index]))
                            {
                                buffer[count++] = pixels[index];
                                if (x + 1 < right && valid[index + 1] && (saturated.Length == 0 || !saturated[index + 1]))
                                {
                                    differences[differenceCount++] = pixels[index + 1] - pixels[index];
                                }
                            }
                        }
                    }
                    var tile = ty * tilesX + tx;
                    if (count < options.MinimumBackgroundTileSamples)
                    {
                        background[tile] = noise[tile] = double.NaN;
                        continue;
                    }
                    (background[tile], noise[tile]) = Clipped(buffer.AsSpan(0, count), deviations, options.BackgroundClipSigma);
                    // Adjacent differences cancel smooth gradients and vignetting that would inflate a tile's spread.
                    if (options.NoiseEstimator == StellarNoiseEstimator.AdjacentDifferences &&
                        differenceCount >= options.MinimumBackgroundTileSamples / 2)
                    {
                        noise[tile] = Clipped(differences.AsSpan(0, differenceCount), deviations, options.BackgroundClipSigma).Noise /
                            Math.Sqrt(2);
                    }
                    available++;
                }
            }
            var medianBackground = Median(background.Where(double.IsFinite).ToArray());
            var medianNoise = Median(noise.Where(double.IsFinite).ToArray());
            var filledBackground = (double[])background.Clone();
            var filledNoise = (double[])noise.Clone();
            var filled = 0;
            for (var ty = 0; ty < tilesY; ty++)
            {
                for (var tx = 0; tx < tilesX; tx++)
                {
                    var tile = ty * tilesX + tx;
                    if (double.IsFinite(background[tile]))
                    {
                        continue;
                    }
                    double b = 0, n = 0;
                    var neighbors = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var nx = tx + dx;
                            var ny = ty + dy;
                            if (nx < 0 || ny < 0 || nx >= tilesX || ny >= tilesY || !double.IsFinite(background[ny * tilesX + nx]))
                            {
                                continue;
                            }
                            b += background[ny * tilesX + nx];
                            n += noise[ny * tilesX + nx];
                            neighbors++;
                        }
                    }
                    if (neighbors > 0)
                    {
                        filledBackground[tile] = b / neighbors;
                        filledNoise[tile] = n / neighbors;
                        filled++;
                    }
                }
            }
            return new(size, tilesX, tilesY, width, height, filledBackground, filledNoise)
            {
                AvailableCount = available,
                FilledCount = filled,
                MedianBackground = medianBackground,
                MedianNoise = medianNoise
            };
        }

        public bool TrySample(int x, int y, out double backgroundValue, out double noiseValue)
        {
            var (tx0, tx1, fx) = Axis(x + 0.5, tilesX, width);
            var (ty0, ty1, fy) = Axis(y + 0.5, tilesY, height);
            double weightSum = 0, b = 0, n = 0;
            Accumulate(tx0, ty0, (1 - fx) * (1 - fy));
            Accumulate(tx1, ty0, fx * (1 - fy));
            Accumulate(tx0, ty1, (1 - fx) * fy);
            Accumulate(tx1, ty1, fx * fy);
            if (!(weightSum > 0))
            {
                backgroundValue = noiseValue = double.NaN;
                return false;
            }
            backgroundValue = b / weightSum;
            noiseValue = n / weightSum;
            return true;

            void Accumulate(int tx, int ty, double weight)
            {
                var tile = ty * tilesX + tx;
                if (weight > 0 && double.IsFinite(background[tile]))
                {
                    weightSum += weight;
                    b += weight * background[tile];
                    n += weight * noise[tile];
                }
            }
        }

        private (int Low, int High, double Fraction) Axis(double position, int tiles, int extent)
        {
            double Center(int tile) => (tile * tileSize + Math.Min(extent, (tile + 1) * tileSize)) / 2.0;
            if (tiles == 1 || position <= Center(0))
            {
                return (0, 0, 0);
            }
            if (position >= Center(tiles - 1))
            {
                return (tiles - 1, tiles - 1, 0);
            }
            var low = Math.Min(tiles - 2, (int)Math.Floor((position - tileSize / 2.0) / tileSize));
            while (low > 0 && position < Center(low))
            {
                low--;
            }
            while (low < tiles - 2 && position >= Center(low + 1))
            {
                low++;
            }
            return (low, low + 1, (position - Center(low)) / (Center(low + 1) - Center(low)));
        }

        private static (double Background, double Noise) Clipped(Span<double> samples, double[] deviations, double clip)
        {
            samples.Sort();
            var low = 0;
            var high = samples.Length - 1;
            double median = 0, sigma = 0;
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var count = high - low + 1;
                median = (samples[low + (count - 1) / 2] + samples[low + count / 2]) / 2;
                for (var index = 0; index < count; index++)
                {
                    deviations[index] = Math.Abs(samples[low + index] - median);
                }
                var spread = deviations.AsSpan(0, count);
                spread.Sort();
                sigma = 1.4826 * (spread[(count - 1) / 2] + spread[count / 2]) / 2;
                var newLow = low;
                var newHigh = high;
                while (newLow < newHigh && samples[newLow] < median - clip * sigma)
                {
                    newLow++;
                }
                while (newHigh > newLow && samples[newHigh] > median + clip * sigma)
                {
                    newHigh--;
                }
                if (newLow == low && newHigh == high)
                {
                    break;
                }
                low = newLow;
                high = newHigh;
            }
            return (median, sigma);
        }

        private static double? Median(double[] values)
        {
            if (values.Length == 0)
            {
                return null;
            }
            Array.Sort(values);
            return (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
        }
    }
}
