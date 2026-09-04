using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CommandLineTests
{
    private static readonly string[] ValidArguments =
    [
        "cameraagent", "install",
        "--friendly-name", "North Camera",
        "--owner-email", "admin@example.test",
        "--catalog-bundle", "/srv/hvo/catalog.bundle",
        "--image-ref", $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}"
    ];

    [TestMethod]
    public void Parse_ValidArguments_ReturnsCanonicalRequest()
    {
        var request = CommandLine.Parse(ValidArguments);

        Assert.AreEqual("North Camera", request.FriendlyName);
        Assert.AreEqual("127.0.0.1", request.BindAddress);
        Assert.AreEqual(5130, request.Port);
        Assert.AreEqual(InstallRequest.DefaultProductRoot, request.ProductRoot);
        Assert.AreEqual(CameraAgentReplayProfile.InProcess, request.ReplayProfile);
    }

    [TestMethod]
    public void Parse_LocalReplayRunnerProfile_IsExplicitlySelectable()
    {
        var request = CommandLine.Parse(ValidArguments.Concat([
            "--replay-profile", "local-runner"
        ]).ToArray());

        Assert.AreEqual(CameraAgentReplayProfile.LocalRunner, request.ReplayProfile);
    }

    [TestMethod]
    public void Parse_UnknownReplayProfile_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--replay-profile", "remote"]).ToArray();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_ConfigAndArguments_ProduceEquivalentRequests()
    {
        var expected = CommandLine.Parse(ValidArguments);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(expected, InstallRequestJsonContext.Default.InstallRequest));

            var actual = CommandLine.Parse(["cameraagent", "install", "--config", path]);

            Assert.AreEqual(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Parse_UnknownOption_FailsClosed()
    {
        var arguments = ValidArguments.Concat(["--password", "secret"]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "Unknown option", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_NonLoopbackWithoutAcknowledgement_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--bind-address", "0.0.0.0"]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "--acknowledge-plaintext-http", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_MutableImageTag_IsRejected()
    {
        var arguments = ValidArguments.ToArray();
        arguments[^1] = "ghcr.io/example/cameraagent:latest";

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "immutable", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_ResumeWithoutInstanceId_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--resume"]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "--instance-id", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_ShortImageDigest_IsRejected()
    {
        var arguments = ValidArguments.ToArray();
        arguments[^1] = "sha256:abc";

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_GenerateAndPasswordFile_AreMutuallyExclusive()
    {
        var arguments = ValidArguments.Concat([
            "--password-file", "/srv/hvo/password",
            "--generate-password"
        ]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "cannot be combined", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_SignedLocalCatalog_UsesLocalChannelWithoutNetwork()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat(["--catalog-manifest", "/srv/hvo/catalog-manifest.json", "--no-download"])
            .ToArray();

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual(DistributionChannel.Local, request.Channel);
        Assert.IsTrue(request.NoDownload);
        Assert.IsNull(request.CatalogBundle);
    }

    [TestMethod]
    public void Parse_StableCatalogManifest_RequiresHttpsLocator()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat(["--channel", "stable", "--catalog-manifest", "https://downloads.example/catalog/catalog-manifest.json"])
            .ToArray();

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual(DistributionChannel.Stable, request.Channel);
    }

    [TestMethod]
    public void Parse_NonLocalChannelWithLocalManifest_IsRejected()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat(["--channel", "stable", "--catalog-manifest", "/srv/hvo/catalog-manifest.json"])
            .ToArray();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_UnknownChannel_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--channel", "latest"]).ToArray();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_SignedIndex_AllowsExplicitOrDefaultCatalogVersion()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat([
                "--channel", "prerelease",
                "--catalog-index", "https://downloads.example/catalog-index.json",
                "--catalog-version", "hyg-v4.2-p3-s2-r1",
                "--asset-base-url", "https://mirror.example/releases"
            ])
            .ToArray();

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual("hyg-v4.2-p3-s2-r1", request.CatalogVersion);
        Assert.AreEqual(DistributionChannel.Prerelease, request.Channel);
    }

    [TestMethod]
    public void ParseCommand_Status_RequiresExactInstanceIdentity()
    {
        var instanceId = Guid.NewGuid();

        var request = (LifecycleRequest)CommandLine.ParseCommand([
            "status", "--instance-id", instanceId.ToString("D"), "--json"
        ]);

        Assert.IsNull(request.Operation);
        Assert.AreEqual(instanceId, request.InstanceId);
        Assert.IsTrue(request.Json);
    }

    [TestMethod]
    public void ParseCommand_Upgrade_RequiresImmutableImageAndCompatibilityAcknowledgement()
    {
        var request = (LifecycleRequest)CommandLine.ParseCommand([
            "cameraagent", "upgrade",
            "--instance-id", Guid.NewGuid().ToString("D"),
            "--image-ref", $"cameraagent@sha256:{new string('a', 64)}",
            "--migration-backward-compatible",
            "--no-download"
        ]);

        Assert.AreEqual(LifecycleOperationKind.Upgrade, request.Operation);
        Assert.IsTrue(request.MigrationBackwardCompatible);
        Assert.IsTrue(request.NoDownload);
    }

    [TestMethod]
    public void ParseCommand_UpgradeWithMutableTag_IsRejected()
    {
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "upgrade", "--instance-id", Guid.NewGuid().ToString("D"),
            "--image-ref", "cameraagent:latest"
        ]));
    }

    [TestMethod]
    public void ParseCommand_OwnerPasswordFile_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "rollback", "--instance-id", Guid.NewGuid().ToString("D"),
            "--owner-password-file", "/owner-private/password"
        ]));

        StringAssert.Contains(exception.Message, "Unknown option", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParseCommand_OwnerRecoveryRequiresOneSafePasswordSource()
    {
        var instanceId = Guid.NewGuid();
        var generated = (OwnerRecoveryRequest)CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", instanceId.ToString("D"),
            "--generate-password",
            "--json"
        ]);

        Assert.AreEqual(instanceId, generated.InstanceId);
        Assert.IsTrue(generated.GeneratePassword);
        Assert.IsTrue(generated.Json);
        Assert.IsNull(generated.PasswordFile);
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", instanceId.ToString("D")
        ]));
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", instanceId.ToString("D"),
            "--generate-password",
            "--password-file", "/owner-private/password"
        ]));
    }

    [TestMethod]
    public void ParseCommand_OwnerRecoveryRejectsPasswordOnCommandLine()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", Guid.NewGuid().ToString("D"),
            "--password", "must-not-be-accepted"
        ]));

        StringAssert.Contains(exception.Message, "Unknown option", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParseCommand_OwnerRecoveryDoesNotReflectUnexpectedPositionalInput()
    {
        const string secret = "must-not-be-reflected";

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner", secret
        ]));

        Assert.IsFalse(exception.Message.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseCommand_PurgeRequiresMatchingConfirmation()
    {
        var instanceId = Guid.NewGuid();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "purge", "--instance-id", instanceId.ToString("D"),
            "--confirm-instance-id", Guid.NewGuid().ToString("D")
        ]));
    }

    [TestMethod]
    public void ParseCommand_CatalogCommandsHaveDistinctScope()
    {
        var install = (LifecycleRequest)CommandLine.ParseCommand([
            "catalog", "install", "--catalog-bundle", "/srv/hvo/catalog.bundle", "--dry-run"
        ]);
        var select = (LifecycleRequest)CommandLine.ParseCommand([
            "catalog", "select", "--instance-id", Guid.NewGuid().ToString("D"),
            "--catalog-version", "hyg-v4.2-p3-s2-r2"
        ]);

        Assert.AreEqual(LifecycleOperationKind.CatalogInstall, install.Operation);
        Assert.AreEqual(LifecycleOperationKind.CatalogSelect, select.Operation);
    }
}
