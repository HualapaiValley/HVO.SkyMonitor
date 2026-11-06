using System.Buffers;
using System.Text;
using SkiaSharp;

namespace HVO.SkyMonitor.Imaging;

public enum PresentationRichFontFaceV1 { MonoRegular, MonoBold, PlexRegular }

[Flags]
public enum PresentationRichEmphasisV1 { None = 0, Bold = 1, Italic = 2, Underline = 4 }

public sealed record PresentationRichRunV1(string Text, PresentationRichEmphasisV1 Emphasis);
public sealed record PresentationRichLineV1(IReadOnlyList<PresentationRichRunV1> Runs);

/// <summary>Source and evaluated text are both retained; text is never reparsed by a renderer.</summary>
public sealed record PresentationRichCornerV1(PresentationTextAnchor Anchor, string Template, string MissingPolicy,
    PresentationRichFontFaceV1 Font, int SizeMilliPixels, int Inset, int LineSpacing, PresentationColor Color,
    IReadOnlyList<PresentationRichLineV1> Lines);

public sealed record PresentationRichTextV1(string ContextIdentitySha256, string GrammarVersion, string CatalogVersion,
    string ProducerVersion, string LayoutVersion, string FontSetIdentity, IReadOnlyList<PresentationRichCornerV1> Corners);

/// <summary>Shared font paths, panel geometry and resource bounds for retained rich-text SVG and raster.</summary>
public static class PresentationRichText
{
    public const string LayoutVersion = "frame-facts-corner-layout-v1";
    public const string FontSetIdentity = PresentationFont.MonoRegularSha256 + ":" + PresentationFont.MonoBoldSha256 + ":" + PresentationFont.FontSha256;
    public const float ItalicShear = -0.21255656f; // tan(12 degrees), pinned independently of platform font synthesis.
    public const int PanelPadding = 6;
    public static PresentationColor PanelColor => new(3, 8, 14);
    public const int PanelOpacityMillionths = 800_000;

    public static void Validate(PresentationRichTextV1 rich, int width, int height)
    {
        if (rich is null || rich.LayoutVersion != LayoutVersion || rich.FontSetIdentity != FontSetIdentity ||
            rich.ContextIdentitySha256 is not { Length: 64 } || rich.ContextIdentitySha256.Any(c => !char.IsAsciiHexDigitUpper(c) && !char.IsAsciiDigit(c)) ||
            !Bounded(rich.GrammarVersion, 128) || !Bounded(rich.CatalogVersion, 128) || !Bounded(rich.ProducerVersion, 128) ||
            rich.Corners is null || rich.Corners.Count != 4 || rich.Corners.Any(corner => corner is null) ||
            rich.Corners.Select(corner => corner.Anchor).Distinct().Count() != 4)
            throw new ArgumentException("Rich-text contract is invalid.", nameof(rich));
        foreach (var corner in rich.Corners)
        {
            if (corner.Anchor is not (PresentationTextAnchor.TopLeft or PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomLeft or PresentationTextAnchor.BottomRight) ||
                corner.Template is null || corner.Template.Length > 4096 || corner.MissingPolicy is not ("Placeholder" or "DropLine") ||
                !Enum.IsDefined(corner.Font) || corner.SizeMilliPixels is < 4_000 or > 64_000 || corner.Inset is < 0 or > 64 ||
                corner.LineSpacing is < 0 or > 16 || corner.Lines is null || corner.Lines.Count > 8)
                throw new ArgumentException("Rich-text corner is invalid.", nameof(rich));
            foreach (var line in corner.Lines)
            {
                if (line?.Runs is null || line.Runs.Count > 64)
                    throw new ArgumentException("Rich-text runs exceed their bound.", nameof(rich));
                var characters = 0;
                foreach (var run in line.Runs)
                {
                    if (run is null || run.Text is null || (run.Emphasis & ~(PresentationRichEmphasisV1)7) != 0 ||
                        corner.Font == PresentationRichFontFaceV1.PlexRegular && run.Emphasis.HasFlag(PresentationRichEmphasisV1.Bold))
                        throw new ArgumentException("Rich-text emphasis/font combination is invalid.", nameof(rich));
                    for (var offset = 0; offset < run.Text.Length;)
                    {
                        var status = Rune.DecodeFromUtf16(run.Text.AsSpan(offset), out var rune, out var consumed);
                        if (status != OperationStatus.Done || Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029 || ++characters > 64)
                            throw new ArgumentException("Rich-text line is invalid or exceeds 64 scalars.", nameof(rich));
                        offset += consumed;
                    }
                }
            }
            // Layout is bounded before a compositor allocates its full-frame output.
            using var layout = Layout(corner, width, height);
        }
    }

    /// <summary>Each corner owns one quadrant. Oversize panels fail as a whole, without clipping or shrinking text.</summary>
    public static PresentationRichLayout Layout(PresentationRichCornerV1 corner, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(corner);
        using var builder = new SKPathBuilder();
        var size = corner.SizeMilliPixels / 1000f;
        var maximumAdvance = 0f;
        for (var lineIndex = 0; lineIndex < corner.Lines.Count; lineIndex++)
        {
            var x = 0f;
            var baseline = size + lineIndex * (size * 1.5f + corner.LineSpacing);
            foreach (var run in corner.Lines[lineIndex].Runs)
            {
                using var font = PresentationFont.Create(corner.Font, corner.SizeMilliPixels, run.Emphasis.HasFlag(PresentationRichEmphasisV1.Bold));
                using var path = font.GetTextPath(run.Text, new SKPoint(0, 0));
                var matrix = SKMatrix.CreateSkew(run.Emphasis.HasFlag(PresentationRichEmphasisV1.Italic) ? ItalicShear : 0, 0);
                matrix.TransX = x;
                matrix.TransY = baseline;
                builder.AddPath(path, matrix);
                var advance = font.MeasureText(run.Text);
                if (run.Emphasis.HasFlag(PresentationRichEmphasisV1.Underline) && advance > 0)
                    builder.AddRect(new SKRect(x, baseline + size * 0.12f, x + advance, baseline + size * 0.12f + Math.Max(1, size / 16)));
                x += advance;
            }
            maximumAdvance = Math.Max(maximumAdvance, x);
        }
        using var original = builder.Detach();
        if (corner.Lines.Count == 0) return new(builder.Detach(), SKRect.Empty);
        var bounds = original.Bounds;
        var left = Math.Min(0, bounds.Left);
        var top = Math.Min(0, bounds.Top);
        var right = Math.Max(maximumAdvance, bounds.Right);
        var bottom = Math.Max(size * 1.5f * corner.Lines.Count + corner.LineSpacing * (corner.Lines.Count - 1), bounds.Bottom);
        var panelWidth = MathF.Ceiling(right - left + 2 * PanelPadding);
        var panelHeight = MathF.Ceiling(bottom - top + 2 * PanelPadding);
        if (panelWidth + corner.Inset > width / 2f || panelHeight + corner.Inset > height / 2f)
            throw new ArgumentException("Frame Facts corner exceeds its image quadrant.", nameof(corner));
        var panelX = corner.Anchor is PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomRight ? width - corner.Inset - panelWidth : corner.Inset;
        var panelY = corner.Anchor is PresentationTextAnchor.BottomLeft or PresentationTextAnchor.BottomRight ? height - corner.Inset - panelHeight : corner.Inset;
        using var translated = new SKPathBuilder();
        translated.AddPath(original, panelX + PanelPadding - left, panelY + PanelPadding - top);
        return new(translated.Detach(), new(panelX, panelY, panelX + panelWidth, panelY + panelHeight));
    }

    public static PresentationRichTextV1 Freeze(PresentationRichTextV1 rich)
    {
        ArgumentNullException.ThrowIfNull(rich);
        if (rich.Corners is null || rich.Corners.Count != 4 || rich.Corners.Any(corner => corner?.Lines is null ||
                corner.Lines.Count > 8 || corner.Lines.Any(line => line?.Runs is null || line.Runs.Count > 64)))
            throw new ArgumentException("Rich-text collections exceed their bounds.", nameof(rich));
        return rich with
        {
            Corners = Array.AsReadOnly(rich.Corners.Select(corner => corner with
            {
                Lines = Array.AsReadOnly(corner.Lines.Select(line => line with { Runs = Array.AsReadOnly(line.Runs.ToArray()) }).ToArray())
            }).ToArray())
        };
    }

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}

/// <summary>One owned native path and its immutable panel rectangle.</summary>
public sealed class PresentationRichLayout(SKPath path, SKRect panel) : IDisposable
{
    public SKPath Path { get; } = path;
    public SKRect Panel { get; } = panel;
    public void Dispose() => Path.Dispose();
}
