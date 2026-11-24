using System.Numerics;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

public enum ExtendedStackRejection { StellarStructure, ElongatedStructure, BackgroundStructure, StellarProfileUnavailable }

public sealed record ExtendedStackDetection(int Id, PixelPoint Centroid, double RadiusRmsPixels,
    double NetCodeUnits, double BackgroundCodeUnits, double ApertureNoiseCodeUnits, double Significance,
    byte SupportSlots, ExtendedStackRejection? Rejection)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LocalStellarProfile? StellarProfile { get; init; }
}

public sealed record LocalStellarProfile(int SampleCount, double? MaximumRadiusRmsPixels);

public sealed record ExtendedStackExtraction(int SearchPositions, int SupportedPositions, int Peaks,
    int NoiseUnsupportedPeaks, int RejectedPeaks, bool LimitExceeded, IReadOnlyList<ExtendedStackDetection> Detections);

public delegate ExtendedStackDetection? StellarStackProfileQuery(ReadOnlySpan<float> luminance, PixelPoint measuredStar);

/// <summary>Blind, bounded assessment of compact resolved emission on a linear registered raster.</summary>
public static class ExtendedStackSourceExtractor
{
    public const string AlgorithmVersion = "compact-diffuse-aperture-v2";
    public const int ApertureRadius = 5;
    public const int BackgroundRadius = 9;
    public const int MinimumContributors = 3;
    public const int SearchStride = 2;
    public const int MaximumDetections = 128;
    public const int MaximumEvaluatedPeaks = 4096;
    public const double MinimumSignificance = 5.5;
    private const int MaximumRetainedRejections = 64;
    private const int CoreArea = 121;
    private const int BackgroundArea = 240;
    private const int NoiseOffset = 20;
    private const int MinimumNoiseSamples = 6;
    private const int SearchMargin = NoiseOffset + ApertureRadius;
    private const double BackgroundWeight = CoreArea / (double)BackgroundArea;

    // Support is common to all reconstructed channels in the v1 registered mean.
    // A negative luminance marks clipping in any contributing output channel.
    public static ExtendedStackExtraction Extract(ReadOnlySpan<float> luminance, ReadOnlySpan<byte> support,
        int width, int height, CancellationToken cancellationToken = default) =>
        Extract(luminance, support, width, height, out _, cancellationToken);

    /// <summary>
    /// Extracts without target coordinates. The returned query can then check whether a location was
    /// searchable with both a supported aperture and same-slot empirical noise samples.
    /// </summary>
    public static ExtendedStackExtraction Extract(ReadOnlySpan<float> luminance, ReadOnlySpan<byte> support,
        int width, int height, out Func<PixelPoint, byte?> searchSupport, CancellationToken cancellationToken = default)
        => Extract(luminance, support, width, height, out searchSupport, out _, cancellationToken);

    /// <summary>Also returns a targeted measurement for independently measured stellar centroids.</summary>
    public static ExtendedStackExtraction Extract(ReadOnlySpan<float> luminance, ReadOnlySpan<byte> support,
        int width, int height, out Func<PixelPoint, byte?> searchSupport, out StellarStackProfileQuery stellarProfile,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0 || (long)width * height > LinearRegisteredMean.MaximumPixels ||
            luminance.Length != width * height || support.Length != luminance.Length)
        {
            throw new ArgumentException("The planes must share one bounded reference raster.");
        }

        var grid = new Apertures(luminance, support, width, height, cancellationToken);
        searchSupport = grid.SearchSupportAt;
        stellarProfile = grid.MeasureStellarProfile;
        var positions = 0;
        var supported = 0;
        var peaks = 0;
        var noiseUnsupported = 0;
        var rejected = 0;
        var accepted = 0;
        var detections = new List<ExtendedStackDetection>();
        for (var y = SearchMargin; y < height - SearchMargin; y += SearchStride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = SearchMargin; x < width - SearchMargin; x += SearchStride)
            {
                positions++;
                if (!grid.TryMeasure(x, y, out var measurement))
                {
                    continue;
                }
                supported++;
                if (measurement.Net < MinimumSignificance * measurement.WhiteNoise || !IsPeak(x, y, measurement))
                {
                    continue;
                }
                if (++peaks > MaximumEvaluatedPeaks)
                {
                    return new(positions, supported, peaks, noiseUnsupported, rejected, true, detections);
                }

                var empirical = grid.ApertureNoise(x, y, measurement.Support);
                if (empirical is not { } noise)
                {
                    noiseUnsupported++;
                    continue;
                }

                noise = Math.Max(noise, measurement.WhiteNoise);
                var significance = measurement.Net / noise;
                if (significance < MinimumSignificance)
                {
                    continue;
                }
                var detection = MeasureShape(luminance, width, x, y, measurement, noise, significance);
                if (detection.Rejection is not null)
                {
                    if (++rejected <= MaximumRetainedRejections)
                    {
                        detections.Add(detection);
                    }
                }
                else if (++accepted > MaximumDetections)
                {
                    return new(positions, supported, peaks, noiseUnsupported, rejected, true, detections);
                }
                else
                {
                    detections.Add(detection);
                }
            }
        }

        return new(positions, supported, peaks, noiseUnsupported, rejected, false, detections);

        bool IsPeak(int x, int y, ApertureMeasurement measurement)
        {
            for (var dy = -SearchStride; dy <= SearchStride; dy += SearchStride)
            {
                for (var dx = -SearchStride; dx <= SearchStride; dx += SearchStride)
                {
                    var column = x + dx;
                    var row = y + dy;
                    if (grid.IsSearchCenter(column, row) && grid.TryMeasure(column, row, out var other) &&
                        other.Support == measurement.Support &&
                        (other.Net > measurement.Net || (other.Net == measurement.Net && (dy < 0 || (dy == 0 && dx < 0)))) &&
                        grid.ApertureNoise(column, row, other.Support) is { } noise &&
                        other.Net >= MinimumSignificance * Math.Max(noise, other.WhiteNoise))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }

    private static ExtendedStackDetection MeasureShape(ReadOnlySpan<float> pixels, int width, int x, int y,
        ApertureMeasurement aperture, double noise, double significance)
    {
        var mass = 0.0;
        var mx = 0.0;
        var my = 0.0;
        var xx = 0.0;
        var yy = 0.0;
        var xy = 0.0;
        var peak = double.NegativeInfinity;
        for (var dy = -ApertureRadius; dy <= ApertureRadius; dy++)
        {
            for (var dx = -ApertureRadius; dx <= ApertureRadius; dx++)
            {
                // Suppress pixel noise in the moments with a fixed 3x3 kernel.
                var sample = 0.0;
                for (var sy = -1; sy <= 1; sy++)
                {
                    for (var sx = -1; sx <= 1; sx++)
                    {
                        sample += pixels[(y + dy + sy) * width + x + dx + sx];
                    }
                }
                var excess = sample / 9 - aperture.Background;
                peak = Math.Max(peak, excess);
                var weight = Math.Max(0, excess);
                mass += weight;
                mx += dx * weight;
                my += dy * weight;
                xx += dx * dx * weight;
                yy += dy * dy * weight;
                xy += dx * dy * weight;
            }
        }

        if (mass <= 0)
        {
            return new(y * width + x + 1, new(x + .5, y + .5), 0, aperture.Net, aperture.Background, noise,
                significance, aperture.Support, ExtendedStackRejection.BackgroundStructure);
        }

        mx /= mass;
        my /= mass;
        xx = Math.Max(0, xx / mass - mx * mx);
        yy = Math.Max(0, yy / mass - my * my);
        xy = xy / mass - mx * my;
        var spread = xx + yy;
        var asymmetry = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
        var rejection = peak * 9 > aperture.Net * .27 || spread < 1.8 * 1.8
            ? ExtendedStackRejection.StellarStructure
            : asymmetry > .54 * spread
                ? ExtendedStackRejection.ElongatedStructure
                : aperture.BackgroundRange > aperture.Net / CoreArea
                    ? ExtendedStackRejection.BackgroundStructure
                    : (ExtendedStackRejection?)null;
        return new(y * width + x + 1, new(x + .5 + mx, y + .5 + my), Math.Sqrt(spread),
            aperture.Net, aperture.Background, noise, significance, aperture.Support, rejection);
    }

    private readonly record struct ApertureMeasurement(double Net, double Background, double WhiteNoise,
        double BackgroundRange, byte Support);

    private sealed class Apertures
    {
        // The summed-area tables preserve the exact slot set across the source and sky ring.
        // A mixed set can create a step even when every pixel has the same contributor count.
        private readonly double[] _sum;
        private readonly double[] _squares;
        private readonly int[] _support;
        private readonly uint[] _supportSquares;
        private readonly int _stride;
        private readonly int _height;

        public Apertures(ReadOnlySpan<float> luminance, ReadOnlySpan<byte> support, int width, int height,
            CancellationToken cancellationToken)
        {
            _stride = width + 1;
            _height = height;
            var length = checked(_stride * (height + 1));
            _sum = new double[length];
            _squares = new double[length];
            _support = new int[length];
            _supportSquares = new uint[length];
            for (var y = 1; y <= height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sum = 0, squares = 0;
                var mask = 0;
                var maskSquares = 0u;
                for (var x = 1; x <= width; x++)
                {
                    var source = (y - 1) * width + x - 1;
                    double value = luminance[source];
                    var slots = value >= 0 && support[source] <= 31 ? support[source] : 0;
                    value = slots == 0 ? 0 : value;
                    sum += value;
                    squares += value * value;
                    mask += slots;
                    maskSquares += (uint)(slots * slots);
                    var index = y * _stride + x;
                    _sum[index] = _sum[index - _stride] + sum;
                    _squares[index] = _squares[index - _stride] + squares;
                    _support[index] = _support[index - _stride] + mask;
                    _supportSquares[index] = _supportSquares[index - _stride] + maskSquares;
                }
            }
        }

        public bool TryMeasure(int x, int y, out ApertureMeasurement measurement)
        {
            measurement = default;
            if (x < BackgroundRadius || y < BackgroundRadius ||
                x + BackgroundRadius >= _stride - 1 || y + BackgroundRadius >= _height ||
                UniformSupport(x, y, BackgroundRadius) is not { } slots)
            {
                return false;
            }

            var core = Square(_sum, x, y, ApertureRadius);
            var ring = Square(_sum, x, y, BackgroundRadius) - core;
            var background = ring / BackgroundArea;
            var variance = (Square(_squares, x, y, BackgroundRadius) - Square(_squares, x, y, ApertureRadius)) /
                BackgroundArea - background * background;
            var top = Box(_sum, x - 9, y - 9, x + 10, y - 5) / 76;
            var bottom = Box(_sum, x - 9, y + 6, x + 10, y + 10) / 76;
            var left = Box(_sum, x - 9, y - 5, x - 5, y + 6) / 44;
            var right = Box(_sum, x + 6, y - 5, x + 10, y + 6) / 44;
            var range = Math.Max(Math.Abs(top - bottom), Math.Abs(left - right));
            measurement = new(core - background * CoreArea, background,
                Math.Sqrt(Math.Max(1, variance) * CoreArea * (1 + BackgroundWeight)), range, slots);
            return true;
        }

        public double? ApertureNoise(int x, int y, byte slots)
        {
            Span<double> means = stackalloc double[8];
            var count = NoiseSamples(x, y, slots, means);
            if (count < MinimumNoiseSamples)
            {
                return null;
            }
            means = means[..count];
            means.Sort();
            var median = (means[(count - 1) / 2] + means[count / 2]) / 2;
            for (var i = 0; i < count; i++)
            {
                means[i] = Math.Abs(means[i] - median);
            }
            means.Sort();
            return 1.4826 * (means[(count - 1) / 2] + means[count / 2]) / 2 * Math.Sqrt(1 + BackgroundWeight);
        }

        public bool IsSearchCenter(int x, int y) =>
            x >= SearchMargin && x < _stride - 1 - SearchMargin &&
            y >= SearchMargin && y < _height - SearchMargin;

        public bool SupportsNoise(int x, int y, byte slots) =>
            NoiseSamples(x, y, slots, stackalloc double[8]) >= MinimumNoiseSamples;

        public ExtendedStackDetection? MeasureStellarProfile(ReadOnlySpan<float> luminance, PixelPoint measuredStar)
        {
            var x = (int)measuredStar.X;
            var y = (int)measuredStar.Y;
            if (!IsSearchCenter(x, y) || !TryMeasure(x, y, out var aperture) ||
                ApertureNoise(x, y, aperture.Support) is not { } noise)
            {
                return null;
            }
            noise = Math.Max(noise, aperture.WhiteNoise);
            var significance = aperture.Net / noise;
            return significance >= 2 * MinimumSignificance
                ? MeasureShape(luminance, _stride - 1, x, y, aperture, noise, significance) : null;
        }

        public byte? SearchSupportAt(PixelPoint point)
        {
            if (!(point.X >= SearchMargin && point.X < _stride - 1 - SearchMargin &&
                  point.Y >= SearchMargin && point.Y < _height - SearchMargin))
            {
                return null;
            }
            var x = (int)point.X;
            var y = (int)point.Y;
            // Nearest sampled center, with ties toward the lower pixel.
            var gridX = SearchMargin + (int)Math.Ceiling((point.X - SearchMargin - .5) / SearchStride - .5) * SearchStride;
            var gridY = SearchMargin + (int)Math.Ceiling((point.Y - SearchMargin - .5) / SearchStride - .5) * SearchStride;
            if (gridX >= _stride - 1 - SearchMargin || gridY >= _height - SearchMargin ||
                UniformSupport(x, y, BackgroundRadius) is not { } slots ||
                UniformSupport(gridX, gridY, BackgroundRadius) != slots)
            {
                return null;
            }
            return SupportsNoise(gridX, gridY, slots) ? slots : null;
        }

        private int NoiseSamples(int x, int y, byte slots, Span<double> means)
        {
            var count = 0;
            for (var dy = -NoiseOffset; dy <= NoiseOffset; dy += NoiseOffset)
            {
                for (var dx = -NoiseOffset; dx <= NoiseOffset; dx += NoiseOffset)
                {
                    if ((dx != 0 || dy != 0) && UniformSupport(x + dx, y + dy, ApertureRadius) == slots)
                    {
                        means[count++] = Square(_sum, x + dx, y + dy, ApertureRadius);
                    }
                }
            }
            return count;
        }

        private byte? UniformSupport(int x, int y, int radius)
        {
            var side = 2 * radius + 1;
            var area = side * side;
            var sum = Square(_support, x, y, radius);
            var slots = sum / area;
            return BitOperations.PopCount((uint)slots) >= MinimumContributors &&
                sum * sum == area * Square(_supportSquares, x, y, radius) ? (byte)slots : null;
        }

        private T Square<T>(T[] table, int x, int y, int radius) where T : INumber<T> =>
            Box(table, x - radius, y - radius, x + radius + 1, y + radius + 1);

        private T Box<T>(T[] table, int left, int top, int right, int bottom) where T : INumber<T> =>
            (table[bottom * _stride + right] - table[bottom * _stride + left]) -
            (table[top * _stride + right] - table[top * _stride + left]);
    }
}
