using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.AgentCore;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A decoded, tightly packed JPEG image without codec-specific resource ownership.</summary>
public sealed record DecodedImage(
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    int StrideBytes,
    ReadOnlyMemory<byte> PixelData,
    string MediaType,
    string AlgorithmVersion);

/// <summary>Bounded JPEG header facts read without decoding a pixel buffer.</summary>
public sealed record EncodedImageInfo(
    int Width,
    int Height,
    CameraPixelFormat PixelFormat,
    string MediaType);

/// <summary>Encodes and decodes JPEG display images without exposing native codec objects.</summary>
public static class JpegImageCodec
{
    public const string MediaType = "image/jpeg";
    public const string AlgorithmVersion = "skia-jpeg-v1";
    public const int DefaultQuality = 80;

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The native codec factory can return null for malformed input despite its managed nullability annotation.")]
    public static EncodedImageInfo InspectJpeg(ReadOnlyMemory<byte> encodedData)
    {
        if (encodedData.IsEmpty)
        {
            throw new ArgumentException("JPEG data must not be empty.", nameof(encodedData));
        }
        using var data = SKData.CreateCopy(encodedData.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg ||
            codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new ArgumentException("The supplied data is not a valid JPEG image.", nameof(encodedData));
        }
        var pixelFormat = codec.Info.ColorType == SKColorType.Gray8
            ? CameraPixelFormat.Mono8
            : CameraPixelFormat.Rgb24;
        return new(codec.Info.Width, codec.Info.Height, pixelFormat, MediaType);
    }

    /// <summary>Fully decodes a JPEG through a bounded scanline buffer and returns its encoded layout.</summary>
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The native codec factory can return null for malformed input despite its managed nullability annotation.")]
    public static EncodedImageInfo ValidateJpeg(
        ReadOnlyMemory<byte> encodedData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (encodedData.Length < 2 || encodedData.Span[^2] != 0xff || encodedData.Span[^1] != 0xd9)
        {
            throw new ArgumentException("JPEG data is incomplete.", nameof(encodedData));
        }
        if (!HasBaselineSequentialFrame(encodedData.Span))
        {
            throw new ArgumentException("JPEG validation supports only baseline sequential images.", nameof(encodedData));
        }

        using var data = SKData.CreateCopy(encodedData.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg ||
            codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new ArgumentException("The supplied data is not a valid JPEG image.", nameof(encodedData));
        }

        var pixelFormat = codec.Info.ColorType == SKColorType.Gray8
            ? CameraPixelFormat.Mono8
            : CameraPixelFormat.Rgb24;
        var targetInfo = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            pixelFormat == CameraPixelFormat.Mono8 ? SKColorType.Gray8 : SKColorType.Rgba8888,
            SKAlphaType.Opaque);
        // JPEG dimensions are 16-bit, so even an RGBA scanline remains below 256 KiB.
        var row = new byte[targetInfo.RowBytes];
        var pinned = GCHandle.Alloc(row, GCHandleType.Pinned);
        try
        {
            var result = codec.StartScanlineDecode(targetInfo);
            if (result != SKCodecResult.Success)
            {
                throw new InvalidOperationException($"JPEG scanline decoding failed with result {result}.");
            }
            for (var line = 0; line < targetInfo.Height; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (codec.GetScanlines(pinned.AddrOfPinnedObject(), 1, targetInfo.RowBytes) != 1)
                {
                    throw new InvalidOperationException("JPEG scanline decoding encountered incomplete image data.");
                }
            }
        }
        finally
        {
            pinned.Free();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(codec.Info.Width, codec.Info.Height, pixelFormat, MediaType);
    }

    private static bool HasBaselineSequentialFrame(ReadOnlySpan<byte> encodedData)
    {
        if (encodedData.Length < 4 || encodedData[0] != 0xff || encodedData[1] != 0xd8)
        {
            return false;
        }

        var offset = 2;
        var foundBaselineFrame = false;
        var frameComponentCount = 0;
        while (offset < encodedData.Length)
        {
            if (encodedData[offset++] != 0xff)
            {
                return false;
            }
            while (offset < encodedData.Length && encodedData[offset] == 0xff)
            {
                offset++;
            }
            if (offset >= encodedData.Length)
            {
                return false;
            }

            var marker = encodedData[offset++];
            if (marker == 0xd9)
            {
                return false;
            }
            if (marker is 0x01 or >= 0xd0 and <= 0xd8)
            {
                continue;
            }
            if (offset > encodedData.Length - 2)
            {
                return false;
            }

            var segmentLength = (encodedData[offset] << 8) | encodedData[offset + 1];
            if (segmentLength < 2 || segmentLength > encodedData.Length - offset)
            {
                return false;
            }
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (marker != 0xc0 || foundBaselineFrame)
                {
                    return false;
                }
                foundBaselineFrame = true;
                if (segmentLength < 8)
                {
                    return false;
                }
                frameComponentCount = encodedData[offset + 7];
                if (frameComponentCount == 0 || segmentLength != 8 + (3 * frameComponentCount))
                {
                    return false;
                }
            }
            if (marker == 0xda)
            {
                var scanComponentCount = segmentLength >= 6 ? encodedData[offset + 2] : 0;
                return foundBaselineFrame && scanComponentCount == frameComponentCount &&
                    segmentLength == 6 + (2 * scanComponentCount) &&
                    offset + segmentLength < encodedData.Length - 2;
            }
            offset += segmentLength;
        }
        return false;
    }

    /// <summary>Encodes a supported Mono8 or RGB24 image as JPEG.</summary>
    public static byte[] EncodeToJpeg(
        ImageLayout layout,
        ReadOnlyMemory<byte> pixelData,
        int quality = DefaultQuality,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return layout.PixelFormat switch
        {
            CameraPixelFormat.Mono8 => EncodeMono8ToJpeg(
                layout.Width, layout.Height, pixelData, layout.StrideBytes, quality, cancellationToken),
            CameraPixelFormat.Rgb24 => EncodeRgb24ToJpeg(
                layout.Width, layout.Height, pixelData, layout.StrideBytes, quality, cancellationToken),
            _ => throw new ArgumentException("JPEG encoding supports only Mono8 and RGB24 images.", nameof(layout))
        };
    }

    /// <summary>Encodes packed or row-padded Mono8 pixels as a grayscale JPEG.</summary>
    public static byte[] EncodeMono8ToJpeg(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        int? strideBytes = null,
        int quality = DefaultQuality,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var layout = ValidateInput(width, height, CameraPixelFormat.Mono8, pixelData, strideBytes, quality);
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(
            info, pixelData.Span[..layout.RequiredByteLength], layout.StrideBytes);
        if (image is null)
        {
            throw new InvalidOperationException("The JPEG codec could not create a grayscale image.");
        }

        return Encode(image, quality, cancellationToken);
    }

    /// <summary>Encodes packed or row-padded red, green, blue pixels as a JPEG.</summary>
    public static byte[] EncodeRgb24ToJpeg(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        int? strideBytes = null,
        int quality = DefaultQuality,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var layout = ValidateInput(width, height, CameraPixelFormat.Rgb24, pixelData, strideBytes, quality);
        var rgba = new byte[checked(width * height * 4)];
        var source = pixelData.Span;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = y * layout.StrideBytes;
            var destinationRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = sourceRow + x * 3;
                var destinationOffset = destinationRow + x * 4;
                rgba[destinationOffset] = source[sourceOffset];
                rgba[destinationOffset + 1] = source[sourceOffset + 1];
                rgba[destinationOffset + 2] = source[sourceOffset + 2];
                rgba[destinationOffset + 3] = byte.MaxValue;
            }
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, rgba, info.RowBytes);
        if (image is null)
        {
            throw new InvalidOperationException("The JPEG codec could not create an RGB image.");
        }

        return Encode(image, quality, cancellationToken);
    }

    /// <summary>Decodes a JPEG to tightly packed Mono8 or RGB24 pixels according to its encoded color model.</summary>
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The native codec factory can return null for malformed input despite its managed nullability annotation.")]
    public static DecodedImage DecodeJpeg(
        ReadOnlyMemory<byte> encodedData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (encodedData.IsEmpty)
        {
            throw new ArgumentException("JPEG data must not be empty.", nameof(encodedData));
        }

        using var data = SKData.CreateCopy(encodedData.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Jpeg)
        {
            throw new ArgumentException("The supplied data is not a valid JPEG image.", nameof(encodedData));
        }

        var sourceInfo = codec.Info;
        if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0)
        {
            throw new InvalidOperationException("The JPEG contains invalid image dimensions.");
        }

        return sourceInfo.ColorType == SKColorType.Gray8
            ? DecodeMono8(codec, sourceInfo.Width, sourceInfo.Height, cancellationToken)
            : DecodeRgb24(codec, sourceInfo.Width, sourceInfo.Height, cancellationToken);
    }

    private static ImageLayout ValidateInput(
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        ReadOnlyMemory<byte> pixelData,
        int? strideBytes,
        int quality)
    {
        if (quality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality), "JPEG quality must be between 1 and 100.");
        }

        int packedStride;
        try
        {
            packedStride = checked(width * ImageLayout.BytesPerPixel(pixelFormat));
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(width), exception, "Image width is too large.");
        }

        var layout = new ImageLayout(width, height, pixelFormat, strideBytes ?? packedStride);
        layout.Validate();
        if (pixelData.Length < layout.RequiredByteLength)
        {
            throw new ArgumentException("Pixel buffer does not contain the declared image layout.", nameof(pixelData));
        }

        return layout;
    }

    private static byte[] Encode(SKImage image, int quality, CancellationToken cancellationToken)
    {
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        cancellationToken.ThrowIfCancellationRequested();
        var output = data?.ToArray()
            ?? throw new InvalidOperationException("The JPEG codec returned no encoded data.");
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static DecodedImage DecodeMono8(
        SKCodec codec,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = new byte[checked(width * height)];
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        var result = codec.GetPixels(info, pixels);
        if (result != SKCodecResult.Success)
        {
            throw new InvalidOperationException($"JPEG decoding failed with result {result}.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        return new DecodedImage(
            width, height, CameraPixelFormat.Mono8, width, pixels, MediaType, AlgorithmVersion);
    }

    private static DecodedImage DecodeRgb24(
        SKCodec codec,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var rgba = new byte[checked(width * height * 4)];
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var result = codec.GetPixels(info, rgba);
        if (result != SKCodecResult.Success)
        {
            throw new InvalidOperationException($"JPEG decoding failed with result {result}.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = y * width * 4;
            var destinationRow = y * width * 3;
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = sourceRow + x * 4;
                var destinationOffset = destinationRow + x * 3;
                pixels[destinationOffset] = rgba[sourceOffset];
                pixels[destinationOffset + 1] = rgba[sourceOffset + 1];
                pixels[destinationOffset + 2] = rgba[sourceOffset + 2];
            }
        }

        return new DecodedImage(
            width, height, CameraPixelFormat.Rgb24, width * 3, pixels, MediaType, AlgorithmVersion);
    }
}
