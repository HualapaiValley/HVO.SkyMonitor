using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Sqlite;

internal static class SqliteHashFunctions
{
    internal static void RegisterSha256(SqliteConnection connection)
    {
        connection.CreateFunction<string?, string>(
            "hvo_sha256",
            static value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))),
            isDeterministic: true);
    }
}
