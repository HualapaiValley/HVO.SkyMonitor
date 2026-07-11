namespace HVO.SkyMonitor.Imaging;

/// <summary>Deterministic linear Mono16 scene renderer used by virtual-camera fixtures.</summary>
public static class Mono16GradientRenderer
{
    /// <summary>Creates a little-endian Mono16 gradient with a deterministic exposure/gain scale.</summary>
    public static byte[] Render(ImageLayout layout, double exposureSeconds, double gain)
    {
        if (layout.PixelFormat != HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16)
        {
            throw new ArgumentException("Mono16 layout is required.", nameof(layout));
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
                var sample = (ushort)Math.Clamp(Math.Round(normalized * ushort.MaxValue * scale), 0, ushort.MaxValue);
                var offset = y * layout.StrideBytes + x * 2;
                pixels[offset] = (byte)sample;
                pixels[offset + 1] = (byte)(sample >> 8);
            }
        }

        return pixels;
    }
}
