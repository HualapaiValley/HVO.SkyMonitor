using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Common.Security;

public interface IApiKeyHasher
{
    string Hash(string apiKey);

    bool Verify(string apiKey, string hashedValue)
        => string.Equals(Hash(apiKey), hashedValue, StringComparison.Ordinal);
}

public sealed class ApiKeyHasher : IApiKeyHasher
{
    public string Hash(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }
}
