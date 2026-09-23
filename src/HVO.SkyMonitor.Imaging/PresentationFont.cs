using System.Security.Cryptography;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Embedded, pinned font and shared layout for pixel and vector presentation text.</summary>
public static class PresentationFont
{
    public const string FontSha256 = "975DCDA37D80F038DCD143C22E33CA2D97A0CC5A929AACE1C749153B0FE1AFA5";
    private static readonly Lazy<SKTypeface> TypefaceSource = new(() =>
    {
        using var stream = typeof(PresentationFont).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Imaging.Fonts.IBMPlexSans-Regular.ttf")
            ?? throw new InvalidDataException("Embedded presentation font is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var fontBytes = bytes.ToArray();
        if (Convert.ToHexString(SHA256.HashData(fontBytes)) != FontSha256)
            throw new InvalidDataException("Embedded presentation font checksum mismatch.");
        using var data = SKData.CreateCopy(fontBytes);
        return SKTypeface.FromData(data) ?? throw new InvalidDataException("Embedded presentation font is invalid.");
    });

    public static SKFont Create(int scale) => new(TypefaceSource.Value, 7 * scale);

    public static int FrameScale(int width, int height, int minimum = 1) =>
        Math.Clamp(Math.Max(minimum, (int)Math.Round(Math.Min(width, height) * 0.019 / 5,
            MidpointRounding.AwayFromZero)), 1, 16);

    public static int Halo(int scale) => scale > 2 ? Math.Max(1, scale / 4) : 0;

    public static SKPath LinePath(SKFont font, string text, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(font);
        using var original = font.GetTextPath(text, new SKPoint(0, 0));
        var bounds = original.Bounds;
        using var builder = new SKPathBuilder();
        builder.AddPath(original, x - bounds.Left, y - bounds.Top);
        return builder.Detach();
    }

    public static SKRect LineBounds(SKFont font, string text, float x, float y)
    {
        using var path = LinePath(font, text, x, y);
        return path.Bounds;
    }

    public static (float X, float Y) LineOrigin(PresentationTextBlockV1 block, int width, int height,
        SKFont font, string line, int index)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(font);
        var bounds = LineBounds(font, line, 0, 0);
        var lineHeight = font.Size * 1.2f;
        var blockHeight = (block.Lines.Count - 1) * (lineHeight + block.LineSpacing) + font.Size;
        var x = block.Anchor switch
        {
            PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomRight => width - block.Inset - bounds.Width,
            PresentationTextAnchor.Point => (float)Math.Round(block.Point.X, MidpointRounding.AwayFromZero),
            _ => block.Inset
        };
        var y = block.Anchor switch
        {
            PresentationTextAnchor.BottomLeft or PresentationTextAnchor.BottomRight => height - block.Inset - blockHeight,
            PresentationTextAnchor.Point => (float)Math.Round(block.Point.Y, MidpointRounding.AwayFromZero),
            _ => block.Inset
        } + index * (lineHeight + block.LineSpacing);
        return (x, y);
    }
}
