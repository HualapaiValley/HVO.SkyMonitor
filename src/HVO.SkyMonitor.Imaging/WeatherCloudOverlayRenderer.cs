using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

public sealed record WeatherCloudOverlayRenderOptions(
    int LineThickness = 1,
    byte MonoValue = byte.MaxValue,
    byte Red = byte.MaxValue,
    byte Green = 64,
    byte Blue = 32,
    bool DrawLabels = true,
    int MaximumLabelCharacters = 32);

public sealed record WeatherCloudOverlayResult(ReadOnlyMemory<byte> Pixels, string AlgorithmVersion);

/// <summary>Draws a tile mask and bounded supplied labels over a packed preview copy.</summary>
public static class WeatherCloudOverlayRenderer
{
    public const string AlgorithmVersion = "weather-cloud-overlay-raster-v1";

    public static WeatherCloudOverlayResult Render(
        ImageLayout layout,
        ReadOnlyMemory<byte> preview,
        int gridColumns,
        int gridRows,
        ReadOnlySpan<byte> cloudyMask,
        IReadOnlyList<string> labels,
        WeatherCloudOverlayRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        layout.Validate();
        options ??= new WeatherCloudOverlayRenderOptions();
        ArgumentNullException.ThrowIfNull(labels);
        if (layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
            layout.StrideBytes != checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)) ||
            preview.Length != layout.RequiredByteLength)
        {
            throw new ArgumentException("Weather/cloud overlay requires a packed Mono8 or RGB24 preview.", nameof(layout));
        }
        var tileCount = checked(gridColumns * gridRows);
        if (gridColumns < 1 || gridRows < 1 || gridColumns > layout.Width || gridRows > layout.Height ||
            cloudyMask.Length != (tileCount + 7) / 8 || options.LineThickness is < 1 or > 8 ||
            options.MaximumLabelCharacters is < 0 or > 128 || labels.Count > 16 ||
            labels.Any(label => label is null || label.Length > options.MaximumLabelCharacters))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var pixels = preview.ToArray();
        for (var row = 0; row < gridRows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y0 = (int)((long)row * layout.Height / gridRows);
            var y1 = (int)((long)(row + 1) * layout.Height / gridRows);
            for (var column = 0; column < gridColumns; column++)
            {
                var index = row * gridColumns + column;
                if ((cloudyMask[index >> 3] & 1 << (index & 7)) == 0)
                {
                    continue;
                }
                var x0 = (int)((long)column * layout.Width / gridColumns);
                var x1 = (int)((long)(column + 1) * layout.Width / gridColumns);
                DrawBorder(pixels, layout, x0, y0, x1, y1, options);
            }
        }

        if (options.DrawLabels && labels.Count > 0)
        {
            var objects = labels.Select((label, index) => new ProjectedAnnotationObject(
                $"weather-{index}",
                label,
                new PixelPoint(1, 1 + index * 9),
                DrawMark: false,
                DrawLabel: true)).ToArray();
            var annotationOptions = new AnnotationOptions
            {
                MarkRadius = 0,
                DrawLabels = true,
                MaximumLabelCharacters = options.MaximumLabelCharacters,
                LabelScale = 1,
                MarkValue = options.MonoValue
            };
            var annotated = layout.PixelFormat == CameraPixelFormat.Rgb24
                ? AnnotationRenderer.AnnotateRgb24WithSegments(
                    pixels, layout.Width, layout.Height, objects, [], new PreviewTransform(1, 1), annotationOptions,
                    cancellationToken: cancellationToken)
                : AnnotationRenderer.AnnotateMono8WithSegments(
                    pixels, layout.Width, layout.Height, objects, [], new PreviewTransform(1, 1), annotationOptions,
                    cancellationToken: cancellationToken);
            pixels = annotated.Pixels.ToArray();
        }

        return new WeatherCloudOverlayResult(pixels, AlgorithmVersion);
    }

    private static void DrawBorder(
        byte[] pixels,
        ImageLayout layout,
        int x0,
        int y0,
        int x1,
        int y1,
        WeatherCloudOverlayRenderOptions options)
    {
        for (var thickness = 0; thickness < options.LineThickness; thickness++)
        {
            var left = x0 + thickness;
            var right = x1 - 1 - thickness;
            var top = y0 + thickness;
            var bottom = y1 - 1 - thickness;
            if (left > right || top > bottom)
            {
                break;
            }
            for (var x = left; x <= right; x++)
            {
                SetPixel(pixels, layout, x, top, options);
                SetPixel(pixels, layout, x, bottom, options);
            }
            for (var y = top; y <= bottom; y++)
            {
                SetPixel(pixels, layout, left, y, options);
                SetPixel(pixels, layout, right, y, options);
            }
        }
    }

    private static void SetPixel(
        byte[] pixels,
        ImageLayout layout,
        int x,
        int y,
        WeatherCloudOverlayRenderOptions options)
    {
        var bytesPerPixel = ImageLayout.BytesPerPixel(layout.PixelFormat);
        var offset = y * layout.StrideBytes + x * bytesPerPixel;
        if (layout.PixelFormat == CameraPixelFormat.Mono8)
        {
            pixels[offset] = options.MonoValue;
            return;
        }
        pixels[offset] = options.Red;
        pixels[offset + 1] = options.Green;
        pixels[offset + 2] = options.Blue;
    }
}
