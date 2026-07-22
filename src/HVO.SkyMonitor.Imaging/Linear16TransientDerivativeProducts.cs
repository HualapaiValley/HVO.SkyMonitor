using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

public sealed record Linear16TransientDerivativeRenderOptions(
    int CropPaddingPixels = 16,
    int JpegQuality = 90);

public sealed record Linear16TransientDerivativeGeometry(
    Linear16TransientReconstructionBounds Bounds,
    IReadOnlyList<PixelPoint> Polyline);

public sealed record Linear16TransientDerivativeProducts(
    ReadOnlyMemory<byte> Reconstruction,
    ReadOnlyMemory<byte> Mask,
    ReadOnlyMemory<byte> Preview,
    ReadOnlyMemory<byte> Crop,
    ReadOnlyMemory<byte> Overlay,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    string AlgorithmVersion);

/// <summary>Creates the frozen V1 raw, mask, and JPEG display products for one reconstruction.</summary>
public static class Linear16TransientDerivativeProductFactory
{
    public const string AlgorithmVersion = "linear16-transient-derivative-products-v1";
    public const string Linear16MediaType = "application/x-hvo-linear16";
    public const string PackedMaskMediaType = "application/x-hvo-bitmask";

    public static Linear16TransientDerivativeProducts Create(
        Linear16TransientReconstructionResult reconstruction,
        IReadOnlyList<Linear16TransientDerivativeGeometry> geometries,
        Linear16TransientDerivativeRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconstruction);
        ArgumentNullException.ThrowIfNull(geometries);
        options ??= new();
        if (geometries.Count is < 1 or > 2 || options.CropPaddingPixels < 0 || options.JpegQuality is < 1 or > 100)
        {
            throw new ArgumentException("V1 derivative rendering requires one or two geometries and valid options.");
        }

        var frame = reconstruction.Reconstruction;
        if (geometries.Any(item => !Valid(item, frame.Width, frame.Height)))
        {
            throw new ArgumentException("Derivative geometry is outside the reconstruction layout.", nameof(geometries));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var previewPixels = ToMono8(frame, cancellationToken);
        var preview = JpegImageCodec.EncodeMono8ToJpeg(
            frame.Width, frame.Height, previewPixels, frame.Width, options.JpegQuality, cancellationToken);
        var cropBounds = ResolveCrop(geometries, frame.Width, frame.Height, options.CropPaddingPixels);
        var cropPixels = Crop(previewPixels, frame.Width, cropBounds, cancellationToken);
        var crop = JpegImageCodec.EncodeMono8ToJpeg(
            cropBounds.Width, cropBounds.Height, cropPixels, cropBounds.Width, options.JpegQuality, cancellationToken);
        var overlayPixels = ToRgb24(previewPixels, cancellationToken);
        foreach (var geometry in geometries)
        {
            DrawGeometry(overlayPixels, frame.Width, frame.Height, geometry, cancellationToken);
        }
        var overlay = JpegImageCodec.EncodeRgb24ToJpeg(
            frame.Width, frame.Height, overlayPixels, frame.Width * 3, options.JpegQuality, cancellationToken);

        return new(
            frame.PixelData,
            reconstruction.EventMask.Bits,
            preview,
            crop,
            overlay,
            cropBounds.X,
            cropBounds.Y,
            cropBounds.Width,
            cropBounds.Height,
            AlgorithmVersion);
    }

    private static bool Valid(Linear16TransientDerivativeGeometry geometry, int width, int height)
    {
        var bounds = geometry.Bounds;
        return geometry.Polyline is not null &&
            double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
            bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
            bounds.X + bounds.Width <= width && bounds.Y + bounds.Height <= height &&
            geometry.Polyline.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                point.X >= 0 && point.Y >= 0 && point.X <= width && point.Y <= height);
    }

    private static byte[] ToMono8(Linear16Frame frame, CancellationToken cancellationToken)
    {
        var output = new byte[checked(frame.Width * frame.Height)];
        var input = frame.PixelData.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < frame.Width; x++)
            {
                output[y * frame.Width + x] = input[y * frame.StrideBytes + x * 2 + 1];
            }
        }
        return output;
    }

    private static CropBounds ResolveCrop(
        IReadOnlyList<Linear16TransientDerivativeGeometry> geometries,
        int width,
        int height,
        int padding)
    {
        var minimumX = Math.Max(0, (int)Math.Floor(geometries.Min(item => item.Bounds.X)) - padding);
        var minimumY = Math.Max(0, (int)Math.Floor(geometries.Min(item => item.Bounds.Y)) - padding);
        var maximumX = Math.Min(width,
            (int)Math.Ceiling(geometries.Max(item => item.Bounds.X + item.Bounds.Width)) + padding);
        var maximumY = Math.Min(height,
            (int)Math.Ceiling(geometries.Max(item => item.Bounds.Y + item.Bounds.Height)) + padding);
        return new(minimumX, minimumY, maximumX - minimumX, maximumY - minimumY);
    }

    private static byte[] Crop(byte[] pixels, int sourceWidth, CropBounds bounds, CancellationToken cancellationToken)
    {
        var output = new byte[checked(bounds.Width * bounds.Height)];
        for (var y = 0; y < bounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pixels.AsSpan((bounds.Y + y) * sourceWidth + bounds.X, bounds.Width)
                .CopyTo(output.AsSpan(y * bounds.Width, bounds.Width));
        }
        return output;
    }

    private static byte[] ToRgb24(byte[] mono, CancellationToken cancellationToken)
    {
        var output = new byte[checked(mono.Length * 3)];
        for (var index = 0; index < mono.Length; index++)
        {
            if ((index & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var destination = index * 3;
            output[destination] = mono[index];
            output[destination + 1] = mono[index];
            output[destination + 2] = mono[index];
        }
        return output;
    }

    private static void DrawGeometry(
        byte[] pixels,
        int width,
        int height,
        Linear16TransientDerivativeGeometry geometry,
        CancellationToken cancellationToken)
    {
        var left = Math.Clamp((int)Math.Floor(geometry.Bounds.X), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(geometry.Bounds.Y), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling(geometry.Bounds.X + geometry.Bounds.Width) - 1, 0, width - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(geometry.Bounds.Y + geometry.Bounds.Height) - 1, 0, height - 1);
        DrawLine(pixels, width, height, left, top, right, top, cancellationToken);
        DrawLine(pixels, width, height, right, top, right, bottom, cancellationToken);
        DrawLine(pixels, width, height, right, bottom, left, bottom, cancellationToken);
        DrawLine(pixels, width, height, left, bottom, left, top, cancellationToken);
        for (var index = 1; index < geometry.Polyline.Count; index++)
        {
            var start = geometry.Polyline[index - 1];
            var end = geometry.Polyline[index];
            DrawLine(
                pixels,
                width,
                height,
                Math.Clamp((int)Math.Floor(start.X), 0, width - 1),
                Math.Clamp((int)Math.Floor(start.Y), 0, height - 1),
                Math.Clamp((int)Math.Floor(end.X), 0, width - 1),
                Math.Clamp((int)Math.Floor(end.Y), 0, height - 1),
                cancellationToken);
        }
    }

    private static void DrawLine(
        byte[] pixels,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        CancellationToken cancellationToken)
    {
        var deltaX = Math.Abs(x1 - x0);
        var stepX = x0 < x1 ? 1 : -1;
        var deltaY = -Math.Abs(y1 - y0);
        var stepY = y0 < y1 ? 1 : -1;
        var error = deltaX + deltaY;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (x0 >= 0 && y0 >= 0 && x0 < width && y0 < height)
            {
                var offset = (y0 * width + x0) * 3;
                pixels[offset] = byte.MaxValue;
                pixels[offset + 1] = 160;
                pixels[offset + 2] = 0;
            }
            if (x0 == x1 && y0 == y1)
            {
                break;
            }
            var doubled = error * 2;
            if (doubled >= deltaY)
            {
                error += deltaY;
                x0 += stepX;
            }
            if (doubled <= deltaX)
            {
                error += deltaX;
                y0 += stepY;
            }
        }
    }

    private readonly record struct CropBounds(int X, int Y, int Width, int Height);
}
