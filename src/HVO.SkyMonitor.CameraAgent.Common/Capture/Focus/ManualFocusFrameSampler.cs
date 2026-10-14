using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

/// <summary>Linear luminance samples of a rectangular window, in exact source-frame pixel coordinates.</summary>
internal sealed record ManualFocusFrameWindow(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    double[] Pixels,
    bool[] ValidMask,
    bool[] SaturatedMask);

/// <summary>
/// A binned display overview. One overview pixel covers <see cref="BinFactor"/> source pixels per axis.
/// <see cref="SkyMask"/> marks the pixels whose cell centre lies inside the image circle (all of them without one).
/// </summary>
internal sealed record ManualFocusFrameOverview(int Width, int Height, int BinFactor, double[] Pixels, bool[] SkyMask);

/// <summary>
/// Reads stored camera codes into linear luminance for focus measurement. Values are stored codes (linear in signal for
/// every supported layout), not calibrated photometry, so only background-subtracted shape metrics are meaningful.
/// Saturation is the declared white level in stored-code space, or the full container range when none is declared.
/// RGGB mosaics are reconstructed with <see cref="LinearBayerReconstruction"/> on an even-aligned padded window, so the
/// reconstruction border never reaches the returned samples and coordinates stay exact.
/// </summary>
internal static class ManualFocusFrameSampler
{
    public const string AlgorithmVersion = "manual-focus-frame-sampler-v1";

    public static bool IsSupported(CameraPixelFormat format)
        => format is CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or CameraPixelFormat.Rgb24
            or CameraPixelFormat.BayerRggb16;

    public static ManualFocusFrameWindow ExtractWindow(
        CameraFrame frame,
        int x,
        int y,
        int width,
        int height,
        CancellationToken cancellationToken,
        MeteringImageCircle? imageCircle = null)
    {
        var reader = FrameReader.Create(frame);
        var x0 = Math.Clamp(x, 0, frame.Width - 1);
        var y0 = Math.Clamp(y, 0, frame.Height - 1);
        var x1 = Math.Clamp(x + width, x0 + 1, frame.Width);
        var y1 = Math.Clamp(y + height, y0 + 1, frame.Height);
        var window = frame.PixelFormat == CameraPixelFormat.BayerRggb16
            ? ExtractBayerWindow(reader, x0, y0, x1, y1, cancellationToken)
            : ExtractDirectWindow(reader, x0, y0, x1, y1, cancellationToken);
        if (imageCircle is { } circle)
        {
            // Same sample-index convention as metering: a sample is sky only when its index lies inside the circle.
            var limit = circle.Radius * circle.Radius;
            for (var row = 0; row < window.Height; row++)
            {
                var dy = window.OriginY + row - circle.CenterY;
                for (var column = 0; column < window.Width; column++)
                {
                    var dx = window.OriginX + column - circle.CenterX;
                    if (dx * dx + dy * dy > limit)
                    {
                        window.ValidMask[row * window.Width + column] = false;
                    }
                }
            }
        }
        return window;
    }

    public static ManualFocusFrameOverview CreateOverview(
        CameraFrame frame,
        int maximumDimension,
        CancellationToken cancellationToken,
        MeteringImageCircle? imageCircle = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDimension, 16);
        var reader = FrameReader.Create(frame);
        var bin = Math.Max(1, (int)Math.Ceiling(Math.Max(frame.Width, frame.Height) / (double)maximumDimension));
        if (frame.PixelFormat == CameraPixelFormat.BayerRggb16 && (bin & 1) != 0)
        {
            bin++; // Whole 2x2 cells sum every channel, so the overview carries no mosaic pattern.
        }
        var width = (frame.Width + bin - 1) / bin;
        var height = (frame.Height + bin - 1) / bin;
        var pixels = new double[checked(width * height)];
        var sky = new bool[pixels.Length];
        var limit = imageCircle is { } bounds ? bounds.Radius * bounds.Radius : double.PositiveInfinity;
        for (var oy = 0; oy < height; oy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sy0 = oy * bin;
            var sy1 = Math.Min(frame.Height, sy0 + bin);
            for (var ox = 0; ox < width; ox++)
            {
                var sx0 = ox * bin;
                var sx1 = Math.Min(frame.Width, sx0 + bin);
                // Same sample-index convention as ExtractWindow, applied at the cell centre.
                var dx = (sx0 + sx1 - 1) / 2d - (imageCircle?.CenterX ?? 0);
                var dy = (sy0 + sy1 - 1) / 2d - (imageCircle?.CenterY ?? 0);
                sky[oy * width + ox] = dx * dx + dy * dy <= limit;
                double sum = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        sum += reader.Luminance(sx, sy);
                    }
                }
                pixels[oy * width + ox] = sum / ((sx1 - sx0) * (sy1 - sy0));
            }
        }
        return new(width, height, bin, pixels, sky);
    }

    private static ManualFocusFrameWindow ExtractDirectWindow(
        FrameReader reader, int x0, int y0, int x1, int y1, CancellationToken cancellationToken)
    {
        var width = x1 - x0;
        var height = y1 - y0;
        var pixels = new double[checked(width * height)];
        var valid = new bool[pixels.Length];
        var saturated = new bool[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                pixels[index] = reader.Luminance(x0 + x, y0 + y);
                valid[index] = true;
                saturated[index] = reader.IsSaturated(x0 + x, y0 + y);
            }
        }
        return new(x0, y0, width, height, pixels, valid, saturated);
    }

    private static ManualFocusFrameWindow ExtractBayerWindow(
        FrameReader reader, int x0, int y0, int x1, int y1, CancellationToken cancellationToken)
    {
        // Pad by two photosites and align to the RGGB cell so the declared phase holds at the padded origin.
        var px0 = Math.Max(0, (x0 - 2) & ~1);
        var py0 = Math.Max(0, (y0 - 2) & ~1);
        var px1 = Math.Min(reader.Width, x1 + 2);
        var py1 = Math.Min(reader.Height, y1 + 2);
        var paddedWidth = px1 - px0;
        var paddedHeight = py1 - py0;
        var raw = new double[checked(paddedWidth * paddedHeight)];
        var rawValid = new bool[raw.Length];
        var rawSaturated = new bool[raw.Length];
        for (var y = 0; y < paddedHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < paddedWidth; x++)
            {
                var index = y * paddedWidth + x;
                raw[index] = reader.Raw(px0 + x, py0 + y);
                rawValid[index] = true;
                rawSaturated[index] = reader.IsSaturated(px0 + x, py0 + y);
            }
        }
        var luminance = LinearBayerReconstruction.Reconstruct(
            raw, rawValid, paddedWidth, paddedHeight, BayerPattern.Rggb, cancellationToken).ToLuminance(cancellationToken);
        var width = x1 - x0;
        var height = y1 - y0;
        var pixels = new double[checked(width * height)];
        var valid = new bool[pixels.Length];
        var saturated = new bool[pixels.Length];
        var sourcePixels = luminance.Pixels.Span;
        var sourceValid = luminance.ValidMask.Span;
        for (var y = 0; y < height; y++)
        {
            var py = y0 + y - py0;
            for (var x = 0; x < width; x++)
            {
                var px = x0 + x - px0;
                var index = y * width + x;
                var source = py * paddedWidth + px;
                pixels[index] = sourcePixels[source];
                valid[index] = sourceValid[source];
                // Any clipped photosite feeding the bilinear 3x3 neighbourhood clips the reconstructed sample.
                var clipped = false;
                for (var dy = -1; dy <= 1 && !clipped; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = px + dx;
                        var ny = py + dy;
                        if (nx >= 0 && ny >= 0 && nx < paddedWidth && ny < paddedHeight &&
                            rawSaturated[ny * paddedWidth + nx])
                        {
                            clipped = true;
                            break;
                        }
                    }
                }
                saturated[index] = clipped;
            }
        }
        return new(x0, y0, width, height, pixels, valid, saturated);
    }

    private sealed class FrameReader
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly int _stride;
        private readonly CameraPixelFormat _format;
        private readonly bool _bigEndian;
        private readonly double _saturationCode;

        private FrameReader(CameraFrame frame, int stride, bool bigEndian, double saturationCode)
        {
            _data = frame.PixelData;
            _stride = stride;
            _format = frame.PixelFormat;
            _bigEndian = bigEndian;
            _saturationCode = saturationCode;
            Width = frame.Width;
            Height = frame.Height;
        }

        public int Width { get; }
        public int Height { get; }

        public static FrameReader Create(CameraFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (!IsSupported(frame.PixelFormat))
            {
                throw new NotSupportedException($"Focus preview does not support {frame.PixelFormat} frames.");
            }
            if (frame.Width < 1 || frame.Height < 1)
            {
                throw new InvalidDataException("The preview frame dimensions are invalid.");
            }
            var bytesPerPixel = ImageLayout.BytesPerPixel(frame.PixelFormat);
            var stride = frame.StrideBytes ?? frame.Layout?.StrideBytes ?? checked(frame.Width * bytesPerPixel);
            if (stride < frame.Width * bytesPerPixel ||
                frame.PixelData.Length < (long)stride * (frame.Height - 1) + (long)frame.Width * bytesPerPixel)
            {
                throw new InvalidDataException("The preview frame payload is shorter than its declared layout.");
            }
            if (frame.Layout is { } declared &&
                (declared.Width != frame.Width || declared.Height != frame.Height || declared.PixelFormat != frame.PixelFormat))
            {
                throw new InvalidDataException("The preview frame layout does not match the frame.");
            }
            var bigEndian = frame.Layout?.ByteOrder == FrameByteOrder.BigEndian;
            return new(frame, stride, bigEndian, SaturationCode(frame));
        }

        public double Raw(int x, int y)
        {
            var span = _data.Span;
            return _format switch
            {
                CameraPixelFormat.Mono8 => span[y * _stride + x],
                CameraPixelFormat.Rgb24 => Luminance(x, y),
                _ => _bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(span.Slice(y * _stride + x * 2, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(y * _stride + x * 2, 2))
            };
        }

        public double Luminance(int x, int y)
        {
            if (_format != CameraPixelFormat.Rgb24)
            {
                return Raw(x, y);
            }
            var span = _data.Span;
            var offset = y * _stride + x * 3;
            return 0.2126 * span[offset] + 0.7152 * span[offset + 1] + 0.0722 * span[offset + 2];
        }

        public bool IsSaturated(int x, int y)
        {
            if (_format != CameraPixelFormat.Rgb24)
            {
                return Raw(x, y) >= _saturationCode;
            }
            var span = _data.Span;
            var offset = y * _stride + x * 3;
            return span[offset] >= _saturationCode || span[offset + 1] >= _saturationCode || span[offset + 2] >= _saturationCode;
        }

        private static double SaturationCode(CameraFrame frame)
        {
            var layout = frame.Layout;
            var container = layout?.ContainerDepthBits ?? (frame.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 ? 16 : 8);
            var sample = layout?.SampleDepthBits ?? container;
            var containerMaximum = Math.Pow(2, container) - 1;
            var sampleMaximum = Math.Pow(2, sample) - 1;
            if (layout?.WhiteLevel is not { } white)
            {
                return layout?.StoredCodeTransform is FrameStoredCodeTransform.RightAlignedV1 or FrameStoredCodeTransform.IdentityV1
                    ? sampleMaximum
                    : containerMaximum;
            }
            if (layout.LevelCodeSpace != FrameLevelCodeSpace.NativeSample || sample == container)
            {
                return white;
            }
            return layout.StoredCodeTransform switch
            {
                FrameStoredCodeTransform.LeftShiftedV1 => white * Math.Pow(2, container - sample),
                FrameStoredCodeTransform.FullRangeScaledV1 => white * containerMaximum / sampleMaximum,
                _ => white
            };
        }
    }
}
