using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HVO.Core.Results;
using HVO.SkyMonitor.Imaging;
using SkiaSharp;

namespace HVO.SkyMonitor.CameraAgent.Common.Imaging;

/// <summary>
/// Provides SkiaSharp-backed helpers for encoding preview imagery.
/// </summary>
/// <remarks>
/// TODO: add RGB/Bayer encoders once the camera pipeline exposes those buffers so downstream callers reuse the same zero-copy path.
/// </remarks>
public static class SkiaPreviewEncoder
{
    private const int DefaultQuality = 80;

    /// <summary>
    /// Encodes a grayscale (Mono8) pixel buffer into a JPEG payload using SkiaSharp.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="pixelData">Mono8 pixel data (width * height bytes).</param>
    /// <param name="quality">JPEG quality between 1-100.</param>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Callers rely on Result<T> for error propagation without throwing.")]
    public static Result<byte[]> EncodeMono8ToJpeg(int width, int height, ReadOnlyMemory<byte> pixelData, int quality = DefaultQuality)
    {
        try
        {
            if (width <= 0 || height <= 0)
            {
                return Result<byte[]>.Failure(new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive."));
            }

            var qualityClamp = Math.Clamp(quality, 1, 100);
            var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            if (pixelData.Length != info.BytesSize)
            {
                return Result<byte[]>.Failure(new InvalidOperationException($"Pixel buffer length {pixelData.Length} does not match expected size {info.BytesSize} for {width}x{height}."));
            }

            if (!MemoryMarshal.TryGetArray(pixelData, out ArraySegment<byte> segment) || segment.Array is null)
            {
                return Result<byte[]>.Failure(new InvalidOperationException("Pixel buffer must be array-backed."));
            }

            var handle = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
            try
            {
                var pixelPtr = handle.AddrOfPinnedObject() + segment.Offset;
                using var image = SKImage.FromPixelCopy(info, pixelPtr, info.RowBytes);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, qualityClamp);
                if (data is null)
                {
                    return Result<byte[]>.Failure(new InvalidOperationException("SkiaSharp returned null data for JPEG encoding."));
                }

                return Result<byte[]>.Success(data.ToArray());
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            return Result<byte[]>.Failure(ex);
        }
    }

    /// <summary>Encodes packed red, green, blue bytes as a JPEG display derivative.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Callers rely on Result<T> for error propagation without throwing.")]
    public static Result<byte[]> EncodeRgb24ToJpeg(
        int width, int height, ReadOnlyMemory<byte> pixelData, int quality = DefaultQuality)
    {
        if (width <= 0 || height <= 0 || pixelData.Length != checked(width * height * 3))
        {
            return Result<byte[]>.Failure(new InvalidOperationException("Pixel buffer does not match the RGB24 dimensions."));
        }

        var rgba = new byte[checked(width * height * 4)];
        var source = pixelData.Span;
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            rgba[pixel * 4] = source[pixel * 3];
            rgba[pixel * 4 + 1] = source[pixel * 3 + 1];
            rgba[pixel * 4 + 2] = source[pixel * 3 + 2];
            rgba[pixel * 4 + 3] = byte.MaxValue;
        }

        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var image = SKImage.FromPixelCopy(info, rgba, info.RowBytes);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100));
            return data is null
                ? Result<byte[]>.Failure(new InvalidOperationException("SkiaSharp returned null data for JPEG encoding."))
                : Result<byte[]>.Success(data.ToArray());
        }
        catch (Exception exception)
        {
            return Result<byte[]>.Failure(exception);
        }
    }

    /// <summary>Encodes a Mono16 frame as a contrast-normalized 8-bit JPEG preview.</summary>
    public static Result<byte[]> EncodeMono16ToJpeg(int width, int height, ReadOnlyMemory<byte> pixelData, int quality = DefaultQuality)
    {
        if (pixelData.Length != checked(width * height * 2))
        {
            return Result<byte[]>.Failure(new InvalidOperationException($"Pixel buffer length {pixelData.Length} does not match expected Mono16 size for {width}x{height}."));
        }

        return EncodeMono8ToJpeg(width, height, Mono16DisplayStretch.Apply(width, height, pixelData), quality);
    }

    /// <summary>Demosaics RGGB RAW16 and encodes the display-only RGB result as JPEG.</summary>
    public static Result<byte[]> EncodeBayerRggb16ToJpeg(
        int width, int height, ReadOnlyMemory<byte> pixelData, int quality = DefaultQuality)
    {
        if (pixelData.Length != checked(width * height * 2))
        {
            return Result<byte[]>.Failure(new InvalidOperationException(
                $"Pixel buffer length {pixelData.Length} does not match expected BayerRggb16 size for {width}x{height}."));
        }

        return EncodeRgb24ToJpeg(
            width, height, BayerRggb16Demosaicer.DemosaicToRgb24(width, height, pixelData), quality);
    }
}
