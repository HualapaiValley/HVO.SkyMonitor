using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Common.Security;

/// <summary>
/// Interface for hashing and verifying API keys.
/// </summary>
public interface IApiKeyHasher
{
    string HashApiKey(string apiKey);
    bool VerifyApiKey(string apiKey, string hashedApiKey);
}

/// <summary>
/// Hashes and verifies API keys using SHA256.
/// </summary>
public class ApiKeyHasher : IApiKeyHasher
{
    public string HashApiKey(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public bool VerifyApiKey(string apiKey, string hashedApiKey)
    {
        var hash = HashApiKey(apiKey);
        return hash == hashedApiKey;
    }
}
