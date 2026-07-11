using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Validated pixel-buffer layout with explicit row stride and byte order.</summary>
public readonly record struct ImageLayout(int Width, int Height, CameraPixelFormat PixelFormat, int StrideBytes)
{
    /// <summary>Gets the minimum tightly packed stride for the selected pixel format.</summary>
    public int MinimumStrideBytes => checked(Width * BytesPerPixel(PixelFormat));

    /// <summary>Gets the required buffer length including stride padding.</summary>
    public int RequiredByteLength => checked(StrideBytes * Height);

    /// <summary>Validates dimensions and stride for a non-empty image.</summary>
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || StrideBytes < MinimumStrideBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(ImageLayout));
        }
    }

    /// <summary>Returns bytes per pixel for currently supported packed formats.</summary>
    public static int BytesPerPixel(CameraPixelFormat format) => format switch
    {
        CameraPixelFormat.Mono8 => 1,
        CameraPixelFormat.Mono16 => 2,
        CameraPixelFormat.Rgb24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

/// <summary>Validates image buffers against their declared layouts.</summary>
public static class ImageBuffer
{
    /// <summary>Throws when the supplied pixel buffer does not match the layout's required length.</summary>
    public static void Validate(ImageLayout layout, ReadOnlyMemory<byte> pixels)
    {
        layout.Validate();
        if (pixels.Length != layout.RequiredByteLength)
        {
            throw new ArgumentException("Pixel buffer length does not match the image layout.", nameof(pixels));
        }
    }
}
