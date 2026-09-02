using System.Diagnostics.CodeAnalysis;
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
