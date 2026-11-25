namespace HVO.SkyMonitor.Imaging;

/// <summary>Creates display-only RGB24 derivatives from linear little-endian RGGB RAW16 photosites.</summary>
public static class BayerRggb16Demosaicer
{
    public const string AlgorithmVersion = "bilinear-rggb16-demosaic-v1";

    /// <summary>Applies one global raw stretch, then bilinearly interpolates missing CFA channels.</summary>
    public static byte[] DemosaicToRgb24(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        int? strideBytes = null,
        Mono16DisplayStretchOptions? stretchOptions = null,
        BayerPattern pattern = BayerPattern.Rggb)
        => DemosaicToRgb24(
            width, height, pixelData, CancellationToken.None, strideBytes, stretchOptions, pattern);

    /// <summary>Demosaics while observing cancellation at row boundaries and honoring the delivered CFA phase.</summary>
    public static byte[] DemosaicToRgb24(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        CancellationToken cancellationToken,
        int? strideBytes = null,
        Mono16DisplayStretchOptions? stretchOptions = null,
        BayerPattern pattern = BayerPattern.Rggb)
    {
        if (!Enum.IsDefined(pattern))
        {
            throw new ArgumentOutOfRangeException(nameof(pattern));
        }

        var phaseX = pattern is BayerPattern.Bggr or BayerPattern.Grbg ? 1 : 0;
        var phaseY = pattern is BayerPattern.Bggr or BayerPattern.Gbrg ? 1 : 0;
        var mosaic = Mono16DisplayStretch.Apply(
            width, height, pixelData, cancellationToken, strideBytes, stretchOptions);
        var output = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var sourceChannel = ChannelAt(x, y, phaseX, phaseY);
                var outputOffset = (y * width + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    output[outputOffset + channel] = channel == sourceChannel
                        ? mosaic[y * width + x]
                        : Interpolate(mosaic, width, height, x, y, channel, phaseX, phaseY);
                }
            }
        }
        return output;
    }

    private static byte Interpolate(
        byte[] mosaic,
        int width,
        int height,
        int centerX,
        int centerY,
        int channel,
        int phaseX,
        int phaseY)
    {
        var total = 0;
        var count = 0;
        for (var deltaY = -1; deltaY <= 1; deltaY++)
        {
            var y = centerY + deltaY;
            if ((uint)y >= (uint)height)
            {
                continue;
            }
            for (var deltaX = -1; deltaX <= 1; deltaX++)
            {
                var x = centerX + deltaX;
                if ((uint)x < (uint)width && ChannelAt(x, y, phaseX, phaseY) == channel)
                {
                    total += mosaic[y * width + x];
                    count++;
                }
            }
        }
        return count == 0 ? mosaic[centerY * width + centerX] : (byte)Math.Round(total / (double)count);
    }

    private static int ChannelAt(int x, int y, int phaseX, int phaseY) =>
        ((y + phaseY) & 1, (x + phaseX) & 1) switch
        {
            (0, 0) => 0,
            (1, 1) => 2,
            _ => 1
        };
}
