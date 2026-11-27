using System.Numerics;
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
/// <see cref="MaximumComponentSamples"/> and <see cref="MaximumWindowSamples"/> together bound each candidate's work:
/// a sparse elongated component can have few samples but a large bounding window.
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
    StellarNoiseEstimator NoiseEstimator = StellarNoiseEstimator.AdjacentDifferences,
    int MaximumWindowSamples = 4096);

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
    public const string CentroidUnconverged = "centroid-unconverged";
    public const string Blended = "blended";
    public const string Crowded = "crowded";
    public const string LowSignalToNoise = "low-snr";

    /// <summary>Every code in gate order. The first failing gate is reported; a windowed centroid that cannot converge
    /// on positive signal is also reported as <see cref="LowSignalToNoise"/>.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        ImageEdge, ExtendedRegion, BackgroundUnavailable, MaskedAperture, SaturatedExcessive,
        HotPixelOrCosmicRay, TooBroad, TrailTooLong, CentroidUnconverged, Blended, Crowded, LowSignalToNoise
    });
}

public enum StellarMeasurementStatus
{
    Completed,
    CandidateBudgetExceeded
}

/// <summary>A gated candidate that was not accepted. The pixel is the peak sample center, not a measured centroid.</summary>
public sealed record StellarMeasurementExclusion(PixelPoint Peak, double PeakAboveBackground, int SampleCount, string ReasonCode)
{
    /// <summary>Inclusive sample bounds of the segmented component. An extended or blended component, such as a transient
    /// streak crossing a star, can cover a location far from its peak; null only for exclusions created outside the measurer.</summary>
    public StellarFootprintBounds? Footprint { get; init; }

    /// <summary>Bounded occupied-cell bitmap over <see cref="Footprint"/> for non-solid excluded components; null when
    /// every sample in <see cref="Footprint"/> is occupied or for legacy/external exclusions without occupancy data.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StellarFootprintOccupancy? Occupancy { get; init; }
}

/// <summary>Inclusive integer sample bounds of a segmented component.</summary>
public readonly record struct StellarFootprintBounds(int MinX, int MinY, int MaxX, int MaxY)
{
    /// <summary>Returns whether a continuous pixel-edge point lies within the bounds expanded by a margin in pixels.</summary>
    public bool Contains(PixelPoint point, double marginPixels) =>
        point.X >= MinX - marginPixels && point.X <= MaxX + 1 + marginPixels &&
        point.Y >= MinY - marginPixels && point.Y <= MaxY + 1 + marginPixels;
}

/// <summary>
/// Fine sub-cell occupancy bitmask refining only the occupied coarse cells of a <see cref="StellarFootprintOccupancy"/>
/// in row-major coarse-cell order. Keeps sparse large-box exclusions (such as a long diagonal streak across a megapixel
/// frame) at near-pixel precision within a bounded byte budget.
/// </summary>
public sealed record StellarFootprintSubgridOccupancy(
    int CellWidth,
    int CellHeight,
    string BitsBase64);

/// <summary>
/// Bounded row-major LSB-first bitmask recording which cells of a <see cref="StellarFootprintBounds"/> contain at
/// least one segmented sample of the excluded component. Each coarse cell covers <see cref="CellWidth"/> by
/// <see cref="CellHeight"/> integer samples starting at <c>(Footprint.MinX, Footprint.MinY)</c>. When coarse cells are
/// larger than a single pixel, <see cref="Subgrid"/> refines only the occupied coarse cells to finer sub-cells.
/// </summary>
public sealed record StellarFootprintOccupancy(
    int Columns,
    int Rows,
    int CellWidth,
    int CellHeight,
    string BitsBase64)
{
    public const int MaximumCells = 2048;
    public const int MaximumBytes = (MaximumCells + 7) / 8;
    public const int MaximumSubgridBits = 65536;
    public const int MaximumSubgridBytes = (MaximumSubgridBits + 7) / 8;

    /// <summary>
    /// Whole-product subgrid bit budget shared deterministically across all refinable coarse-grid exclusions in a
    /// measurement product (512 KiB total raw subgrid bytes), keeping even worst-case 4,096-candidate frames below
    /// the 4 MiB structured processing product envelope.
    /// </summary>
    public const int MaximumTotalSubgridBits = 4_194_304;
    public const int MaximumTotalSubgridBytes = (MaximumTotalSubgridBits + 7) / 8;

    /// <summary>
    /// Returns the deterministic per-exclusion subgrid bit budget shared across
    /// <paramref name="refinableExclusionCount"/> non-solid coarse-grid exclusions in one measurement product.
    /// </summary>
    public static int SubgridBitBudgetPerExclusion(int refinableExclusionCount) =>
        refinableExclusionCount <= 0
            ? 0
            : Math.Min(MaximumSubgridBits, MaximumTotalSubgridBits / refinableExclusionCount);

    /// <summary>
    /// Returns whether a non-solid footprint of <paramref name="width"/> by <paramref name="height"/> samples requires
    /// a coarse (&gt; 1x1) occupancy grid eligible for fine subgrid refinement.
    /// </summary>
    public static bool UsesCoarseGrid(int width, int height) =>
        width > 0 && height > 0 && (long)width * height > MaximumCells;

    /// <summary>Optional fine sub-cell occupancy over the occupied coarse cells; omitted for 1x1 coarse grids.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StellarFootprintSubgridOccupancy? Subgrid { get; init; }

    /// <summary>Creates a bounded occupancy bitmap over <paramref name="footprint"/> from an exact pixel predicate.</summary>
    public static StellarFootprintOccupancy Create(StellarFootprintBounds footprint, Func<int, int, bool> isOccupied)
    {
        ArgumentNullException.ThrowIfNull(isOccupied);
        var width = footprint.MaxX - footprint.MinX + 1;
        var height = footprint.MaxY - footprint.MinY + 1;
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(footprint));
        var (columns, rows, cellWidth, cellHeight) = ChooseGrid(width, height);
        var bits = new byte[(columns * rows + 7) / 8];
        for (var y = footprint.MinY; y <= footprint.MaxY; y++)
        {
            var rowOffset = ((y - footprint.MinY) / cellHeight) * columns;
            for (var x = footprint.MinX; x <= footprint.MaxX; x++)
            {
                if (!isOccupied(x, y)) continue;
                var bit = rowOffset + (x - footprint.MinX) / cellWidth;
                bits[bit >> 3] |= (byte)(1 << (bit & 7));
            }
        }

        StellarFootprintSubgridOccupancy? subgrid = null;
        var occupiedCoarse = CountSetBits(bits);
        if (ChooseSubgrid(cellWidth, cellHeight, occupiedCoarse) is { } subSpec)
        {
            var coarseToOrdinal = BuildCoarseToOrdinal(bits, columns * rows);
            var bitsPerCoarseCell = subSpec.SubColumns * subSpec.SubRows;
            var subBits = new byte[(occupiedCoarse * bitsPerCoarseCell + 7) / 8];
            for (var y = footprint.MinY; y <= footprint.MaxY; y++)
            {
                var relY = y - footprint.MinY;
                var row = relY / cellHeight;
                var subRow = (relY - row * cellHeight) / subSpec.SubCellHeight;
                var rowOffset = row * columns;
                for (var x = footprint.MinX; x <= footprint.MaxX; x++)
                {
                    if (!isOccupied(x, y)) continue;
                    var relX = x - footprint.MinX;
                    var col = relX / cellWidth;
                    var ordinal = coarseToOrdinal[rowOffset + col];
                    var subCol = (relX - col * cellWidth) / subSpec.SubCellWidth;
                    var subBit = ordinal * bitsPerCoarseCell + subRow * subSpec.SubColumns + subCol;
                    subBits[subBit >> 3] |= (byte)(1 << (subBit & 7));
                }
            }
            subgrid = new(subSpec.SubCellWidth, subSpec.SubCellHeight, Convert.ToBase64String(subBits));
        }

        return new(columns, rows, cellWidth, cellHeight, Convert.ToBase64String(bits))
        {
            Subgrid = subgrid
        };
    }

    internal static (int Columns, int Rows, int CellWidth, int CellHeight) ChooseGrid(int width, int height)
    {
        if ((long)width * height <= MaximumCells)
            return (width, height, 1, 1);
        long w = width;
        long h = height;
        var step = Math.Max(2L, (long)Math.Ceiling(Math.Sqrt((double)w * h / MaximumCells)));
        var minSingleAxis = (long)Math.Ceiling((double)Math.Max(w, h) / MaximumCells);
        if (step < minSingleAxis)
            step = minSingleAxis;
        while (true)
        {
            var columns = (w + step - 1) / step;
            var rows = (h + step - 1) / step;
            if (columns * rows <= MaximumCells)
                return ((int)columns, (int)rows, (int)Math.Min(step, w), (int)Math.Min(step, h));
            step++;
        }
    }

    internal static (int SubCellWidth, int SubCellHeight, int SubColumns, int SubRows)? ChooseSubgrid(
        int coarseCellWidth, int coarseCellHeight, int occupiedCoarseCount,
        int maximumSubgridBits = MaximumSubgridBits)
    {
        if (occupiedCoarseCount <= 0 || maximumSubgridBits <= 0 || (coarseCellWidth <= 1 && coarseCellHeight <= 1))
            return null;
        var effectiveBudget = Math.Min(maximumSubgridBits, MaximumSubgridBits);
        var maxBitsPerCell = effectiveBudget / occupiedCoarseCount;
        if (maxBitsPerCell < 2)
            return null;
        long cw = coarseCellWidth;
        long ch = coarseCellHeight;
        var step = Math.Max(1L, (long)Math.Ceiling(Math.Sqrt((double)cw * ch / maxBitsPerCell)));
        var minSingleAxis = (long)Math.Ceiling((double)Math.Max(cw, ch) / maxBitsPerCell);
        if (step < minSingleAxis)
            step = minSingleAxis;
        while (true)
        {
            var subCols = (cw + step - 1) / step;
            var subRows = (ch + step - 1) / step;
            if (subCols * subRows <= maxBitsPerCell)
            {
                if (subCols == 1 && subRows == 1)
                    return null;
                return ((int)Math.Min(step, cw), (int)Math.Min(step, ch), (int)subCols, (int)subRows);
            }
            step++;
        }
    }

    internal static int CountSetBits(ReadOnlySpan<byte> bits)
    {
        var count = 0;
        for (var i = 0; i < bits.Length; i++)
            count += BitOperations.PopCount(bits[i]);
        return count;
    }

    internal static int[] BuildCoarseToOrdinal(ReadOnlySpan<byte> bits, int cellCount)
    {
        var map = new int[cellCount];
        Array.Fill(map, -1);
        var ordinal = 0;
        for (var bit = 0; bit < cellCount; bit++)
        {
            if ((bits[bit >> 3] & (1 << (bit & 7))) != 0)
                map[bit] = ordinal++;
        }
        return map;
    }

    /// <summary>Validates dimensions, grid partition, canonical Base64 encoding, zero trailing padding bits, subgrid integrity, and non-emptiness.</summary>
    public bool IsValid(StellarFootprintBounds footprint) =>
        ValidateCore(footprint, peak: null, sampleCount: null, subgridBitBudget: null);

    /// <summary>
    /// Validates occupancy geometry and encoding together with physical consistency against the recorded exclusion
    /// <paramref name="peak"/> and segmented <paramref name="sampleCount"/>.
    /// </summary>
    public bool IsValid(StellarFootprintBounds footprint, PixelPoint peak, int sampleCount) =>
        ValidateCore(footprint, peak, sampleCount, subgridBitBudget: null);

    /// <summary>
    /// Validates occupancy geometry, encoding, physical consistency, and exact deterministic coarse/subgrid refinement
    /// compliance against the whole-product <paramref name="subgridBitBudget"/>.
    /// </summary>
    public bool IsValid(StellarFootprintBounds footprint, PixelPoint peak, int sampleCount, int subgridBitBudget) =>
        subgridBitBudget >= 0 && ValidateCore(footprint, peak, sampleCount, subgridBitBudget);

    private bool ValidateCore(
        StellarFootprintBounds footprint, PixelPoint? peak, int? sampleCount, int? subgridBitBudget)
    {
        var fpWidth = (long)footprint.MaxX - footprint.MinX + 1;
        var fpHeight = (long)footprint.MaxY - footprint.MinY + 1;
        if (fpWidth <= 0 || fpHeight <= 0 || fpWidth > int.MaxValue || fpHeight > int.MaxValue ||
            Columns <= 0 || Rows <= 0 || CellWidth <= 0 || CellHeight <= 0 ||
            CellWidth > fpWidth || CellHeight > fpHeight)
            return false;
        var (expectedCols, expectedRows, expectedCellW, expectedCellH) = ChooseGrid((int)fpWidth, (int)fpHeight);
        var cellCount = (long)Columns * Rows;
        if (cellCount > MaximumCells ||
            Columns != expectedCols || Rows != expectedRows ||
            CellWidth != expectedCellW || CellHeight != expectedCellH ||
            string.IsNullOrEmpty(BitsBase64))
            return false;
        var expectedBytes = (int)((cellCount + 7) / 8);
        Span<byte> decoded = stackalloc byte[MaximumBytes];
        if (!Convert.TryFromBase64String(BitsBase64, decoded, out var written) || written != expectedBytes)
            return false;
        var payload = decoded[..written];
        if (!string.Equals(Convert.ToBase64String(payload), BitsBase64, StringComparison.Ordinal))
            return false;
        var remainderBits = (int)(cellCount & 7);
        if (remainderBits != 0 && (payload[^1] >> remainderBits) != 0)
            return false;
        var occupiedCoarse = CountSetBits(payload);
        if (occupiedCoarse == 0)
            return false;

        var peakX = 0;
        var peakY = 0;
        var peakCoarseCol = -1;
        var peakCoarseRow = -1;
        if (peak is { } peakPoint)
        {
            if (!double.IsFinite(peakPoint.X) || !double.IsFinite(peakPoint.Y))
                return false;
            var floorX = Math.Floor(peakPoint.X);
            var floorY = Math.Floor(peakPoint.Y);
            if (floorX < footprint.MinX || floorX > footprint.MaxX || floorY < footprint.MinY || floorY > footprint.MaxY)
                return false;
            peakX = (int)floorX;
            peakY = (int)floorY;
            peakCoarseCol = (peakX - footprint.MinX) / CellWidth;
            peakCoarseRow = (peakY - footprint.MinY) / CellHeight;
            var peakBit = peakCoarseRow * Columns + peakCoarseCol;
            if ((payload[peakBit >> 3] & (1 << (peakBit & 7))) == 0)
                return false;
        }

        if (sampleCount is { } samples)
        {
            if (samples <= 0 || samples > fpWidth * fpHeight || occupiedCoarse > samples)
                return false;
        }

        if (subgridBitBudget is { } budget)
        {
            var expectedSub = ChooseSubgrid(CellWidth, CellHeight, occupiedCoarse, budget);
            if (expectedSub is null)
            {
                if (Subgrid is not null)
                    return false;
            }
            else if (Subgrid is not { } actualSub ||
                     actualSub.CellWidth != expectedSub.Value.SubCellWidth ||
                     actualSub.CellHeight != expectedSub.Value.SubCellHeight)
            {
                return false;
            }
        }
        else if (Subgrid is null &&
                 ChooseSubgrid(CellWidth, CellHeight, occupiedCoarse,
                     SubgridBitBudgetPerExclusion(StellarDetector.MaximumSupportedCandidates)) is not null)
        {
            return false;
        }

        if (Subgrid is null)
        {
            if (sampleCount is { } coarseSamples)
            {
                long capacity = 0;
                for (var row = 0; row < Rows; row++)
                {
                    var ch = Math.Min(CellHeight, fpHeight - (long)row * CellHeight);
                    var rowBase = row * Columns;
                    for (var col = 0; col < Columns; col++)
                    {
                        var bit = rowBase + col;
                        if ((payload[bit >> 3] & (1 << (bit & 7))) == 0)
                            continue;
                        var cw = Math.Min(CellWidth, fpWidth - (long)col * CellWidth);
                        capacity += cw * ch;
                        if (capacity >= coarseSamples)
                            return true;
                    }
                }
                return capacity >= coarseSamples;
            }
            return true;
        }

        var sub = Subgrid;
        if ((CellWidth <= 1 && CellHeight <= 1) ||
            sub.CellWidth <= 0 || sub.CellHeight <= 0 ||
            sub.CellWidth > CellWidth || sub.CellHeight > CellHeight ||
            (sub.CellWidth == CellWidth && sub.CellHeight == CellHeight) ||
            string.IsNullOrEmpty(sub.BitsBase64))
            return false;
        var subCols = (CellWidth + sub.CellWidth - 1) / sub.CellWidth;
        var subRows = (CellHeight + sub.CellHeight - 1) / sub.CellHeight;
        var bitsPerCoarseCell = (long)subCols * subRows;
        var totalSubBits = (long)occupiedCoarse * bitsPerCoarseCell;
        if (totalSubBits <= 0 || totalSubBits > MaximumSubgridBits)
            return false;
        var expectedSubBytes = (int)((totalSubBits + 7) / 8);
        var subPayload = new byte[expectedSubBytes];
        if (!Convert.TryFromBase64String(sub.BitsBase64, subPayload, out var subWritten) || subWritten != expectedSubBytes)
            return false;
        if (!string.Equals(Convert.ToBase64String(subPayload), sub.BitsBase64, StringComparison.Ordinal))
            return false;
        var subRemainderBits = (int)(totalSubBits & 7);
        if (subRemainderBits != 0 && (subPayload[^1] >> subRemainderBits) != 0)
            return false;

        var ordinal = 0;
        long occupiedLeaf = 0;
        long leafCapacity = 0;
        for (var row = 0; row < Rows; row++)
        {
            var ch = (int)Math.Min(CellHeight, fpHeight - (long)row * CellHeight);
            var validSubRows = (ch + sub.CellHeight - 1) / sub.CellHeight;
            var rowBase = row * Columns;
            for (var col = 0; col < Columns; col++)
            {
                var bit = rowBase + col;
                if ((payload[bit >> 3] & (1 << (bit & 7))) == 0)
                    continue;
                var cw = (int)Math.Min(CellWidth, fpWidth - (long)col * CellWidth);
                var validSubCols = (cw + sub.CellWidth - 1) / sub.CellWidth;
                var cellSubBase = (long)ordinal * bitsPerCoarseCell;
                var cellOccupiedSub = 0;
                for (var sr = 0; sr < subRows; sr++)
                {
                    var sh = sr < validSubRows ? Math.Min(sub.CellHeight, ch - sr * sub.CellHeight) : 0;
                    var srBase = cellSubBase + (long)sr * subCols;
                    for (var sc = 0; sc < subCols; sc++)
                    {
                        var subBit = (int)(srBase + sc);
                        if ((subPayload[subBit >> 3] & (1 << (subBit & 7))) == 0)
                            continue;
                        if (sc >= validSubCols || sr >= validSubRows)
                            return false;
                        cellOccupiedSub++;
                        occupiedLeaf++;
                        if (sampleCount.HasValue)
                        {
                            var sw = Math.Min(sub.CellWidth, cw - sc * sub.CellWidth);
                            leafCapacity += (long)sw * sh;
                        }
                    }
                }
                if (cellOccupiedSub == 0)
                    return false;
                if (peak.HasValue && row == peakCoarseRow && col == peakCoarseCol)
                {
                    var peakSubCol = ((peakX - footprint.MinX) - col * CellWidth) / sub.CellWidth;
                    var peakSubRow = ((peakY - footprint.MinY) - row * CellHeight) / sub.CellHeight;
                    var peakSubBit = (int)(cellSubBase + (long)peakSubRow * subCols + peakSubCol);
                    if ((subPayload[peakSubBit >> 3] & (1 << (peakSubBit & 7))) == 0)
                        return false;
                }
                ordinal++;
            }
        }

        if (sampleCount is { } leafSamples && (occupiedLeaf > leafSamples || leafSamples > leafCapacity))
            return false;
        return true;
    }

    /// <summary>
    /// Returns the minimum squared Euclidean distance from continuous pixel-edge <paramref name="point"/> to any
    /// occupied cell within <paramref name="maxRadiusPixels"/>, or <see cref="double.PositiveInfinity"/> if none lies
    /// within that radius.
    /// </summary>
    public double DistanceSquaredToOccupied(StellarFootprintBounds footprint, PixelPoint point, double maxRadiusPixels)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(maxRadiusPixels) || maxRadiusPixels < 0 ||
            Columns <= 0 || Rows <= 0 || CellWidth <= 0 || CellHeight <= 0 ||
            !footprint.Contains(point, maxRadiusPixels))
            return double.PositiveInfinity;
        var cellCount = (long)Columns * Rows;
        if (cellCount > MaximumCells || string.IsNullOrEmpty(BitsBase64))
            return double.PositiveInfinity;
        var expectedBytes = (int)((cellCount + 7) / 8);
        Span<byte> bits = stackalloc byte[MaximumBytes];
        if (!Convert.TryFromBase64String(BitsBase64, bits, out var written) || written != expectedBytes)
            return double.PositiveInfinity;

        var startCol = (int)Math.Max(0, Math.Ceiling((point.X - maxRadiusPixels - footprint.MinX) / CellWidth) - 1);
        var endCol = (int)Math.Min(Columns - 1, Math.Floor((point.X + maxRadiusPixels - footprint.MinX) / CellWidth));
        var startRow = (int)Math.Max(0, Math.Ceiling((point.Y - maxRadiusPixels - footprint.MinY) / CellHeight) - 1);
        var endRow = (int)Math.Min(Rows - 1, Math.Floor((point.Y + maxRadiusPixels - footprint.MinY) / CellHeight));
        if (endCol < startCol || endRow < startRow)
            return double.PositiveInfinity;

        byte[]? subPayload = null;
        var subCols = 0;
        var subRows = 0;
        var bitsPerCoarseCell = 0;
        var subCellW = 0;
        var subCellH = 0;
        Span<int> coarsePrefixPop = stackalloc int[MaximumBytes];
        if (Subgrid is { } sub)
        {
            if (sub.CellWidth <= 0 || sub.CellHeight <= 0 ||
                sub.CellWidth > CellWidth || sub.CellHeight > CellHeight ||
                string.IsNullOrEmpty(sub.BitsBase64))
                return double.PositiveInfinity;
            var occupiedCoarse = 0;
            for (var i = 0; i < written; i++)
            {
                coarsePrefixPop[i] = occupiedCoarse;
                occupiedCoarse += BitOperations.PopCount(bits[i]);
            }
            subCellW = sub.CellWidth;
            subCellH = sub.CellHeight;
            subCols = (CellWidth + subCellW - 1) / subCellW;
            subRows = (CellHeight + subCellH - 1) / subCellH;
            bitsPerCoarseCell = subCols * subRows;
            var totalSubBits = (long)occupiedCoarse * bitsPerCoarseCell;
            if (totalSubBits <= 0 || totalSubBits > MaximumSubgridBits)
                return double.PositiveInfinity;
            var expectedSubBytes = (int)((totalSubBits + 7) / 8);
            subPayload = new byte[expectedSubBytes];
            if (!Convert.TryFromBase64String(sub.BitsBase64, subPayload, out var subWritten) || subWritten != expectedSubBytes)
                return double.PositiveInfinity;
        }

        var maxFootprintX = (long)footprint.MaxX + 1;
        var maxFootprintY = (long)footprint.MaxY + 1;
        var maxRadiusSquared = maxRadiusPixels * maxRadiusPixels;
        var best = double.PositiveInfinity;
        for (var row = startRow; row <= endRow; row++)
        {
            var minY = (long)footprint.MinY + (long)row * CellHeight;
            var maxY = Math.Min(maxFootprintY, minY + CellHeight);
            var dy = point.Y < minY ? minY - point.Y : point.Y > maxY ? point.Y - maxY : 0.0;
            var dy2 = dy * dy;
            if (dy2 >= best || dy2 > maxRadiusSquared) continue;
            var rowBase = row * Columns;
            for (var col = startCol; col <= endCol; col++)
            {
                var bit = rowBase + col;
                if ((bits[bit >> 3] & (1 << (bit & 7))) == 0)
                    continue;
                var minX = (long)footprint.MinX + (long)col * CellWidth;
                var maxX = Math.Min(maxFootprintX, minX + CellWidth);
                var dx = point.X < minX ? minX - point.X : point.X > maxX ? point.X - maxX : 0.0;
                var d2 = dx * dx + dy2;
                if (d2 >= best || d2 > maxRadiusSquared)
                    continue;
                if (subPayload is null)
                {
                    best = d2;
                    if (best == 0.0)
                        return 0.0;
                    continue;
                }

                var byteIndex = bit >> 3;
                var lowerMask = (1 << (bit & 7)) - 1;
                var ordinal = coarsePrefixPop[byteIndex] + BitOperations.PopCount((uint)(bits[byteIndex] & lowerMask));
                var cellSubBase = ordinal * bitsPerCoarseCell;
                var validSubCols = (int)((maxX - minX + subCellW - 1) / subCellW);
                var validSubRows = (int)((maxY - minY + subCellH - 1) / subCellH);
                var sStartCol = (int)Math.Max(0, Math.Ceiling((point.X - maxRadiusPixels - minX) / subCellW) - 1);
                var sEndCol = (int)Math.Min(validSubCols - 1, Math.Floor((point.X + maxRadiusPixels - minX) / subCellW));
                var sStartRow = (int)Math.Max(0, Math.Ceiling((point.Y - maxRadiusPixels - minY) / subCellH) - 1);
                var sEndRow = (int)Math.Min(validSubRows - 1, Math.Floor((point.Y + maxRadiusPixels - minY) / subCellH));
                for (var sr = sStartRow; sr <= sEndRow; sr++)
                {
                    var sMinY = minY + (long)sr * subCellH;
                    var sMaxY = Math.Min(maxY, sMinY + subCellH);
                    var sdy = point.Y < sMinY ? sMinY - point.Y : point.Y > sMaxY ? point.Y - sMaxY : 0.0;
                    var sdy2 = sdy * sdy;
                    if (sdy2 >= best || sdy2 > maxRadiusSquared) continue;
                    var srBase = cellSubBase + sr * subCols;
                    for (var sc = sStartCol; sc <= sEndCol; sc++)
                    {
                        var subBit = srBase + sc;
                        if ((subPayload[subBit >> 3] & (1 << (subBit & 7))) == 0)
                            continue;
                        var sMinX = minX + (long)sc * subCellW;
                        var sMaxX = Math.Min(maxX, sMinX + subCellW);
                        var sdx = point.X < sMinX ? sMinX - point.X : point.X > sMaxX ? point.X - sMaxX : 0.0;
                        var sd2 = sdx * sdx + sdy2;
                        if (sd2 < best)
                        {
                            best = sd2;
                            if (best == 0.0)
                                return 0.0;
                        }
                    }
                }
            }
        }
        return best <= maxRadiusSquared ? best : double.PositiveInfinity;
    }
}

/// <summary>Owned, immutable measurement output. A budget failure carries no partial detections or exclusions.</summary>
public sealed class StellarMeasurementResult
{
    internal StellarMeasurementResult(StellarMeasurementStatus status, List<StellarDetection> detections,
        List<StellarMeasurementExclusion> exclusions, int candidateCount, int tiles, int availableTiles, int filledTiles,
        double? medianBackground, double? medianNoiseSigma, string settingsIdentitySha256,
        string algorithmVersion = StellarSourceMeasurer.AlgorithmVersion)
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
        AlgorithmVersion = algorithmVersion;
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
    public string AlgorithmVersion { get; }
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
/// <para>Compact-source centroids are iterative Gaussian-windowed first moments; one whose iteration does not converge
/// is excluded as <see cref="StellarExclusionReasons.CentroidUnconverged"/>. A narrow window has no unique fixed point
/// along a trail, so a trailed source's centroid is the flux-weighted first moment of its grown footprint. The centroid
/// covariance propagates the declared per-sample noise model through the corresponding estimator. It ignores interpolation-correlated noise, PSF
/// model error, and saturation clipping, so it is reported as unvalidated and must not be treated as calibrated
/// coverage. No caller buffers are retained or modified.</para>
/// </remarks>
public static class StellarSourceMeasurer
{
    /// <summary>Historical v2 measurement algorithm identifier (omits exclusion occupancy bitmaps).</summary>
    public const string V2AlgorithmVersion = "linear-stellar-local-v2";

    /// <summary>Current measurement algorithm identifier (emits two-level exclusion occupancy bitmaps).</summary>
    public const string AlgorithmVersion = "linear-stellar-local-v3";

    private const int MinimumDimension = 16;
    private const int MaximumCentroidIterations = 100;
    private const double CentroidConvergencePixels = 1e-6;

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
        => MeasureCore(pixels, validMask, saturatedMask, width, height, options,
            includeExclusionOccupancy: true, AlgorithmVersion, cancellationToken);

    /// <summary>
    /// Replays the legacy <see cref="V2AlgorithmVersion"/> measurement contract (omitting exclusion occupancy bitmaps
    /// and using the v2 settings identity) so archived outputs reproduce bit-for-bit.
    /// </summary>
    public static StellarMeasurementResult MeasureLegacyV2(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        StellarMeasurementOptions? options = null,
        CancellationToken cancellationToken = default)
        => MeasureCore(pixels, validMask, saturatedMask, width, height, options,
            includeExclusionOccupancy: false, V2AlgorithmVersion, cancellationToken);

    private static StellarMeasurementResult MeasureCore(
        ReadOnlySpan<double> pixels,
        ReadOnlySpan<bool> validMask,
        ReadOnlySpan<bool> saturatedMask,
        int width,
        int height,
        StellarMeasurementOptions? options,
        bool includeExclusionOccupancy,
        string algorithmVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ValidateOptions(options);
        LinearStellarInput.Validate(pixels, validMask, width, height, MinimumDimension, options.MaximumPixelCount, cancellationToken);
        if (!saturatedMask.IsEmpty && saturatedMask.Length != pixels.Length)
        {
            throw new ArgumentException("The saturation mask must be empty or match the image.", nameof(saturatedMask));
        }

        var identity = SettingsIdentity(options, algorithmVersion);
        var saturated = Dilate(saturatedMask, width, height, options.SaturationDilationPixels, cancellationToken);
        var mesh = BackgroundMesh.Create(pixels, validMask, saturated, width, height, options, cancellationToken);
        var labels = new int[pixels.Length];
        var components = Segment(pixels, validMask, saturated, width, height, mesh, options, labels, cancellationToken);
        var candidates = components.Where(component => component.IsCandidate).ToList();
        if (candidates.Count > options.MaximumCandidateCount)
        {
            return new(StellarMeasurementStatus.CandidateBudgetExceeded, [], [], candidates.Count,
                mesh.TileCount, mesh.AvailableCount, mesh.FilledCount, mesh.MedianBackground, mesh.MedianNoise,
                identity, algorithmVersion);
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
        var excludedComponents = new List<(Component Component, string Reason)>();
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
                excludedComponents.Add((component, outcome.Reason!));
            }
        }
        var exclusions = BuildExclusions(excludedComponents, components.Count, labels, width, height,
            includeExclusionOccupancy, cancellationToken);
        var ordered = accepted
            .OrderByDescending(entry => entry.Detection.Flux)
            .ThenBy(entry => entry.PeakIndex)
            .Select((entry, index) => entry.Detection with { Index = index })
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        return new(StellarMeasurementStatus.Completed, ordered, exclusions, candidates.Count,
            mesh.TileCount, mesh.AvailableCount, mesh.FilledCount, mesh.MedianBackground, mesh.MedianNoise,
            identity, algorithmVersion);
    }

    private sealed class PendingSubgrid(
        Component component, int columns, int cellWidth, int cellHeight,
        int subCellWidth, int subCellHeight, int subColumns, int subRows,
        int[] coarseToOrdinal, byte[] subBits)
    {
        public Component Component { get; } = component;
        public int Columns { get; } = columns;
        public int CellWidth { get; } = cellWidth;
        public int CellHeight { get; } = cellHeight;
        public int SubCellWidth { get; } = subCellWidth;
        public int SubCellHeight { get; } = subCellHeight;
        public int SubColumns { get; } = subColumns;
        public int BitsPerCoarseCell { get; } = subColumns * subRows;
        public int[] CoarseToOrdinal { get; } = coarseToOrdinal;
        public byte[] SubBits { get; } = subBits;
    }

    private static List<StellarMeasurementExclusion> BuildExclusions(
        List<(Component Component, string Reason)> excludedComponents,
        int componentCount,
        int[] labels,
        int width,
        int height,
        bool includeExclusionOccupancy,
        CancellationToken cancellationToken)
    {
        if (!includeExclusionOccupancy)
        {
            var legacy = new List<StellarMeasurementExclusion>(excludedComponents.Count);
            foreach (var (component, reason) in excludedComponents)
            {
                legacy.Add(new(new(component.PeakIndex % width + 0.5, component.PeakIndex / width + 0.5),
                    component.PeakExcess, component.Count, reason)
                {
                    Footprint = new(component.MinX, component.MinY, component.MaxX, component.MaxY)
                });
            }
            return legacy;
        }

        var pending = new List<(int ExclusionIndex, Component Component, int Columns, int Rows, int CellWidth, int CellHeight, byte[] Bits)>();
        var pendingIndexByExclusion = new int[excludedComponents.Count];
        Array.Fill(pendingIndexByExclusion, -1);
        long totalBoxArea = 0;

        for (var i = 0; i < excludedComponents.Count; i++)
        {
            var component = excludedComponents[i].Component;
            var fpWidth = component.MaxX - component.MinX + 1;
            var fpHeight = component.MaxY - component.MinY + 1;
            var boxArea = (long)fpWidth * fpHeight;
            if (fpWidth <= 0 || fpHeight <= 0 || component.Count >= boxArea)
                continue;
            var (columns, rows, cellWidth, cellHeight) = StellarFootprintOccupancy.ChooseGrid(fpWidth, fpHeight);
            var bits = new byte[(columns * rows + 7) / 8];
            pendingIndexByExclusion[i] = pending.Count;
            pending.Add((i, component, columns, rows, cellWidth, cellHeight, bits));
            totalBoxArea += boxArea;
        }

        var subgridByPending = new PendingSubgrid?[pending.Count];
        if (pending.Count > 0)
        {
            if (totalBoxArea <= labels.Length)
            {
                foreach (var (_, component, columns, _, cellWidth, cellHeight, bits) in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var label = component.Label;
                    for (var y = component.MinY; y <= component.MaxY; y++)
                    {
                        var rowOffset = ((y - component.MinY) / cellHeight) * columns;
                        var pixelRow = y * width;
                        for (var x = component.MinX; x <= component.MaxX; x++)
                        {
                            if (labels[pixelRow + x] != label) continue;
                            var bit = rowOffset + (x - component.MinX) / cellWidth;
                            bits[bit >> 3] |= (byte)(1 << (bit & 7));
                        }
                    }
                }
            }
            else
            {
                // Bounded worst-case O(width * height) single pass when many large exclusion boxes overlap.
                var labelToPending = new int[componentCount];
                Array.Fill(labelToPending, -1);
                for (var p = 0; p < pending.Count; p++)
                    labelToPending[pending[p].Component.Label] = p;
                for (var y = 0; y < height; y++)
                {
                    if ((y & 63) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var pixelRow = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var label = labels[pixelRow + x];
                        if (label <= 0) continue;
                        var p = labelToPending[label];
                        if (p < 0) continue;
                        var item = pending[p];
                        var bit = ((y - item.Component.MinY) / item.CellHeight) * item.Columns +
                                  (x - item.Component.MinX) / item.CellWidth;
                        item.Bits[bit >> 3] |= (byte)(1 << (bit & 7));
                    }
                }
            }

            // Refine coarse (> 1x1) occupied cells with a whole-product bounded sparse fine subgrid in one bounded pass.
            var refinableCount = 0;
            for (var p = 0; p < pending.Count; p++)
            {
                var item = pending[p];
                if (item.CellWidth > 1 || item.CellHeight > 1)
                    refinableCount++;
            }
            var subgridBitBudget = StellarFootprintOccupancy.SubgridBitBudgetPerExclusion(refinableCount);
            var pendingSubgrids = new List<PendingSubgrid>();
            long totalSubBoxArea = 0;
            for (var p = 0; p < pending.Count; p++)
            {
                var item = pending[p];
                if (item.CellWidth <= 1 && item.CellHeight <= 1)
                    continue;
                var occupiedCoarse = StellarFootprintOccupancy.CountSetBits(item.Bits);
                if (StellarFootprintOccupancy.ChooseSubgrid(item.CellWidth, item.CellHeight, occupiedCoarse, subgridBitBudget) is not { } subSpec)
                    continue;
                var coarseToOrdinal = StellarFootprintOccupancy.BuildCoarseToOrdinal(item.Bits, item.Columns * item.Rows);
                var subBits = new byte[(occupiedCoarse * subSpec.SubColumns * subSpec.SubRows + 7) / 8];
                var sub = new PendingSubgrid(item.Component, item.Columns, item.CellWidth, item.CellHeight,
                    subSpec.SubCellWidth, subSpec.SubCellHeight, subSpec.SubColumns, subSpec.SubRows,
                    coarseToOrdinal, subBits);
                subgridByPending[p] = sub;
                pendingSubgrids.Add(sub);
                var fpW = item.Component.MaxX - item.Component.MinX + 1;
                var fpH = item.Component.MaxY - item.Component.MinY + 1;
                totalSubBoxArea += (long)fpW * fpH;
            }

            if (pendingSubgrids.Count > 0)
            {
                if (totalSubBoxArea <= labels.Length)
                {
                    foreach (var sub in pendingSubgrids)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var component = sub.Component;
                        var label = component.Label;
                        for (var y = component.MinY; y <= component.MaxY; y++)
                        {
                            var relY = y - component.MinY;
                            var row = relY / sub.CellHeight;
                            var subRow = (relY - row * sub.CellHeight) / sub.SubCellHeight;
                            var rowOffset = row * sub.Columns;
                            var pixelRow = y * width;
                            for (var x = component.MinX; x <= component.MaxX; x++)
                            {
                                if (labels[pixelRow + x] != label) continue;
                                var relX = x - component.MinX;
                                var col = relX / sub.CellWidth;
                                var ordinal = sub.CoarseToOrdinal[rowOffset + col];
                                var subCol = (relX - col * sub.CellWidth) / sub.SubCellWidth;
                                var subBit = ordinal * sub.BitsPerCoarseCell + subRow * sub.SubColumns + subCol;
                                sub.SubBits[subBit >> 3] |= (byte)(1 << (subBit & 7));
                            }
                        }
                    }
                }
                else
                {
                    var labelToSub = new int[componentCount];
                    Array.Fill(labelToSub, -1);
                    for (var s = 0; s < pendingSubgrids.Count; s++)
                        labelToSub[pendingSubgrids[s].Component.Label] = s;
                    for (var y = 0; y < height; y++)
                    {
                        if ((y & 63) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        var pixelRow = y * width;
                        for (var x = 0; x < width; x++)
                        {
                            var label = labels[pixelRow + x];
                            if (label <= 0) continue;
                            var s = labelToSub[label];
                            if (s < 0) continue;
                            var sub = pendingSubgrids[s];
                            var relY = y - sub.Component.MinY;
                            var relX = x - sub.Component.MinX;
                            var row = relY / sub.CellHeight;
                            var col = relX / sub.CellWidth;
                            var ordinal = sub.CoarseToOrdinal[row * sub.Columns + col];
                            var subRow = (relY - row * sub.CellHeight) / sub.SubCellHeight;
                            var subCol = (relX - col * sub.CellWidth) / sub.SubCellWidth;
                            var subBit = ordinal * sub.BitsPerCoarseCell + subRow * sub.SubColumns + subCol;
                            sub.SubBits[subBit >> 3] |= (byte)(1 << (subBit & 7));
                        }
                    }
                }
            }
        }

        var exclusions = new List<StellarMeasurementExclusion>(excludedComponents.Count);
        for (var i = 0; i < excludedComponents.Count; i++)
        {
            var (component, reason) = excludedComponents[i];
            StellarFootprintOccupancy? occupancy = null;
            if (pendingIndexByExclusion[i] is var p and >= 0)
            {
                var item = pending[p];
                var sub = subgridByPending[p];
                occupancy = new(item.Columns, item.Rows, item.CellWidth, item.CellHeight, Convert.ToBase64String(item.Bits))
                {
                    Subgrid = sub is null ? null : new(sub.SubCellWidth, sub.SubCellHeight, Convert.ToBase64String(sub.SubBits))
                };
            }
            exclusions.Add(new(new(component.PeakIndex % width + 0.5, component.PeakIndex / width + 0.5),
                component.PeakExcess, component.Count, reason)
            {
                Footprint = new(component.MinX, component.MinY, component.MaxX, component.MaxY),
                Occupancy = occupancy
            });
        }
        return exclusions;
    }

    /// <summary>Content identity of the current algorithm version and every declared threshold.</summary>
    public static string SettingsIdentity(StellarMeasurementOptions options)
        => SettingsIdentity(options, AlgorithmVersion);

    /// <summary>Content identity of a specified algorithm version and every declared threshold.</summary>
    public static string SettingsIdentity(StellarMeasurementOptions options, string algorithmVersion)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmVersion);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "stellar-measurement-settings-v1",
            algorithm = algorithmVersion,
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
        var windowWidth = x1 - x0 + 1;
        var windowHeight = y1 - y0 + 1;
        if (component.Count > options.MaximumComponentSamples || (long)windowWidth * windowHeight > options.MaximumWindowSamples)
        {
            return new(null, StellarExclusionReasons.ExtendedRegion);
        }
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
        var converged = false;
        for (var iteration = 0; iteration < MaximumCentroidIterations; iteration++)
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
            if (Math.Abs(stepX) + Math.Abs(stepY) < CentroidConvergencePixels)
            {
                converged = true;
                break;
            }
        }

        // Flux and shape use the own footprint grown by the window radius, excluding other candidates' samples.
        // Shape moments are central about the aperture's own first moment, independent of the windowed centroid.
        var grow = Math.Max(2, (int)Math.Ceiling(2 * options.WindowSigmaPixels));
        var aperture = Grow(own, windowWidth, windowHeight, grow);
        double flux = 0, fluxVariance = 0, fx = 0, fy = 0, positive = 0, px = 0, py = 0;
        for (var local = 0; local < excess.Length; local++)
        {
            if (!aperture[local] || !usable[local])
            {
                continue;
            }
            var x = local % windowWidth + 0.5;
            var y = local / windowWidth + 0.5;
            flux += excess[local];
            fluxVariance += variance[local];
            fx += excess[local] * x;
            fy += excess[local] * y;
            var weight = Math.Max(0, excess[local]);
            positive += weight;
            px += weight * x;
            py += weight * y;
        }
        if (!(flux > 0) || !(positive > 0))
        {
            return new(null, StellarExclusionReasons.LowSignalToNoise);
        }
        px /= positive;
        py /= positive;
        double mxx = 0, myy = 0, mxy = 0;
        for (var local = 0; local < excess.Length; local++)
        {
            if (!aperture[local] || !usable[local] || !(excess[local] > 0))
            {
                continue;
            }
            var dx = local % windowWidth + 0.5 - px;
            var dy = local / windowWidth + 0.5 - py;
            mxx += excess[local] * dx * dx;
            myy += excess[local] * dy * dy;
            mxy += excess[local] * dx * dy;
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
        // A narrow window has no unique fixed point along a trail's flat ridge, so a trail's centroid is its aperture's
        // flux-weighted first moment. A compact source whose windowed centroid did not converge is not measured.
        var trailed = trail >= options.MinimumTrailLengthPixels;
        if (!trailed && !converged)
        {
            return new(null, StellarExclusionReasons.CentroidUnconverged);
        }
        if (trailed)
        {
            cx = fx / flux;
            cy = fy / flux;
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

        var (covariance, covarianceStatus) = trailed
            ? ApertureCovariance(excess, variance, usable, aperture, windowWidth, cx, cy, flux, component.SaturatedCount)
            : Covariance(excess, variance, usable, windowWidth, cx, cy, sigmaSquared, component.SaturatedCount);
        var flags = StellarSourceConditions.None;
        if (component.SaturatedCount > 0)
        {
            flags |= StellarSourceConditions.Saturated;
        }
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

    private static (StellarCentroidCovariance? Covariance, string Status) ApertureCovariance(double[] excess,
        double[] variance, bool[] usable, bool[] aperture, int windowWidth, double cx, double cy, double flux, int saturatedCount)
    {
        // The first moment c = sum I r / sum I has dc/dI_i = (r_i - c) / F, so Cov(c) = sum var_i (r_i - c)(r_i - c)^T / F^2.
        double sxx = 0, sxy = 0, syy = 0;
        for (var local = 0; local < excess.Length; local++)
        {
            if (!aperture[local] || !usable[local])
            {
                continue;
            }
            var dx = local % windowWidth + 0.5 - cx;
            var dy = local / windowWidth + 0.5 - cy;
            sxx += variance[local] * dx * dx;
            syy += variance[local] * dy * dy;
            sxy += variance[local] * dx * dy;
        }
        var scale = (saturatedCount > 0 ? 1 + saturatedCount : 1) / (flux * flux);
        var covariance = new StellarCentroidCovariance(sxx * scale, sxy * scale, syy * scale);
        if (!(covariance.XX > 0) || !(covariance.YY > 0) || !double.IsFinite(covariance.XX) || !double.IsFinite(covariance.YY) ||
            !double.IsFinite(covariance.XY) || covariance.XX * covariance.YY - covariance.XY * covariance.XY <= 0)
        {
            return (null, "unavailable-singular-window");
        }
        return (covariance, saturatedCount > 0
            ? "propagated-noise-trail-moment-saturation-inflated-unvalidated"
            : "propagated-noise-trail-moment-unvalidated");
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
            !Enum.IsDefined(options.NoiseEstimator) || options.MaximumWindowSamples is < 64 or > 1 << 20)
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
