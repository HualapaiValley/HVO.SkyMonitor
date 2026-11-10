using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

// Advisory, authenticated parse proofs, never installation identity. Every lookup hashes bytes
// freshly read by the caller. The owner serializes access and publishes only a completed scan.
internal sealed class RetainedManifestValidationSummary(byte[] identity)
{
    internal const int MaximumEntries = 262_144;
    private const int HeaderLength = 44;
    internal const int MaximumPlaintextBytes = HeaderLength + MaximumEntries * 32;
    internal const string FileName = "retained-validation.v1.protected";
    private readonly HashSet<Digest> _validated = [];
    private readonly HashSet<Digest> _observed = [];

    internal bool Changed => !_validated.SetEquals(_observed);

    internal static Digest Hash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(bytes, digest);
        return Digest.Read(digest);
    }

    internal bool Contains(Digest digest) => _validated.Contains(digest) || _observed.Contains(digest);

    internal void Record(Digest digest)
    {
        if (_observed.Count < MaximumEntries) _observed.Add(digest);
    }

    internal bool TryLoad(ReadOnlySpan<byte> plaintext)
    {
        _validated.Clear();
        if (plaintext.Length < HeaderLength || plaintext.Length > MaximumPlaintextBytes ||
            !plaintext[..8].SequenceEqual("HVORV001"u8) || !plaintext.Slice(8, 32).SequenceEqual(identity)) return false;
        var count = BinaryPrimitives.ReadInt32LittleEndian(plaintext[40..]);
        if (count < 0 || count > MaximumEntries || plaintext.Length != HeaderLength + count * 32) return false;
        for (var index = 0; index < count; index++)
        {
            if (!_validated.Add(Digest.Read(plaintext.Slice(HeaderLength + index * 32, 32))))
            {
                _validated.Clear();
                return false;
            }
        }
        return true;
    }

    internal byte[] Serialize()
    {
        var bytes = new byte[HeaderLength + _observed.Count * 32];
        "HVORV001"u8.CopyTo(bytes);
        identity.CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), _observed.Count);
        var offset = HeaderLength;
        foreach (var digest in _observed)
        {
            digest.Write(bytes.AsSpan(offset, 32));
            offset += 32;
        }
        return bytes;
    }

    // Value keys avoid a string or byte[] allocation for each freshly inspected manifest.
    internal readonly record struct Digest(ulong A, ulong B, ulong C, ulong D)
    {
        internal static Digest Read(ReadOnlySpan<byte> bytes) => new(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));

        internal void Write(Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, A);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], B);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], C);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], D);
        }
    }
}
