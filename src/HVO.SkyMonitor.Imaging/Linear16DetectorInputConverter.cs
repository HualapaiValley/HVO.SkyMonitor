using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Identifies whether detector pixels borrow caller memory or are owned by the conversion result.</summary>
public enum Linear16DetectorInputOwnership
{
    /// <summary>The caller owns the memory and must keep it alive and unchanged while the result is in use.</summary>
    Borrowed,

    /// <summary>The result owns a newly allocated managed buffer whose lifetime is controlled by the result reference.</summary>
    Owned
}

/// <summary>
/// Maps source pixel-edge coordinates to output pixel-edge coordinates using
/// <c>output = source * scale + offset</c> independently on each axis.
/// </summary>
public readonly record struct Linear16SourceToOutputTransform(
    string Version,
    double ScaleX,
    double ScaleY,
    double OffsetX,
    double OffsetY);

/// <summary>A validated little-endian Mono16 detector input and its conversion evidence.</summary>
/// <remarks>
/// Borrowed memory remains owned by the caller and is valid only while the caller keeps it alive and unchanged.
/// Owned memory is retained by this result and requires no disposal. The result exposes read-only memory and is safe
/// for concurrent readers provided that borrowed source memory is not mutated concurrently.
/// </remarks>
public sealed record Linear16DetectorInputResult(
    ImageLayout Layout,
    ReadOnlyMemory<byte> PixelData,
    Linear16DetectorInputOwnership Ownership,
    string AlgorithmVersion,
    long BytesScanned,
    long BytesCopied,
    Linear16SourceToOutputTransform SourceToOutputTransform);

/// <summary>Creates canonical little-endian Mono16 inputs for detector algorithms.</summary>
/// <remarks>
/// This type is stateless and thread-safe. Mono16 conversion borrows the exact caller buffer without scanning or
/// copying it. RGGB16 conversion reads each logical source sample once and returns one bounded managed allocation.
/// Source buffers are never modified or retained beyond a borrowed Mono16 result.
/// </remarks>
public static class Linear16DetectorInputConverter
{
    private const int CancellationCheckBlockSize = 1024;

    public const string Mono16AlgorithmVersion = "linear16-detector-mono16-borrow-v1";
    public const string Rggb16AlgorithmVersion = "linear16-detector-rggb16-cell-average-v1";
    public const string TransformVersion = "linear16-detector-source-to-output-affine-v1";

    /// <summary>
    /// Validates and converts a little-endian Mono16 or RGGB16 source. Mono16 preserves its layout and memory;
    /// RGGB16 emits a tightly packed half-resolution Mono16 image using rounded <c>(R + G1 + G2 + B) / 4</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The format, byte order, dimensions, stride, or exact source-buffer length is unsupported.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The image layout is non-positive or overflows.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested before or during conversion.</exception>
    public static Linear16DetectorInputResult Convert(
        ImageLayout sourceLayout,
        FrameByteOrder sourceByteOrder,
        ReadOnlyMemory<byte> sourcePixels,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException(
                "Detector input conversion supports only Mono16 and BayerRggb16 sources.",
                nameof(sourceLayout));
        }
        if (sourceByteOrder != FrameByteOrder.LittleEndian)
        {
            throw new ArgumentException(
                "Detector input conversion supports only little-endian 16-bit sources.",
                nameof(sourceByteOrder));
        }

        sourceLayout.Validate();
        ImageBuffer.Validate(sourceLayout, sourcePixels);
        if (sourceLayout.PixelFormat == CameraPixelFormat.Mono16)
        {
            return new Linear16DetectorInputResult(
                sourceLayout,
                sourcePixels,
                Linear16DetectorInputOwnership.Borrowed,
                Mono16AlgorithmVersion,
                0,
                0,
                new Linear16SourceToOutputTransform(TransformVersion, 1, 1, 0, 0));
        }
        if ((sourceLayout.Width & 1) != 0 || (sourceLayout.Height & 1) != 0)
        {
            throw new ArgumentException(
                "BayerRggb16 detector conversion requires even width and height.",
                nameof(sourceLayout));
        }

        var outputWidth = sourceLayout.Width / 2;
        var outputHeight = sourceLayout.Height / 2;
        var outputStride = checked(outputWidth * 2);
        var output = GC.AllocateUninitializedArray<byte>(checked(outputStride * outputHeight));
        var source = sourcePixels.Span;
        for (var outputY = 0; outputY < outputHeight; outputY++)
        {
            var redRow = outputY * 2 * sourceLayout.StrideBytes;
            var blueRow = redRow + sourceLayout.StrideBytes;
            var outputRow = outputY * outputStride;
            for (var blockStart = 0; blockStart < outputWidth; blockStart += CancellationCheckBlockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blockEnd = Math.Min(blockStart + CancellationCheckBlockSize, outputWidth);
                for (var outputX = blockStart; outputX < blockEnd; outputX++)
                {
                    var sourceOffset = outputX * 4;
                    var red = ReadLittleEndian(source, redRow + sourceOffset);
                    var greenOnRedRow = ReadLittleEndian(source, redRow + sourceOffset + 2);
                    var greenOnBlueRow = ReadLittleEndian(source, blueRow + sourceOffset);
                    var blue = ReadLittleEndian(source, blueRow + sourceOffset + 2);
                    var luminance = (ushort)(((uint)red + greenOnRedRow + greenOnBlueRow + blue + 2) / 4);
                    var outputOffset = outputRow + outputX * 2;
                    output[outputOffset] = (byte)luminance;
                    output[outputOffset + 1] = (byte)(luminance >> 8);
                }
            }
        }

        var outputLayout = new ImageLayout(
            outputWidth,
            outputHeight,
            CameraPixelFormat.Mono16,
            outputStride);
        cancellationToken.ThrowIfCancellationRequested();
        return new Linear16DetectorInputResult(
            outputLayout,
            output,
            Linear16DetectorInputOwnership.Owned,
            Rggb16AlgorithmVersion,
            checked((long)sourceLayout.Width * sourceLayout.Height * 2),
            output.LongLength,
            new Linear16SourceToOutputTransform(TransformVersion, 0.5, 0.5, 0, 0));
    }

    private static ushort ReadLittleEndian(ReadOnlySpan<byte> source, int offset)
        => (ushort)(source[offset] | source[offset + 1] << 8);
}
