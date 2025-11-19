using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HVO;
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
}
