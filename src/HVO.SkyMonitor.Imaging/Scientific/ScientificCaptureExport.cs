using System.Buffers.Binary;
using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

public enum ScientificExportFormat { Fits, FitsTileGzip, Xisf }

public sealed record ScientificExportEncoding(
    string MediaType, string Extension, string Encoder, string Version, string Options)
{
    public static ScientificExportEncoding For(ScientificExportFormat format) => format switch
    {
        ScientificExportFormat.Fits => new("image/fits", ".fits", "hvo-fits", "1.1", "compression=none"),
        ScientificExportFormat.FitsTileGzip => new("application/fits", ".fits.fz", "hvo-fits", "1.1", FitsScientificWriter.CompressionOptions),
        ScientificExportFormat.Xisf => new("application/xisf", ".xisf", "hvo-xisf", "1.1", "compression=none;checksum=sha256"),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}

public sealed record ScientificExportLineage(Guid ArtifactId, Guid CaptureId, long CaptureSequence, string Sha256);

public sealed record ScientificExportReceipt(
    ScientificExportEncoding Encoding, long ByteLength, string Sha256, ScientificExportLineage Source);

public sealed record ScientificExportObservation(
    double? LatitudeDegrees = null, double? LongitudeDegrees = null, double? ElevationMeters = null,
    string? Observatory = null, string? Observer = null, CelestialWcs? Wcs = null, string? SolutionSha256 = null);

public sealed class ScientificExportException : IOException
{
    public ScientificExportException() : this(ScientificCaptureExport.CorruptSource) { }

    public ScientificExportException(string reasonCode) : base(reasonCode) => ReasonCode = reasonCode;

    public ScientificExportException(string reasonCode, Exception innerException) : base(reasonCode, innerException)
        => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

/// <summary>Writes lossless exports from canonical raw bytes. The destination is private until a receipt is returned.</summary>
public static class ScientificCaptureExport
{
    public const string UnsupportedSource = "scientific-export.unsupported-source";
    public const string CorruptSource = "scientific-export.corrupt-source";
    public const string SourceTooLarge = "scientific-export.source-too-large";
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    internal const int ChunkSamples = 8192;

    public static bool Supports(CameraPixelFormat format)
        => format is CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or
            CameraPixelFormat.BayerRggb8 or CameraPixelFormat.BayerRggb16;

    /// <summary>Returns a staging-space bound including headers, padding, and any compression overhead.</summary>
    public static long MaximumOutputBytes(ReconstructionDescriptor descriptor, ScientificExportFormat format,
        ScientificExportObservation? observation = null)
    {
        var metadata = CreateMetadata(descriptor, format, observation);
        return format == ScientificExportFormat.Xisf
            ? XisfScientificWriter.MaximumOutputBytes(metadata)
            : FitsScientificWriter.MaximumOutputBytes(format, metadata);
    }

    public static async Task<ScientificExportReceipt> WriteAsync(
        Stream source, Stream destination, ReconstructionDescriptor descriptor, ScientificExportFormat format,
        ScientificExportObservation? observation = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(source, destination) || !source.CanRead || !destination.CanRead ||
            !destination.CanWrite || !destination.CanSeek || destination.Length != 0)
            throw new ArgumentException("A readable source and an empty, readable, seekable output are required.");

        var metadata = CreateMetadata(descriptor, format, observation);
        using var pixels = new PixelReader(source, descriptor.Layout);
        destination.Position = 0;
        switch (format)
        {
            case ScientificExportFormat.Fits:
                await FitsScientificWriter.WritePrimaryAsync(destination, pixels, metadata, cancellationToken).ConfigureAwait(false);
                break;
            case ScientificExportFormat.FitsTileGzip:
                await FitsScientificWriter.WriteCompressedAsync(destination, pixels, metadata, cancellationToken).ConfigureAwait(false);
                break;
            case ScientificExportFormat.Xisf:
                await XisfScientificWriter.WriteAsync(destination, pixels, metadata, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        await VerifySourceEndAsync(source, pixels.Digest(), metadata.Lineage.Sha256, cancellationToken).ConfigureAwait(false);

        var length = destination.Length;
        destination.Position = 0;
        var digest = await SHA256.HashDataAsync(destination, cancellationToken).ConfigureAwait(false);
        destination.Position = 0;
        return new(metadata.Encoding, length, Convert.ToHexString(digest), metadata.Lineage);
    }

    /// <summary>Rechecks the exact source length and checksum before serving a previously encoded representation.</summary>
    public static async ValueTask VerifySourceAsync(Stream source, long length, string sha256, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        try
        {
            while (length > 0)
            {
                var bytes = buffer.AsMemory(0, (int)Math.Min(length, buffer.Length));
                await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                hash.AppendData(bytes.Span);
                length -= bytes.Length;
            }
        }
        catch (EndOfStreamException)
        {
            throw new ScientificExportException(CorruptSource);
        }
        await VerifySourceEndAsync(source, hash.GetHashAndReset(), sha256, token).ConfigureAwait(false);
    }

    private static async ValueTask VerifySourceEndAsync(Stream source, byte[] digest, string expected, CancellationToken token)
    {
        if (await source.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0 ||
            !string.Equals(Convert.ToHexString(digest), expected, StringComparison.OrdinalIgnoreCase))
            throw new ScientificExportException(CorruptSource);
    }

    private static ScientificExportMetadata CreateMetadata(ReconstructionDescriptor descriptor, ScientificExportFormat format,
        ScientificExportObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.Validate().IsValid)
            throw new ScientificExportException(CorruptSource);
        if (descriptor.Artifact.Role != FrameArtifactRole.Raw || !Supports(descriptor.Layout.PixelFormat))
            throw new ScientificExportException(UnsupportedSource);
        if (descriptor.Layout.ByteLength > MaximumSourceBytes)
            throw new ScientificExportException(SourceTooLarge);

        var encoding = ScientificExportEncoding.For(format);
        var lineage = new ScientificExportLineage(descriptor.Artifact.ArtifactId, descriptor.Capture.CaptureId,
            descriptor.Capture.CaptureSequence, descriptor.Artifact.ChecksumSha256.ToUpperInvariant());
        return new(descriptor, observation ?? new(), encoding, lineage);
    }

    internal sealed class PixelReader(Stream source, FrameLayoutDescriptor layout) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _buffer = new byte[ChunkSamples * 2];

        public async ValueTask<Memory<byte>> ReadSamplesAsync(int samples, bool fits, CancellationToken token)
        {
            var bytes = _buffer.AsMemory(0, samples * (layout.ContainerDepthBits / 8));
            await ReadAndHashAsync(bytes, token).ConfigureAwait(false);
            if (layout.ContainerDepthBits == 16)
                Convert16(bytes.Span, fits, layout.ByteOrder == FrameByteOrder.LittleEndian);
            return bytes;
        }

        private static void Convert16(Span<byte> bytes, bool fits, bool littleEndian)
        {
            if (!fits && littleEndian) return;
            for (var offset = 0; offset < bytes.Length; offset += 2)
            {
                var sample = bytes.Slice(offset, 2);
                var value = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(sample) :
                    BinaryPrimitives.ReadUInt16BigEndian(sample);
                if (fits)
                    BinaryPrimitives.WriteUInt16BigEndian(sample, (ushort)(value ^ 0x8000));
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(sample, value);
            }
        }

        public async ValueTask EndRowAsync(CancellationToken token)
        {
            var remaining = layout.StrideBytes - layout.Width * (layout.ContainerDepthBits / 8);
            while (remaining > 0)
            {
                var count = Math.Min(remaining, _buffer.Length);
                await ReadAndHashAsync(_buffer.AsMemory(0, count), token).ConfigureAwait(false);
                remaining -= count;
            }
        }

        private async ValueTask ReadAndHashAsync(Memory<byte> bytes, CancellationToken token)
        {
            try
            {
                await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                throw new ScientificExportException(CorruptSource);
            }
            _hash.AppendData(bytes.Span);
        }

        public byte[] Digest() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }
}
