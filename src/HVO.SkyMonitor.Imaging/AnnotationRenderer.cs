using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Applies deterministic point annotations to a Mono8 preview without modifying its source buffer.</summary>
public static class AnnotationRenderer
{
    /// <summary>Copies a Mono8 preview and draws visible projector points as white pixels.</summary>
    public static byte[] AnnotateMono8(ReadOnlyMemory<byte> preview, int width, int height, IImageProjector projector, IEnumerable<AltAzPoint> directions)
    {
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(directions);
        if (width <= 0 || height <= 0 || preview.Length != checked(width * height))
        {
            throw new ArgumentException("Preview dimensions do not match its pixel buffer.", nameof(preview));
        }

        var annotated = preview.ToArray();
        foreach (var direction in directions)
        {
            if (projector.Project(direction) is not { } pixel)
            {
                continue;
            }

            var x = (int)Math.Round(pixel.X);
            var y = (int)Math.Round(pixel.Y);
            if (x >= 0 && x < width && y >= 0 && y < height)
            {
                annotated[y * width + x] = byte.MaxValue;
            }
        }

        return annotated;
    }
}
