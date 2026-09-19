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
        Assert.AreEqual("object://skymonitor-artifacts/", defaults.ArtifactPrefix);

        var configured = new CentralObjectStorageNames(Options.Create(new CentralObjectStorageOptions
        {
            ArtifactBucket = "hvo-run-42-artifacts",
            DiagnosticsBucket = "hvo-run-42-diagnostics"
        }));
        Assert.AreEqual("hvo-run-42-artifacts", configured.ArtifactBucket);
        Assert.AreEqual("object://hvo-run-42-artifacts/", configured.ArtifactPrefix);
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

    // #687: the loader asked for EphemeralKeySet unconditionally and macOS has no ephemeral
    // path, so it threw PlatformNotSupportedException before any of the checks below could run.
    // Every one of these rejections has to survive the fix, because a portability change that
    // quietly stopped refusing bad certificates would be a worse defect than the one it fixed.
    [TestMethod]
    public void ProductionCertificateLoaderRejectsEveryUnusableCertificate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-cert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            static string Write(string root, string name, X509Certificate2 certificate)
            {
                var path = Path.Combine(root, name);
                File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "secret"));
                return path;
            }

            static X509Certificate2 Make(RSA key, DateTimeOffset from, DateTimeOffset to, X509KeyUsageFlags usage)
            {
                var request = new CertificateRequest("CN=hvo-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
                return request.CreateSelfSigned(from, to);
            }

            using var rsa = RSA.Create(2048);
            var now = DateTimeOffset.UtcNow;

            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load("relative/signing.pfx", "secret", X509KeyUsageFlags.DigitalSignature),
                "a relative path must be refused before the file is opened");

            var empty = Path.Combine(root, "empty.pfx");
            File.WriteAllBytes(empty, []);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load(empty, "secret", X509KeyUsageFlags.DigitalSignature),
                "an empty file must be refused");

            var oversized = Path.Combine(root, "oversized.pfx");
            File.WriteAllBytes(oversized, new byte[(1024 * 1024) + 1]);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load(oversized, "secret", X509KeyUsageFlags.DigitalSignature),
                "a file above the size bound must be refused");

            using var expired = Make(rsa, now.AddHours(-2), now.AddHours(-1), X509KeyUsageFlags.DigitalSignature);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load(Write(root, "expired.pfx", expired), "secret", X509KeyUsageFlags.DigitalSignature),
                "an expired certificate must be refused");

            using var future = Make(rsa, now.AddHours(1), now.AddHours(2), X509KeyUsageFlags.DigitalSignature);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load(Write(root, "future.pfx", future), "secret", X509KeyUsageFlags.DigitalSignature),
                "a not-yet-valid certificate must be refused");

            using var withKey = Make(rsa, now.AddMinutes(-1), now.AddHours(1), X509KeyUsageFlags.DigitalSignature);
            using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));
            Assert.IsFalse(publicOnly.HasPrivateKey, "the public-only fixture must genuinely carry no private key");
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load(Write(root, "public-only.pfx", publicOnly), "secret", X509KeyUsageFlags.DigitalSignature),
                "a certificate without a private key must be refused");

            using var wrongUsage = Make(rsa, now.AddMinutes(-1), now.AddHours(1), X509KeyUsageFlags.KeyEncipherment);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => OpenIddictCertificateOptions.Load(Write(root, "wrong-usage.pfx", wrongUsage), "secret", X509KeyUsageFlags.DigitalSignature),
                "a certificate whose key usage excludes the configured use must be refused");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The production branch is the refusal, and it is the branch this host never takes. Reading
    // the decision through a parameter is what makes both answers checkable from either
    // platform; a test that could only exercise its own host's branch would leave the one that
    // matters unverified everywhere it matters.
    [TestMethod]
    public void NonEphemeralFallbackIsRefusedOnLinuxAndAllowedElsewhere()
    {
        Assert.IsFalse(
            OpenIddictCertificateOptions.AllowsNonEphemeralFallback(isLinuxPlatform: true),
            "Linux is the production platform: losing EphemeralKeySet there must surface, not downgrade to keys on disk");
        Assert.IsTrue(
            OpenIddictCertificateOptions.AllowsNonEphemeralFallback(isLinuxPlatform: false),
            "a development platform without an ephemeral key path must still be able to load a certificate");
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

    [TestMethod]
    public void ProviderSelection_DefaultsToS3AndFlattenedKeysForwardToTheS3Group()
    {
        // The default keeps every existing deployment working: S3 is the only delivered
        // adapter, and the flattened ObjectStorage:* keys the inventory writes land in the
        // S3 group with no second copy of any value.
        var options = new CentralObjectStorageOptions();
        Assert.AreEqual(ObjectStorageProvider.S3, options.Provider);
        Assert.IsFalse(options.HasS3Settings);
        Assert.IsFalse(options.HasFilesystemSettings);

        options.ServiceEndpoint = "minio.example.test:9000";
        options.CredentialMode = ObjectStorageCredentialMode.Static;
        options.AccessKey = "key";
        options.SecretKey = "secret";
        Assert.AreEqual("minio.example.test:9000", options.S3.ServiceEndpoint);
        Assert.AreEqual(ObjectStorageCredentialMode.Static, options.S3.CredentialMode);
        Assert.AreEqual("key", options.S3.AccessKey);
        Assert.IsTrue(options.HasS3Settings);
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasExclusiveProviderSettings(options));
    }

    [TestMethod]
    public void ProviderSelection_IsMutuallyExclusiveAndFailsClosed()
    {
        // Filesystem selected with any S3 transport value present: a half-migrated deployment.
        var filesystemWithS3 = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        filesystemWithS3.Filesystem.Root = "/srv/skymonitor/objects";
        filesystemWithS3.ServiceEndpoint = "minio.example.test:9000";
        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasExclusiveProviderSettings(filesystemWithS3));

        // S3 selected with a filesystem root present: the same contradiction the other way.
        var s3WithFilesystem = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.S3 };
        s3WithFilesystem.Filesystem.Root = "/srv/skymonitor/objects";
        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasExclusiveProviderSettings(s3WithFilesystem));

        // Filesystem selected cleanly: exclusive, and the root must be absolute and normalized.
        var filesystem = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        filesystem.Filesystem.Root = "/srv/skymonitor/objects";
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasExclusiveProviderSettings(filesystem));
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasValidFilesystemRoot(filesystem));

        filesystem.Filesystem.Root = "relative/objects";
        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasValidFilesystemRoot(filesystem));
        filesystem.Filesystem.Root = "/srv/skymonitor/../objects";
        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasValidFilesystemRoot(filesystem));
        filesystem.Filesystem.Root = null;
        Assert.IsFalse(HVO.SkyMonitor.LogicHost.Program.HasValidFilesystemRoot(filesystem));

        // The root rule does not apply when S3 is selected.
        Assert.IsTrue(HVO.SkyMonitor.LogicHost.Program.HasValidFilesystemRoot(new CentralObjectStorageOptions()));
    }

    [TestMethod]
    public void LogicalIdentity_DoesNotDependOnTheProvider()
    {
        // The persisted reference is object://bucket/key for every provider, so switching the
        // physical provider changes no stored row.
        var s3 = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.S3 };
        var filesystem = new CentralObjectStorageOptions { Provider = ObjectStorageProvider.Filesystem };
        filesystem.Filesystem.Root = "/srv/skymonitor/objects";
        Assert.AreEqual(s3.ArtifactPrefix, filesystem.ArtifactPrefix);
        Assert.IsTrue(s3.ArtifactPrefix.StartsWith("object://", StringComparison.Ordinal),
            "the persisted scheme is the logical one, not a provider's");
    }

    [TestMethod]
    public void FailureKinds_CapacityIsOperatorActionAndCorruptStateIsTerminal()
    {
        var capacity = new ObjectStoreException(ObjectStoreFailureKind.Capacity, "put");
        Assert.IsFalse(capacity.IsRetryable, "capacity cannot be retried into success");
        Assert.IsFalse(capacity.IsTerminal, "reads and deletes still work under capacity pressure");
        Assert.IsTrue(capacity.RequiresOperator);
        Assert.AreEqual("capacity", ObjectStoreException.GetOutcome(ObjectStoreFailureKind.Capacity));

        var corrupt = new ObjectStoreException(ObjectStoreFailureKind.CorruptState, "read");
        Assert.IsFalse(corrupt.IsRetryable);
        Assert.IsTrue(corrupt.IsTerminal, "a store that contradicts itself must not serve");
        Assert.IsTrue(corrupt.RequiresOperator);
        Assert.AreEqual("corrupt-state", ObjectStoreException.GetOutcome(ObjectStoreFailureKind.CorruptState));

        // Existing categories keep their semantics.
        Assert.IsTrue(new ObjectStoreException(ObjectStoreFailureKind.Throttled, "put").IsRetryable);
        Assert.IsFalse(new ObjectStoreException(ObjectStoreFailureKind.Throttled, "put").RequiresOperator);
    }
}
