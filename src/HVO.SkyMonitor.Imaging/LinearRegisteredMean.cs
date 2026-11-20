using System.Numerics;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A direct pair of mappings in delivered-image edge coordinates (sample centers at i + 0.5).</summary>
public interface IImageRegistrationMap
{
    PixelPoint? SourceToReference(PixelPoint source);
    PixelPoint? ReferenceToSource(PixelPoint reference);
}

public sealed record LinearRegistrationImage(int Width, int Height, ReadOnlyMemory<bool> Valid,
    ReadOnlyMemory<double> First, ReadOnlyMemory<double> Green = default, ReadOnlyMemory<double> Blue = default);

/// <summary>Interleaved unsigned linear codes (mono or RGB) and a five-slot support bitset per output pixel.</summary>
public sealed record RegisteredLinearSamples(ReadOnlyMemory<ushort> Codes, ReadOnlyMemory<byte> Support, int ValidPixelCount);

/// <summary>Streaming inverse-sampled, unit-weight mean in common linear code units.</summary>
/// <remarks>
/// Callers supply up to five matched images on one compatible response scale. Reconstruction precedes sampling.
/// Finish returns zero codes wherever fewer than two slots provide support. A canceled or failed accumulator is discarded.
/// </remarks>
public sealed class LinearRegisteredMean
{
    public const string AlgorithmVersion = "inverse-bilinear-positive-taps-mean-v1";
    public const int MaximumPixels = 4_000_000;
    public const int SlotCount = 5;
    private readonly int _width;
    private readonly int _height;
    private readonly int _channels;
    private readonly double[] _sum;
    private readonly byte[] _support;
    private int _added;

    public LinearRegisteredMean(int width, int height, int channels)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels || channels is not (1 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        _width = width;
        _height = height;
        _channels = channels;
        _support = new byte[width * height];
        _sum = new double[_support.Length * channels];
    }

    /// <summary>Adds one slot and returns the number of reference pixels it supported.</summary>
    public int Add(int slot, LinearRegistrationImage image, IImageRegistrationMap? map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (slot is < 0 or >= SlotCount || (_added & (1 << slot)) != 0 || (map is null) != (slot == 2))
        {
            throw new ArgumentException("Each slot is added once and only the center uses identity.", nameof(slot));
        }
        var size = (long)image.Width * image.Height;
        if (image.Width != _width || image.Height != _height || size != image.First.Length ||
            image.Valid.Length != size ||
            (_channels == 3 ? image.Green.Length != size || image.Blue.Length != size : !image.Green.IsEmpty || !image.Blue.IsEmpty))
        {
            throw new ArgumentException("Source planes must match the reference grid and channel count.", nameof(image));
        }

        _added |= 1 << slot;
        var first = image.First.Span;
        var green = image.Green.Span;
        var blue = image.Blue.Span;
        var valid = image.Valid.Span;
        var supported = 0;
        var bit = (byte)(1 << slot);
        Span<int> indices = stackalloc int[4];
        Span<double> weights = stackalloc double[4];
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var index = y * _width + x;
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (map is null)
                {
                    if (!valid[index])
                    {
                        continue;
                    }
                    indices[0] = index;
                    weights[0] = 1;
                    weights[1..].Clear();
                }
                else if (map.ReferenceToSource(new(x + .5, y + .5)) is not { } point ||
                    !TryFootprint(point, valid, indices, weights))
                {
                    continue;
                }

                var output = index * _channels;
                _sum[output] += Sample(first, indices, weights);
                if (_channels == 3)
                {
                    _sum[output + 1] += Sample(green, indices, weights);
                    _sum[output + 2] += Sample(blue, indices, weights);
                }
                _support[index] |= bit;
                supported++;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return supported;
    }

    public RegisteredLinearSamples Finish(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var codes = new ushort[_sum.Length];
        var count = 0;
        for (var index = 0; index < _support.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var contributors = BitOperations.PopCount(_support[index]);
            if (contributors < 2)
            {
                continue;
            }
            count++;
            for (var channel = 0; channel < _channels; channel++)
            {
                var output = index * _channels + channel;
                codes[output] = (ushort)Math.Clamp(Math.Round(_sum[output] / contributors, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(codes, _support.ToArray(), count);
    }

    private bool TryFootprint(PixelPoint point, ReadOnlySpan<bool> valid, Span<int> indices, Span<double> weights)
    {
        var x = point.X - .5;
        var y = point.Y - .5;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > _width - 1 || y > _height - 1)
        {
            return false;
        }
        var left = (int)x;
        var top = (int)y;
        var dx = x - left;
        var dy = y - top;
        var origin = top * _width + left;
        indices[0] = origin;
        indices[1] = origin + 1;
        indices[2] = origin + _width;
        indices[3] = origin + _width + 1;
        weights[0] = (1 - dx) * (1 - dy);
        weights[1] = dx * (1 - dy);
        weights[2] = (1 - dx) * dy;
        weights[3] = dx * dy;
        for (var tap = 0; tap < weights.Length; tap++)
        {
            if (weights[tap] > 0 && !valid[indices[tap]])
            {
                return false;
            }
        }
        return true;
    }

    private static double Sample(ReadOnlySpan<double> plane, ReadOnlySpan<int> indices, ReadOnlySpan<double> weights)
    {
        var sum = 0.0;
        for (var tap = 0; tap < weights.Length; tap++)
        {
            if (weights[tap] > 0)
            {
                var code = plane[indices[tap]];
                if (!double.IsFinite(code) || code is < 0 or > ushort.MaxValue)
                {
                    throw new ArgumentException("Linear samples must be finite unsigned code values.", nameof(plane));
                }
                sum += weights[tap] * code;
            }
        }
        return sum;
    }
}
