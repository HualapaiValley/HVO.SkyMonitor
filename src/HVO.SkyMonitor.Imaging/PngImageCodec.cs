using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Decodes PNG display images without exposing native codec objects.</summary>
public static class PngImageCodec
{
    public const string MediaType = "image/png";
    public const string AlgorithmVersion = "skia-png-v2";

    /// <summary>Verifies that a static PNG, including one with transparency, decodes within a pixel budget.</summary>
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The native codec factory can return null for malformed input despite its managed nullability annotation.")]
    public static bool IsDecodablePng(ReadOnlySpan<byte> encodedData, int maximumPixels)
    {
        if (HasAnimationControl(encodedData))
            return false;

        using var data = SKData.CreateCopy(encodedData);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1)
            return false;
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > maximumPixels)
            return false;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return codec.GetPixels(info, new byte[checked(width * height * 4)]) == SKCodecResult.Success;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The native codec factory can return null for malformed input despite its managed nullability annotation.")]
    public static EncodedImageInfo InspectPng(ReadOnlyMemory<byte> encodedData)
    {
        if (encodedData.IsEmpty)
        {
            throw new ArgumentException("PNG data must not be empty.", nameof(encodedData));
        }
        if (HasAnimationControl(encodedData.Span))
            throw new ArgumentException("Animated PNG images are unsupported.", nameof(encodedData));

        using var data = SKData.CreateCopy(encodedData.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1 ||
            codec.EncodedOrigin != SKEncodedOrigin.TopLeft ||
            codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new ArgumentException(
                $"The supplied data must be a static, opaque, top-left-oriented PNG image " +
                $"(frames: {codec?.FrameCount}, origin: {codec?.EncodedOrigin}).", nameof(encodedData));
        }
        return new(codec.Info.Width, codec.Info.Height, CameraPixelFormat.Rgb24, MediaType);
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "The native codec factory can return null for malformed input despite its managed nullability annotation.")]
    public static DecodedImage DecodePng(
        ReadOnlyMemory<byte> encodedData,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (encodedData.IsEmpty)
        {
            throw new ArgumentException("PNG data must not be empty.", nameof(encodedData));
        }
        if (HasAnimationControl(encodedData.Span))
            throw new ArgumentException("Animated PNG images are unsupported.", nameof(encodedData));

        using var data = SKData.CreateCopy(encodedData.Span);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png || codec.FrameCount > 1 ||
            codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
        {
            throw new ArgumentException(
                "The supplied data must be a static, opaque, top-left-oriented PNG image.", nameof(encodedData));
        }
        var sourceInfo = codec.Info;
        if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0)
        {
            throw new InvalidOperationException("The PNG contains invalid image dimensions.");
        }

        var rgba = new byte[checked(sourceInfo.Width * sourceInfo.Height * 4)];
        var info = new SKImageInfo(
            sourceInfo.Width, sourceInfo.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var result = codec.GetPixels(info, rgba);
        if (result != SKCodecResult.Success)
        {
            throw new InvalidOperationException($"PNG decoding failed with result {result}.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var pixels = new byte[checked(sourceInfo.Width * sourceInfo.Height * 3)];
        for (var y = 0; y < sourceInfo.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = y * sourceInfo.Width * 4;
            var destinationRow = y * sourceInfo.Width * 3;
            for (var x = 0; x < sourceInfo.Width; x++)
            {
                var sourceOffset = sourceRow + x * 4;
                var destinationOffset = destinationRow + x * 3;
                if (rgba[sourceOffset + 3] != byte.MaxValue)
                {
                    throw new ArgumentException("Transparent PNG presentation bases are unsupported.", nameof(encodedData));
                }
                pixels[destinationOffset] = rgba[sourceOffset];
                pixels[destinationOffset + 1] = rgba[sourceOffset + 1];
                pixels[destinationOffset + 2] = rgba[sourceOffset + 2];
            }
        }
        return new(
            sourceInfo.Width,
            sourceInfo.Height,
            CameraPixelFormat.Rgb24,
            sourceInfo.Width * 3,
            pixels,
            MediaType,
            AlgorithmVersion);
    }

    private static bool HasAnimationControl(ReadOnlySpan<byte> encodedData)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (!encodedData.StartsWith(signature))
            return false;

        // Some PNG decoders expose only the default image. APNG requires acTL before the first IDAT.
        const int chunkOverhead = 12; // Length, type and CRC, excluding the chunk data.
        var chunks = encodedData[signature.Length..];
        while (chunks.Length >= chunkOverhead)
        {
            var type = chunks.Slice(4, 4);
            if (type.SequenceEqual("acTL"u8))
                return true;
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
                break;

            var length = BinaryPrimitives.ReadUInt32BigEndian(chunks);
            if (length > chunks.Length - chunkOverhead)
                break;
            chunks = chunks[(chunkOverhead + (int)length)..];
        }
        return false;
    }
}
