using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One timestamped display frame sampled into a single keogram column.</summary>
public sealed record KeogramFrame(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Bounded keogram parameters. <paramref name="SamplePath"/> holds one continuous pixel-edge coordinate per output row,
/// normally the projected north-zenith-south meridian; a <see langword="null"/> entry is a direction the rig does not
/// image and renders black. A time gap larger than <paramref name="MaximumGapSeconds"/> is rendered as patterned
/// columns proportional to the missing time at the observed cadence, bounded by
/// <paramref name="MaximumGapColumnCount"/> per gap.
/// </summary>
public sealed record KeogramCompositionOptions(
    IReadOnlyList<PixelPoint?> SamplePath,
    double MaximumGapSeconds = KeogramComposer.DefaultMaximumGapSeconds,
    int MaximumGapColumnCount = KeogramComposer.DefaultMaximumGapColumnCount,
    int MaximumColumnCount = KeogramComposer.DefaultMaximumColumnCount);

/// <summary>A rendered capture gap between two consecutive source frames.</summary>
public sealed record KeogramGap(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int FirstColumn,
    int ColumnCount);

/// <summary>The output column layout: one column per frame plus every rendered gap.</summary>
public sealed record KeogramTimeAxis(int Width, double CadenceSeconds, IReadOnlyList<KeogramGap> Gaps);

/// <summary>A tightly packed keogram whose columns are time samples and whose rows follow the sample path.</summary>
public sealed record KeogramResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    int FrameCount,
    IReadOnlyList<KeogramGap> Gaps,
    double CadenceSeconds,
    int MappedRowCount,
    string AlgorithmVersion)
{
    public int GapCount => Gaps.Count;
}

/// <summary>Samples a sky path from each frame into a time-axis keogram with explicit, proportional gap columns.</summary>
public static class KeogramComposer
{
    public const string AlgorithmVersion = "keogram-path-bilinear-v1";
    public const int DefaultMaximumGapSeconds = 300;
    public const int DefaultMaximumColumnCount = 4096;
    public const int DefaultMaximumGapColumnCount = 1024;
    public const int MaximumColumnLimit = 65536;
    public const int MinimumPathLength = 2;
    public const int MaximumPathLength = 8192;

    private const byte PatternLow = 0x20;
    private const byte PatternHigh = 0x60;

    /// <summary>Returns the deterministic output width for the supplied frames and options.</summary>
    public static int ComputeOutputWidth(IReadOnlyList<KeogramFrame> frames, KeogramCompositionOptions options) =>
        ComputeTimeAxis(frames, options).Width;

    /// <summary>Composes the keogram into a new packed buffer without retaining or mutating the source frames.</summary>
    public static KeogramResult Compose(
        IReadOnlyList<KeogramFrame> frames,
        KeogramCompositionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = ComputeTimeAxis(frames, options);
        var height = options.SamplePath.Count;
        var pixelFormat = frames[0].PixelFormat;
        var bytesPerPixel = ImageLayout.BytesPerPixel(pixelFormat);
        var outputStride = checked(plan.Width * bytesPerPixel);
        var output = new byte[checked(outputStride * height)];
        var samples = CreateSamples(options.SamplePath, frames[0].Width, frames[0].Height);

        var gapIndex = 0;
        var outputColumn = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gapIndex < plan.Gaps.Count && plan.Gaps[gapIndex].FirstColumn == outputColumn)
            {
                for (var gap = 0; gap < plan.Gaps[gapIndex].ColumnCount; gap++)
                {
                    WritePatternColumn(output, outputStride, height, bytesPerPixel, outputColumn);
                    outputColumn++;
                }
                gapIndex++;
            }

            SampleColumn(frames[index], samples, output, outputStride, outputColumn, bytesPerPixel);
            outputColumn++;
        }

        return new KeogramResult(
            plan.Width,
            height,
            outputStride,
            pixelFormat,
            output,
            frames.Count,
            plan.Gaps,
            plan.CadenceSeconds,
            samples.Count(static sample => sample.Mapped),
            AlgorithmVersion);
    }

    /// <summary>
    /// Returns the observed capture cadence: the median positive interval no longer than the gap threshold, or the
    /// threshold itself when no such interval exists.
    /// </summary>
    public static double ComputeCadenceSeconds(IReadOnlyList<DateTimeOffset> timestamps, double maximumGapSeconds)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        var intervals = new List<double>(Math.Max(0, timestamps.Count - 1));
        for (var index = 1; index < timestamps.Count; index++)
        {
            var seconds = (timestamps[index] - timestamps[index - 1]).TotalSeconds;
            if (seconds > 0 && seconds <= maximumGapSeconds)
            {
                intervals.Add(seconds);
            }
        }
        if (intervals.Count == 0)
        {
            return maximumGapSeconds;
        }

        intervals.Sort();
        var middle = intervals.Count / 2;
        return intervals.Count % 2 == 1 ? intervals[middle] : (intervals[middle - 1] + intervals[middle]) / 2d;
    }

    /// <summary>Returns the patterned column count representing <paramref name="gapSeconds"/> at the cadence.</summary>
    public static int ComputeGapColumnCount(double gapSeconds, double cadenceSeconds, int maximumGapColumnCount)
    {
        var missing = Math.Round(gapSeconds / cadenceSeconds, MidpointRounding.AwayFromZero) - 1;
        return (int)Math.Clamp(missing, 1, maximumGapColumnCount);
    }

    /// <summary>Validates the inputs and returns the deterministic column layout of the time axis.</summary>
    public static KeogramTimeAxis ComputeTimeAxis(IReadOnlyList<KeogramFrame> frames, KeogramCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SamplePath);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one source frame is required.", nameof(frames));
        }
        if (options.MaximumGapSeconds <= 0 || !double.IsFinite(options.MaximumGapSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum gap must be a positive finite duration.");
        }
        if (options.MaximumColumnCount is < 1 or > MaximumColumnLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum column count is out of range.");
        }
        if (options.MaximumGapColumnCount < 1 || options.MaximumGapColumnCount > options.MaximumColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum gap column count is out of range.");
        }
        if (options.SamplePath.Count is < MinimumPathLength or > MaximumPathLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The sample path length is out of range.");
        }

        var first = frames[0] ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
        ValidateFrame(first, nameof(frames));
        foreach (var point in options.SamplePath)
        {
            if (point is { } pixel &&
                (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y) ||
                 pixel.X < 0 || pixel.X > first.Width || pixel.Y < 0 || pixel.Y > first.Height))
            {
                throw new ArgumentException("Every mapped sample must lie within the frame bounds.", nameof(options));
            }
        }

        var timestamps = new DateTimeOffset[frames.Count];
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index]
                ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
            ValidateFrame(frame, nameof(frames));
            if (frame.Width != first.Width || frame.Height != first.Height || frame.PixelFormat != first.PixelFormat)
            {
                throw new ArgumentException(
                    "All keogram frames must share dimensions and pixel format.", nameof(frames));
            }
            if (index > 0 && frame.TimestampUtc < timestamps[index - 1])
            {
                throw new ArgumentException("Keogram frames must be in non-decreasing time order.", nameof(frames));
            }
            timestamps[index] = frame.TimestampUtc;
        }

        var cadence = ComputeCadenceSeconds(timestamps, options.MaximumGapSeconds);
        var gaps = new List<KeogramGap>();
        var width = 0;
        for (var index = 0; index < timestamps.Length; index++)
        {
            if (index > 0)
            {
                var seconds = (timestamps[index] - timestamps[index - 1]).TotalSeconds;
                if (seconds > options.MaximumGapSeconds)
                {
                    var columns = ComputeGapColumnCount(seconds, cadence, options.MaximumGapColumnCount);
                    gaps.Add(new KeogramGap(timestamps[index - 1], timestamps[index], width, columns));
                    width = checked(width + columns);
                }
            }
            width = checked(width + 1);
            if (width > options.MaximumColumnCount)
            {
                throw new ArgumentException("The keogram column count exceeds the configured bound.", nameof(frames));
            }
        }

        return new KeogramTimeAxis(width, cadence, gaps);
    }

    private static PathSample[] CreateSamples(IReadOnlyList<PixelPoint?> path, int width, int height)
    {
        var samples = new PathSample[path.Count];
        for (var row = 0; row < path.Count; row++)
        {
            if (path[row] is not { } pixel)
            {
                continue;
            }

            // Pixel-edge coordinates place sample centres at +0.5; clamp the bilinear footprint to the frame.
            var x = Math.Clamp(pixel.X - 0.5, 0, width - 1);
            var y = Math.Clamp(pixel.Y - 0.5, 0, height - 1);
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            samples[row] = new PathSample(
                true,
                x0,
                y0,
                Math.Min(x0 + 1, width - 1),
                Math.Min(y0 + 1, height - 1),
                x - x0,
                y - y0);
        }
        return samples;
    }

    private static void SampleColumn(
        KeogramFrame frame,
        PathSample[] samples,
        byte[] output,
        int outputStride,
        int outputColumn,
        int bytesPerPixel)
    {
        var source = frame.PixelData.Span;
        var destinationColumn = outputColumn * bytesPerPixel;
        for (var row = 0; row < samples.Length; row++)
        {
            var sample = samples[row];
            if (!sample.Mapped)
            {
                continue;
            }

            var destination = checked(row * outputStride + destinationColumn);
            if (frame.PixelFormat == CameraPixelFormat.Mono16)
            {
                var value = Interpolate(
                    ReadMono16(source, frame.StrideBytes, sample.X0, sample.Y0),
                    ReadMono16(source, frame.StrideBytes, sample.X1, sample.Y0),
                    ReadMono16(source, frame.StrideBytes, sample.X0, sample.Y1),
                    ReadMono16(source, frame.StrideBytes, sample.X1, sample.Y1),
                    sample);
                var rounded = (ushort)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
                output[destination] = (byte)rounded;
                output[destination + 1] = (byte)(rounded >> 8);
                continue;
            }

            for (var channel = 0; channel < bytesPerPixel; channel++)
            {
                var value = Interpolate(
                    source[sample.Y0 * frame.StrideBytes + sample.X0 * bytesPerPixel + channel],
                    source[sample.Y0 * frame.StrideBytes + sample.X1 * bytesPerPixel + channel],
                    source[sample.Y1 * frame.StrideBytes + sample.X0 * bytesPerPixel + channel],
                    source[sample.Y1 * frame.StrideBytes + sample.X1 * bytesPerPixel + channel],
                    sample);
                output[destination + channel] = (byte)Math.Clamp(
                    Math.Round(value, MidpointRounding.AwayFromZero), 0, byte.MaxValue);
            }
        }
    }

    private static double Interpolate(double topLeft, double topRight, double bottomLeft, double bottomRight, PathSample sample)
    {
        var top = topLeft + (topRight - topLeft) * sample.FractionX;
        var bottom = bottomLeft + (bottomRight - bottomLeft) * sample.FractionX;
        return top + (bottom - top) * sample.FractionY;
    }

    private static ushort ReadMono16(ReadOnlySpan<byte> source, int stride, int x, int y)
    {
        var offset = y * stride + x * 2;
        return (ushort)(source[offset] | source[offset + 1] << 8);
    }

    private static void WritePatternColumn(
        byte[] output,
        int outputStride,
        int height,
        int bytesPerPixel,
        int outputColumn)
    {
        var destinationColumn = outputColumn * bytesPerPixel;
        for (var y = 0; y < height; y++)
        {
            var value = ((outputColumn + y) & 1) == 0 ? PatternLow : PatternHigh;
            var destinationOffset = checked(y * outputStride + destinationColumn);
            output.AsSpan(destinationOffset, bytesPerPixel).Fill(value);
        }
    }

    private static void ValidateFrame(KeogramFrame frame, string parameterName)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or CameraPixelFormat.Rgb24))
        {
            throw new ArgumentException(
                "Keogram composition supports Mono8, Mono16, and RGB24 frames.", parameterName);
        }
        if (frame.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Source frame timestamps must be UTC.", parameterName);
        }

        var layout = new ImageLayout(frame.Width, frame.Height, frame.PixelFormat, frame.StrideBytes);
        try
        {
            layout.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Source frame layout is invalid.", parameterName, exception);
        }
        if (frame.PixelData.Length < layout.RequiredByteLength)
        {
            throw new ArgumentException("Source frame buffer is shorter than its declared layout.", parameterName);
        }
    }

    private readonly record struct PathSample(
        bool Mapped,
        int X0,
        int Y0,
        int X1,
        int Y1,
        double FractionX,
        double FractionY);
}
