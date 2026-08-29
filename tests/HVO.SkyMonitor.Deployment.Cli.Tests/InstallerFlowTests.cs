using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class InstallerFlowTests
{
    [TestMethod]
    public async Task InstallAsync_FreshThenCompletedRerun_PreservesEveryIdentity()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-flow-{Guid.NewGuid():N}");
        var previousTestRoot = Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT");
        Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", "1");
        var imageId = $"sha256:{new string('b', 64)}";
        var request = new InstallRequest
        {
            InstanceId = Guid.Parse("e8260a29-0717-4730-a9ea-7780b3a8d377"),
            FriendlyName = "Contract Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            Port = 55315
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None);
            var passwordSha256 = await SafeFileSystem.ComputeSha256Async(first.PasswordFile, CancellationToken.None);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var statePath = Path.Combine(first.InstanceRoot, "state", "deployment", "installation-state.json");
            var retainedManifest = await File.ReadAllTextAsync(manifestPath);
            var retainedState = await File.ReadAllTextAsync(statePath);
            var driftedManifest = JsonNode.Parse(retainedManifest)?.AsObject()
                ?? throw new AssertFailedException("The retained manifest was not valid JSON.");
            driftedManifest["composeModelSha256"] = new string('0', 64);
            await File.WriteAllTextAsync(manifestPath, driftedManifest.ToJsonString());

            var drift = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None));
            StringAssert.Contains(drift.Message, "retained immutable manifest", StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifestPath, retainedManifest);
            await File.WriteAllTextAsync(statePath, retainedState);

            var second = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None);

            Assert.AreEqual(InstallationOutcome.Installed, first.Outcome);
            Assert.AreEqual("owner-password-change-required", first.OwnerBootstrapState);
            Assert.AreEqual(first.InstallationId, second.InstallationId);
            Assert.AreEqual(first.InstanceId, second.InstanceId);
            Assert.AreEqual(first.ApplicationIdentity, second.ApplicationIdentity);
            Assert.AreEqual(first.ConfigurationSha256, second.ConfigurationSha256);
            Assert.AreEqual(first.RigProfileSha256, second.RigProfileSha256);
            Assert.AreEqual(first.ScheduleSha256, second.ScheduleSha256);
            Assert.AreEqual(first.Catalog, second.Catalog);
            Assert.AreEqual(first.Image, second.Image);
            Assert.AreEqual(passwordSha256, await SafeFileSystem.ComputeSha256Async(second.PasswordFile, CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(
                first.InstanceRoot,
                "config",
                "secrets",
                "LocalIdentity__AdminPasswordFile")));

            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                first.InstanceRoot,
                "state",
                "deployment",
                "installation-state.json")));
            Assert.AreEqual("Completed", state.RootElement.GetProperty("phase").GetString());
            Assert.AreEqual("Completed", state.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(3, runner.ComposeUpCount);

            var resultPath = Path.Combine(first.InstanceRoot, "state", "deployment", "installation-result.json");
            var retainedResult = await File.ReadAllTextAsync(resultPath);
            var driftedResult = JsonNode.Parse(retainedResult)?.AsObject()
                ?? throw new AssertFailedException("The retained result was not valid JSON.");
            driftedResult["friendlyName"] = "Drifted Result";
            await File.WriteAllTextAsync(resultPath, driftedResult.ToJsonString());
            var resultDrift = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None));
            StringAssert.Contains(resultDrift.Message, "retained result", StringComparison.Ordinal);
            Assert.AreEqual(3, runner.ComposeUpCount);
            await File.WriteAllTextAsync(resultPath, retainedResult);

            var unsupportedState = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new AssertFailedException("The retained state was not valid JSON.");
            unsupportedState["schemaVersion"] = 999;
            await File.WriteAllTextAsync(statePath, unsupportedState.ToJsonString());
            var unsupported = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None));
            StringAssert.Contains(unsupported.Message, "invalid or unsupported", StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previousTestRoot);
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InstallAsync_OwnerAuthenticationFailureThenResume_PreservesPasswordAuthorityUntilSeeded()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-resume-{Guid.NewGuid():N}");
        var previousTestRoot = Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT");
        Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", "1");
        var imageId = $"sha256:{new string('c', 64)}";
        var request = new InstallRequest
        {
            InstanceId = Guid.Parse("a71e12c8-bef2-4184-8fd4-3548b74a7d89"),
            FriendlyName = "Resume Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            Port = 55316
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var owner = new FailingOnceOwnerClient();
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None));
            var statePath = Path.Combine(
                root,
                "cameraagents",
                request.InstanceId.Value.ToString("D"),
                "state",
                "deployment",
                "installation-state.json");
            using (var failed = JsonDocument.Parse(await File.ReadAllTextAsync(statePath)))
            {
                Assert.AreEqual("OwnerBootstrap", failed.RootElement.GetProperty("phase").GetString());
                Assert.AreEqual("Failed", failed.RootElement.GetProperty("status").GetString());
            }

            var resumed = await CameraAgentInstaller.InstallAsync(
                request with { Resume = true },
                runner,
                _ => owner,
                1000,
                1000,
                CancellationToken.None);

            Assert.AreEqual(InstallationOutcome.Installed, resumed.Outcome);
            Assert.AreEqual("owner-password-change-required", resumed.OwnerBootstrapState);
            Assert.AreEqual(3, runner.ComposeUpCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT", previousTestRoot);
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class InstallerOwnerClient : IOwnerBootstrapClient
    {
        private int stateReadCount;

        public Task WaitForHealthAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("owner@example.test", ownerEmail);
            Assert.IsGreaterThanOrEqualTo(16, password.Length);
            return Task.FromResult(stateReadCount++ == 0
                ? "owner-temporary-password"
                : "owner-password-change-required");
        }

        public Task VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("hyg-v42-production", expectation.Catalog.CatalogId);
            Assert.AreEqual(64, expectation.ConfigurationSha256.Length);
            return Task.CompletedTask;
        }
    }

    private sealed class InstallerProcessRunner(string imageId, string root, Guid instanceId) : IProcessRunner
    {
        public int ComposeUpCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
            {
                return Success("unix:///var/run/docker.sock");
            }
            if (arguments.Count >= 2 && arguments[0] == "--host") arguments = arguments.Skip(2).ToArray();
            if (arguments.SequenceEqual(["info", "--format", "{{json .}}"], StringComparer.Ordinal))
            {
                return Success("{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}");
            }
            if (arguments.SequenceEqual(["compose", "version", "--short"], StringComparer.Ordinal))
            {
                return Success("2.40.0");
            }
            if (arguments.Count == 3 && arguments[0] == "image" && arguments[1] == "inspect")
            {
                return Success($"[{{\"Id\":\"{imageId}\",\"Architecture\":\"amd64\",\"Os\":\"linux\",\"RepoDigests\":[]}}]");
            }
            if (arguments.Count == 3 && arguments[0] == "container" && arguments[1] == "inspect")
            {
                var id = instanceId.ToString("D");
                var instanceRoot = Path.Combine(root, "cameraagents", id);
                var catalog = Path.Combine(root, "catalogs", "hyg-v42-production");
                return Success(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Image = imageId,
                        State = new { Running = true, Health = new { Status = "healthy" } },
                        Config = new
                        {
                            User = "1000:1000",
                            Labels = new Dictionary<string, string>
                            {
                                ["com.docker.compose.project"] = arguments[2],
                                ["io.hvo.skymonitor.instance-id"] = id
                            }
                        },
                        HostConfig = new { ReadonlyRootfs = true, Privileged = false },
                        Mounts = new[]
                        {
                            new { Source = Path.Combine(instanceRoot, "config", "camera-module.json"), Destination = "/app/cameraagent.deploy.json", RW = false },
                            new { Source = catalog, Destination = "/app/catalog", RW = false },
                            new { Source = Path.Combine(instanceRoot, "state", "identity"), Destination = "/app/App_Data", RW = true }
                        }
                    }
                }));
            }
            if (arguments.Contains("config", StringComparer.Ordinal))
            {
                return Success("services:\n  cameraagent:\n    image: immutable\n");
            }
            if (arguments.Contains("up", StringComparer.Ordinal))
            {
                ComposeUpCount++;
                return Success(string.Empty);
            }
            Assert.Fail($"Unexpected process: {fileName} {string.Join(' ', arguments)}");
            return Success(string.Empty);
        }

        private static Task<ProcessResult> Success(string output)
            => Task.FromResult(new ProcessResult(0, output, string.Empty));
    }

    private sealed class FailingOnceOwnerClient : IOwnerBootstrapClient
    {
        private int stateCount;

        public Task WaitForHealthAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stateCount++ == 0)
            {
                throw new InstallerException("injected owner authentication failure");
            }
            return Task.FromResult(stateCount == 2 ? "owner-temporary-password" : "owner-password-change-required");
        }

        public Task VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

}
