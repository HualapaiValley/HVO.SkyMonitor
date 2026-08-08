using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Configuration;

[TestClass]
[TestCategory("Unit")]
public sealed class DeploymentConfigurationTests
{
    [TestMethod]
    public void ObjectStorageDefaultsAndRunScopedNamesStayConsistent()
    {
        var defaults = new CentralObjectStorageNames();
        Assert.AreEqual("skymonitor-artifacts", defaults.ArtifactBucket);
        Assert.AreEqual("minio://skymonitor-artifacts/", defaults.ArtifactPrefix);

        var configured = new CentralObjectStorageNames(Options.Create(new CentralObjectStorageOptions
        {
            ArtifactBucket = "hvo-run-42-artifacts",
            DiagnosticsBucket = "hvo-run-42-diagnostics"
        }));
        Assert.AreEqual("hvo-run-42-artifacts", configured.ArtifactBucket);
        Assert.AreEqual("minio://hvo-run-42-artifacts/", configured.ArtifactPrefix);
    }

    [TestMethod]
    public void ProductionCertificateLoaderRequiresUsablePrivateKeyFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-cert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=hvo-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var path = Path.Combine(root, "signing.pfx");
            File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx, "secret"));
            using var loaded = OpenIddictCertificateOptions.Load(path, "secret", X509KeyUsageFlags.DigitalSignature);
            Assert.IsTrue(loaded.HasPrivateKey);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                OpenIddictCertificateOptions.Load(Path.Combine(root, "missing.pfx"), null, X509KeyUsageFlags.DigitalSignature));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InsecureOpenIddictTransportRequiresExplicitIsolatedModeInProduction()
    {
        Assert.IsTrue(DeploymentTransportSecurity.AllowsInsecureOpenIddictTransport(false, null));
        Assert.IsTrue(DeploymentTransportSecurity.AllowsInsecureOpenIddictTransport(true, "isolated"));
        Assert.IsFalse(DeploymentTransportSecurity.AllowsInsecureOpenIddictTransport(true, null));
        Assert.IsFalse(DeploymentTransportSecurity.AllowsInsecureOpenIddictTransport(true, "persistent"));
        Assert.IsFalse(DeploymentTransportSecurity.AllowsInsecureOpenIddictTransport(true, "ISOLATED"));
    }
}
