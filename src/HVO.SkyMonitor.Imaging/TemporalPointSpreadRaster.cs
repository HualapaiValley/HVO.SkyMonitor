using System.Collections.ObjectModel;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One point-source location with original exposure weight and instantaneous optical transmission.</summary>
public readonly record struct WeightedPointSpreadSample(PixelPoint Pixel, double ExposureFraction, double Transmission = 1);

/// <summary>A sparse native photosite contribution, before vignetting/channel response and sensor noise.</summary>
public readonly record struct PointSpreadRasterPixel(int Index, double BeforeTransmission, double AfterTransmission);

/// <summary>Frozen sparse optical energy with explicit work and clipping evidence.</summary>
public sealed class PointSpreadRaster
{
    internal PointSpreadRaster(List<PointSpreadRasterPixel> pixels, long cellVisits, double intervalEnergy)
    {
        Pixels = new ReadOnlyCollection<PointSpreadRasterPixel>(pixels.ToArray());
        KernelCellVisits = cellVisits;
        OriginalIntervalEnergy = intervalEnergy;
        RetainedBeforeTransmission = pixels.Sum(static pixel => pixel.BeforeTransmission);
        RetainedAfterTransmission = pixels.Sum(static pixel => pixel.AfterTransmission);
    }

    /// <summary>Gets deterministic ascending native pixel indices.</summary>
    public IReadOnlyList<PointSpreadRasterPixel> Pixels { get; }
    /// <summary>Gets actual normalization/deposition bounding-cell visits.</summary>
    public long KernelCellVisits { get; }
    /// <summary>Gets original temporal energy; horizon/interval omissions are never filled back in.</summary>
    public double OriginalIntervalEnergy { get; }
    /// <summary>Gets retained sensor/aperture energy before transmission.</summary>
    public double RetainedBeforeTransmission { get; }
    /// <summary>Gets retained energy after instantaneous optical transmission.</summary>
    public double RetainedAfterTransmission { get; }
}

/// <summary>Shared bounded Gaussian point/short-trail deposition with full-kernel normalization before clipping.</summary>
public static class TemporalPointSpreadRaster
{
    /// <summary>Identifies discrete Gaussian normalization and original temporal-weight preservation.</summary>
    public const string AlgorithmVersion = "bounded-temporal-gaussian-native-v1";

    /// <summary>Integrates sparse noiseless optical fractions without allocating full-frame temporal buffers.</summary>
    public static PointSpreadRaster Rasterize(IReadOnlyList<WeightedPointSpreadSample> samples,
        ProjectionContext projection, double sigmaPixels, double radiusPixels,
        long maximumKernelCellVisits = 100000000, int maximumSparsePixels = 65536,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        projection.Validate();
        if (samples.Count > 64 || (long)projection.WidthPixels * projection.HeightPixels > 16777216 ||
            !double.IsFinite(sigmaPixels) || sigmaPixels is <= 0 or > 64 ||
            !double.IsFinite(radiusPixels) || radiusPixels is < .75 or > 64 ||
            maximumKernelCellVisits is < 1 or > 1000000000 || maximumSparsePixels is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(samples));
        var pixels = new Dictionary<int, (double Before, double After)>();
        var visits = 0L;
        var originalEnergy = 0d;
        var radiusSquared = radiusPixels * radiusPixels;
        var variance = 2 * sigmaPixels * sigmaPixels;
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(sample.Pixel.X) || !double.IsFinite(sample.Pixel.Y) ||
                Math.Abs(sample.Pixel.X) > int.MaxValue - 128d || Math.Abs(sample.Pixel.Y) > int.MaxValue - 128d ||
                !double.IsFinite(sample.ExposureFraction) || sample.ExposureFraction is < 0 or > 1 ||
                !double.IsFinite(sample.Transmission) || sample.Transmission is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(samples));
            originalEnergy += sample.ExposureFraction;
            if (originalEnergy > 1 + 1e-12) throw new ArgumentException("Original exposure weights cannot exceed one.", nameof(samples));
            if (sample.ExposureFraction == 0) continue;
            var left = (int)Math.Ceiling(sample.Pixel.X - radiusPixels - .5);
            var right = (int)Math.Floor(sample.Pixel.X + radiusPixels - .5);
            var top = (int)Math.Ceiling(sample.Pixel.Y - radiusPixels - .5);
            var bottom = (int)Math.Floor(sample.Pixel.Y + radiusPixels - .5);
            var normalization = 0d;
            for (var y = top; y <= bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = left; x <= right; x++)
                {
                    Visit();
                    var squared = SquaredDistance(x, y, sample.Pixel);
                    if (squared <= radiusSquared) normalization += Math.Exp(-squared / variance);
                }
            }
            if (normalization <= 0 || !double.IsFinite(normalization))
                throw new InvalidOperationException("point-spread-kernel-has-no-sample-support");
            for (var y = Math.Max(0, top); y <= Math.Min(projection.HeightPixels - 1, bottom); y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = Math.Max(0, left); x <= Math.Min(projection.WidthPixels - 1, right); x++)
                {
                    Visit();
                    var squared = SquaredDistance(x, y, sample.Pixel);
                    if (squared > radiusSquared || !projection.ContainsSample(x + .5, y + .5)) continue;
                    var value = sample.ExposureFraction * Math.Exp(-squared / variance) / normalization;
                    var index = y * projection.WidthPixels + x;
                    var prior = pixels.GetValueOrDefault(index);
                    if (prior == default && !pixels.ContainsKey(index) && pixels.Count >= maximumSparsePixels)
                        throw new InvalidOperationException("point-spread-sparse-budget-exceeded");
                    pixels[index] = (prior.Before + value, prior.After + value * sample.Transmission);
                }
            }
        }
        return new(pixels.OrderBy(static pixel => pixel.Key)
            .Select(static pixel => new PointSpreadRasterPixel(pixel.Key, pixel.Value.Before, pixel.Value.After)).ToList(), visits, originalEnergy);

        void Visit()
        {
            if (++visits > maximumKernelCellVisits) throw new InvalidOperationException("point-spread-raster-budget-exceeded");
        }
    }

    private static double SquaredDistance(int x, int y, PixelPoint point)
        => (x + .5 - point.X) * (x + .5 - point.X) + (y + .5 - point.Y) * (y + .5 - point.Y);
}
