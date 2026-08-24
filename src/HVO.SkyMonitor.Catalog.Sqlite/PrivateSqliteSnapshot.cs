using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace HVO.SkyMonitor.Catalog.Sqlite;

internal sealed class PrivateSqliteSnapshot : IDisposable
{
    private readonly long _length;
    private nint _buffer;

    private PrivateSqliteSnapshot(nint buffer, long length, string sha256)
    {
        _buffer = buffer;
        _length = length;
        Sha256 = sha256;
    }

    internal string Sha256 { get; }

    internal static PrivateSqliteSnapshot Create(FileStream source, long expectedLength, string expectedSha256)
    {
        if (expectedLength is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Catalog database length {expectedLength} cannot be loaded into a private snapshot.");
        }

        var destination = Marshal.AllocHGlobal((nint)expectedLength);
        try
        {
            var actualSha256 = CopyAndHash(source, destination, expectedLength);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualSha256), Convert.FromHexString(expectedSha256)))
            {
                throw new InvalidDataException(
                    $"Catalog snapshot SHA-256 mismatch. Expected {expectedSha256.ToUpperInvariant()}, got {actualSha256}.");
            }
            var result = new PrivateSqliteSnapshot(destination, expectedLength, actualSha256);
            destination = 0;
            return result;
        }
        finally
        {
            if (destination != 0)
            {
                Marshal.FreeHGlobal(destination);
            }
        }
    }

    private static string CopyAndHash(FileStream source, nint destination, long expectedLength)
    {
        source.Position = 0;
        var bytes = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            int read;
            while ((read = source.Read(bytes, 0, bytes.Length)) != 0)
            {
                if (copied + read > expectedLength)
                {
                    throw new InvalidDataException(
                        $"Catalog database length mismatch. Expected {expectedLength}, got more than {expectedLength}.");
                }
                Marshal.Copy(bytes, 0, IntPtr.Add(destination, checked((int)copied)), read);
                hash.AppendData(bytes, 0, read);
                copied += read;
            }
            if (copied != expectedLength)
            {
                throw new InvalidDataException(
                    $"Catalog database length mismatch. Expected {expectedLength}, got {copied}.");
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            source.Position = 0;
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    internal void Load(SqliteConnection connection)
    {
        const int flags = raw.SQLITE_DESERIALIZE_READONLY;
        var result = raw.sqlite3_deserialize(
            connection.Handle,
            "main",
            _buffer,
            _length,
            _length,
            flags);
        if (result != raw.SQLITE_OK)
        {
            throw new SqliteException(raw.sqlite3_errmsg(connection.Handle).utf8_to_string(), result);
        }
    }

    public void Dispose()
    {
        if (_buffer != 0)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = 0;
        }
    }
}
