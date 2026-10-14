using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>An explicitly laid-out linear 16-bit source frame.</summary>
public sealed record Linear16Frame(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData);

/// <summary>A tightly packed linear 16-bit arithmetic mean.</summary>
public sealed record Linear16MeanResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    int SourceCount,
    string AlgorithmVersion);

/// <summary>Computes a stateless arithmetic mean over compatible linear 16-bit frames.</summary>
public static class Linear16ArithmeticMean
{
    public const string AlgorithmVersion = "linear16-arithmetic-mean-v1";

    /// <summary>Computes a packed little-endian mean without retaining source buffers.</summary>
    public static Linear16MeanResult Compute(
        IReadOnlyList<Linear16Frame> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        cancellationToken.ThrowIfCancellationRequested();
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one source frame is required.", nameof(frames));
        }

        var first = frames[0] ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var frame = frames[frameIndex]
                ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
            ValidateFrame(frame, nameof(frames));
            if (frame.Width != first.Width || frame.Height != first.Height || frame.PixelFormat != first.PixelFormat)
            {
                throw new ArgumentException("All source frames must have compatible dimensions and pixel format.", nameof(frames));
            }
        }

        // Rows are summed across frames into one row-sized accumulator instead of a full-frame accumulator, so the
        // only frame-sized allocation is the output. A 32-bit total holds up to 65537 frames of 65535 exactly, and
        // its truncating division is the same quotient as the 64-bit one.
        var output = new byte[checked(first.Width * first.Height * 2)];
        if (frames.Count <= NarrowAccumulatorMaximumFrames && BitConverter.IsLittleEndian)
        {
            ComputeNarrow(frames, first.Width, first.Height, output, cancellationToken);
        }
        else
        {
            ComputeWide(frames, first.Width, first.Height, output, cancellationToken);
        }

        return new Linear16MeanResult(
            first.Width,
            first.Height,
            checked(first.Width * 2),
            first.PixelFormat,
            output,
            frames.Count,
            AlgorithmVersion);
    }

    private const int NarrowAccumulatorMaximumFrames = 65537;

    private static void ComputeNarrow(
        IReadOnlyList<Linear16Frame> frames,
        int width,
        int height,
        byte[] output,
        CancellationToken cancellationToken)
    {
        var count = (uint)frames.Count;
        var rented = ArrayPool<uint>.Shared.Rent(width);
        try
        {
            var totals = rented.AsSpan(0, width);
            var destination = MemoryMarshal.Cast<byte, ushort>(output.AsSpan());
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totals.Clear();
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    AccumulateRow(
                        MemoryMarshal.Cast<byte, ushort>(frame.PixelData.Span.Slice(y * frame.StrideBytes, width * 2)),
                        totals);
                }

                var row = destination.Slice(y * width, width);
                for (var x = 0; x < width; x++)
                {
                    row[x] = (ushort)(totals[x] / count);
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rented);
        }
    }

    private static void AccumulateRow(ReadOnlySpan<ushort> source, Span<uint> totals)
    {
        var x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x <= source.Length - Vector<ushort>.Count; x += Vector<ushort>.Count)
            {
                Vector.Widen(new Vector<ushort>(source[x..]), out var low, out var high);
                var lowTotals = totals.Slice(x, Vector<uint>.Count);
                (new Vector<uint>(lowTotals) + low).CopyTo(lowTotals);
                var highTotals = totals.Slice(x + Vector<uint>.Count, Vector<uint>.Count);
                (new Vector<uint>(highTotals) + high).CopyTo(highTotals);
            }
        }

        for (; x < source.Length; x++)
        {
            totals[x] += source[x];
        }
    }

    private static void ComputeWide(
        IReadOnlyList<Linear16Frame> frames,
        int width,
        int height,
        byte[] output,
        CancellationToken cancellationToken)
    {
        var count = (ulong)frames.Count;
        var rented = ArrayPool<ulong>.Shared.Rent(width);
        try
        {
            var totals = rented.AsSpan(0, width);
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                totals.Clear();
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    var frame = frames[frameIndex];
                    var source = frame.PixelData.Span.Slice(y * frame.StrideBytes, width * 2);
                    for (var x = 0; x < width; x++)
                    {
                        totals[x] += (ushort)(source[x * 2] | source[x * 2 + 1] << 8);
                    }
                }

                var row = output.AsSpan(y * width * 2, width * 2);
                for (var x = 0; x < width; x++)
                {
                    var average = (ushort)(totals[x] / count);
                    row[x * 2] = (byte)average;
                    row[x * 2 + 1] = (byte)(average >> 8);
                }
            }
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(rented);
        }
    }

    private static void ValidateFrame(Linear16Frame frame, string parameterName)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("Linear arithmetic mean supports only Mono16 and BayerRggb16 frames.", parameterName);
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
