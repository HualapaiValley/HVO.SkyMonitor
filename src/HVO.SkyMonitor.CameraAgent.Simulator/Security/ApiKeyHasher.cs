using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Simulator.Security;

public interface IApiKeyHasher
{
    string Hash(string apiKey);
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
