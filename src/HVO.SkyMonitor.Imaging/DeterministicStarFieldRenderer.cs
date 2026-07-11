using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Renders a seeded time-dependent point-star field into packed raw camera pixels.</summary>
public static class DeterministicStarFieldRenderer
{
    /// <summary>Renders a Mono16 star field whose horizontal geometry advances once per minute.</summary>
    public static byte[] RenderMono16(ImageLayout layout, DateTimeOffset utc, int seed, int starCount, double exposureSeconds, double gain)
    {
        if (layout.PixelFormat != CameraPixelFormat.Mono16)
        {
            throw new ArgumentException("Mono16 layout is required.", nameof(layout));
        }

        if (starCount is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(starCount));
        }

        var pixels = Mono16GradientRenderer.Render(layout, exposureSeconds, gain);
        var random = new DeterministicRandom(seed);
        var offset = (int)(utc.ToUniversalTime().ToUnixTimeSeconds() / 60 % layout.Width);
        for (var index = 0; index < starCount; index++)
        {
            var x = (random.Next(layout.Width) + offset) % layout.Width;
            var y = random.Next(layout.Height);
            var brightness = (ushort)random.Next(32_768, 65_536);
            var byteOffset = y * layout.StrideBytes + x * 2;
            pixels[byteOffset] = (byte)brightness;
            pixels[byteOffset + 1] = (byte)(brightness >> 8);
        }

        return pixels;
    }

    private sealed class DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(int seed) => _state = unchecked((uint)seed) + 1u;

        public int Next(int exclusiveMaximum)
        {
            _state = _state * 1_664_525u + 1_013_904_223u;
            return (int)(_state % (uint)exclusiveMaximum);
        }

        public int Next(int inclusiveMinimum, int exclusiveMaximum)
            => inclusiveMinimum + Next(exclusiveMaximum - inclusiveMinimum);
    }
}
