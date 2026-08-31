using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime.Credentials;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.Tests.LogicHost.Configuration;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class DeploymentConfigurationTests
{
    [TestMethod]
    public void ObjectStorageDefaultsAndRunScopedNamesStayConsistent()
    {
        var defaults = new CentralObjectStorageNames();
        Assert.AreEqual("skymonitor-artifacts", defaults.ArtifactBucket);
        Assert.AreEqual("s3://skymonitor-artifacts/", defaults.ArtifactPrefix);

        var configured = new CentralObjectStorageNames(Options.Create(new CentralObjectStorageOptions
        {
            ArtifactBucket = "hvo-run-42-artifacts",
            DiagnosticsBucket = "hvo-run-42-diagnostics"
        }));
        Assert.AreEqual("hvo-run-42-artifacts", configured.ArtifactBucket);
        Assert.AreEqual("s3://hvo-run-42-artifacts/", configured.ArtifactPrefix);
    }

    [TestMethod]
    public void ObjectStorageConfiguration_RepresentsCustomAndCloudEndpointsWithoutProviderTypes()
    {
        var custom = new CentralObjectStorageOptions
        {
            ServiceEndpoint = "object-store.internal:9000",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "access",
            SecretKey = "secret"
        };
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasValidObjectStorageEndpoint(custom));
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasValidObjectStorageCredentials(custom));

        var cloud = new CentralObjectStorageOptions
        {
            ServiceEndpoint = null,
            Region = "us-west-2",
            UseTls = true,
            AddressingStyle = ObjectStorageAddressingStyle.VirtualHost,
            CredentialMode = ObjectStorageCredentialMode.DefaultChain
        };
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasValidObjectStorageEndpoint(cloud));
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasValidObjectStorageCredentials(cloud));
    }

    [TestMethod]
    public void ObjectStorageConfiguration_RejectsSchemesAndCredentialLeakage()
    {
        var invalid = new CentralObjectStorageOptions
        {
            ServiceEndpoint = "https://object-store.internal:9000/path",
            AddressingStyle = ObjectStorageAddressingStyle.VirtualHost,
            CredentialMode = ObjectStorageCredentialMode.DefaultChain,
            AccessKey = "must-not-be-present"
        };

        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasValidObjectStorageEndpoint(invalid));
        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasValidObjectStorageCredentials(invalid));
    }

    [TestMethod]
    public void ObjectStorageDefaultChain_ResolvesRefreshingAwsSessionCredentials()
    {
        var names = new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "default-chain-access");
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "default-chain-secret");
            Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", "default-chain-session");
            using var client = S3ObjectStoreClientFactory.Create(new CentralObjectStorageOptions());
            var credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials().GetCredentials();

            Assert.IsNotNull(client);
            Assert.AreEqual("default-chain-access", credentials.AccessKey);
            Assert.AreEqual("default-chain-secret", credentials.SecretKey);
            Assert.AreEqual("default-chain-session", credentials.Token);
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, saved[name]);
            }
        }
    }

    [TestMethod]
    public void ObjectStorageClientFactory_SupportsAddressingIndependentlyOfEndpointSelection()
    {
        var pathWithoutEndpoint = new CentralObjectStorageOptions
        {
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "access",
            SecretKey = "secret"
        };
        var virtualHostWithCustomEndpoint = new CentralObjectStorageOptions
        {
            ServiceEndpoint = "object-store.internal:9000",
            AddressingStyle = ObjectStorageAddressingStyle.VirtualHost,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "access",
            SecretKey = "secret"
        };

        var awsPath = S3ObjectStoreClientFactory.CreateConfig(pathWithoutEndpoint);
        Assert.IsTrue(awsPath.ForcePathStyle);
        Assert.IsNull(awsPath.ServiceURL);
        var customVirtualHost = S3ObjectStoreClientFactory.CreateConfig(virtualHostWithCustomEndpoint);
        Assert.IsFalse(customVirtualHost.ForcePathStyle);
        Assert.AreEqual(new Uri("https://object-store.internal:9000"), new Uri(customVirtualHost.ServiceURL));
    }

    [TestMethod]
    public void ObjectStorageClientFactory_AppliesPathAndVirtualHostAddressing()
    {
        var pathConfig = S3ObjectStoreClientFactory.CreateConfig(new CentralObjectStorageOptions
        {
            ServiceEndpoint = "object-store.internal:9000",
            Region = "us-east-1",
            UseTls = false,
            AddressingStyle = ObjectStorageAddressingStyle.Path,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "access",
            SecretKey = "secret"
        });
        Assert.IsTrue(pathConfig.ForcePathStyle);
        Assert.AreEqual(new Uri("http://object-store.internal:9000"), new Uri(pathConfig.ServiceURL));
        Assert.AreEqual("us-east-1", pathConfig.AuthenticationRegion);

        var virtualHostConfig = S3ObjectStoreClientFactory.CreateConfig(new CentralObjectStorageOptions
        {
            Region = "us-west-2",
            AddressingStyle = ObjectStorageAddressingStyle.VirtualHost,
            CredentialMode = ObjectStorageCredentialMode.Static,
            AccessKey = "access",
            SecretKey = "secret"
        });
        Assert.IsFalse(virtualHostConfig.ForcePathStyle);
        Assert.IsNull(virtualHostConfig.ServiceURL);
        Assert.AreEqual("us-west-2", virtualHostConfig.RegionEndpoint.SystemName);
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
