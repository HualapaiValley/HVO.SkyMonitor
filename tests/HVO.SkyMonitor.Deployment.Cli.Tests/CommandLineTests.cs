using System.Text.Json;
using HVO.SkyMonitor.Deployment;

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
}
