using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Deterministic linear RGB24 scene renderer used by virtual-camera fixtures.</summary>
public static class Rgb24GradientRenderer
{
    /// <summary>Creates RGB24 pixels in red, green, blue channel order.</summary>
    public static byte[] Render(ImageLayout layout, double exposureSeconds, double gain)
    {
        if (layout.PixelFormat != CameraPixelFormat.Rgb24)
        {
            throw new ArgumentException("RGB24 layout is required.", nameof(layout));
        }

        layout.Validate();
        if (!double.IsFinite(exposureSeconds) || !double.IsFinite(gain) || exposureSeconds < 0 || gain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds));
        }

        var pixels = new byte[layout.RequiredByteLength];
        var scale = exposureSeconds * gain;
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                var normalized = (x + y) / (double)Math.Max(1, layout.Width + layout.Height - 2);
                var intensity = (byte)Math.Clamp(Math.Round(normalized * byte.MaxValue * scale), 0, byte.MaxValue);
                var offset = y * layout.StrideBytes + x * 3;
                pixels[offset] = intensity;
                pixels[offset + 1] = (byte)(intensity / 2);
                pixels[offset + 2] = (byte)(byte.MaxValue - intensity);
            }
        }

        return pixels;
    }
}
