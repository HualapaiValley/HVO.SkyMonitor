using System;
using System.Diagnostics.CodeAnalysis;
using HVO.Core.Results;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Imaging;

/// <summary>
/// Provides compatibility helpers for encoding preview imagery.
/// </summary>
public static class SkiaPreviewEncoder
{
    private const int DefaultQuality = 80;

    /// <summary>
    /// Encodes a grayscale (Mono8) pixel buffer into a JPEG payload.
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

            var expectedLength = checked(width * height);
            if (pixelData.Length != expectedLength)
            {
                return Result<byte[]>.Failure(new InvalidOperationException($"Pixel buffer length {pixelData.Length} does not match expected size {expectedLength} for {width}x{height}."));
            }

            return Result<byte[]>.Success(JpegImageCodec.EncodeMono8ToJpeg(
                width, height, pixelData, quality: Math.Clamp(quality, 1, 100)));
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

        try
        {
            return Result<byte[]>.Success(JpegImageCodec.EncodeRgb24ToJpeg(
                width, height, pixelData, quality: Math.Clamp(quality, 1, 100)));
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
