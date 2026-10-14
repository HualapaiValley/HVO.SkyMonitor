using System.Security.Cryptography;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Embedded, pinned font and shared layout for pixel and vector presentation text.</summary>
public static class PresentationFont
{
    public const string FontSha256 = "975DCDA37D80F038DCD143C22E33CA2D97A0CC5A929AACE1C749153B0FE1AFA5";
    public const string MonoRegularSha256 = "C805F9436DBC268644C1D9584F01A601A653E028E08FD74B9B949F6CF8304D88";
    public const string MonoBoldSha256 = "3A3C502EEFF669A231549E80DF9F7C49DE109BAFE303170409E905D0B31A38FE";
    private static readonly Lazy<SKTypeface> TypefaceSource = new(() => Load("IBMPlexSans-Regular.ttf", FontSha256));
    private static readonly Lazy<SKTypeface> MonoRegular = new(() => Load("DejaVuSansMono.ttf", MonoRegularSha256));
    private static readonly Lazy<SKTypeface> MonoBold = new(() => Load("DejaVuSansMono-Bold.ttf", MonoBoldSha256));

    private static SKTypeface Load(string name, string checksum)
    {
        using var stream = typeof(PresentationFont).Assembly.GetManifestResourceStream(
            $"HVO.SkyMonitor.Imaging.Fonts.{name}")
            ?? throw new InvalidDataException("Embedded presentation font is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var fontBytes = bytes.ToArray();
        if (Convert.ToHexString(SHA256.HashData(fontBytes)) != checksum)
            throw new InvalidDataException("Embedded presentation font checksum mismatch.");
        using var data = SKData.CreateCopy(fontBytes);
        return SKTypeface.FromData(data) ?? throw new InvalidDataException("Embedded presentation font is invalid.");
    }

    public static SKFont Create(int scale) => new(TypefaceSource.Value, 7 * scale);

    public static SKFont Create(PresentationTextStyleV3 style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new(style.Face switch
        {
            PresentationFontFaceV3.MonoRegular => MonoRegular.Value,
            PresentationFontFaceV3.MonoBold => MonoBold.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        }, style.SizeMilliPixels / 1000f);
    }

    public static PresentationTextStyleV3? LineStyle(PresentationTextBlockV1 block, int index)
    {
        ArgumentNullException.ThrowIfNull(block);
        return index == 0 && block.Appearance?.Heading is { } heading ? heading : block.Appearance?.Body;
    }

    public static SKFont Create(PresentationTextBlockV1 block, int index)
    {
        ArgumentNullException.ThrowIfNull(block);
        return LineStyle(block, index) is { } style ? Create(style) : Create(block.Scale);
    }

    public static int FrameScale(int width, int height, int minimum = 1) =>
        Math.Clamp(Math.Max(minimum, (int)Math.Round(Math.Min(width, height) * 0.019 / 5,
            MidpointRounding.AwayFromZero)), 1, 16);

    public static int StarFrameScale(int width, int height, int minimum = 1) =>
        Math.Clamp(Math.Max(minimum, (int)Math.Round(Math.Min(width, height) * 0.013 / 7,
            MidpointRounding.AwayFromZero)), 1, 16);

    public static int Halo(int scale) => scale > 2 ? Math.Max(1, scale / 4) : 0;

    public static SKPath LinePath(SKFont font, string text, float x, float y, float letterSpacing = 0)
    {
        ArgumentNullException.ThrowIfNull(font);
        using var original = letterSpacing == 0 ? font.GetTextPath(text, new SKPoint(0, 0)) : TrackedPath(font, text, letterSpacing);
        var bounds = original.Bounds;
        using var builder = new SKPathBuilder();
        builder.AddPath(original, x - bounds.Left, y - bounds.Top);
        return builder.Detach();
    }

    private static SKPath TrackedPath(SKFont font, string text, float letterSpacing)
    {
        var positions = font.GetGlyphPositions(text, new SKPoint(0, 0));
        for (var index = 0; index < positions.Length; index++) positions[index].X += index * letterSpacing;
        return font.GetTextPath(text, positions);
    }

    public static SKRect LineBounds(SKFont font, string text, float x, float y, float letterSpacing = 0)
    {
        using var path = LinePath(font, text, x, y, letterSpacing);
        return path.Bounds;
    }

    /// <summary>Returns the same bounded text plate rectangle for both rendering targets.</summary>
    public static SKRect BackplateBounds(PresentationTextBlockV1 block, int width, int height, SKFont font)
    {
        ArgumentNullException.ThrowIfNull(block);
        var bounds = SKRect.Empty;
        for (var index = 0; index < block.Lines.Count; index++)
        {
            using var lineFont = Create(block, index);
            var selectedFont = block.Appearance is null ? font : lineFont;
            var (x, y) = LineOrigin(block, width, height, selectedFont, block.Lines[index], index);
            var line = LineBounds(selectedFont, block.Lines[index], x, y,
                (LineStyle(block, index)?.LetterSpacingMilliPixels ?? 0) / 1000f);
            bounds = index == 0 ? line : SKRect.Union(bounds, line);
        }
        bounds.Inflate(block.Backplate?.Padding ?? 0, block.Backplate?.Padding ?? 0);
        if (block.Backplate?.Style is { } plate)
        {
            var minimumWidth = (plate.MinimumWidthMilliPixels ?? 0) / 1000f;
            var minimumHeight = (plate.MinimumHeightMilliPixels ?? 0) / 1000f;
            if (minimumWidth > bounds.Width)
            {
                var left = MathF.Floor(bounds.MidX - minimumWidth / 2);
                bounds = new(left, bounds.Top, left + minimumWidth, bounds.Bottom);
            }
            if (minimumHeight > bounds.Height)
            {
                var top = MathF.Floor(bounds.MidY - minimumHeight / 2);
                bounds = new(bounds.Left, top, bounds.Right, top + minimumHeight);
            }
        }
        return new(MathF.Floor(bounds.Left), MathF.Floor(bounds.Top), MathF.Ceiling(bounds.Right), MathF.Ceiling(bounds.Bottom));
    }

    public static (float X, float Y) LineOrigin(PresentationTextBlockV1 block, int width, int height,
        SKFont font, string line, int index)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(font);
        if (block.Appearance is not null) return StyledLineOrigin(block, width, height, index);
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

    private static (float X, float Y) StyledLineOrigin(PresentationTextBlockV1 block, int width, int height, int index)
    {
        var maximumWidth = 0f;
        var blockHeight = 0f;
        var offset = 0f;
        for (var lineIndex = 0; lineIndex < block.Lines.Count; lineIndex++)
        {
            var style = LineStyle(block, lineIndex)!;
            using var font = Create(style);
            var bounds = LineBounds(font, block.Lines[lineIndex], 0, 0, style.LetterSpacingMilliPixels / 1000f);
            maximumWidth = Math.Max(maximumWidth, bounds.Width);
            if (lineIndex == index) offset = blockHeight;
            blockHeight += lineIndex == block.Lines.Count - 1 ? bounds.Height : font.Size * 1.2f + block.LineSpacing;
        }
        var x = block.Anchor switch
        {
            PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomRight => width - block.Inset - maximumWidth,
            PresentationTextAnchor.Point => (float)Math.Round(block.Point.X, MidpointRounding.AwayFromZero),
            _ => block.Inset
        };
        var y = block.Anchor switch
        {
            PresentationTextAnchor.BottomLeft or PresentationTextAnchor.BottomRight => height - block.Inset - blockHeight,
            PresentationTextAnchor.Point => (float)Math.Round(block.Point.Y, MidpointRounding.AwayFromZero),
            _ => block.Inset
        };
        return (x, y + offset);
    }

    public static float HeadingRuleY(PresentationTextBlockV1 block, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(block);
        using var font = Create(block, 0);
        var (_, y) = LineOrigin(block, width, height, font, block.Lines[0], 0);
        return y + font.Size * 1.2f + block.LineSpacing / 2f;
    }
}
