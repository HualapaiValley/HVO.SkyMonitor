using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public sealed record GroupedSvgPresentationLayer(
    string IdentitySha256,
    string Kind,
    string DomGroupId,
    int ZOrder,
    bool EnabledByDefault,
    int OpacityMillionths,
    string RendererVersion,
    string StyleVersion);

public sealed record GroupedSvgPresentation(
    string PresentationIdentitySha256,
    string SvgChecksumSha256,
    int WidthPixels,
    int HeightPixels,
    IReadOnlyList<GroupedSvgPresentationLayer> Layers,
    ReadOnlyMemory<byte> Svg);

public static class GroupedSvgPresentationRenderer
{
    public const int MaximumSvgBytes = 2 * 1024 * 1024;
    public const int MaximumSvgElements = 20_000;
    public const string RendererVersion = "cameraagent-grouped-svg-v2";

    public static GroupedSvgPresentation Render(
        OverlayManifestV1 manifest,
        IReadOnlyList<PresentationLayerPayloadV1> payloads,
        string baseChecksumSha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseChecksumSha256);
        if (payloads.Count != manifest.Layers.Count)
        {
            throw new ArgumentException("The payload count must match the manifest layer count.", nameof(payloads));
        }
        if (manifest.Layers.Any(static layer => layer.CoordinateSpace != PresentationCoordinateSpace.ScenePixels))
        {
            throw new ArgumentException("Grouped SVG rendering supports ScenePixels layers only.", nameof(manifest));
        }

        var elementCount = payloads.Sum(ElementCount);
        if (elementCount > MaximumSvgElements)
        {
            throw new InvalidDataException("The grouped presentation exceeds its element bound.");
        }

        var identity = ComputeIdentity(manifest, baseChecksumSha256);
        var buffer = new StringBuilder(Math.Min(MaximumSvgBytes, 4096 + elementCount * 64));
        using (var output = new BoundedSvgWriter(buffer))
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Document,
            NewLineHandling = NewLineHandling.None
        }))
        {
            writer.WriteStartElement("svg", "http://www.w3.org/2000/svg");
            writer.WriteAttributeString("viewBox", FormattableString.Invariant(
                $"0 0 {manifest.BaseProduct.Compatibility.WidthPixels} {manifest.BaseProduct.Compatibility.HeightPixels}"));
            writer.WriteAttributeString("role", "img");
            writer.WriteAttributeString("aria-label", "Selectable capture presentation layers");
            writer.WriteAttributeString("data-presentation-identity", identity);
            for (var index = 0; index < manifest.Layers.Count; index++)
            {
                WriteLayer(writer, manifest.Layers[index], payloads[index], index);
            }
            writer.WriteEndElement();
        }

        var svg = Encoding.UTF8.GetBytes(buffer.ToString());
        if (svg.Length > MaximumSvgBytes)
        {
            throw new InvalidDataException("The grouped presentation exceeds its payload bound.");
        }
        var layers = manifest.Layers.Select((layer, index) => new GroupedSvgPresentationLayer(
            layer.LayerIdentitySha256,
            layer.LayerKind,
            DomGroupId(index),
            layer.ZOrder,
            layer.EnabledByDefault,
            layer.OpacityMillionths,
            layer.RendererVersion,
            layer.StyleVersion)).ToArray();
        return new GroupedSvgPresentation(
            identity,
            Convert.ToHexString(SHA256.HashData(svg)),
            manifest.BaseProduct.Compatibility.WidthPixels,
            manifest.BaseProduct.Compatibility.HeightPixels,
            layers,
            svg);
    }

    public static string ComputeIdentity(OverlayManifestV1 manifest, string baseChecksumSha256)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseChecksumSha256);
        if (baseChecksumSha256.Length != 64 || baseChecksumSha256.Any(static character =>
                character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new ArgumentException("Base checksum must be a canonical SHA-256 value.", nameof(baseChecksumSha256));
        }
        var bytes = Encoding.ASCII.GetBytes(string.Join('|',
            RendererVersion,
            manifest.ManifestIdentitySha256,
            baseChecksumSha256,
            string.Join(',', manifest.Layers.Select(static layer => layer.LayerIdentitySha256))));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void WriteLayer(
        XmlWriter writer,
        PresentationLayerV1 layer,
        PresentationLayerPayloadV1 payload,
        int index)
    {
        writer.WriteStartElement("g");
        writer.WriteAttributeString("id", DomGroupId(index));
        writer.WriteAttributeString("data-layer-identity", layer.LayerIdentitySha256);
        writer.WriteAttributeString("data-layer-kind", layer.LayerKind);
        writer.WriteAttributeString("opacity", Number(layer.OpacityMillionths / 1_000_000d));
        if (layer.BlendMode != PresentationBlendMode.Normal)
        {
            writer.WriteAttributeString("style", FormattableString.Invariant(
                $"mix-blend-mode:{BlendMode(layer.BlendMode)}"));
        }
        if (!layer.EnabledByDefault)
        {
            writer.WriteAttributeString("display", "none");
        }

        foreach (var segment in payload.Segments)
        {
            writer.WriteStartElement("line");
            Coordinate(writer, "x1", segment.From.X);
            Coordinate(writer, "y1", segment.From.Y);
            Coordinate(writer, "x2", segment.To.X);
            Coordinate(writer, "y2", segment.To.Y);
            writer.WriteAttributeString("stroke", Color(segment.Color));
            writer.WriteAttributeString("stroke-width", segment.Thickness.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        foreach (var ellipse in payload.Ellipses)
        {
            writer.WriteStartElement("ellipse");
            Coordinate(writer, "cx", ellipse.Center.X);
            Coordinate(writer, "cy", ellipse.Center.Y);
            Coordinate(writer, "rx", ellipse.RadiusX);
            Coordinate(writer, "ry", ellipse.RadiusY);
            writer.WriteAttributeString("fill", "none");
            writer.WriteAttributeString("stroke", Color(ellipse.Color));
            writer.WriteEndElement();
        }
        foreach (var marker in payload.Markers)
        {
            writer.WriteStartElement(marker.Radius == 0 ? "rect" : "circle");
            if (marker.Radius == 0)
            {
                writer.WriteAttributeString("x", Math.Round(marker.Center.X, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("y", Math.Round(marker.Center.Y, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("width", "1");
                writer.WriteAttributeString("height", "1");
                writer.WriteAttributeString("fill", Color(marker.Color));
            }
            else
            {
                Coordinate(writer, "cx", marker.Center.X);
                Coordinate(writer, "cy", marker.Center.Y);
                writer.WriteAttributeString("r", marker.Radius.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("fill", "none");
                writer.WriteAttributeString("stroke", Color(marker.Color));
            }
            writer.WriteEndElement();
        }
        foreach (var block in payload.TextBlocks)
        {
            for (var lineIndex = 0; lineIndex < block.Lines.Count; lineIndex++)
            {
                var line = block.Lines[lineIndex];
                var scale = block.Scale;
                var lineHeight = 7 * scale;
                var blockHeight = block.Lines.Count * lineHeight + (block.Lines.Count - 1) * block.LineSpacing;
                var width = (line.Length * 6 - 1) * scale;
                var x = block.Anchor switch
                {
                    PresentationTextAnchor.TopRight or PresentationTextAnchor.BottomRight => payload.WidthPixels - block.Inset - width,
                    PresentationTextAnchor.Point => Math.Round(block.Point.X, MidpointRounding.AwayFromZero),
                    _ => block.Inset
                };
                var y = block.Anchor switch
                {
                    PresentationTextAnchor.BottomLeft or PresentationTextAnchor.BottomRight => payload.HeightPixels - block.Inset - blockHeight,
                    PresentationTextAnchor.Point => Math.Round(block.Point.Y, MidpointRounding.AwayFromZero),
                    _ => block.Inset
                } + lineIndex * (lineHeight + block.LineSpacing);
                var path = new StringBuilder();
                for (var character = 0; character < line.Length; character++)
                {
                    var rows = PresentationLayerCompositor.Glyph(line[character]);
                    for (var row = 0; row < 7; row++)
                        for (var column = 0; column < 5; column++)
                            if ((rows[row] & 1 << (4 - column)) != 0)
                                path.Append('M').Append(Number(x + (character * 6 + column) * scale))
                                    .Append(' ').Append(Number(y + row * scale))
                                    .Append('h').Append(scale).Append('v').Append(scale)
                                    .Append('h').Append(-scale).Append('z');
                    if (path.Length > MaximumSvgBytes)
                        throw new InvalidDataException("Text SVG exceeds its payload bound.");
                }
                writer.WriteStartElement("path");
                writer.WriteAttributeString("d", path.ToString());
                writer.WriteAttributeString("fill", Color(block.Color));
                if (block.Scale > 2)
                {
                    writer.WriteAttributeString("stroke", "#000000");
                    writer.WriteAttributeString("stroke-width", Number(2 * Math.Max(1, block.Scale / 4)));
                    writer.WriteAttributeString("stroke-linejoin", "round");
                    writer.WriteAttributeString("paint-order", "stroke fill");
                }
                writer.WriteEndElement();
            }
        }
        if (payload.TileMask is { } mask)
        {
            writer.WriteStartElement("path");
            writer.WriteAttributeString("d", TileMaskPath(mask, payload.WidthPixels, payload.HeightPixels));
            writer.WriteAttributeString("fill", "none");
            writer.WriteAttributeString("stroke", Color(mask.Color));
            writer.WriteAttributeString("stroke-width", mask.LineThickness.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static string TileMaskPath(PresentationTileMaskV1 mask, int width, int height)
    {
        var path = new StringBuilder();
        for (var row = 0; row < mask.Rows; row++)
        {
            for (var column = 0; column < mask.Columns; column++)
            {
                var index = row * mask.Columns + column;
                if ((mask.Bits.Span[index >> 3] & 1 << (index & 7)) == 0)
                {
                    continue;
                }
                var x = (long)column * width / mask.Columns;
                var y = (long)row * height / mask.Rows;
                var tileWidth = (long)(column + 1) * width / mask.Columns - x;
                var tileHeight = (long)(row + 1) * height / mask.Rows - y;
                path.Append('M').Append(x).Append(' ').Append(y)
                    .Append('h').Append(tileWidth).Append('v').Append(tileHeight)
                    .Append('h').Append(-tileWidth).Append('z');
                if (path.Length > MaximumSvgBytes)
                {
                    throw new InvalidDataException("Tile-mask SVG exceeds its payload bound.");
                }
            }
        }
        return path.ToString();
    }

    private static int ElementCount(PresentationLayerPayloadV1 payload) => checked(
        payload.Markers.Count + payload.Segments.Count + payload.Ellipses.Count +
        payload.TextBlocks.Sum(static block => block.Lines.Count) + (payload.TileMask is null ? 0 : 1));

    private static void Coordinate(XmlWriter writer, string name, double value) =>
        writer.WriteAttributeString(name, Number(value));

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Color(PresentationColor color) => FormattableString.Invariant(
        $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}");

    private static string BlendMode(PresentationBlendMode blendMode) => blendMode switch
    {
        PresentationBlendMode.Multiply => "multiply",
        PresentationBlendMode.Screen => "screen",
        PresentationBlendMode.Lighten => "lighten",
        _ => "normal"
    };

    private static string DomGroupId(int index) => FormattableString.Invariant($"hvo-layer-{index}");

    private sealed class BoundedSvgWriter(StringBuilder buffer) : StringWriter(buffer, CultureInfo.InvariantCulture)
    {
        private void Check(int length)
        {
            // UTF-8 uses at least as many bytes as UTF-16 code units for valid XML text.
            if (length > MaximumSvgBytes - GetStringBuilder().Length)
                throw new InvalidDataException("The grouped presentation exceeds its payload bound.");
        }

        public override void Write(char value)
        {
            Check(1);
            base.Write(value);
        }

        public override void Write(string? value)
        {
            Check(value?.Length ?? 0);
            base.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Check(count);
            base.Write(buffer, index, count);
        }

        public override void Write(ReadOnlySpan<char> value)
        {
            Check(value.Length);
            base.Write(value);
        }
    }
}
