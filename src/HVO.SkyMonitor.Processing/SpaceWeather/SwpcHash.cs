using System.Security.Cryptography;

namespace HVO.SkyMonitor.Processing.SpaceWeather;

/// <summary>Culture-independent uppercase SHA-256 content identities.</summary>
public static class SwpcHash
{
    /// <summary>Hashes bytes into the contract's 64-character uppercase hexadecimal form.</summary>
    public static string Sha256UpperHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
