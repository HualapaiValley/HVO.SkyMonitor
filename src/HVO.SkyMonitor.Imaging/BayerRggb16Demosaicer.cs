namespace HVO.SkyMonitor.Imaging;

/// <summary>Creates display-only RGB24 derivatives from linear little-endian RGGB RAW16 photosites.</summary>
public static class BayerRggb16Demosaicer
{
    /// <summary>Applies one global raw stretch, then bilinearly interpolates missing RGGB channels.</summary>
    public static byte[] DemosaicToRgb24(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        int? strideBytes = null,
        Mono16DisplayStretchOptions? stretchOptions = null)
    {
        var mosaic = Mono16DisplayStretch.Apply(width, height, pixelData, strideBytes, stretchOptions);
        var output = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceChannel = ChannelAt(x, y);
                var outputOffset = (y * width + x) * 3;
                for (var channel = 0; channel < 3; channel++)
                {
                    output[outputOffset + channel] = channel == sourceChannel
                        ? mosaic[y * width + x]
                        : Interpolate(mosaic, width, height, x, y, channel);
                }
            }
        }
        return output;
    }

    private static byte Interpolate(byte[] mosaic, int width, int height, int centerX, int centerY, int channel)
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
                if ((uint)x < (uint)width && ChannelAt(x, y) == channel)
                {
                    total += mosaic[y * width + x];
                    count++;
                }
            }
        }
        return count == 0 ? mosaic[centerY * width + centerX] : (byte)Math.Round(total / (double)count);
    }

    private static int ChannelAt(int x, int y) => (y & 1, x & 1) switch
    {
        (0, 0) => 0,
        (1, 1) => 2,
        _ => 1
    };
}
