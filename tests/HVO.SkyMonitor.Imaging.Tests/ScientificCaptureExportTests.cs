using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ScientificCaptureExportTests
{
    private const int Block = 2880;
    private const int Card = 80;
    private static readonly DateTimeOffset Utc = new(2026, 11, 26, 12, 34, 56, TimeSpan.Zero);
    private static readonly ScientificExportObservation Site = new(35.5, -113.25, 1400, "A & B Observatory", "O'Neil");
    private static readonly ScientificExportObservation Solved = Site with
    {
        Wcs = new("TAN", 2.5, 3.5, 42.1, -20.5, -0.01, 0.002, 0.001, 0.01),
        SolutionSha256 = new string('B', 64)
    };

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8, false, false)]
    [DataRow(CameraPixelFormat.Mono16, true, true)]
    [DataRow(CameraPixelFormat.BayerRggb8, false, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, false, false)]
    public async Task FitsPrimaryRoundTripsStoredSamplesAndHeadersAsync(CameraPixelFormat format, bool bigEndian, bool solved)
    {
        var (descriptor, payload, samples) = Frame(format, 3, 2, bigEndian);
        var (bytes, receipt) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Fits, solved ? Solved : Site).ConfigureAwait(false);
        var (header, data) = ReadHdu(bytes, 0);
        var hashCard = Encoding.ASCII.GetString(bytes, 0, data).IndexOf("HVOHASH ", StringComparison.Ordinal);
        Assert.AreEqual($"HVOHASH = '{descriptor.Artifact.ChecksumSha256}'".PadRight(80),
            Encoding.ASCII.GetString(bytes, hashCard, 80));
        Assert.AreEqual("T", header["SIMPLE"]);
        Assert.AreEqual("2", header["NAXIS"]);
        Assert.AreEqual("3", header["NAXIS1"]);
        Assert.AreEqual("2", header["NAXIS2"]);
        var depth = descriptor.Layout.ContainerDepthBits;
        Assert.AreEqual(depth.ToString(CultureInfo.InvariantCulture), header["BITPIX"]);
        AssertScaling(header, depth);
        AssertMetadata(header, descriptor, solved);
        CollectionAssert.AreEqual(samples, ReadFitsSamples(bytes.AsSpan(data, samples.Length * depth / 8), depth));
        Assert.IsTrue(bytes.AsSpan(data + samples.Length * depth / 8).IndexOfAnyExcept((byte)0) < 0);
        Assert.AreEqual((solved ? 3 : 2) * Block, bytes.Length);
        AssertReceipt(bytes, receipt, descriptor, ".fits", "image/fits");
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8, 8195, 3)]
    [DataRow(CameraPixelFormat.BayerRggb16, 3001, 7)]
    public async Task TiledFitsDecodesGzipHeapAndPartialTilesAsync(CameraPixelFormat format, int width, int height)
    {
        var (descriptor, payload, expected) = Frame(format, width, height, bigEndian: true);
        var (bytes, receipt) = await EncodeAsync(descriptor, payload, ScientificExportFormat.FitsTileGzip, Solved).ConfigureAwait(false);
        var (primary, extension) = ReadHdu(bytes, 0);
        Assert.AreEqual("0", primary["NAXIS"]);
        Assert.AreEqual("T", primary["EXTEND"]);
        Assert.AreEqual(Block, extension);
        var (header, tableStart) = ReadHdu(bytes, extension);
        Assert.AreEqual("'BINTABLE'", header["XTENSION"]);
        Assert.AreEqual("T", header["ZIMAGE"]);
        Assert.AreEqual("'GZIP_1'", header["ZCMPTYPE"]);
        Assert.AreEqual("compression=GZIP_1;zlib=6/Filtered;tile=16384", receipt.Encoding.Options);
        Assert.AreEqual($"'{receipt.Encoding.Options}'", header["HVOOPT"]);
        Assert.AreEqual("'COMPRESSED_DATA'", header["TTYPE1"]);
        Assert.AreEqual("'1PB'", header["TFORM1"]);
        Assert.AreEqual("2", header["ZNAXIS"]);
        Assert.AreEqual(width, int.Parse(header["ZNAXIS1"], CultureInfo.InvariantCulture));
        Assert.AreEqual(height, int.Parse(header["ZNAXIS2"], CultureInfo.InvariantCulture));
        AssertMetadata(header, descriptor, solved: true);
        var depth = int.Parse(header["ZBITPIX"], CultureInfo.InvariantCulture);
        AssertScaling(header, depth);

        var tileWidth = int.Parse(header["ZTILE1"], CultureInfo.InvariantCulture);
        var tileHeight = int.Parse(header["ZTILE2"], CultureInfo.InvariantCulture);
        Assert.AreEqual("8", header["NAXIS1"]);
        var tileCount = int.Parse(header["NAXIS2"], CultureInfo.InvariantCulture);
        var heap = tableStart + tileCount * 8;
        var actual = new ushort[expected.Length];
        var heapLength = 0;
        var tileIndex = 0;
        for (var y = 0; y < height; y += tileHeight)
        {
            for (var x = 0; x < width; x += tileWidth)
            {
                var columns = Math.Min(tileWidth, width - x);
                var rows = Math.Min(tileHeight, height - y);
                var entry = tableStart + tileIndex++ * 8;
                var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(entry, 4));
                var offset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(entry + 4, 4));
                Assert.AreEqual(heapLength, offset);
                Assert.AreEqual(0x1F, bytes[heap + offset]);
                Assert.AreEqual(0x8B, bytes[heap + offset + 1]);
                using var compressed = new MemoryStream(bytes, heap + offset, count);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                var decoded = new byte[columns * rows * depth / 8];
                await gzip.ReadExactlyAsync(decoded).ConfigureAwait(false);
                Assert.AreEqual(-1, gzip.ReadByte());
                var samples = ReadFitsSamples(decoded, depth);
                for (var row = 0; row < rows; row++)
                    samples.AsSpan(row * columns, columns).CopyTo(actual.AsSpan((y + row) * width + x, columns));
                heapLength += count;
            }
        }
        Assert.AreEqual(tileCount, tileIndex);
        Assert.AreEqual(heapLength, int.Parse(header["PCOUNT"], CultureInfo.InvariantCulture));
        Assert.IsTrue(bytes.AsSpan(heap + heapLength).IndexOfAnyExcept((byte)0) < 0);
        Assert.AreEqual(0, bytes.Length % Block);
        CollectionAssert.AreEqual(expected, actual);
        AssertReceipt(bytes, receipt, descriptor, ".fits.fz", "application/fits");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded synthetic sensor noise must be reproducible.")]
    public async Task FitsTileGzipPreservesNoiseHeavy16BitSamplesWithinStagingBoundAsync(bool fullEntropy)
    {
        const int width = 4096;
        const int height = 64;
        const int rowBytes = width * 2;
        var (descriptor, _, _) = Frame(CameraPixelFormat.Mono16, width, height, bigEndian: true);
        var payload = new byte[rowBytes * height];
        new Random(2145).NextBytes(payload);
        if (!fullEntropy)
        {
            // A mostly random capture with a repeated readout strip two rows later, across the old tile boundary.
            for (var offset = 0; offset < payload.Length; offset += 4 * rowBytes)
                payload.AsSpan(offset + rowBytes, 2048).CopyTo(payload.AsSpan(offset + 3 * rowBytes, 2048));
        }
        descriptor = descriptor with
        {
            Layout = descriptor.Layout with { StrideBytes = rowBytes, ByteLength = payload.Length },
            Artifact = descriptor.Artifact with { ChecksumSha256 = PayloadChecksum.ComputeSha256(payload) }
        };

        var (bytes, receipt) = await EncodeAsync(descriptor, payload, ScientificExportFormat.FitsTileGzip).ConfigureAwait(false);
        var (_, extension) = ReadHdu(bytes, 0);
        var (header, table) = ReadHdu(bytes, extension);
        var tiles = int.Parse(header["NAXIS2"], CultureInfo.InvariantCulture);
        var heap = table + tiles * 8;
        var decoded = new byte[payload.Length / tiles];
        // FITS stores unsigned 16-bit input as signed big-endian values with BZERO=32768.
        var fitsSamples = (byte[])payload.Clone();
        for (var index = 0; index < fitsSamples.Length; index += 2)
            fitsSamples[index] ^= 0x80;
        for (var tile = 0; tile < tiles; tile++)
        {
            var entry = table + tile * 8;
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(entry));
            var offset = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(entry + 4));
            using var member = new MemoryStream(bytes, heap + offset, length);
            using var gzip = new GZipStream(member, CompressionMode.Decompress);
            await gzip.ReadExactlyAsync(decoded).ConfigureAwait(false);
            Assert.AreEqual(-1, gzip.ReadByte());
            Assert.IsTrue(fitsSamples.AsSpan(tile * decoded.Length, decoded.Length).SequenceEqual(decoded));
        }

        long baseline = 0;
        using var originalMember = new MemoryStream();
        for (var offset = 0; offset < fitsSamples.Length; offset += 2 * rowBytes)
        {
            originalMember.SetLength(0);
            using (var gzip = new GZipStream(originalMember, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(fitsSamples.AsSpan(offset, 2 * rowBytes));
            baseline += originalMember.Length + 8;
        }
        var tuned = int.Parse(header["PCOUNT"], CultureInfo.InvariantCulture) + tiles * 8;
        Console.WriteLine($"Full entropy={fullEntropy}, raw={payload.Length}, Fastest tiles={baseline}, tuned tiles={tuned} bytes");
        Assert.IsGreaterThan(payload.Length, baseline);
        Assert.IsLessThan(baseline, tuned);
        if (!fullEntropy)
            Assert.IsLessThan(payload.Length, tuned);
        Assert.IsLessThanOrEqualTo(ScientificCaptureExport.MaximumOutputBytes(descriptor, ScientificExportFormat.FitsTileGzip), receipt.ByteLength);
    }

    [TestMethod]
    public async Task FitsScientificNumbersUseShortestRoundTripRepresentationAsync()
    {
        var (descriptor, payload, _) = Frame(CameraPixelFormat.Mono8, 4, 3);
        const double preciseScale = -0.00012345678901234567;
        var observation = Solved with { Wcs = Solved.Wcs! with { ReferenceX = 2.50025, Cd11 = preciseScale } };
        var (bytes, _) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Fits, observation).ConfigureAwait(false);
        var (header, _) = ReadHdu(bytes, 0);
        Assert.AreEqual("2.50025", header["CRPIX1"]);
        Assert.AreEqual("42.1", header["CRVAL1"]);
        Assert.AreEqual(preciseScale, double.Parse(header["CD1_1"], CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono8, false, false)]
    [DataRow(CameraPixelFormat.Mono16, false, true)]
    [DataRow(CameraPixelFormat.BayerRggb8, false, true)]
    [DataRow(CameraPixelFormat.BayerRggb16, true, false)]
    public async Task XisfAttachmentRoundTripsStoredSamplesCfaAndPropertiesAsync(CameraPixelFormat format, bool bigEndian, bool solved)
    {
        var (descriptor, payload, samples) = Frame(format, 4, 3, bigEndian);
        var (bytes, receipt) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Xisf, solved ? Solved : Site).ConfigureAwait(false);
        CollectionAssert.AreEqual("XISF0100"u8.ToArray(), bytes[..8]);
        var xmlLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        Assert.AreEqual(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)));
        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, xmlLength));
        Assert.AreEqual("UTF-8", document.Declaration?.Encoding);
        var root = document.Root!;
        XNamespace ns = "http://www.pixinsight.com/xisf";
        Assert.AreEqual(ns + "xisf", root.Name);
        Assert.AreEqual("1.0", (string?)root.Attribute("version"));
        var image = root.Elements(ns + "Image").Single();
        Assert.AreEqual("4:3:1", (string?)image.Attribute("geometry"));
        Assert.AreEqual("Light", (string?)image.Attribute("imageType"));
        var depth = descriptor.Layout.ContainerDepthBits;
        Assert.AreEqual(depth == 16 ? "UInt16" : "UInt8", (string?)image.Attribute("sampleFormat"));
        Assert.AreEqual("Gray", (string?)image.Attribute("colorSpace"));

        var location = image.Attribute("location")!.Value.Split(':');
        Assert.AreEqual("attachment", location[0]);
        var offset = int.Parse(location[1], CultureInfo.InvariantCulture);
        var length = int.Parse(location[2], CultureInfo.InvariantCulture);
        Assert.IsGreaterThanOrEqualTo(16 + xmlLength, offset);
        Assert.AreEqual(0, offset % 4096);
        Assert.AreEqual(samples.Length * depth / 8, length);
        Assert.AreEqual(bytes.Length, offset + length);
        Assert.IsTrue(bytes.AsSpan(16 + xmlLength, offset - 16 - xmlLength).IndexOfAnyExcept((byte)0) < 0);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(offset, length)));
        Assert.AreEqual($"sha256:{checksum}", (string?)image.Attribute("checksum"));
        var actual = new ushort[samples.Length];
        for (var i = 0; i < actual.Length; i++)
            actual[i] = depth == 8 ? bytes[offset + i] : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + i * 2));
        CollectionAssert.AreEqual(samples, actual);

        var header = image.Elements(ns + "FITSKeyword").ToDictionary(e => e.Attribute("name")!.Value, e => e.Attribute("value")!.Value);
        AssertMetadata(header, descriptor, solved);
        var properties = image.Elements(ns + "Property").ToDictionary(e => e.Attribute("id")!.Value);
        Assert.AreEqual("2026-11-26T12:34:56.0000000Z", (string?)properties["Observation:Time:Start"].Attribute("value"));
        Assert.AreEqual("1.25", (string?)properties["Instrument:ExposureTime"].Attribute("value"));
        Assert.AreEqual("Float32", (string?)properties["Instrument:ExposureTime"].Attribute("type"));
        Assert.AreEqual("Float32", (string?)properties["Instrument:Sensor:Temperature"].Attribute("type"));
        Assert.AreEqual("Float64", (string?)properties["Observation:Location:Latitude"].Attribute("type"));
        Assert.IsFalse(properties.ContainsKey("Instrument:Camera:Gain"));
        Assert.AreEqual("12", (string?)properties["HVO:Camera:ControlGain"].Attribute("value"));
        Assert.AreEqual("Test 'Camera'", properties["Instrument:Camera:Name"].Value);
        Assert.AreEqual("A & B Observatory", properties["Observation:Location:Name"].Value);
        Assert.AreEqual(descriptor.Artifact.ChecksumSha256, properties["HVO:Source:SHA256"].Value);
        var cfa = image.Element(ns + "ColorFilterArray");
        if (descriptor.Layout.CfaPattern == ColorFilterArrayPattern.None) Assert.IsNull(cfa);
        else
        {
            Assert.AreEqual("GRBG", (string?)cfa!.Attribute("pattern"));
            Assert.AreEqual("GRBG Bayer Filter", (string?)cfa.Attribute("name"));
        }
        var metadata = root.Elements(ns + "Metadata").Single().Elements(ns + "Property")
            .ToDictionary(e => e.Attribute("id")!.Value);
        Assert.AreEqual("TimePoint", (string?)metadata["XISF:CreationTime"].Attribute("type"));
        Assert.AreEqual("2026-11-26T12:34:58.0000000Z", (string?)metadata["XISF:CreationTime"].Attribute("value"));
        Assert.AreEqual("String", (string?)metadata["XISF:CreatorApplication"].Attribute("type"));
        var productVersion = typeof(ScientificCaptureExport).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
        Assert.AreEqual($"HVO.SkyMonitor {productVersion}", metadata["XISF:CreatorApplication"].Value);
        Assert.AreEqual("UInt16", (string?)metadata["XISF:BlockAlignmentSize"].Attribute("type"));
        Assert.AreEqual("4096", (string?)metadata["XISF:BlockAlignmentSize"].Attribute("value"));
        AssertReceipt(bytes, receipt, descriptor, ".xisf", "application/xisf");
        var (reencoded, nextReceipt) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Xisf, solved ? Solved : Site).ConfigureAwait(false);
        CollectionAssert.AreEqual(bytes, reencoded);
        Assert.AreEqual(receipt, nextReceipt);
    }

    [TestMethod]
    [DataRow("bias", "Bias")]
    [DataRow("dark", "Dark")]
    [DataRow("flat", "Flat")]
    [DataRow("defect", "DefectMap")]
    public async Task XisfImageTypeUsesRetainedRawCalibrationKindAsync(string variant, string imageType)
    {
        var (descriptor, payload, _) = Frame(CameraPixelFormat.Mono8, 3, 2);
        descriptor = descriptor with { Artifact = descriptor.Artifact with { Variant = variant } };
        var (bytes, _) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Xisf).ConfigureAwait(false);
        Assert.AreEqual(imageType, (string?)ReadXisfImage(bytes).Attribute("imageType"));
    }

    [TestMethod]
    [DataRow(double.NaN, double.PositiveInfinity, double.NegativeInfinity)]
    [DataRow(90.01, -180.01, double.NaN)]
    [DataRow(-90.01, 180.01, double.PositiveInfinity)]
    public async Task OmitsInvalidCoordinatesAndSolveWithoutUsableWcsAsync(double latitude, double longitude, double elevation)
    {
        var (descriptor, payload, _) = Frame(CameraPixelFormat.Mono8, 3, 2);
        var observation = Solved with
        {
            LatitudeDegrees = latitude,
            LongitudeDegrees = longitude,
            ElevationMeters = elevation,
            Wcs = latitude > 0 ? Solved.Wcs! with { ReferenceX = double.NaN } : null
        };
        var (fits, _) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Fits, observation).ConfigureAwait(false);
        var (xisf, _) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Xisf, observation).ConfigureAwait(false);
        var (header, _) = ReadHdu(fits, 0);
        var image = ReadXisfImage(xisf);
        foreach (var name in new[] { "SITELAT", "SITELONG", "SITEELEV", "CTYPE1", "HVOSOLVE" })
        {
            Assert.IsFalse(header.ContainsKey(name), name);
            Assert.IsFalse(image.Elements(image.Name.Namespace + "FITSKeyword").Any(node => (string?)node.Attribute("name") == name), name);
        }
        Assert.IsFalse(image.Elements(image.Name.Namespace + "Property").Any(node =>
            (string?)node.Attribute("id") is "Observation:Location:Latitude" or "Observation:Location:Longitude" or
                "Observation:Location:Elevation" or "HVO:Source:AstrometrySHA256"));
    }

    [TestMethod]
    public async Task XisfFitsKeywordsMatchFitsAsciiEscapingAndLengthAsync()
    {
        var (descriptor, payload, _) = Frame(CameraPixelFormat.Mono8, 3, 2);
        var observation = Site with { Observatory = "Café '🔭' " + new string('x', 100) };
        var (fits, _) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Fits, observation).ConfigureAwait(false);
        var (xisf, _) = await EncodeAsync(descriptor, payload, ScientificExportFormat.Xisf, observation).ConfigureAwait(false);
        var (header, _) = ReadHdu(fits, 0);
        Assert.AreEqual("'Caf? ''?'' " + new string('x', 57) + "'", header["OBSERVAT"]);
        var image = ReadXisfImage(xisf);
        var keywords = image.Elements(image.Name.Namespace + "FITSKeyword").ToArray();
        foreach (var keyword in keywords)
        {
            var name = keyword.Attribute("name")!.Value;
            if (name is not ("HVOENC" or "HVOOPT"))
                Assert.AreEqual(header[name], (string?)keyword.Attribute("value"), name);
        }
        Assert.AreEqual("", (string?)keywords.Single(node => (string?)node.Attribute("name") == "HVOHASH").Attribute("comment"));
        var location = image.Elements(image.Name.Namespace + "Property").Single(node =>
            (string?)node.Attribute("id") == "Observation:Location:Name");
        Assert.AreEqual(observation.Observatory, location.Value);
    }

    private static XElement ReadXisfImage(byte[] bytes)
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)))).Root!;
        return root.Elements(root.Name.Namespace + "Image").Single();
    }

    [TestMethod]
    [DataRow(ScientificExportFormat.Fits)]
    [DataRow(ScientificExportFormat.FitsTileGzip)]
    [DataRow(ScientificExportFormat.Xisf)]
    public async Task RejectsTruncationExtraBytesChangedPaddingAndUnsupportedFormatsAsync(ScientificExportFormat format)
    {
        var (descriptor, payload, _) = Frame(CameraPixelFormat.Mono8, 3, 2);
        var (rgb, rgbBytes, _) = Frame(CameraPixelFormat.Rgb24, 3, 2);
        Assert.AreEqual(ScientificCaptureExport.UnsupportedSource,
            await FailureAsync(rgb, rgbBytes, format).ConfigureAwait(false));
        Assert.AreEqual(ScientificCaptureExport.CorruptSource,
            await FailureAsync(descriptor, payload[..^1], format).ConfigureAwait(false));
        Assert.AreEqual(ScientificCaptureExport.CorruptSource,
            await FailureAsync(descriptor, [.. payload, 0], format).ConfigureAwait(false));
        payload[^1] ^= 1;
        Assert.AreEqual(ScientificCaptureExport.CorruptSource,
            await FailureAsync(descriptor, payload, format).ConfigureAwait(false));
        Assert.AreEqual(ScientificCaptureExport.CorruptSource,
            await FailureAsync(descriptor with { Layout = descriptor.Layout with { StrideBytes = 0 } }, payload, format).ConfigureAwait(false));
        Assert.AreEqual(ScientificCaptureExport.SourceTooLarge,
            await FailureAsync(descriptor with
            {
                Layout = descriptor.Layout with
                {
                    Height = 1,
                    StrideBytes = (int)ScientificCaptureExport.MaximumSourceBytes + 1,
                    ByteLength = ScientificCaptureExport.MaximumSourceBytes + 1
                }
            }, [], format).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ExportPropagatesCancellationDuringReadAsync()
    {
        var (descriptor, payload, _) = Frame(CameraPixelFormat.Mono8, 3, 2);
        using var cts = new CancellationTokenSource();
        using var input = new CancellationStream(payload, cts);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ScientificCaptureExport.WriteAsync(
            input, output, descriptor, ScientificExportFormat.Xisf, cancellationToken: cts.Token)).ConfigureAwait(false);
    }

    private static async Task<string> FailureAsync(ReconstructionDescriptor descriptor, byte[] bytes, ScientificExportFormat format)
    {
        var error = await Assert.ThrowsExactlyAsync<ScientificExportException>(() => EncodeAsync(descriptor, bytes, format)).ConfigureAwait(false);
        return error.ReasonCode;
    }

    private static async Task<(byte[] Bytes, ScientificExportReceipt Receipt)> EncodeAsync(
        ReconstructionDescriptor descriptor, byte[] payload, ScientificExportFormat format, ScientificExportObservation? observation = null)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var destination = new MemoryStream();
        var receipt = await ScientificCaptureExport.WriteAsync(input, destination, descriptor, format, observation).ConfigureAwait(false);
        Assert.AreEqual(0, destination.Position);
        return (destination.ToArray(), receipt);
    }

    private static void AssertReceipt(byte[] bytes, ScientificExportReceipt receipt, ReconstructionDescriptor descriptor,
        string extension, string mediaType)
    {
        Assert.AreEqual(extension, receipt.Encoding.Extension);
        Assert.AreEqual(mediaType, receipt.Encoding.MediaType);
        Assert.AreEqual("1.1", receipt.Encoding.Version);
        Assert.AreEqual(bytes.LongLength, receipt.ByteLength);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)), receipt.Sha256);
        Assert.AreEqual(new ScientificExportLineage(descriptor.Artifact.ArtifactId, descriptor.Capture.CaptureId,
            descriptor.Capture.CaptureSequence, descriptor.Artifact.ChecksumSha256), receipt.Source);
    }

    private static (Dictionary<string, string> Header, int Data) ReadHdu(byte[] bytes, int start)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var offset = start; offset + Card <= bytes.Length; offset += Card)
        {
            var card = Encoding.ASCII.GetString(bytes, offset, Card);
            var name = card[..8].TrimEnd();
            if (name == "END")
            {
                Assert.AreEqual("END".PadRight(Card), card);
                var data = (offset + Card + Block - 1) / Block * Block;
                Assert.IsTrue(bytes.AsSpan(offset + Card, data - offset - Card).IndexOfAnyExcept((byte)' ') < 0);
                return (fields, data);
            }
            Assert.AreEqual("= ", card.Substring(8, 2));
            fields.Add(name, card[10..].Split(" / ", 2)[0].Trim());
        }
        throw new AssertFailedException("FITS HDU did not terminate with an END card.");
    }

    private static void AssertScaling(Dictionary<string, string> header, int depth)
    {
        if (depth == 16)
        {
            Assert.AreEqual("32768", header["BZERO"]);
            Assert.AreEqual("1", header["BSCALE"]);
        }
        else
        {
            Assert.IsFalse(header.ContainsKey("BZERO"));
            Assert.IsFalse(header.ContainsKey("BSCALE"));
        }
    }

    private static ushort[] ReadFitsSamples(ReadOnlySpan<byte> bytes, int depth)
    {
        var result = new ushort[bytes.Length / (depth / 8)];
        for (var i = 0; i < result.Length; i++)
            result[i] = depth == 8 ? bytes[i] : (ushort)(BinaryPrimitives.ReadInt16BigEndian(bytes[(i * 2)..]) + 32768);
        return result;
    }

    private static void AssertMetadata(Dictionary<string, string> header, ReconstructionDescriptor descriptor, bool solved)
    {
        Assert.AreEqual("'2026-11-26T12:34:56.0000000'", header["DATE-OBS"]);
        Assert.AreEqual("1.25", header["EXPTIME"]);
        Assert.AreEqual("12", header["GAIN"]);
        Assert.AreEqual("-10.5", header["CCD-TEMP"]);
        Assert.AreEqual("'Test ''Camera'''", header["INSTRUME"]);
        Assert.AreEqual("35.5", header["SITELAT"]);
        Assert.AreEqual("-113.25", header["SITELONG"]);
        Assert.AreEqual("1400", header["SITEELEV"]);
        Assert.AreEqual("'A & B Observatory'", header["OBSERVAT"]);
        Assert.AreEqual("'O''Neil'", header["OBSERVER"]);
        Assert.AreEqual($"'{descriptor.Capture.CaptureId:D}'", header["HVOCAPID"]);
        Assert.AreEqual($"'{descriptor.Artifact.ArtifactId:D}'", header["HVOSRCID"]);
        Assert.AreEqual($"'{descriptor.Artifact.ChecksumSha256}'", header["HVOHASH"]);
        Assert.AreEqual("42", header["HVOSEQ"]);
        Assert.Contains("hvo-", header["HVOENC"]);
        Assert.AreEqual("'1.1'", header["HVOENCV"]);
        Assert.Contains("compression=", header["HVOOPT"]);
        if (descriptor.Layout.CfaPattern == ColorFilterArrayPattern.None)
            Assert.IsFalse(header.ContainsKey("BAYERPAT"));
        else
        {
            Assert.AreEqual("'GRBG'", header["BAYERPAT"]);
            Assert.AreEqual("0", header["XBAYROFF"]);
            Assert.AreEqual("0", header["YBAYROFF"]);
        }

        if (solved)
        {
            Assert.AreEqual("'RA---TAN'", header["CTYPE1"]);
            Assert.AreEqual("'DEC--TAN'", header["CTYPE2"]);
            Assert.AreEqual("'FK5'", header["RADESYS"]);
            Assert.AreEqual("2.5", header["CRPIX1"]);
            Assert.AreEqual("3.5", header["CRPIX2"]);
            Assert.AreEqual(-0.01, double.Parse(header["CD1_1"], CultureInfo.InvariantCulture));
            Assert.AreEqual($"'{Solved.SolutionSha256}'", header["HVOSOLVE"]);
        }
        else
            Assert.IsFalse(header.Keys.Any(static key => key.StartsWith("CTYP", StringComparison.Ordinal) ||
                key.StartsWith("CR", StringComparison.Ordinal) || key.StartsWith("CD", StringComparison.Ordinal) && key != "CCD-TEMP"));
    }

    private static (ReconstructionDescriptor Descriptor, byte[] Payload, ushort[] Samples) Frame(
        CameraPixelFormat format, int width, int height, bool bigEndian = false)
    {
        var sixteen = format is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16;
        var bayer = format is CameraPixelFormat.BayerRggb8 or CameraPixelFormat.BayerRggb16;
        var bytesPerPixel = sixteen ? 2 : format == CameraPixelFormat.Rgb24 ? 3 : 1;
        var stride = width * bytesPerPixel + 3;
        var depth = sixteen ? 16 : 8;
        var order = sixteen ? (bigEndian ? FrameByteOrder.BigEndian : FrameByteOrder.LittleEndian) : FrameByteOrder.NotApplicable;
        var layout = new FrameLayoutDescriptor(width, height, stride, format, order, depth, depth,
            FrameSamplePacking.ByteAligned, bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None, 0,
            sixteen ? ushort.MaxValue : byte.MaxValue, (long)stride * height)
        {
            Readout = bayer ? new(width + 1, height, 1, 0, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 0) : null
        };
        var payload = new byte[stride * height];
        Array.Fill(payload, (byte)0xA5);
        var samples = new ushort[width * height];
        ushort[] boundary = sixteen ? [0, 1, 32767, 32768, 65534, 65535] : [0, 1, 127, 128, 254, 255];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = i < boundary.Length ? boundary[i] : (ushort)((i * 251) & (sixteen ? ushort.MaxValue : byte.MaxValue));
            samples[i] = value;
            var offset = i / width * stride + i % width * bytesPerPixel;
            if (!sixteen) payload[offset] = (byte)value;
            else if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), value);
            else BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), value);
        }
        var profile = new ProfileIdentityDescriptor("profile", "1", new string('A', 64));
        var descriptor = new ReconstructionDescriptor(
            new("agent", "rig", 42, Guid.Parse("bdd43203-dedf-432c-80cc-71bcb1b01802")),
            new(Utc, Utc, Utc.AddSeconds(1.25), Utc.AddSeconds(2), Utc.AddSeconds(3)),
            new(TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(1.25), 12, 12, null, null, null, -10.5),
            new(profile, profile, profile, profile, profile), layout,
            new(Guid.Parse("e2cd5491-e187-42a7-b366-48ce2f62ef45"), FrameArtifactRole.Raw, "Test 'Camera'", "native", Utc.AddSeconds(2), [],
                RecipeIdentityDescriptor.Create("raw", "1.0.0", "capture-v1", JsonSerializer.SerializeToElement(new { })),
                "application/octet-stream", Convert.ToHexString(SHA256.HashData(payload))));
        return (descriptor, payload, samples);
    }

    private sealed class CancellationStream(byte[] payload, CancellationTokenSource cancellation) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
