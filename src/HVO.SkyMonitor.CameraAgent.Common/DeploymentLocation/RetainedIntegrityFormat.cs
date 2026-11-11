using System.Security.Cryptography;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

// Shared by the agent and the installer's offline restore writer. Changing this
// authenticated layout requires a new protocol/version and reader boundary.
internal static class RetainedIntegrityFormat
{
    internal sealed record EntryProof(string Nonce, string Key, string Digest, int Kind, long Generation);
    internal sealed record IntentProof(string Nonce, string Key, bool Tree, long Generation, string? Planned, string? Source, bool Denied);
    internal sealed record Metadata(string Nonce, long Generation, string Chain, long Count, bool Established, string Pending,
        string? EntryKey = null);

    internal static byte[] ProtectEntry(EntryProof proof, byte[]? key, Func<byte[], byte[]> protect)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(proof);
        return key is null ? protect(bytes) : HMACSHA256.HashData(key, bytes);
    }

    internal static bool AuthenticateEntry(EntryProof proof, byte[] auth, byte[]? key, Func<byte[], byte[]> unprotect)
        => auth.Length == 32
            ? key is not null && CryptographicOperations.FixedTimeEquals(auth,
                HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(proof)))
            : JsonSerializer.Deserialize<EntryProof>(unprotect(auth)) == proof;

    internal static byte[]? EntryKey(Metadata metadata)
    {
        if (metadata.EntryKey is null) return null;
        var key = Convert.FromBase64String(metadata.EntryKey);
        return key.Length == 32 ? key : throw new InvalidDataException("Retained integrity authentication key is invalid.");
    }
    internal const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS entries(key TEXT PRIMARY KEY,digest BLOB NOT NULL,kind INTEGER NOT NULL,generation INTEGER NOT NULL,auth BLOB NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS scan_entries(key TEXT PRIMARY KEY,digest BLOB NOT NULL,kind INTEGER NOT NULL,generation INTEGER NOT NULL,auth BLOB NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS pending(key TEXT PRIMARY KEY,tree INTEGER NOT NULL,generation INTEGER NOT NULL,planned TEXT,source TEXT,denied INTEGER NOT NULL,auth BLOB NOT NULL) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1),payload BLOB NOT NULL);
        """;

    internal static byte[] Hash(EntryProof proof) => SHA512.HashData(JsonSerializer.SerializeToUtf8Bytes(proof));
    internal static void Accumulate(byte[] accumulator, byte[] leaf, bool remove = false)
    {
        var carry = 0;
        for (var i = 0; i < accumulator.Length; i++)
        {
            var sum = accumulator[i] + (remove ? -leaf[i] : leaf[i]) + carry;
            accumulator[i] = unchecked((byte)sum);
            carry = sum >> 8;
        }
    }
}
