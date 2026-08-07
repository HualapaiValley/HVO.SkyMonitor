using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;

namespace HVO.SkyMonitor.LogicHost.Configuration;

internal sealed class OpenIddictCertificateOptions
{
    public const string SectionName = "OpenIddictCertificates";

    [Required]
    public string SigningPath { get; set; } = string.Empty;

    public string? SigningPassword { get; set; }

    [Required]
    public string EncryptionPath { get; set; } = string.Empty;

    public string? EncryptionPassword { get; set; }

    internal static X509Certificate2 Load(string path, string? password, X509KeyUsageFlags usage)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("OpenIddict certificate paths must be absolute.");
        }
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 1024 * 1024)
        {
            throw new InvalidOperationException("An OpenIddict certificate file is missing or invalid.");
        }
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        if (!certificate.HasPrivateKey || DateTimeOffset.UtcNow < certificate.NotBefore || DateTimeOffset.UtcNow > certificate.NotAfter)
        {
            certificate.Dispose();
            throw new InvalidOperationException("An OpenIddict certificate is not currently valid or lacks a private key.");
        }
        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (keyUsage is not null && (keyUsage.KeyUsages & usage) == 0)
        {
            certificate.Dispose();
            throw new InvalidOperationException("An OpenIddict certificate does not permit its configured use.");
        }
        return certificate;
    }
}
