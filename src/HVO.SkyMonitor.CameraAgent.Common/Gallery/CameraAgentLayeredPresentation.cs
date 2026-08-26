using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

public enum CameraAgentLayeredPresentationStatus
{
    Found,
    Unavailable,
    Malformed,
    TooLarge
}

public sealed record CameraAgentPresentationLayer(
    string IdentitySha256,
    string Kind,
    string DomGroupId,
    int ZOrder,
    bool EnabledByDefault,
    int OpacityMillionths,
    string RendererVersion,
    string StyleVersion);

public sealed record CameraAgentLayeredPresentation(
    Guid CaptureId,
    Guid BaseArtifactId,
    string ManifestIdentitySha256,
    string PresentationIdentitySha256,
    string SvgChecksumSha256,
    int WidthPixels,
    int HeightPixels,
    IReadOnlyList<CameraAgentPresentationLayer> Layers,
    ReadOnlyMemory<byte> Svg);

public sealed record CameraAgentLayeredPresentationResult(
    CameraAgentLayeredPresentationStatus Status,
    CameraAgentLayeredPresentation? Presentation = null,
    string? Reason = null);

public interface ICameraAgentLayeredPresentationService
{
    ValueTask<CameraAgentLayeredPresentationResult> GetAsync(
        Guid captureId,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentLayeredPresentationService(
    SqliteCaptureProcessingStore processingStore,
    ICameraAgentArtifactService artifacts) : ICameraAgentLayeredPresentationService
{
    internal const int MaximumSvgBytes = 2 * 1024 * 1024;
    internal const int MaximumSvgElements = 20_000;
    internal const int MaximumSourcePayloadBytes = 16 * 1024 * 1024;
    internal const int MaximumCacheEntries = 64;
    internal const long MaximumCacheBytes = 16L * 1024 * 1024;
    internal const string SvgRendererVersion = "cameraagent-grouped-svg-v1";
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _captureCache = [];
    private readonly Dictionary<Guid, Task<CameraAgentLayeredPresentationResult>> _flights = [];
    private long _cacheBytes;
    private long _sequence;

    public async ValueTask<CameraAgentLayeredPresentationResult> GetAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty)
        {
            return Unavailable("Capture presentation is unavailable.");
        }

        Task<CameraAgentLayeredPresentationResult> flight;
        lock (_cacheGate)
        {
            if (_captureCache.TryGetValue(captureId, out var identity) &&
                _cache.TryGetValue(identity, out var cached))
            {
                cached.Sequence = ++_sequence;
                return new(CameraAgentLayeredPresentationStatus.Found, cached.Presentation);
            }
            if (!_flights.TryGetValue(captureId, out flight!))
            {
                flight = BuildAsync(captureId);
                _flights.Add(captureId, flight);
            }
        }
        return await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated presentation boundary returns only fixed failure states for malformed retained evidence.")]
    private async Task<CameraAgentLayeredPresentationResult> BuildAsync(Guid captureId)
    {
        try
        {
            var manifests = await processingStore.ReadCaptureProductsAsync(
                captureId, OverlayManifestV1.CurrentSchemaVersion, 2, CancellationToken.None).ConfigureAwait(false);
            if (manifests.Count == 0)
            {
                return Unavailable("Structured layers were not retained for this capture.");
            }
            if (manifests.Count != 1 || manifests[0].AvailabilityState != "Available")
            {
                return Malformed("The retained layer manifest is ambiguous or unavailable.");
            }

            var manifestProduct = manifests[0];
            var manifestBytes = await ReadBytesAsync(
                manifestProduct.ArtifactId, PresentationProcessingProducts.ManifestMediaType, CancellationToken.None)
                .ConfigureAwait(false);
            if (manifestBytes.Status != CameraAgentLayeredPresentationStatus.Found)
            {
                return new(manifestBytes.Status, Reason: manifestBytes.Reason);
            }
            var parsed = LayeredPresentationJson.ParseManifest(manifestBytes.Content);
            if (!parsed.IsValid || parsed.Document is not { } manifest ||
                !string.Equals(manifest.ManifestIdentitySha256, manifestProduct.ContentIdentitySha256, StringComparison.Ordinal))
            {
                return Malformed("The retained layer manifest failed validation.");
            }

            var baseRead = await artifacts.OpenContentAsync(manifest.BaseProduct.ArtifactId, CancellationToken.None)
                .ConfigureAwait(false);
            if (baseRead.Status != CameraAgentArtifactReadStatus.Found || baseRead.Content is null)
            {
                return Unavailable("The presentation base image is unavailable.");
            }
            string baseChecksum;
            var baseContent = baseRead.Content;
            await using (baseContent.ConfigureAwait(false))
            {
                if (!string.Equals(baseContent.MediaType, manifest.BaseProduct.MediaType, StringComparison.OrdinalIgnoreCase) ||
                    baseContent.Descriptor?.Layout is not { } layout ||
                    layout.Width != manifest.BaseProduct.Compatibility.WidthPixels ||
                    layout.Height != manifest.BaseProduct.Compatibility.HeightPixels)
                {
                    return Malformed("The retained presentation base does not match its manifest.");
                }
                baseChecksum = baseContent.ChecksumSha256;
            }

            var payloads = new List<PresentationLayerPayloadV1>(manifest.Layers.Count);
            var sourcePayloadBytes = 0;
            var elementCount = 0;
            foreach (var layer in manifest.Layers)
            {
                var remainingPayloadBytes = MaximumSourcePayloadBytes - sourcePayloadBytes;
                if (remainingPayloadBytes <= 0)
                {
                    return TooLarge("The retained presentation metadata exceeds its aggregate bound.");
                }
                var read = await ReadBytesAsync(
                    layer.SourceProduct.ArtifactId, PresentationLayerPayloadJson.MediaType, CancellationToken.None,
                    remainingPayloadBytes)
                    .ConfigureAwait(false);
                if (read.Status != CameraAgentLayeredPresentationStatus.Found)
                {
                    return new(read.Status, Reason: read.Reason);
                }
                var payload = PresentationLayerPayloadJson.Parse(read.Content).Payload;
                if (payload is null ||
                    !string.Equals(payload.ContentIdentitySha256, layer.SourceProduct.ProductIdentitySha256, StringComparison.Ordinal) ||
                    payload.WidthPixels != manifest.BaseProduct.Compatibility.WidthPixels ||
                    payload.HeightPixels != manifest.BaseProduct.Compatibility.HeightPixels)
                {
                    return Malformed("A retained presentation layer failed validation.");
                }
                sourcePayloadBytes = checked(sourcePayloadBytes + read.Content.Length);
                elementCount = checked(elementCount + ElementCount(payload));
                if (elementCount > MaximumSvgElements)
                {
                    return TooLarge("The grouped presentation exceeds its element bound.");
                }
                payloads.Add(payload);
            }

            var identity = ComputePresentationIdentity(manifest, baseChecksum);
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(identity, out var cached))
                {
                    cached.Sequence = ++_sequence;
                    return new(CameraAgentLayeredPresentationStatus.Found, cached.Presentation);
                }
            }

            var rendered = Render(captureId, manifest, payloads, identity);
            if (rendered.Status == CameraAgentLayeredPresentationStatus.Found && rendered.Presentation is { } presentation)
            {
                AddCache(identity, presentation);
            }
            return rendered;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or OverflowException or XmlException)
        {
            return Malformed("The retained presentation evidence is malformed.");
        }
        finally
        {
            lock (_cacheGate)
            {
                _flights.Remove(captureId);
            }
        }
    }

    private async ValueTask<PayloadReadResult> ReadBytesAsync(
        Guid artifactId,
        string mediaType,
        CancellationToken cancellationToken,
        int byteBudget = LayeredPresentationJson.MaximumPayloadBytes)
    {
        var opened = await artifacts.OpenContentAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (opened.Status != CameraAgentArtifactReadStatus.Found || opened.Content is null)
        {
            return new(CameraAgentLayeredPresentationStatus.Unavailable, default,
                "A retained presentation product is unavailable.");
        }
        var content = opened.Content;
        await using var contentLease = content.ConfigureAwait(false);
        if (!string.Equals(content.MediaType, mediaType, StringComparison.OrdinalIgnoreCase) ||
            content.ByteLength is < 1 or > LayeredPresentationJson.MaximumPayloadBytes)
        {
            return new(CameraAgentLayeredPresentationStatus.Malformed, default,
                "A retained presentation product is malformed.");
        }
        if (content.ByteLength > byteBudget)
        {
            return new(CameraAgentLayeredPresentationStatus.TooLarge, default,
                "The retained presentation metadata exceeds its aggregate bound.");
        }
        var bytes = new byte[checked((int)content.ByteLength)];
        await content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new(CameraAgentLayeredPresentationStatus.Found, bytes);
    }

    internal static CameraAgentLayeredPresentationResult Render(
        Guid captureId,
        OverlayManifestV1 manifest,
        List<PresentationLayerPayloadV1> payloads,
        string presentationIdentity)
    {
        var elementCount = payloads.Sum(ElementCount);
        if (elementCount > MaximumSvgElements)
        {
            return new(CameraAgentLayeredPresentationStatus.TooLarge, Reason: "The grouped presentation exceeds its element bound.");
        }

        var buffer = new StringBuilder(Math.Min(MaximumSvgBytes, 4096 + elementCount * 64));
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings
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
            writer.WriteAttributeString("data-presentation-identity", presentationIdentity);
            for (var index = 0; index < manifest.Layers.Count; index++)
            {
                WriteLayer(writer, manifest.Layers[index], payloads[index], index);
            }
            writer.WriteEndElement();
        }
        var svg = Encoding.UTF8.GetBytes(buffer.ToString());
        if (svg.Length > MaximumSvgBytes)
        {
            return new(CameraAgentLayeredPresentationStatus.TooLarge, Reason: "The grouped presentation exceeds its payload bound.");
        }
        var layers = manifest.Layers.Select((layer, index) => new CameraAgentPresentationLayer(
            layer.LayerIdentitySha256, layer.LayerKind, DomGroupId(index), layer.ZOrder,
            layer.EnabledByDefault, layer.OpacityMillionths, layer.RendererVersion, layer.StyleVersion)).ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(svg));
        return new(CameraAgentLayeredPresentationStatus.Found, new CameraAgentLayeredPresentation(
            captureId, manifest.BaseProduct.ArtifactId, manifest.ManifestIdentitySha256, presentationIdentity,
            checksum, manifest.BaseProduct.Compatibility.WidthPixels, manifest.BaseProduct.Compatibility.HeightPixels,
            layers, svg));
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
        if (!layer.EnabledByDefault) writer.WriteAttributeString("display", "none");

        foreach (var segment in payload.Segments)
        {
            writer.WriteStartElement("line");
            Coordinate(writer, "x1", segment.From.X); Coordinate(writer, "y1", segment.From.Y);
            Coordinate(writer, "x2", segment.To.X); Coordinate(writer, "y2", segment.To.Y);
            writer.WriteAttributeString("stroke", Color(segment.Color));
            writer.WriteAttributeString("stroke-width", segment.Thickness.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        foreach (var ellipse in payload.Ellipses)
        {
            writer.WriteStartElement("ellipse");
            Coordinate(writer, "cx", ellipse.Center.X); Coordinate(writer, "cy", ellipse.Center.Y);
            Coordinate(writer, "rx", ellipse.RadiusX); Coordinate(writer, "ry", ellipse.RadiusY);
            writer.WriteAttributeString("fill", "none"); writer.WriteAttributeString("stroke", Color(ellipse.Color));
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
                Coordinate(writer, "cx", marker.Center.X); Coordinate(writer, "cy", marker.Center.Y);
                writer.WriteAttributeString("r", marker.Radius.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("fill", "none");
                writer.WriteAttributeString("stroke", Color(marker.Color));
            }
            writer.WriteEndElement();
        }
        foreach (var block in payload.TextBlocks)
        {
            var (x, y, anchor) = TextOrigin(block, payload.WidthPixels, payload.HeightPixels);
            for (var lineIndex = 0; lineIndex < block.Lines.Count; lineIndex++)
            {
                writer.WriteStartElement("text");
                writer.WriteAttributeString("x", Number(x));
                writer.WriteAttributeString("y", Number(y + lineIndex * (7 * block.Scale + block.LineSpacing)));
                writer.WriteAttributeString("fill", Color(block.Color));
                writer.WriteAttributeString("font-size", (7 * block.Scale).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("font-family", "monospace");
                writer.WriteAttributeString("text-anchor", anchor);
                writer.WriteString(block.Lines[lineIndex]);
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

    private static (double X, double Y, string Anchor) TextOrigin(
        PresentationTextBlockV1 block,
        int width,
        int height) => block.Anchor switch
        {
            PresentationTextAnchor.TopRight => (width - block.Inset, block.Inset + 7 * block.Scale, "end"),
            PresentationTextAnchor.BottomLeft => (block.Inset, height - block.Inset, "start"),
            PresentationTextAnchor.BottomRight => (width - block.Inset, height - block.Inset, "end"),
            PresentationTextAnchor.Point => (block.Point.X, block.Point.Y + 7 * block.Scale, "start"),
            _ => (block.Inset, block.Inset + 7 * block.Scale, "start")
        };

    private static string TileMaskPath(PresentationTileMaskV1 mask, int width, int height)
    {
        var path = new StringBuilder();
        for (var row = 0; row < mask.Rows; row++)
        {
            for (var column = 0; column < mask.Columns; column++)
            {
                var index = row * mask.Columns + column;
                if ((mask.Bits.Span[index >> 3] & 1 << (index & 7)) == 0) continue;
                var x = (long)column * width / mask.Columns;
                var y = (long)row * height / mask.Rows;
                var tileWidth = (long)(column + 1) * width / mask.Columns - x;
                var tileHeight = (long)(row + 1) * height / mask.Rows - y;
                path.Append('M').Append(x).Append(' ').Append(y)
                    .Append('h').Append(tileWidth).Append('v').Append(tileHeight)
                    .Append('h').Append(-tileWidth).Append('z');
                if (path.Length > MaximumSvgBytes) throw new InvalidDataException("Tile-mask SVG exceeds its payload bound.");
            }
        }
        return path.ToString();
    }

    private static string ComputePresentationIdentity(OverlayManifestV1 manifest, string baseChecksum)
    {
        var bytes = Encoding.ASCII.GetBytes(string.Join('|',
            SvgRendererVersion, manifest.ManifestIdentitySha256, baseChecksum,
            string.Join(',', manifest.Layers.Select(static layer => layer.LayerIdentitySha256))));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static int ElementCount(PresentationLayerPayloadV1 payload) => checked(
        payload.Markers.Count + payload.Segments.Count + payload.Ellipses.Count +
        payload.TextBlocks.Sum(static block => block.Lines.Count) + (payload.TileMask is null ? 0 : 1));

    private void AddCache(string identity, CameraAgentLayeredPresentation presentation)
    {
        lock (_cacheGate)
        {
            if (_cache.ContainsKey(identity)) return;
            var bytes = presentation.Svg.Length;
            if (bytes > MaximumCacheBytes) return;
            while (_cache.Count >= MaximumCacheEntries || _cacheBytes + bytes > MaximumCacheBytes)
            {
                var oldest = _cache.MinBy(static pair => pair.Value.Sequence);
                if (oldest.Key is null) break;
                _cache.Remove(oldest.Key);
                _captureCache.Remove(oldest.Value.Presentation.CaptureId);
                _cacheBytes -= oldest.Value.Presentation.Svg.Length;
            }
            _cache.Add(identity, new(presentation, ++_sequence));
            _captureCache[presentation.CaptureId] = identity;
            _cacheBytes += bytes;
        }
    }

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

    private static CameraAgentLayeredPresentationResult Unavailable(string reason) =>
        new(CameraAgentLayeredPresentationStatus.Unavailable, Reason: reason);

    private static CameraAgentLayeredPresentationResult Malformed(string reason) =>
        new(CameraAgentLayeredPresentationStatus.Malformed, Reason: reason);

    private static CameraAgentLayeredPresentationResult TooLarge(string reason) =>
        new(CameraAgentLayeredPresentationStatus.TooLarge, Reason: reason);

    private sealed class CacheEntry(CameraAgentLayeredPresentation presentation, long sequence)
    {
        internal CameraAgentLayeredPresentation Presentation { get; } = presentation;
        internal long Sequence { get; set; } = sequence;
    }

    private sealed record PayloadReadResult(
        CameraAgentLayeredPresentationStatus Status,
        ReadOnlyMemory<byte> Content,
        string? Reason = null);
}
