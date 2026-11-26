using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace HVO.SkyMonitor.Imaging;

internal static class FitsScientificWriter
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;
    private const int DescriptorSize = 8;

    public static async Task WritePrimaryAsync(Stream output, ScientificCaptureExport.PixelReader pixels,
        ScientificExportMetadata metadata, CancellationToken token)
    {
        var layout = metadata.Layout;
        var cards = new List<string>
        {
            Card("SIMPLE", "T"), Integer("BITPIX", layout.ContainerDepthBits), Integer("NAXIS", 2),
            Integer("NAXIS1", layout.Width), Integer("NAXIS2", layout.Height)
        };
        AddImageCards(cards, metadata);
        await output.WriteAsync(Header(cards), token).ConfigureAwait(false);
        var dataStart = output.Position;
        for (var row = 0; row < layout.Height; row++)
        {
            for (var x = 0; x < layout.Width;)
            {
                var samples = Math.Min(layout.Width - x, ScientificCaptureExport.ChunkSamples);
                var bytes = await pixels.ReadSamplesAsync(samples, fits: true, token).ConfigureAwait(false);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                x += samples;
            }
            await pixels.EndRowAsync(token).ConfigureAwait(false);
        }
        await PadDataAsync(output, output.Position - dataStart, token).ConfigureAwait(false);
    }

    public static async Task WriteCompressedAsync(Stream output, ScientificCaptureExport.PixelReader pixels,
        ScientificExportMetadata metadata, CancellationToken token)
    {
        var layout = metadata.Layout;
        var tileWidth = Math.Min(layout.Width, ScientificCaptureExport.ChunkSamples);
        var tileHeight = Math.Min(layout.Height, ScientificCaptureExport.ChunkSamples / tileWidth);
        var tilesAcross = ((long)layout.Width + tileWidth - 1) / tileWidth;
        var tileCount = tilesAcross * (((long)layout.Height + tileHeight - 1) / tileHeight);
        var primary = Header([Card("SIMPLE", "T"), Integer("BITPIX", 8), Integer("NAXIS", 0), Card("EXTEND", "T")]);
        await output.WriteAsync(primary, token).ConfigureAwait(false);
        var cards = new List<string>
        {
            Card("XTENSION", Quote("BINTABLE")), Integer("BITPIX", 8), Integer("NAXIS", 2),
            Integer("NAXIS1", DescriptorSize), Integer("NAXIS2", tileCount),
            Integer("PCOUNT", 0), Integer("GCOUNT", 1), Integer("TFIELDS", 1),
            Card("TTYPE1", Quote("COMPRESSED_DATA")), Card("TFORM1", Quote("1PB")),
            Card("ZIMAGE", "T"), Card("ZSIMPLE", "T"), Card("ZCMPTYPE", Quote("GZIP_1")),
            Integer("ZBITPIX", layout.ContainerDepthBits), Integer("ZNAXIS", 2),
            Integer("ZNAXIS1", layout.Width), Integer("ZNAXIS2", layout.Height),
            Integer("ZTILE1", tileWidth), Integer("ZTILE2", tileHeight)
        };
        AddImageCards(cards, metadata);
        await output.WriteAsync(Header(cards), token).ConfigureAwait(false);
        var tablePosition = output.Position;
        var heapStart = tablePosition + tileCount * DescriptorSize;
        output.Position = heapStart;

        // Keep the heap sequential and write the fixed-width descriptor table in batches.
        var descriptors = new byte[64 * 1024];
        var descriptorBytes = 0;
        var tile = new byte[ScientificCaptureExport.ChunkSamples * 2];
        using var compressed = new MemoryStream(tile.Length + 128);
        for (var y = 0; y < layout.Height;)
        {
            var rows = Math.Min(layout.Height - y, tileHeight);
            for (var x = 0; x < layout.Width;)
            {
                var samples = Math.Min(layout.Width - x, tileWidth);
                var length = 0;
                for (var row = 0; row < rows; row++)
                {
                    var bytes = await pixels.ReadSamplesAsync(samples, fits: true, token).ConfigureAwait(false);
                    bytes.CopyTo(tile.AsMemory(length));
                    length += bytes.Length;
                    if (x + samples == layout.Width)
                        await pixels.EndRowAsync(token).ConfigureAwait(false);
                }
                compressed.SetLength(0);
                using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                    gzip.Write(tile.AsSpan(0, length));

                var size = checked((int)compressed.Length);
                BinaryPrimitives.WriteInt32BigEndian(descriptors.AsSpan(descriptorBytes), size);
                BinaryPrimitives.WriteInt32BigEndian(descriptors.AsSpan(descriptorBytes + 4), checked((int)(output.Position - heapStart)));
                descriptorBytes += DescriptorSize;
                await output.WriteAsync(compressed.GetBuffer().AsMemory(0, size), token).ConfigureAwait(false);
                if (descriptorBytes == descriptors.Length)
                    await FlushDescriptorsAsync().ConfigureAwait(false);
                x += samples;
            }
            y += rows;
        }
        if (descriptorBytes > 0)
            await FlushDescriptorsAsync().ConfigureAwait(false);

        var heapLength = output.Position - heapStart;
        await PadDataAsync(output, tileCount * DescriptorSize + heapLength, token).ConfigureAwait(false);
        // PCOUNT is the sixth mandatory BINTABLE card and is the actual heap length.
        output.Position = primary.Length + 5 * CardSize;
        await output.WriteAsync(Encoding.ASCII.GetBytes(Integer("PCOUNT", heapLength)), token).ConfigureAwait(false);

        async ValueTask FlushDescriptorsAsync()
        {
            var heapPosition = output.Position;
            output.Position = tablePosition;
            await output.WriteAsync(descriptors.AsMemory(0, descriptorBytes), token).ConfigureAwait(false);
            tablePosition += descriptorBytes;
            descriptorBytes = 0;
            output.Position = heapPosition;
        }
    }

    private static void AddImageCards(List<string> cards, ScientificExportMetadata metadata)
    {
        if (metadata.Layout.ContainerDepthBits == 16)
        {
            cards.Add(Integer("BSCALE", 1));
            cards.Add(Integer("BZERO", 32768));
        }
        foreach (var item in metadata.Keywords())
            cards.Add(Card(item.Name, FormatKeywordValue(item), item.Comment));
    }

    private static byte[] Header(IEnumerable<string> cards)
    {
        var header = string.Concat(cards) + "END".PadRight(CardSize);
        return Encoding.ASCII.GetBytes(header.PadRight(header.Length + Padding(header.Length)));
    }

    private static string Integer(string key, long value)
        => Card(key, value.ToString(CultureInfo.InvariantCulture));

    private static string Card(string key, string value, string comment = "")
    {
        var card = key.PadRight(8) + "= " + (value.StartsWith('\'') ? value.PadRight(20) : value.PadLeft(20));
        var inlineComment = FormatKeywordComment(value, comment);
        if (inlineComment.Length > 0)
            card += " / " + inlineComment;
        return card.PadRight(CardSize);
    }

    internal static string FormatKeywordValue(ScientificKeyword keyword)
        => keyword.IsFitsString ? Quote(keyword.Value) : keyword.Value;

    internal static string FormatKeywordComment(string value, string comment)
    {
        var available = CardSize - 10 - Math.Max(value.Length, 20) - 3;
        return available >= 4 ? comment[..Math.Min(comment.Length, available)] : string.Empty;
    }

    private static string Quote(string value)
    {
        var text = new StringBuilder("'");
        foreach (var rune in value.EnumerateRunes())
        {
            var character = rune.Value is >= 32 and <= 126 ? (char)rune.Value : '?';
            var needed = character == '\'' ? 2 : 1;
            if (text.Length + needed >= CardSize - 10) break;
            text.Append(character, needed);
        }
        return text.Append('\'').ToString();
    }

    private static int Padding(long length) => (int)((BlockSize - length % BlockSize) % BlockSize);

    private static ValueTask PadDataAsync(Stream output, long length, CancellationToken token)
    {
        var padding = Padding(length);
        return padding == 0 ? ValueTask.CompletedTask : output.WriteAsync(new byte[padding], token);
    }
}
