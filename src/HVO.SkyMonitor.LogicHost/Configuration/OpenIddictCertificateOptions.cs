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
        var certificate = LoadPkcs12(path, password);
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

    // EphemeralKeySet keeps the private key out of any on-disk key store, which is what
    // production wants. macOS has no ephemeral path at all: X509CertificateLoader throws
    // PlatformNotSupportedException -- "This platform does not support loading with
    // EphemeralKeySet. Remove the flag to allow keys to be temporarily created on disk." --
    // before it looks at the file, so on macOS the loader failed for a reason that had nothing
    // to do with the certificate, ahead of the private-key, validity and key-usage checks.
    //
    // The fallback drops only that flag and only on that exception, and it is refused on the
    // PRODUCTION PLATFORM. That is the invariant; Linux is merely what the production platform
    // is today, because LogicHost ships as mcr.microsoft.com/dotnet/aspnet on Linux. Losing
    // EphemeralKeySet in production is a change in the platform's guarantees and must surface
    // rather than silently downgrade to a key materialised on disk.
    //
    // The predicate below therefore says "not the production platform", not "macOS". It returns
    // true for every non-Linux platform, which is wider than the one host that needs it, and the
    // width is deliberate: any platform without an ephemeral key path is a platform where the
    // downgrade is the only behaviour on offer, and a development host is the only place we run
    // those. **If the production platform ever stops being Linux, this predicate must move with
    // it**, or it silently stops protecting production while continuing to look correct.
    //
    // Not live today on the one platform that makes the width visible: Windows supports
    // EphemeralKeySet, so PlatformNotSupportedException does not fire and the fallback is
    // unreachable there.
    private static X509Certificate2 LoadPkcs12(string path, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (PlatformNotSupportedException) when (AllowsNonEphemeralFallback(OperatingSystem.IsLinux()))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);
        }
    }

    // Separated from the call site so both answers are testable from either platform. A test
    // that could only exercise the branch its own host takes would leave the production branch
    // -- the refusal -- unverified everywhere it matters. That is not a testing convenience: it
    // is the only reason a Linux host was able to falsify a macOS-shaped assumption at all, by
    // forcing this to true and finding that nothing but this predicate's own assertion moved.
    internal static bool AllowsNonEphemeralFallback(bool isLinuxPlatform) => !isLinuxPlatform;
}
