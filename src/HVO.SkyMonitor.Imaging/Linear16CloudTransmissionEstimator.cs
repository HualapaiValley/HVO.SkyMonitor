using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A borrowed calibrated linear frame used for comparative cloud assessment.</summary>
public sealed record Linear16CloudFrame
{
    public Linear16CloudFrame(
        ImageLayout layout,
        ReadOnlyMemory<byte> pixelData,
        ushort blackLevel,
        ushort whiteLevel,
        ushort? saturationLevel = null)
    {
        Layout = layout;
        PixelData = pixelData;
        BlackLevel = blackLevel;
        WhiteLevel = whiteLevel;
        SaturationLevel = saturationLevel ?? whiteLevel;
    }

    public ImageLayout Layout { get; }

    public ReadOnlyMemory<byte> PixelData { get; }

    public ushort BlackLevel { get; }

    public ushort WhiteLevel { get; }

    public ushort SaturationLevel { get; }
}

/// <summary>An image-circle boundary expressed in normalized image millionths.</summary>
public readonly record struct NormalizedCloudCircle(
    int CenterXMillionths,
    int CenterYMillionths,
    int RadiusMillionths);

/// <summary>A half-open exclusion rectangle expressed in normalized image millionths.</summary>
public readonly record struct NormalizedCloudRectangle(
    int LeftMillionths,
    int TopMillionths,
    int RightMillionths,
    int BottomMillionths);

/// <summary>Controls deterministic fixed-grid comparative transmission assessment.</summary>
public readonly record struct CloudTransmissionEstimatorOptions
{
    public CloudTransmissionEstimatorOptions()
        : this(16, 12)
    {
    }

    public CloudTransmissionEstimatorOptions(
        int gridColumns = 16,
        int gridRows = 12,
        int transmissionThresholdMillionths = 850_000,
        ushort minimumReferenceSignal = 64,
        int minimumSamplesPerTile = 16,
        int maximumSaturatedFractionMillionths = 100_000,
        bool includeMask = true,
        NormalizedCloudCircle? imageCircle = null,
        int? horizonRadiusMillionths = null,
        IReadOnlyList<NormalizedCloudRectangle>? excludedRegions = null)
    {
        GridColumns = gridColumns;
        GridRows = gridRows;
        TransmissionThresholdMillionths = transmissionThresholdMillionths;
        MinimumReferenceSignal = minimumReferenceSignal;
        MinimumSamplesPerTile = minimumSamplesPerTile;
        MaximumSaturatedFractionMillionths = maximumSaturatedFractionMillionths;
        IncludeMask = includeMask;
        ImageCircle = imageCircle;
        HorizonRadiusMillionths = horizonRadiusMillionths;
        ExcludedRegions = excludedRegions ?? [];
    }

    public int GridColumns { get; }

    public int GridRows { get; }

    public int TransmissionThresholdMillionths { get; }

    public ushort MinimumReferenceSignal { get; }

    public int MinimumSamplesPerTile { get; }

    public int MaximumSaturatedFractionMillionths { get; }

    public bool IncludeMask { get; }

    public NormalizedCloudCircle? ImageCircle { get; }

    public int? HorizonRadiusMillionths { get; }

    public IReadOnlyList<NormalizedCloudRectangle> ExcludedRegions { get; }
}

/// <summary>Deterministic statistics for one row-major analysis tile.</summary>
public readonly record struct CloudTransmissionRegionResult(
    int Column,
    int Row,
    int X,
    int Y,
    int Width,
    int Height,
    long ConsideredSampleCount,
    long AcceptedSampleCount,
    long SaturatedSampleCount,
    int? TransmissionMillionths,
    bool IsCloudy);

/// <summary>Fixed-point comparative transmission evidence over a tile grid.</summary>
public sealed record CloudTransmissionResult(
    int GridColumns,
    int GridRows,
    int? CoverageMillionths,
    int ConfidenceMillionths,
    int ValidTileCount,
    int CloudyTileCount,
    long ValidSampleCount,
    long CloudySampleCount,
    long ConsideredSampleCount,
    long AcceptedSampleCount,
    long SaturatedSampleCount,
    long ScannedBytes,
    bool IsSaturationContaminated,
    IReadOnlyList<CloudTransmissionRegionResult> Regions,
    IReadOnlyList<byte>? Mask,
    string AlgorithmVersion);

/// <summary>Compares current and clear-reference linear samples without demosaic or full-frame copies.</summary>
public static class Linear16CloudTransmissionEstimator
{
    public const string AlgorithmVersion = "linear16-fixed-tile-transmission-v1";
    public const int MaximumTileCount = 4096;
    public const int MaximumExcludedRegionCount = 32;
    private const int OneMillion = 1_000_000;

    public static CloudTransmissionResult Assess(
        Linear16CloudFrame current,
        Linear16CloudFrame clearReference,
        CancellationToken cancellationToken = default)
        => Assess(current, clearReference, new CloudTransmissionEstimatorOptions(), cancellationToken);

    public static CloudTransmissionResult Assess(
        Linear16CloudFrame current,
        Linear16CloudFrame clearReference,
        CloudTransmissionEstimatorOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInputs(current, clearReference, options);

        var layout = current.Layout;
        var tileCount = checked(options.GridColumns * options.GridRows);
        var laneCount = layout.PixelFormat == CameraPixelFormat.BayerRggb16 ? 4 : 1;
        var laneAccumulatorCount = checked(tileCount * laneCount);
        var currentSums = new ulong[laneAccumulatorCount];
        var referenceSums = new ulong[laneAccumulatorCount];
        var referenceSquareSums = new ulong[laneAccumulatorCount];
        var crossProductSums = new ulong[laneAccumulatorCount];
        var acceptedByLane = new int[laneAccumulatorCount];
        var consideredByTile = new long[tileCount];
        var saturatedByTile = new long[tileCount];
        var currentPixels = current.PixelData.Span;
        var referencePixels = clearReference.PixelData.Span;

        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentRow = y * current.Layout.StrideBytes;
            var referenceRow = y * clearReference.Layout.StrideBytes;
            var tileRow = (int)((long)y * options.GridRows / layout.Height);
            for (var x = 0; x < layout.Width; x++)
            {
                if (!IsIncluded(x, y, layout.Width, layout.Height, options))
                {
                    continue;
                }

                var tileColumn = (int)((long)x * options.GridColumns / layout.Width);
                var tileIndex = tileRow * options.GridColumns + tileColumn;
                consideredByTile[tileIndex]++;
                var currentOffset = currentRow + x * 2;
                var referenceOffset = referenceRow + x * 2;
                var currentValue = (ushort)(currentPixels[currentOffset] | currentPixels[currentOffset + 1] << 8);
                var referenceValue = (ushort)(referencePixels[referenceOffset] | referencePixels[referenceOffset + 1] << 8);
                if (currentValue >= current.SaturationLevel || referenceValue >= clearReference.SaturationLevel)
                {
                    saturatedByTile[tileIndex]++;
                    continue;
                }

                var referenceSignal = referenceValue > clearReference.BlackLevel
                    ? referenceValue - clearReference.BlackLevel
                    : 0;
                if (referenceSignal < options.MinimumReferenceSignal)
                {
                    continue;
                }
                var currentSignal = currentValue > current.BlackLevel
                    ? currentValue - current.BlackLevel
                    : 0;
                var lane = laneCount == 1 ? 0 : ((y & 1) << 1) | (x & 1);
                var accumulatorIndex = tileIndex * laneCount + lane;
                currentSums[accumulatorIndex] += (uint)currentSignal;
                referenceSums[accumulatorIndex] += (uint)referenceSignal;
                referenceSquareSums[accumulatorIndex] += (ulong)(uint)referenceSignal * (uint)referenceSignal;
                crossProductSums[accumulatorIndex] += (ulong)(uint)referenceSignal * (uint)currentSignal;
                acceptedByLane[accumulatorIndex]++;
            }
        }

        var regions = new CloudTransmissionRegionResult[tileCount];
        var mask = options.IncludeMask ? new byte[(tileCount + 7) / 8] : null;
        var validTiles = 0;
        var cloudyTiles = 0;
        long validSamples = 0;
        long cloudySamples = 0;
        long consideredSamples = 0;
        long acceptedSamples = 0;
        long saturatedSamples = 0;
        Int128 separationWeightedSum = 0;
        for (var row = 0; row < options.GridRows; row++)
        {
            var y0 = (int)((long)row * layout.Height / options.GridRows);
            var y1 = (int)((long)(row + 1) * layout.Height / options.GridRows);
            for (var column = 0; column < options.GridColumns; column++)
            {
                var tileIndex = row * options.GridColumns + column;
                var x0 = (int)((long)column * layout.Width / options.GridColumns);
                var x1 = (int)((long)(column + 1) * layout.Width / options.GridColumns);
                long accepted = 0;
                Int128 covariance = 0;
                Int128 variance = 0;
                for (var lane = 0; lane < laneCount; lane++)
                {
                    var accumulatorIndex = tileIndex * laneCount + lane;
                    var laneSamples = acceptedByLane[accumulatorIndex];
                    accepted += laneSamples;
                    if (laneSamples < 2)
                    {
                        continue;
                    }
                    var laneReferenceSum = referenceSums[accumulatorIndex];
                    var laneVariance = (Int128)laneSamples * referenceSquareSums[accumulatorIndex] -
                        (Int128)laneReferenceSum * laneReferenceSum;
                    if (laneVariance <= 0)
                    {
                        continue;
                    }
                    variance += laneVariance;
                    covariance += (Int128)laneSamples * crossProductSums[accumulatorIndex] -
                        (Int128)laneReferenceSum * currentSums[accumulatorIndex];
                }

                int? transmission = null;
                var cloudy = false;
                if (accepted >= options.MinimumSamplesPerTile && variance > 0)
                {
                    transmission = covariance <= 0
                        ? 0
                        : (int)Math.Min(OneMillion, DivideRounded(covariance * OneMillion, variance));
                    cloudy = transmission < options.TransmissionThresholdMillionths;
                    validTiles++;
                    cloudyTiles += cloudy ? 1 : 0;
                    validSamples += accepted;
                    cloudySamples += cloudy ? accepted : 0;
                    var separationDenominator = transmission < options.TransmissionThresholdMillionths
                        ? options.TransmissionThresholdMillionths
                        : OneMillion - options.TransmissionThresholdMillionths;
                    var tileSeparation = Math.Min(
                        OneMillion,
                        DivideRounded(
                            (Int128)Math.Abs(transmission.Value - options.TransmissionThresholdMillionths) * OneMillion,
                            separationDenominator));
                    separationWeightedSum += (Int128)tileSeparation * accepted;
                    if (cloudy && mask is not null)
                    {
                        mask[tileIndex >> 3] |= (byte)(1 << (tileIndex & 7));
                    }
                }

                consideredSamples += consideredByTile[tileIndex];
                acceptedSamples += accepted;
                saturatedSamples += saturatedByTile[tileIndex];
                regions[tileIndex] = new CloudTransmissionRegionResult(
                    column,
                    row,
                    x0,
                    y0,
                    x1 - x0,
                    y1 - y0,
                    consideredByTile[tileIndex],
                    accepted,
                    saturatedByTile[tileIndex],
                    transmission,
                    cloudy);
            }
        }

        var coverage = validSamples == 0
            ? (int?)null
            : (int)DivideRounded((Int128)cloudySamples * OneMillion, validSamples);
        var support = consideredSamples == 0
            ? 0
            : (int)Math.Min(OneMillion, DivideRounded((Int128)validSamples * OneMillion, consideredSamples));
        var separation = validSamples == 0
            ? 0
            : (int)DivideRounded(separationWeightedSum, validSamples);
        var confidence = (int)DivideRounded((Int128)support * separation, OneMillion);
        var saturatedFraction = consideredSamples == 0
            ? 0
            : (int)DivideRounded((Int128)saturatedSamples * OneMillion, consideredSamples);

        return new CloudTransmissionResult(
            options.GridColumns,
            options.GridRows,
            coverage,
            confidence,
            validTiles,
            cloudyTiles,
            validSamples,
            cloudySamples,
            consideredSamples,
            acceptedSamples,
            saturatedSamples,
            checked(consideredSamples * 4),
            saturatedFraction > options.MaximumSaturatedFractionMillionths,
            regions,
            mask,
            AlgorithmVersion);
    }

    private static void ValidateInputs(
        Linear16CloudFrame current,
        Linear16CloudFrame clearReference,
        CloudTransmissionEstimatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(clearReference);
        current.Layout.Validate();
        clearReference.Layout.Validate();
        if (current.Layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            current.Layout.Width != clearReference.Layout.Width ||
            current.Layout.Height != clearReference.Layout.Height ||
            current.Layout.PixelFormat != clearReference.Layout.PixelFormat)
        {
            throw new ArgumentException("Cloud assessment requires matching Mono16 or BayerRggb16 inputs.");
        }
        if (current.PixelData.Length < current.Layout.RequiredByteLength ||
            clearReference.PixelData.Length < clearReference.Layout.RequiredByteLength)
        {
            throw new ArgumentException("A cloud assessment pixel buffer is shorter than its declared layout.");
        }
        if (current.WhiteLevel <= current.BlackLevel || clearReference.WhiteLevel <= clearReference.BlackLevel ||
            current.SaturationLevel <= current.BlackLevel || current.SaturationLevel > current.WhiteLevel ||
            clearReference.SaturationLevel <= clearReference.BlackLevel || clearReference.SaturationLevel > clearReference.WhiteLevel ||
            current.BlackLevel != clearReference.BlackLevel || current.WhiteLevel != clearReference.WhiteLevel ||
            current.SaturationLevel != clearReference.SaturationLevel)
        {
            throw new ArgumentException("Cloud assessment requires matching valid linear levels.");
        }
        if (options.GridColumns < 1 || options.GridRows < 1 ||
            options.GridColumns > current.Layout.Width || options.GridRows > current.Layout.Height ||
            (long)options.GridColumns * options.GridRows > MaximumTileCount ||
            options.TransmissionThresholdMillionths is <= 0 or >= OneMillion ||
            options.MinimumSamplesPerTile < 1 ||
            options.MaximumSaturatedFractionMillionths is < 0 or > OneMillion)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.ImageCircle is { } circle &&
            (circle.CenterXMillionths is < 0 or > OneMillion ||
             circle.CenterYMillionths is < 0 or > OneMillion ||
             circle.RadiusMillionths is <= 0 or > OneMillion) ||
            options.HorizonRadiusMillionths is <= 0 or > OneMillion)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.ExcludedRegions is null || options.ExcludedRegions.Count > MaximumExcludedRegionCount ||
            options.ExcludedRegions.Any(static region =>
                region.LeftMillionths < 0 || region.TopMillionths < 0 ||
                region.RightMillionths > OneMillion || region.BottomMillionths > OneMillion ||
                region.LeftMillionths >= region.RightMillionths || region.TopMillionths >= region.BottomMillionths))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static bool IsIncluded(
        int x,
        int y,
        int width,
        int height,
        CloudTransmissionEstimatorOptions options)
    {
        var normalizedX = (int)(((long)(2 * x + 1) * OneMillion) / (2L * width));
        var normalizedY = (int)(((long)(2 * y + 1) * OneMillion) / (2L * height));
        if (options.ImageCircle is { } circle && !InsideCircle(normalizedX, normalizedY, circle))
        {
            return false;
        }
        if (options.HorizonRadiusMillionths is { } horizonRadius)
        {
            var centerX = options.ImageCircle?.CenterXMillionths ?? OneMillion / 2;
            var centerY = options.ImageCircle?.CenterYMillionths ?? OneMillion / 2;
            if (!InsideCircle(normalizedX, normalizedY, new NormalizedCloudCircle(centerX, centerY, horizonRadius)))
            {
                return false;
            }
        }
        foreach (var excluded in options.ExcludedRegions)
        {
            if (normalizedX >= excluded.LeftMillionths && normalizedX < excluded.RightMillionths &&
                normalizedY >= excluded.TopMillionths && normalizedY < excluded.BottomMillionths)
            {
                return false;
            }
        }
        return true;
    }

    private static bool InsideCircle(int x, int y, NormalizedCloudCircle circle)
    {
        var deltaX = (long)x - circle.CenterXMillionths;
        var deltaY = (long)y - circle.CenterYMillionths;
        return deltaX * deltaX + deltaY * deltaY <= (long)circle.RadiusMillionths * circle.RadiusMillionths;
    }

    private static long DivideRounded(Int128 numerator, Int128 denominator)
        => checked((long)((numerator + denominator / 2) / denominator));
}
