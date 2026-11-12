using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

internal sealed partial class RetainedManifestValidationIndex
{
    private const int MetadataHeaderLength = 97;

    // These hints are optional. Their MAC binds the already authenticated ledger
    // row, semantic validation identity and the exact file's observed metadata.
    // Losing/corrupting a hint falls back to reading actual bytes.
    internal void RecordFileMetadata(RetainedIntegrityLedger.Entry entry)
    {
        if (_connection is null || entry.Stamp is not { } stamp) return;
        var name = Encoding.UTF8.GetBytes(entry.Key);
        if (name.Length > 2048) return;
        var bytes = new byte[MetadataHeaderLength + name.Length];
        "HVORSM01"u8.CopyTo(bytes);
        _identity.CopyTo(bytes, 8);
        entry.Digest.CopyTo(bytes, 40);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(72), entry.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(80), stamp.Length);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(88), stamp.ModifiedUtcTicks);
        bytes[96] = checked((byte)entry.Kind);
        name.CopyTo(bytes, MetadataHeaderLength);
        var key = SHA256.HashData([.. "file-metadata-v1|"u8, .. name]);
        _pending[Convert.ToHexString(key)] = bytes;
        if (_pending.Count >= BatchSize) Flush();
    }

    internal void LoadFileMetadata(IReadOnlyDictionary<string, RetainedIntegrityLedger.Entry> expected,
        CancellationToken cancellationToken)
    {
        if (_connection is null) return;
        try
        {
            using var command = _connection.CreateCommand();
            // New metadata proofs use the ledger-key MAC wrapper (40 bytes), not
            // a Data Protection operation for every retained file on each start.
            command.CommandText = "SELECT proof FROM proofs WHERE length(proof) BETWEEN 137 AND 4096 AND substr(proof,41,8)=X'48564F52534D3031';";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes;
                try { bytes = _protector.Unprotect((byte[])rows.GetValue(0)); }
                catch (CryptographicException) { continue; }
                if (bytes.Length < MetadataHeaderLength || !bytes.AsSpan(0, 8).SequenceEqual("HVORSM01"u8) ||
                    !bytes.AsSpan(8, 32).SequenceEqual(_identity)) continue;
                var key = Encoding.UTF8.GetString(bytes, MetadataHeaderLength, bytes.Length - MetadataHeaderLength);
                if (!expected.TryGetValue(key, out var entry) || entry.Kind != bytes[96] ||
                    entry.Generation != BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(72)) ||
                    !entry.Digest.AsSpan().SequenceEqual(bytes.AsSpan(40, 32))) continue;
                var length = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(80));
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(88));
                if (length < 0 || ticks < 0 || ticks > DateTime.MaxValue.Ticks) continue;
                entry.Stamp = new RetainedIntegrityLedger.FileStamp(length, ticks);
            }
        }
        catch (Exception exception) when (IsOptionalFailure(exception)) { Disable(); }
    }
}
