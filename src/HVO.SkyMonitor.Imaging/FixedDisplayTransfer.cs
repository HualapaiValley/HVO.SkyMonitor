using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One fixed native-sample transfer shared by every frame of a product.</summary>
public sealed record FixedDisplayTransferOptions(double BlackLevel = 64, double WhiteLevel = 4095, double Gamma = 2.2)
{
    public void Validate()
    {
        if (!double.IsFinite(BlackLevel) || !double.IsFinite(WhiteLevel) || !double.IsFinite(Gamma) ||
            BlackLevel < 0 || WhiteLevel <= BlackLevel || WhiteLevel > ushort.MaxValue || Gamma is < 0.1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(WhiteLevel));
        }
    }
}

/// <summary>Display conversion without frame-dependent normalization; clipping remains visible.</summary>
public static class FixedDisplayTransfer
{
    public const string AlgorithmVersion = "fixed-native-levels-gamma-v1";

    public static byte[] Apply(FrameLayoutDescriptor layout, ReadOnlyMemory<byte> pixels,
        FixedDisplayTransferOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (layout.Width <= 0 || layout.Height <= 0 || pixels.Length != layout.ByteLength ||
            layout.StrideBytes < layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat) ||
            layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 or CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            (layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 && layout.ByteOrder != FrameByteOrder.LittleEndian))
        {
            throw new ArgumentException("Fixed display conversion requires a valid little-endian 16-bit frame.", nameof(layout));
        }
        if (layout.PixelFormat is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24)
        {
            var channels = ImageLayout.BytesPerPixel(layout.PixelFormat);
            var display = new byte[checked(layout.Width * layout.Height * channels)];
            for (var row = 0; row < layout.Height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var column = 0; column < layout.Width * channels; column++)
                    display[row * layout.Width * channels + column] = Transfer(pixels.Span[row * layout.StrideBytes + column] - options.BlackLevel, options);
            }
            return display;
        }
        var values = new double[checked(layout.Width * layout.Height)];
        for (var row = 0; row < layout.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < layout.Width; column++)
            {
                values[row * layout.Width + column] = BinaryPrimitives.ReadUInt16LittleEndian(
                    pixels.Span.Slice(row * layout.StrideBytes + column * 2, 2)) - options.BlackLevel;
            }
        }
        if (layout.PixelFormat == CameraPixelFormat.Mono16)
        {
            var mono = new byte[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                mono[index] = Transfer(values[index], options);
            }
            return mono;
        }
        var valid = Enumerable.Repeat(true, values.Length).ToArray();
        var linear = LinearBayerReconstruction.Reconstruct(values, valid, layout.Width, layout.Height,
            cancellationToken: cancellationToken);
        var rgb = new byte[checked(values.Length * 3)];
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            rgb[index * 3] = Transfer(linear.Red.Span[index], options);
            rgb[index * 3 + 1] = Transfer(linear.Green.Span[index], options);
            rgb[index * 3 + 2] = Transfer(linear.Blue.Span[index], options);
        }
        return rgb;
    }

    private static byte Transfer(double value, FixedDisplayTransferOptions options) =>
        (byte)Math.Round(255 * Math.Pow(Math.Clamp(value / (options.WhiteLevel - options.BlackLevel), 0, 1), 1 / options.Gamma));
}
