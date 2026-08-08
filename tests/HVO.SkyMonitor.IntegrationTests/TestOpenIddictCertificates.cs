using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HVO.SkyMonitor.IntegrationTests;

internal sealed class TestOpenIddictCertificates : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hvo-oidc-{Guid.NewGuid():N}");

    public TestOpenIddictCertificates()
    {
        Directory.CreateDirectory(_directory);
        SigningPath = Create("signing.pfx", X509KeyUsageFlags.DigitalSignature);
        EncryptionPath = Create("encryption.pfx", X509KeyUsageFlags.KeyEncipherment);
    }

    public string SigningPath { get; }

    public string EncryptionPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Create(string fileName, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=hvo-integration-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }
}
