using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

public static class PackedImageDownsampler
{
    public const string AlgorithmVersion = "nearest-neighbor-packed-v1";
    public const string PixelCenterAlgorithmVersion = "nearest-neighbor-pixel-centers-v1";

    public static (int Width, int Height, ReadOnlyMemory<byte> Payload) Downsample(
        FrameLayoutDescriptor layout,
        ReadOnlyMemory<byte> payload,
        int maximumDimension)
        => DownsampleCore(layout, payload, maximumDimension, pixelCenters: false);

    public static (int Width, int Height, ReadOnlyMemory<byte> Payload) DownsampleAtPixelCenters(
        FrameLayoutDescriptor layout, ReadOnlyMemory<byte> payload, int maximumDimension)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24))
            throw new ArgumentException("Pixel-center preview resizing requires decoded mono or RGB input.", nameof(layout));
        return DownsampleCore(layout, payload, maximumDimension, pixelCenters: true);
    }

    private static (int Width, int Height, ReadOnlyMemory<byte> Payload) DownsampleCore(
        FrameLayoutDescriptor layout, ReadOnlyMemory<byte> payload, int maximumDimension, bool pixelCenters)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDimension, 1);
        if (layout.StrideBytes != checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)) ||
            payload.Length != layout.ByteLength)
        {
            throw new ArgumentException("A packed image payload is required.", nameof(payload));
        }

        var scale = Math.Min(1d, Math.Min(
            (double)maximumDimension / layout.Width,
            (double)maximumDimension / layout.Height));
        var width = Math.Max(1, (int)Math.Floor(layout.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(layout.Height * scale));
        if (layout.PixelFormat == CameraPixelFormat.BayerRggb16 && scale < 1)
        {
            width -= width > 1 ? width % 2 : 0;
            height -= height > 1 ? height % 2 : 0;
        }
        if (width == layout.Width && height == layout.Height)
        {
            return (width, height, payload);
        }

        var bytesPerPixel = ImageLayout.BytesPerPixel(layout.PixelFormat);
        var resized = new byte[checked(width * height * bytesPerPixel)];
        var source = payload.Span;
        for (var y = 0; y < height; y++)
        {
            var sourceY = SourceCoordinate(y, height, layout.Height, layout.PixelFormat, pixelCenters);
            for (var x = 0; x < width; x++)
            {
                var sourceX = SourceCoordinate(x, width, layout.Width, layout.PixelFormat, pixelCenters);
                source.Slice(
                    checked((sourceY * layout.Width + sourceX) * bytesPerPixel),
                    bytesPerPixel).CopyTo(resized.AsSpan(
                    checked((y * width + x) * bytesPerPixel),
                    bytesPerPixel));
            }
        }
        return (width, height, resized);
    }

    private static int SourceCoordinate(
        int destination,
        int destinationLength,
        int sourceLength,
        CameraPixelFormat format,
        bool pixelCenters)
    {
        if (pixelCenters)
            return (int)((2L * destination + 1) * sourceLength / (2L * destinationLength));
        var coordinate = Math.Min(sourceLength - 1, checked(destination * sourceLength / destinationLength));
        if (format != CameraPixelFormat.BayerRggb16 || (coordinate & 1) == (destination & 1))
        {
            return coordinate;
        }
        return coordinate + 1 < sourceLength ? coordinate + 1 : coordinate - 1;
    }
}
