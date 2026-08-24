using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Deployment.Distribution;

public sealed record DistributionTrustRoot(string KeyId, string PublicKeyPem)
{
    public const string Algorithm = "ecdsa-p256-sha256-p1363";
    public const string ProductionKeyId = "p256-sha256:64aa88e5fd4750839ac5f30fdd4e32c2652eaff9994173ae4b47c799ac215aeb";

    public static DistributionTrustRoot Production { get; } = LoadProduction();

    public static bool IsCanonicalKeyId(string? value)
        => value is not null && value.StartsWith("p256-sha256:", StringComparison.Ordinal) &&
           value.Length == "p256-sha256:".Length + 64 && value["p256-sha256:".Length..].All(static character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static DistributionTrustRoot FromPem(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        return new DistributionTrustRoot($"p256-sha256:{fingerprint}", publicKeyPem);
    }

    public ECDsa CreateVerifier()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        return key;
    }

    private static DistributionTrustRoot LoadProduction()
    {
        var assembly = typeof(DistributionTrustRoot).Assembly;
        var name = assembly.GetManifestResourceNames().Single(static value =>
            value.EndsWith("hvo-skymonitor-production-release-p256-public.pem", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The production distribution trust root is missing.");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var root = FromPem(reader.ReadToEnd());
        return root.KeyId == ProductionKeyId
            ? root
            : throw new InvalidOperationException("The production distribution trust-root fingerprint changed.");
    }
}
