using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Identifies photosites within a globally aligned RGGB Bayer cell.</summary>
[Flags]
public enum BayerMeteringPhotosites
{
    None = 0,
    Red = 1,
    GreenOnRedRow = 2,
    GreenOnBlueRow = 4,
    Blue = 8,
    Green = GreenOnRedRow | GreenOnBlueRow,
    All = Red | Green | Blue
}

/// <summary>A rectangular metering region in logical pixel coordinates.</summary>
public readonly record struct MeteringRegion(int X, int Y, int Width, int Height);

/// <summary>An image circle whose center and radius use logical pixel coordinates.</summary>
public readonly record struct MeteringImageCircle(double CenterX, double CenterY, double Radius);

/// <summary>Controls sparse sampling and linear sensor-level normalization.</summary>
public readonly record struct SparseMeteringOptions
{
    /// <summary>Creates the default sparse metering configuration.</summary>
    public SparseMeteringOptions()
        : this(16, 16)
    {
    }

    /// <summary>
    /// Creates a sparse metering configuration. Strides count pixels and must be even for BayerRggb16.
    /// </summary>
    public SparseMeteringOptions(
        int xStride,
        int yStride,
        ushort blackLevel = 0,
        ushort whiteLevel = ushort.MaxValue,
        ushort? saturationLevel = null,
        MeteringRegion? region = null,
        MeteringImageCircle? imageCircle = null,
        BayerMeteringPhotosites bayerPhotosites = BayerMeteringPhotosites.Green,
        SampleByteOrder byteOrder = SampleByteOrder.LittleEndian)
    {
        XStride = xStride;
        YStride = yStride;
        BlackLevel = blackLevel;
        WhiteLevel = whiteLevel;
        SaturationLevel = saturationLevel;
        Region = region;
        ImageCircle = imageCircle;
        BayerPhotosites = bayerPhotosites;
        ByteOrder = byteOrder;
    }

    public int XStride { get; }

    public int YStride { get; }

    public ushort BlackLevel { get; }

    public ushort WhiteLevel { get; }

    public ushort? SaturationLevel { get; }

    public MeteringRegion? Region { get; }

    public MeteringImageCircle? ImageCircle { get; }

    public BayerMeteringPhotosites BayerPhotosites { get; }

    public SampleByteOrder ByteOrder { get; }
}

/// <summary>A deterministic sparse linear metering result.</summary>
public readonly record struct SparseMeteringResult(
    double NormalizedMean,
    long ConsideredSampleCount,
    long AcceptedSampleCount,
    long SaturatedSampleCount,
    long ScannedBytes)
{
    /// <summary>Gets whether at least one non-saturated sample contributed to the mean.</summary>
    public bool HasMeasurement => AcceptedSampleCount != 0;
}

/// <summary>Computes a sparse mean directly over little-endian Mono16 or BayerRggb16 data.</summary>
public static class SparseLinear16Meter
{
    /// <summary>
    /// Meters a frame without copying it. A non-empty mask contains one least-significant-bit-first inclusion bit
    /// per row-major logical pixel. Scanned bytes count only source pixel bytes read after geometric and mask filters.
    /// </summary>
    public static SparseMeteringResult Measure(
        ImageLayout layout,
        ReadOnlySpan<byte> pixelData,
        SparseMeteringOptions options,
        ReadOnlySpan<byte> inclusionMask = default,
        ReadOnlySpan<MeteringRegion> excludedRegions = default)
    {
        layout.Validate();
        if (layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("Sparse linear metering supports only Mono16 and BayerRggb16 layouts.",
                nameof(layout));
        }
        if (pixelData.Length < layout.RequiredByteLength)
        {
            throw new ArgumentException("Pixel buffer is shorter than its declared image layout.", nameof(pixelData));
        }

        ValidateOptions(layout, options, inclusionMask, excludedRegions);
        var region = options.Region ?? new MeteringRegion(0, 0, layout.Width, layout.Height);
        var saturationLevel = options.SaturationLevel ?? options.WhiteLevel;
        var accumulator = new MeteringAccumulator(
            layout, pixelData, inclusionMask, excludedRegions, options, region, saturationLevel);

        if (layout.PixelFormat == CameraPixelFormat.Mono16)
        {
            var right = (long)region.X + region.Width;
            var bottom = (long)region.Y + region.Height;
            for (var y = (long)region.Y; y < bottom; y += options.YStride)
            {
                for (var x = (long)region.X; x < right; x += options.XStride)
                {
                    accumulator.AddSample((int)x, (int)y);
                }
            }
        }
        else
        {
            var right = (long)region.X + region.Width;
            var bottom = (long)region.Y + region.Height;
            var cellStepX = (long)options.XStride;
            var cellStepY = (long)options.YStride;
            for (var cellY = (long)(region.Y & ~1); cellY < bottom; cellY += cellStepY)
            {
                for (var cellX = (long)(region.X & ~1); cellX < right; cellX += cellStepX)
                {
                    if ((options.BayerPhotosites & BayerMeteringPhotosites.Red) != 0)
                    {
                        accumulator.AddSample((int)cellX, (int)cellY);
                    }
                    if ((options.BayerPhotosites & BayerMeteringPhotosites.GreenOnRedRow) != 0)
                    {
                        accumulator.AddSample((int)cellX + 1, (int)cellY);
                    }
                    if ((options.BayerPhotosites & BayerMeteringPhotosites.GreenOnBlueRow) != 0)
                    {
                        accumulator.AddSample((int)cellX, (int)cellY + 1);
                    }
                    if ((options.BayerPhotosites & BayerMeteringPhotosites.Blue) != 0)
                    {
                        accumulator.AddSample((int)cellX + 1, (int)cellY + 1);
                    }
                }
            }
        }

        var normalizedMean = accumulator.Accepted == 0
            ? 0d
            : accumulator.NormalizedSum /
              ((double)accumulator.Accepted * (options.WhiteLevel - options.BlackLevel));
        return new SparseMeteringResult(
            normalizedMean,
            accumulator.Considered,
            accumulator.Accepted,
            accumulator.Saturated,
            accumulator.ScannedBytes);
    }

    private static void ValidateOptions(
        ImageLayout layout,
        SparseMeteringOptions options,
        ReadOnlySpan<byte> inclusionMask,
        ReadOnlySpan<MeteringRegion> excludedRegions)
    {
        if (options.XStride <= 0 || options.YStride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Metering strides must be positive.");
        }
        if (options.WhiteLevel <= options.BlackLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "White level must be greater than black level.");
        }

        var saturationLevel = options.SaturationLevel ?? options.WhiteLevel;
        if (saturationLevel <= options.BlackLevel || saturationLevel > options.WhiteLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Saturation level must be greater than black level and no greater than white level.");
        }

        if (options.Region is { } region &&
            (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
             region.X > layout.Width - region.Width || region.Y > layout.Height - region.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Metering region must fit within the image.");
        }
        if (options.ImageCircle is { } circle &&
            (!double.IsFinite(circle.CenterX) || !double.IsFinite(circle.CenterY) ||
             !double.IsFinite(circle.Radius) || circle.Radius <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Image circle values must be finite with a positive radius.");
        }

        if (layout.PixelFormat == CameraPixelFormat.BayerRggb16 &&
            ((options.XStride & 1) != 0 || (options.YStride & 1) != 0 ||
             options.BayerPhotosites == BayerMeteringPhotosites.None ||
              (options.BayerPhotosites & ~BayerMeteringPhotosites.All) != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Bayer strides must be even and at least one known photosite must be selected.");
        }
        if (!Enum.IsDefined(options.ByteOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sample byte order is invalid.");
        }
        foreach (var excluded in excludedRegions)
        {
            if (excluded.X < 0 || excluded.Y < 0 || excluded.Width <= 0 || excluded.Height <= 0 ||
                excluded.X > layout.Width - excluded.Width || excluded.Y > layout.Height - excluded.Height)
            {
                throw new ArgumentOutOfRangeException(nameof(excludedRegions),
                    "Every excluded metering region must fit within the image.");
            }
        }

        var requiredMaskBytes = ((long)layout.Width * layout.Height + 7) / 8;
        if (!inclusionMask.IsEmpty && inclusionMask.Length < requiredMaskBytes)
        {
            throw new ArgumentException("Inclusion mask is shorter than one bit per logical pixel.",
                nameof(inclusionMask));
        }
    }

    private ref struct MeteringAccumulator
    {
        private readonly ImageLayout _layout;
        private readonly ReadOnlySpan<byte> _pixelData;
        private readonly ReadOnlySpan<byte> _inclusionMask;
        private readonly ReadOnlySpan<MeteringRegion> _excludedRegions;
        private readonly SparseMeteringOptions _options;
        private readonly MeteringRegion _region;
        private readonly ushort _saturationLevel;

        public MeteringAccumulator(
            ImageLayout layout,
            ReadOnlySpan<byte> pixelData,
            ReadOnlySpan<byte> inclusionMask,
            ReadOnlySpan<MeteringRegion> excludedRegions,
            SparseMeteringOptions options,
            MeteringRegion region,
            ushort saturationLevel)
        {
            _layout = layout;
            _pixelData = pixelData;
            _inclusionMask = inclusionMask;
            _excludedRegions = excludedRegions;
            _options = options;
            _region = region;
            _saturationLevel = saturationLevel;
        }

        public ulong NormalizedSum { get; private set; }

        public long Considered { get; private set; }

        public long Accepted { get; private set; }

        public long Saturated { get; private set; }

        public long ScannedBytes { get; private set; }

        public void AddSample(int x, int y)
        {
            if ((uint)(x - _region.X) >= (uint)_region.Width ||
                (uint)(y - _region.Y) >= (uint)_region.Height)
            {
                return;
            }
            if (_options.ImageCircle is { } circle)
            {
                var deltaX = x - circle.CenterX;
                var deltaY = y - circle.CenterY;
                if (deltaX * deltaX + deltaY * deltaY > circle.Radius * circle.Radius)
                {
                    return;
                }
            }
            foreach (var excluded in _excludedRegions)
            {
                if ((uint)(x - excluded.X) < (uint)excluded.Width &&
                    (uint)(y - excluded.Y) < (uint)excluded.Height)
                {
                    return;
                }
            }

            var pixelIndex = y * _layout.Width + x;
            if (!_inclusionMask.IsEmpty &&
                (_inclusionMask[pixelIndex >> 3] & (1 << (pixelIndex & 7))) == 0)
            {
                return;
            }

            var offset = y * _layout.StrideBytes + x * 2;
            var value = _options.ByteOrder == SampleByteOrder.LittleEndian
                ? (ushort)(_pixelData[offset] | _pixelData[offset + 1] << 8)
                : (ushort)(_pixelData[offset] << 8 | _pixelData[offset + 1]);
            Considered++;
            ScannedBytes += 2;
            if (value >= _saturationLevel)
            {
                Saturated++;
                return;
            }

            Accepted++;
            if (value > _options.BlackLevel)
            {
                NormalizedSum += (uint)(value - _options.BlackLevel);
            }
        }
    }
}
