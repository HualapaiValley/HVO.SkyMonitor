using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Explicit source support and bounded detector work for a catalog-predicted swept mask.</summary>
public sealed record StellarExposureMaskOptions(
    double SupportRadiusSourcePixels = 5,
    double DetectorPaddingPixels = 0,
    long MaximumKernelCellVisits = 100000000)
{
    /// <summary>Validates finite support and an explicit work budget.</summary>
    public void Validate()
    {
        if (!double.IsFinite(SupportRadiusSourcePixels) || SupportRadiusSourcePixels is < .5 or > 64 ||
            !double.IsFinite(DetectorPaddingPixels) || DetectorPaddingPixels is < 0 or > 1 ||
            MaximumKernelCellVisits is < 1 or > 1000000000)
            throw new ArgumentOutOfRangeException(nameof(StellarExposureMaskOptions));
    }
}

/// <summary>Predicted exclusion support and actual bounded raster work; this is not a detected source.</summary>
public sealed record StellarExposureMaskResult(
    Linear16PixelMask Mask,
    int CatalogCandidates,
    int SupportedSources,
    long TemporalSamples,
    long KernelCellVisits);

/// <summary>
/// Creates detector support from independently requested catalog geometry. No rendered source
/// positions, visibility decisions, photon predictions, image truth or class labels are inputs.
/// </summary>
public static class StellarExposureMask
{
    public const string AlgorithmVersion = "catalog-geometric-exposure-swept-mask-v1";
    public const int MaximumRegionCount = 2097152;

    /// <summary>
    /// Unions temporal supports in detector coordinates. Half a maximum motion step covers
    /// the unsampled part of each original interval, including the shutter endpoints.
    /// </summary>
    public static StellarExposureMaskResult Create(
        StellarExposureGeometry geometry,
        int width,
        int height,
        double scaleX,
        double scaleY,
        double offsetX = 0,
        double offsetY = 0,
        StellarExposureMaskOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        options.Validate();
        if (width <= 0 || height <= 0 || (long)width * height > 16777216 ||
            !double.IsFinite(scaleX) || scaleX is <= 0 or > 1 ||
            !double.IsFinite(scaleY) || scaleY is <= 0 or > 1 ||
            !double.IsFinite(offsetX) || Math.Abs(offsetX) > 16777216 ||
            !double.IsFinite(offsetY) || Math.Abs(offsetY) > 16777216)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (options.SupportRadiusSourcePixels > geometry.PsfSupportRadiusPixels)
            throw new ArgumentException("Mask support exceeds the geometry's declared source support.", nameof(options));
        var scale = Math.Max(scaleX, scaleY);
        var radius = (options.SupportRadiusSourcePixels + geometry.MaximumStepPixels / 2) * scale +
            options.DetectorPaddingPixels;
        var regions = new List<Linear16CircularMaskRegion>();
        var visits = 0L;
        var samples = 0L;
        foreach (var source in geometry.Sources)
        {
            foreach (var sample in source.Samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++samples > MaximumRegionCount)
                    throw new InvalidOperationException("stellar-mask-region-budget-exceeded");
                var centerX = sample.Pixel.X * scaleX + offsetX;
                var centerY = sample.Pixel.Y * scaleY + offsetY;
                // Clamp before integer conversion, including fully off-detector transforms.
                var minimumX = (int)Math.Clamp(Math.Floor(centerX - radius), 0, width);
                var maximumX = (int)Math.Clamp(Math.Ceiling(centerX + radius), -1, width - 1);
                var minimumY = (int)Math.Clamp(Math.Floor(centerY - radius), 0, height);
                var maximumY = (int)Math.Clamp(Math.Ceiling(centerY + radius), -1, height - 1);
                var cells = maximumX < minimumX || maximumY < minimumY ? 0 :
                    checked((long)(maximumX - minimumX + 1) * (maximumY - minimumY + 1));
                if (cells > options.MaximumKernelCellVisits - visits)
                    throw new InvalidOperationException("stellar-mask-raster-budget-exceeded");
                visits += cells;
                if (cells > 0) regions.Add(new(centerX, centerY, radius));
            }
        }
        return new(Linear16MaskOperations.CreateCircularSupportMask(width, height, regions, cancellationToken),
            geometry.CandidateCount, geometry.Sources.Count, samples, visits);
    }
}
