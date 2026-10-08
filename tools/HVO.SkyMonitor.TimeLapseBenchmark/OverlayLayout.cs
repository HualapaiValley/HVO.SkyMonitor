using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.TimeLapseBenchmark;

internal static partial class Program
{
    private static (PresentationSceneLayerPayloadsV2, PresentationLayerPayloadV1) ResponsiveOverlayLayout(
        PresentationSceneLayerPayloadsV2 groups, PresentationLayerPayloadV1 metadata)
    {
        var width = metadata.WidthPixels;
        var height = metadata.HeightPixels;
        // Prototype compass style is 18 pixels in a 630-pixel-high view box.
        var unit = Math.Min(width, height) / 630d;
        var cardinals = groups.CardinalDirections.TextBlocks.Select(block =>
        {
            using var previousFont = PresentationFont.Create(block.Appearance!.Body);
            var previousGlyph = PresentationFont.LineBounds(previousFont, block.Lines[0], 0, 0);
            var centerX = block.Point.X + previousGlyph.Width / 2;
            var centerY = block.Point.Y + previousGlyph.Height / 2;
            var body = block.Appearance.Body with
            {
                SizeMilliPixels = Math.Min(112_000, (int)Math.Round(18_000 * unit)),
                LetterSpacingMilliPixels = Math.Min(8_000, (int)Math.Round(720 * unit)),
                HaloWidthMilliPixels = Math.Min(16_000, (int)Math.Round(4_000 * unit))
            };
            var plate = block.Backplate! with
            {
                Padding = Math.Min(32, (int)Math.Round(5 * unit)),
                Style = block.Backplate.Style! with
                {
                    BorderWidthMilliPixels = Math.Min(8_000, (int)Math.Round(1_700 * unit)),
                    MinimumWidthMilliPixels = Math.Min(256_000, (int)Math.Round(38_000 * unit)),
                    MinimumHeightMilliPixels = Math.Min(256_000, (int)Math.Round(25_000 * unit)),
                    CornerRadiusMilliPixels = Math.Min(32_000, (int)Math.Round(5_000 * unit))
                }
            };
            using var font = PresentationFont.Create(body);
            var glyph = PresentationFont.LineBounds(font, block.Lines[0], 0, 0);
            var result = block with
            {
                Point = new PixelPoint(centerX - glyph.Width / 2, centerY - glyph.Height / 2),
                Appearance = new(body), Backplate = plate
            };
            var bounds = PresentationFont.BackplateBounds(result, width, height, font);
            var edge = Math.Max(2, unit);
            var dx = bounds.Left < edge ? edge - bounds.Left : bounds.Right > width - edge ? width - edge - bounds.Right : 0;
            var dy = bounds.Top < edge ? edge - bounds.Top : bounds.Bottom > height - edge ? height - edge - bounds.Bottom : 0;
            result = result with { Point = new PixelPoint(result.Point.X + dx, result.Point.Y + dy) };
            bounds = PresentationFont.BackplateBounds(result, width, height, font);
            if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > width || bounds.Bottom > height)
                throw new InvalidDataException("Compass backplate is clipped.");
            return result;
        }).ToArray();
        if (cardinals.Length != 4) throw new InvalidDataException("The comparison expects four cardinal landmarks.");
        var corners = metadata.TextBlocks.Select(block =>
        {
            // One image pixel absorbs floor/ceiling of fractional glyph metrics.
            // The backplate still meets either image edge within one pixel.
            var result = block with { Inset = block.Backplate!.Padding + 1 };
            using var font = PresentationFont.Create(result, 0);
            var bounds = PresentationFont.BackplateBounds(result, width, height, font);
            var right = result.Anchor is PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomRight;
            if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > width || bounds.Bottom > height ||
                (right ? Math.Abs(bounds.Right - width) : Math.Abs(bounds.Left)) > 1)
                throw new InvalidDataException($"Corner {result.Anchor} bounds {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom} do not meet {width}x{height} image edges.");
            return result;
        }).ToArray();
        var circle = groups.ImageCircle.Ellipses.Select(ellipse => ellipse with
        {
            ThicknessMilliPixels = Math.Min(8_000, (int)Math.Round(2_000 * unit))
        }).ToArray();
        return (groups with
        {
            CardinalDirections = PresentationLayerPayloadJson.Create(groups.CardinalDirections.SourceIdentitySha256,
                width, height, textBlocks: cardinals),
            ImageCircle = PresentationLayerPayloadJson.Create(groups.ImageCircle.SourceIdentitySha256,
                width, height, ellipses: circle)
        }, PresentationLayerPayloadJson.Create(metadata.SourceIdentitySha256, width, height, textBlocks: corners));
    }
}
