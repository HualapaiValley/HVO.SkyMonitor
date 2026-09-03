using HVO.SkyMonitor.Deployment;
using ImageInstallationIdentity = HVO.SkyMonitor.Deployment.Contracts.ImageInstallationIdentity;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ReplayRunnerComposeTests
{
    [TestMethod]
    public void Write_InProcessProfile_OmitsRunnerAndAuthenticationKey()
    {
        var root = CreateRoot();
        try
        {
            var (request, paths) = CreateRequest(root, CameraAgentReplayProfile.InProcess);

            var compose = Write(request, paths);

            var text = File.ReadAllText(compose.ComposeFile);
            Assert.IsFalse(text.Contains("\n  replay-runner:", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains("/run/hvo-replay", StringComparison.Ordinal));
            Assert.IsFalse(File.Exists(Path.Combine(paths.ConfigRoot, "secrets", "replay-runner-auth-key")));
            Assert.IsFalse(File.Exists(Path.Combine(paths.ConfigRoot, "secrets", "OwnerRecovery__Enabled")));
            Assert.IsNull(compose.ReplayRunnerContainerName);
            Assert.AreEqual("cameraagent-compose-v2", ComposeDeployment.TemplateVersionFor(request.ReplayProfile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Write_LocalRunnerProfile_UsesSameImageWithLeastPrivilegeLocalBoundary()
    {
        var root = CreateRoot();
        try
        {
            var (request, paths) = CreateRequest(root, CameraAgentReplayProfile.LocalRunner);

            var compose = Write(request, paths);

            var text = File.ReadAllText(compose.ComposeFile);
            var runner = text[text.IndexOf("\n  replay-runner:", StringComparison.Ordinal)..];
            Assert.AreEqual($"{compose.ContainerName}-replay", compose.ReplayRunnerContainerName);
            Assert.AreEqual("cameraagent-compose-v3", ComposeDeployment.TemplateVersionFor(request.ReplayProfile));
            StringAssert.Contains(runner, "network_mode: none", StringComparison.Ordinal);
            StringAssert.Contains(runner, "entrypoint: [/app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner]", StringComparison.Ordinal);
            StringAssert.Contains(runner, "cap_drop: [ALL]", StringComparison.Ordinal);
            StringAssert.Contains(runner, "cpus: \"0.5\"", StringComparison.Ordinal);
            StringAssert.Contains(runner, "mem_limit: 2G", StringComparison.Ordinal);
            StringAssert.Contains(
                runner,
                "test: [CMD, /app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner, --probe]",
                StringComparison.Ordinal);
            StringAssert.Contains(
                runner,
                "/secrets/replay-runner-auth-key:/run/hvo-secrets/replay-runner-auth-key:ro",
                StringComparison.Ordinal);
            StringAssert.Contains(runner, "HVO_REPLAY_MAX_TRANSFER_BYTES: \"134217728\"", StringComparison.Ordinal);
            Assert.IsFalse(runner.Contains("/secrets:/run/hvo-secrets", StringComparison.Ordinal));
            Assert.IsFalse(runner.Contains("/app/data/raw", StringComparison.Ordinal));
            Assert.IsFalse(runner.Contains("/app/data/archive", StringComparison.Ordinal));
            StringAssert.Contains(
                text[..text.IndexOf("\n  replay-runner:", StringComparison.Ordinal)],
                "${HVO_STATE_ROOT}/replay-runner:/run/hvo-replay",
                StringComparison.Ordinal);

            var keyPath = Path.Combine(paths.ConfigRoot, "secrets", "replay-runner-auth-key");
            Assert.AreEqual(64, File.ReadAllText(keyPath).Length);
            SafeFileSystem.ValidateOwnerFile(keyPath);
            Assert.AreEqual(
                "LocalRunner",
                File.ReadAllText(Path.Combine(
                    paths.ConfigRoot,
                    "secrets",
                    "CameraAgent__ProcessingGraphs__ReplayProfile")));
            Assert.AreEqual(
                "/run/hvo-replay/runner.sock",
                File.ReadAllText(Path.Combine(
                    paths.ConfigRoot,
                    "secrets",
                    "CameraAgent__ProcessingGraphs__LocalRunner__SocketPath")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InstallationRequestIdentityIncludesReplayProfile()
    {
        var root = CreateRoot();
        try
        {
            var (inProcess, _) = CreateRequest(root, CameraAgentReplayProfile.InProcess);
            var localRunner = inProcess with { ReplayProfile = CameraAgentReplayProfile.LocalRunner };
            var instanceId = Guid.NewGuid();

            Assert.AreNotEqual(
                CameraAgentInstaller.ComputeRequestSha256(inProcess, instanceId),
                CameraAgentInstaller.ComputeRequestSha256(localRunner, instanceId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LegacyRequestIdentityMatchesOnlyInProcessProfile()
    {
        var root = CreateRoot();
        try
        {
            var (inProcess, _) = CreateRequest(root, CameraAgentReplayProfile.InProcess);
            var localRunner = inProcess with { ReplayProfile = CameraAgentReplayProfile.LocalRunner };
            var instanceId = Guid.NewGuid();
            var legacySha256 = CameraAgentInstaller.ComputeLegacyRequestSha256(inProcess, instanceId);

            Assert.IsTrue(CameraAgentInstaller.RequestIdentityMatches(legacySha256, inProcess, instanceId));
            Assert.IsFalse(CameraAgentInstaller.RequestIdentityMatches(legacySha256, localRunner, instanceId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LocalRunnerRequiresImmutableImageCapabilityContract()
    {
        var image = new ImageInstallationIdentity(
            "registry",
            $"sha256:{new string('a', 64)}",
            $"sha256:{new string('a', 64)}",
            "amd64",
            null,
            UpgradeCompatibility: "backward-compatible",
            SourceRevision: new string('b', 40),
            Component: "CameraAgent",
            ConfigurationContract: "cameraagent-install-v1",
            CatalogContract: "hyg-v42-production-p3-s2");

        Assert.IsTrue(CameraAgentInstaller.IsValid(image, CameraAgentReplayProfile.InProcess));
        Assert.IsFalse(CameraAgentInstaller.IsValid(image, CameraAgentReplayProfile.LocalRunner));
        Assert.IsTrue(CameraAgentInstaller.IsValid(
            image with { ReplayRunnerContract = "local-replay-runner-v1" },
            CameraAgentReplayProfile.LocalRunner));
    }

    private static ComposeFiles Write(InstallRequest request, InstallationPaths paths) => ComposeDeployment.Write(
        request,
        paths,
        Guid.NewGuid(),
        Guid.NewGuid(),
        1000,
        1000,
        $"sha256:{new string('a', 64)}",
        "test-catalog-v1",
        Path.Combine(paths.ConfigRoot, "owner-password"),
        "verification-token",
        "lifecycle-token",
        passwordAuthorityEnabled: false);

    private static (InstallRequest Request, InstallationPaths Paths) CreateRequest(
        string root,
        CameraAgentReplayProfile replayProfile)
    {
        var request = new InstallRequest
        {
            FriendlyName = "Replay Test",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            ImageReference = $"sha256:{new string('a', 64)}",
            CatalogBundle = Path.Combine(root, "catalog.bundle"),
            ReplayProfile = replayProfile
        };
        return (request, InstallationPaths.Create(root, Guid.NewGuid(), "test-catalog"));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-replay-compose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
