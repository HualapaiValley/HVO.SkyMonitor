using HVO.SkyMonitor.AgentCore;

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
/// Bounded keogram parameters. A time gap larger than <paramref name="MaximumGapSeconds"/> is rendered as a fixed
/// number of patterned columns rather than stretching the neighbouring frames across the missing interval.
/// </summary>
public sealed record KeogramCompositionOptions(
    int? SliceColumn = null,
    double MaximumGapSeconds = KeogramComposer.DefaultMaximumGapSeconds,
    int GapColumnCount = 1,
    int MaximumColumnCount = KeogramComposer.DefaultMaximumColumnCount);

/// <summary>A tightly packed keogram whose columns are time samples and whose rows run north to south.</summary>
public sealed record KeogramResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    int FrameCount,
    int GapCount,
    string AlgorithmVersion);

/// <summary>Composes a north-zenith-south column per frame into a time-axis keogram with explicit gap columns.</summary>
public static class KeogramComposer
{
    public const string AlgorithmVersion = "keogram-slice-v1";
    public const int DefaultMaximumGapSeconds = 300;
    public const int DefaultMaximumColumnCount = 4096;
    public const int MaximumGapColumnCount = 64;

    private const byte PatternLow = 0x20;
    private const byte PatternHigh = 0x60;

    /// <summary>Returns the deterministic output width for the supplied frames and options.</summary>
    public static int ComputeOutputWidth(IReadOnlyList<KeogramFrame> frames, KeogramCompositionOptions options)
    {
        Validate(frames, options, out var gapCount);
        return checked(frames.Count + gapCount * options.GapColumnCount);
    }

    /// <summary>Composes the keogram into a new packed buffer without retaining or mutating the source frames.</summary>
    public static KeogramResult Compose(
        IReadOnlyList<KeogramFrame> frames,
        KeogramCompositionOptions options,
        CancellationToken cancellationToken = default)
    {
        Validate(frames, options, out var gapCount);
        var width = checked(frames.Count + gapCount * options.GapColumnCount);
        var height = frames[0].Height;
        var pixelFormat = frames[0].PixelFormat;
        var bytesPerPixel = ImageLayout.BytesPerPixel(pixelFormat);
        var outputStride = checked(width * bytesPerPixel);
        var output = new byte[checked(outputStride * height)];
        var sliceColumn = options.SliceColumn ?? frames[0].Width / 2;
        var outputColumn = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0 && IsGap(frames[index - 1].TimestampUtc, frames[index].TimestampUtc, options.MaximumGapSeconds))
            {
                for (var gap = 0; gap < options.GapColumnCount; gap++)
                {
                    WritePatternColumn(output, outputStride, height, bytesPerPixel, outputColumn);
                    outputColumn++;
                }
            }

            CopyColumn(frames[index], sliceColumn, output, outputStride, outputColumn, bytesPerPixel);
            outputColumn++;
        }

        return new KeogramResult(
            width, height, outputStride, pixelFormat, output, frames.Count, gapCount, AlgorithmVersion);
    }

    private static bool IsGap(DateTimeOffset previous, DateTimeOffset current, double maximumGapSeconds) =>
        (current - previous).TotalSeconds > maximumGapSeconds;

    private static void CopyColumn(
        KeogramFrame frame,
        int sliceColumn,
        byte[] output,
        int outputStride,
        int outputColumn,
        int bytesPerPixel)
    {
        var source = frame.PixelData.Span;
        var sourceColumn = sliceColumn * bytesPerPixel;
        var destinationColumn = outputColumn * bytesPerPixel;
        for (var y = 0; y < frame.Height; y++)
        {
            var sourceOffset = checked(y * frame.StrideBytes + sourceColumn);
            var destinationOffset = checked(y * outputStride + destinationColumn);
            source.Slice(sourceOffset, bytesPerPixel).CopyTo(output.AsSpan(destinationOffset, bytesPerPixel));
        }
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

    private static void Validate(
        IReadOnlyList<KeogramFrame> frames,
        KeogramCompositionOptions options,
        out int gapCount)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(options);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one source frame is required.", nameof(frames));
        }
        if (options.MaximumGapSeconds <= 0 || !double.IsFinite(options.MaximumGapSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum gap must be a positive finite duration.");
        }
        if (options.GapColumnCount is < 1 or > MaximumGapColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The gap column count is out of range.");
        }
        if (options.MaximumColumnCount is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum column count is out of range.");
        }

        var first = frames[0] ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
        ValidateFrame(first, nameof(frames));
        var width = first.Width;
        var height = first.Height;
        var pixelFormat = first.PixelFormat;
        var sliceColumn = options.SliceColumn ?? width / 2;
        if (sliceColumn < 0 || sliceColumn >= width)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The slice column must lie within the frame width.");
        }

        gapCount = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index]
                ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
            ValidateFrame(frame, nameof(frames));
            if (frame.Width != width || frame.Height != height || frame.PixelFormat != pixelFormat)
            {
                throw new ArgumentException(
                    "All keogram frames must share dimensions and pixel format.", nameof(frames));
            }
            if (index == 0)
            {
                continue;
            }
            if (frame.TimestampUtc < frames[index - 1].TimestampUtc)
            {
                throw new ArgumentException("Keogram frames must be in non-decreasing time order.", nameof(frames));
            }
            if (IsGap(frames[index - 1].TimestampUtc, frame.TimestampUtc, options.MaximumGapSeconds))
            {
                gapCount++;
            }
        }

        if (checked(frames.Count + gapCount * options.GapColumnCount) > options.MaximumColumnCount)
        {
            throw new ArgumentException("The keogram column count exceeds the configured bound.", nameof(frames));
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
}
