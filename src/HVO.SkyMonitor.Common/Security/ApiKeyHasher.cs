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
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }
}
