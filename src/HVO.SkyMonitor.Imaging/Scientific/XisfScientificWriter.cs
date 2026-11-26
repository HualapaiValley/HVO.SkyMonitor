using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace HVO.SkyMonitor.Imaging;

internal static class XisfScientificWriter
{
    private const string Namespace = "http://www.pixinsight.com/xisf";
    private const int PreambleSize = 16;
    private const int AttachmentAlignment = 4096;
    private static readonly string CreatorApplication = "HVO.SkyMonitor " +
        typeof(XisfScientificWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];

    public static async Task WriteAsync(Stream output, ScientificCaptureExport.PixelReader pixels,
        ScientificExportMetadata metadata, CancellationToken token)
    {
        var layout = metadata.Layout;
        var dataSize = (long)layout.Width * layout.Height * (layout.ContainerDepthBits / 8);
        var placeholder = new string('0', 64);
        var estimate = XmlHeader(metadata, long.MaxValue, dataSize, placeholder);
        var offset = (estimate.Length + PreambleSize + AttachmentAlignment - 1) / AttachmentAlignment * AttachmentAlignment;
        output.Position = offset;

        using var dataHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < layout.Height; row++)
        {
            for (var x = 0; x < layout.Width;)
            {
                var samples = Math.Min(layout.Width - x, ScientificCaptureExport.ChunkSamples);
                var bytes = await pixels.ReadSamplesAsync(samples, fits: false, token).ConfigureAwait(false);
                dataHash.AppendData(bytes.Span);
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
                x += samples;
            }
            await pixels.EndRowAsync(token).ConfigureAwait(false);
        }

        var xml = XmlHeader(metadata, offset, dataSize, Convert.ToHexStringLower(dataHash.GetHashAndReset()));
        var preamble = new byte[PreambleSize];
        "XISF0100"u8.CopyTo(preamble);
        BinaryPrimitives.WriteUInt32LittleEndian(preamble.AsSpan(8), (uint)xml.Length);
        output.Position = 0;
        await output.WriteAsync(preamble, token).ConfigureAwait(false);
        await output.WriteAsync(xml, token).ConfigureAwait(false);
        await output.WriteAsync(new byte[offset - PreambleSize - xml.Length], token).ConfigureAwait(false);
    }

    private static byte[] XmlHeader(ScientificExportMetadata metadata, long offset, long size, string sha256)
    {
        using var buffer = new MemoryStream();
        buffer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>"u8);
        using (var xml = XmlWriter.Create(buffer, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true
        }))
        {
            var layout = metadata.Layout;
            xml.WriteStartElement("xisf", Namespace);
            xml.WriteAttributeString("version", "1.0");
            xml.WriteStartElement("Image", Namespace);
            xml.WriteAttributeString("geometry", FormattableString.Invariant($"{layout.Width}:{layout.Height}:1"));
            xml.WriteAttributeString("sampleFormat", layout.ContainerDepthBits == 16 ? "UInt16" : "UInt8");
            xml.WriteAttributeString("colorSpace", "Gray");
            xml.WriteAttributeString("imageType", metadata.ImageType);
            xml.WriteAttributeString("location", FormattableString.Invariant($"attachment:{offset}:{size}"));
            xml.WriteAttributeString("checksum", "sha256:" + sha256);

            if (metadata.BayerPattern is { } pattern)
            {
                xml.WriteStartElement("ColorFilterArray", Namespace);
                xml.WriteAttributeString("pattern", pattern);
                xml.WriteAttributeString("width", "2");
                xml.WriteAttributeString("height", "2");
                xml.WriteAttributeString("name", $"{pattern} Bayer Filter");
                xml.WriteEndElement();
            }

            foreach (var keyword in metadata.Keywords())
            {
                xml.WriteStartElement("FITSKeyword", Namespace);
                xml.WriteAttributeString("name", keyword.Name);
                var value = FitsScientificWriter.FormatKeywordValue(keyword);
                xml.WriteAttributeString("value", value);
                xml.WriteAttributeString("comment", FitsScientificWriter.FormatKeywordComment(value, keyword.Comment));
                xml.WriteEndElement();
                if (keyword.Property is { } property)
                    WriteProperty(xml, property, keyword.Type, keyword.Type == "TimePoint" ? keyword.Value + "Z" : keyword.Value);
            }

            xml.WriteEndElement();
            xml.WriteStartElement("Metadata", Namespace);
            WriteProperty(xml, "XISF:CreationTime", "TimePoint", metadata.CreationTime);
            WriteProperty(xml, "XISF:CreatorApplication", "String", CreatorApplication);
            WriteProperty(xml, "XISF:BlockAlignmentSize", "UInt16", AttachmentAlignment.ToString(CultureInfo.InvariantCulture));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        return buffer.ToArray();
    }

    private static void WriteProperty(XmlWriter xml, string id, string type, string value)
    {
        xml.WriteStartElement("Property", Namespace);
        xml.WriteAttributeString("id", id);
        xml.WriteAttributeString("type", type);
        if (type == "String") xml.WriteString(value);
        else xml.WriteAttributeString("value", value);
        xml.WriteEndElement();
    }
}
