using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed partial class InstallerFlowTests
{
    // The installer always runs as the invoking Docker-capable user, so the contract fixtures must use the real
    // runtime identity rather than a constant that only matches one developer machine.
    private static readonly uint RuntimeUid = NativeLinux.getuid();
    private static readonly uint RuntimeGid = NativeLinux.getgid();

    // The bootstrap reads owner state once with password authority and once after it is removed.
    private static readonly string[] DefaultPasswordReadTwice = ["skymonitor", "skymonitor"];

    [TestMethod]
    public void IsAllowedOwnerBootstrapState_AllowsOnlyInstalledStateOrCompletedReplacement()
    {
        Assert.IsTrue(OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
            "owner-password-change-required", "owner-password-change-required", allowCompletedPasswordReplacement: false));
        Assert.IsTrue(OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
            "owner-password-change-required", "owner-ready", allowCompletedPasswordReplacement: true));
        Assert.IsFalse(OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
            "owner-password-change-required", "owner-ready", allowCompletedPasswordReplacement: false));
        Assert.IsFalse(OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
            "owner-password-change-required", "owner-temporary-password", allowCompletedPasswordReplacement: true));
        Assert.IsFalse(OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
            "owner-ready", "owner-password-change-required", allowCompletedPasswordReplacement: true));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_FreshThenCompletedRerun_PreservesEveryIdentity()
    {
        // Deliberately environment-gated. These contracts install a real catalog bundle, which is
        // too large to check in, so the CI Unit lane restores it from the hyg-v42-contracts artifact
        // and exports this variable; locally it is absent and the test skips. The skip is silent --
        // a skipped MSTest records notExecuted="0" and inconclusive="0" in its TRX -- so the lane
        // that is supposed to run these asserts the bundle exists before exporting the variable,
        // and a skip there is a broken restore rather than an expected local condition.
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-flow-{Guid.NewGuid():N}");
        var imageId = $"sha256:{new string('b', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("e8260a29-0717-4730-a9ea-7780b3a8d377"),
            FriendlyName = "Contract Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            Port = SelectFreePort()
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var probe = new RecordingPortProbe();
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe);
            Assert.IsTrue(first.OwnerPasswordIsDefault);
            CollectionAssert.AreEqual(DefaultPasswordReadTwice, owner.Passwords);
            owner.CurrentInstallationState = "owner-ready";
            var passwordSha256 = await SafeFileSystem.ComputeSha256Async(first.PasswordFile, CancellationToken.None);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var resultPath = Path.Combine(first.InstanceRoot, "state", "deployment", "installation-result.json");
            var statePath = Path.Combine(first.InstanceRoot, "state", "deployment", "installation-state.json");
            var retainedManifest = await File.ReadAllTextAsync(manifestPath);
            var retainedResult = await File.ReadAllTextAsync(resultPath);
            var retainedState = await File.ReadAllTextAsync(statePath);
            var legacyManifest = JsonNode.Parse(retainedManifest)?.AsObject()
                ?? throw new AssertFailedException("The retained manifest was not valid JSON.");
            legacyManifest["composeTemplateVersion"] = "cameraagent-compose-v1";
            await File.WriteAllTextAsync(manifestPath, legacyManifest.ToJsonString());
            var composeUpCount = runner.ComposeUpCount;

            var legacy = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe));

            StringAssert.Contains(legacy.Message, "invalid or unsupported", StringComparison.Ordinal);
            Assert.AreEqual(composeUpCount, runner.ComposeUpCount);
            Assert.AreEqual(retainedState, await File.ReadAllTextAsync(statePath));
            await File.WriteAllTextAsync(manifestPath, retainedManifest);

            var driftedManifest = JsonNode.Parse(retainedManifest)?.AsObject()
                ?? throw new AssertFailedException("The retained manifest was not valid JSON.");
            driftedManifest["composeModelSha256"] = new string('0', 64);
            await File.WriteAllTextAsync(manifestPath, driftedManifest.ToJsonString());

            var drift = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe));
            StringAssert.Contains(drift.Message, "retained immutable manifest", StringComparison.Ordinal);
            await File.WriteAllTextAsync(manifestPath, retainedManifest);
            await File.WriteAllTextAsync(statePath, retainedState);

            var legacyInProcessManifest = JsonNode.Parse(retainedManifest)?.AsObject()
                ?? throw new AssertFailedException("The retained manifest was not valid JSON.");
            var legacyInProcessResult = JsonNode.Parse(retainedResult)?.AsObject()
                ?? throw new AssertFailedException("The retained result was not valid JSON.");
            legacyInProcessManifest["image"]!.AsObject().Remove("replayRunnerContract");
            legacyInProcessResult["image"]!.AsObject().Remove("replayRunnerContract");
            var legacyInProcessManifestJson = legacyInProcessManifest.ToJsonString();
            var legacyInProcessResultJson = legacyInProcessResult.ToJsonString();
            await File.WriteAllTextAsync(manifestPath, legacyInProcessManifestJson);
            await File.WriteAllTextAsync(resultPath, legacyInProcessResultJson);

            var second = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe);

            Assert.AreEqual(InstallationOutcome.Installed, first.Outcome);
            Assert.AreEqual("owner-password-change-required", first.OwnerBootstrapState);
            Assert.AreEqual(first.InstallationId, second.InstallationId);
            Assert.AreEqual(first.InstanceId, second.InstanceId);
            Assert.AreEqual(first.ApplicationIdentity, second.ApplicationIdentity);
            Assert.AreEqual(first.ConfigurationSha256, second.ConfigurationSha256);
            Assert.AreEqual(first.RigProfileSha256, second.RigProfileSha256);
            Assert.AreEqual(first.ScheduleSha256, second.ScheduleSha256);
            Assert.AreEqual(first.Catalog, second.Catalog);
            Assert.AreEqual(first.Image with { ReplayRunnerContract = null }, second.Image);
            Assert.AreEqual(legacyInProcessManifestJson, await File.ReadAllTextAsync(manifestPath));
            Assert.AreEqual(legacyInProcessResultJson, await File.ReadAllTextAsync(resultPath));
            Assert.AreEqual(passwordSha256, await SafeFileSystem.ComputeSha256Async(second.PasswordFile, CancellationToken.None));
            Assert.IsTrue(second.OwnerPasswordIsDefault);
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
            // The install started the container twice, once to seed the owner; the completed re-run, with nothing to
            // change, starts nothing (#1315).
            Assert.AreEqual(2, runner.ComposeUpCount);

            var driftedResult = JsonNode.Parse(legacyInProcessResultJson)?.AsObject()
                ?? throw new AssertFailedException("The retained result was not valid JSON.");
            driftedResult["friendlyName"] = "Drifted Result";
            await File.WriteAllTextAsync(resultPath, driftedResult.ToJsonString());
            var resultDrift = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe));
            StringAssert.Contains(resultDrift.Message, "retained result", StringComparison.Ordinal);
            Assert.AreEqual(2, runner.ComposeUpCount);
            await File.WriteAllTextAsync(resultPath, legacyInProcessResultJson);

            var unsupportedState = JsonNode.Parse(await File.ReadAllTextAsync(statePath))?.AsObject()
                ?? throw new AssertFailedException("The retained state was not valid JSON.");
            unsupportedState["schemaVersion"] = 999;
            await File.WriteAllTextAsync(statePath, unsupportedState.ToJsonString());
            var unsupported = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe));
            StringAssert.Contains(unsupported.Message, "invalid or unsupported", StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_OwnerAuthenticationFailureThenResume_PreservesPasswordAuthorityUntilSeeded()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        // The subject of this flow is the injected owner-authentication failure and its resume, not host socket
        // admission, so an OS-selected port is held occupied for the whole scenario to prove the explicit probe keeps
        // shared-host port state out of the contract.
        using var occupiedPortListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupiedPortListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        occupiedPortListener.Listen(1);
        var occupiedPort = ((IPEndPoint)occupiedPortListener.LocalEndPoint!).Port;
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-resume-{Guid.NewGuid():N}");
        var imageId = $"sha256:{new string('c', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("a71e12c8-bef2-4184-8fd4-3548b74a7d89"),
            FriendlyName = "Resume Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            Port = occupiedPort
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var probe = new RecordingPortProbe();
        var owner = new FailingOnceOwnerClient();
        try
        {
            var ownerFailure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe));
            // Only the injected owner-authentication failure proves the intended phase was reached. Any other
            // InstallerException, such as the port preflight refusal, must fail here instead of masquerading as it.
            Assert.AreEqual("injected owner authentication failure", ownerFailure.Message);
            Assert.AreEqual(1, probe.Invocations);
            Assert.AreEqual("127.0.0.1", probe.LastBindAddress);
            Assert.AreEqual(occupiedPort, probe.LastPort);
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
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe);

            Assert.AreEqual(InstallationOutcome.Installed, resumed.Outcome);
            Assert.AreEqual("owner-password-change-required", resumed.OwnerBootstrapState);
            // The resume authenticates with the default credential the failed run already published.
            Assert.IsTrue(resumed.OwnerPasswordIsDefault);
            Assert.AreEqual("skymonitor\n", await File.ReadAllTextAsync(resumed.PasswordFile));
            Assert.AreEqual(3, runner.ComposeUpCount);
            // Retained state keeps a resume from re-admitting the port, exactly as the production path does.
            Assert.AreEqual(1, probe.Invocations);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_WithoutOwnerEmailOrPasswordOption_SeedsTheDocumentedDefaultSignIn()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-default-owner-{Guid.NewGuid():N}");
        var imageId = $"sha256:{new string('d', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("5d0b8c51-7e0f-4a8e-9a73-1278a0d1e001"),
            FriendlyName = "Default Owner Camera",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            Port = SelectFreePort()
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var owner = new InstallerOwnerClient { ExpectedOwnerEmail = "owner@cameraagent.local" };
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            Assert.AreEqual(InstallationOutcome.Installed, result.Outcome);
            Assert.AreEqual("owner-password-change-required", result.OwnerBootstrapState);
            Assert.AreEqual("owner@cameraagent.local", result.OwnerEmail);
            Assert.IsTrue(result.OwnerPasswordIsDefault);
            CollectionAssert.AreEqual(DefaultPasswordReadTwice, owner.Passwords);
            Assert.AreEqual(
                Path.Combine(result.ConfigRoot, "owner-bootstrap", "temporary-password"),
                result.PasswordFile);
            Assert.AreEqual("skymonitor\n", await File.ReadAllTextAsync(result.PasswordFile));
            Assert.AreEqual(
                "owner@cameraagent.local",
                await File.ReadAllTextAsync(Path.Combine(result.ConfigRoot, "secrets", "LocalIdentity__AdminEmail")));
            Assert.IsFalse(File.Exists(Path.Combine(result.ConfigRoot, "secrets", "LocalIdentity__AdminPasswordFile")));
            Assert.AreEqual(
                "Sign in as admin / skymonitor; a password change is required at first sign-in.",
                Program.OwnerSignInInstruction(result));
            using var retained = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                result.InstanceRoot, "state", "deployment", "installation-result.json")));
            Assert.IsTrue(retained.RootElement.GetProperty("ownerPasswordIsDefault").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("  leading and inner\n", "  leading and inner", false)]
    [DataRow("  leading and inner\r\n", "  leading and inner", false)]
    [DataRow("trailing and inner  \n", "trailing and inner  ", false)]
    [DataRow("trailing and inner  \r\n", "trailing and inner  ", false)]
    [DataRow("skymonitor\r\n", "skymonitor", true)]
    [DataRow(" skymonitor\n", " skymonitor", false)]
    public async Task InstallAsync_PasswordFileWithSignificantSpaces_AuthenticatesWithThePasswordCameraAgentSeeds(
        string content,
        string seededPassword,
        bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-spaced-owner-{Guid.NewGuid():N}");
        var inputRoot = Path.Combine(Path.GetTempPath(), $"hvo-installer-spaced-input-{Guid.NewGuid():N}");
        var imageId = $"sha256:{new string('f', 64)}";
        Directory.CreateDirectory(inputRoot);
        var supplied = Path.Combine(inputRoot, "operator-password");
        await File.WriteAllTextAsync(supplied, content);
        File.SetUnixFileMode(supplied, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("5d0b8c51-7e0f-4a8e-9a73-1278a0d1e003"),
            FriendlyName = "Spaced Password Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            PasswordFile = supplied,
            Port = SelectFreePort()
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            // CameraAgent seeds the owner from the mounted file with this very reader (it is compiled from CameraAgent's
            // own source), so authentication succeeds exactly when the bootstrap submits this value unchanged.
            var cameraAgentSeeds = HVO.SkyMonitor.CameraAgent.Configuration.OwnerPasswordFileText.Decode(
                await File.ReadAllBytesAsync(result.PasswordFile));
            Assert.AreEqual(seededPassword, cameraAgentSeeds);
            Assert.HasCount(2, owner.Passwords);
            Assert.IsTrue(owner.Passwords.All(password => string.Equals(password, cameraAgentSeeds, StringComparison.Ordinal)));
            Assert.AreEqual("owner-password-change-required", result.OwnerBootstrapState);
            Assert.AreEqual(isDefault, result.OwnerPasswordIsDefault);
        }
        finally
        {
            Directory.Delete(inputRoot, recursive: true);
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_GeneratePassword_SeedsARandomTemporaryPasswordAndNamesItsFile()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-generated-owner-{Guid.NewGuid():N}");
        var imageId = $"sha256:{new string('e', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("5d0b8c51-7e0f-4a8e-9a73-1278a0d1e002"),
            FriendlyName = "Generated Owner Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            GeneratePassword = true,
            Port = SelectFreePort()
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                _ => owner,
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            Assert.AreEqual("owner-password-change-required", result.OwnerBootstrapState);
            Assert.IsFalse(result.OwnerPasswordIsDefault);
            Assert.HasCount(2, owner.Passwords);
            Assert.AreEqual(owner.Passwords[0], owner.Passwords[1]);
            Assert.IsGreaterThanOrEqualTo(32, owner.Passwords[0].Length);
            Assert.AreNotEqual("skymonitor", owner.Passwords[0]);
            Assert.AreEqual($"Temporary password file: {result.PasswordFile}", Program.OwnerSignInInstruction(result));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReleaseFolder_LoadsItsOfflineArchiveAndRecordsTheRelease()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-install-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        // Docker's classic store names the loaded image by its configuration digest, which release.json gives.
        var imageId = release.ConfigDigest(platform);
        var instanceId = Guid.Parse("2b7f6a3c-1d54-4e0a-9c31-6f2b0a5d4e18");
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder);
        var runner = new InstallerProcessRunner(imageId, root, instanceId) { Labels = release.Labels() };
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            Assert.AreEqual(InstallationOutcome.Installed, result.Outcome);
            Assert.AreEqual(imageId, result.Image.ImageId);
            Assert.AreEqual("archive", result.Image.Source);
            Assert.AreEqual((string)release.Archive(platform)["sha256"]!, result.Image.ArchiveSha256);
            Assert.AreEqual(1, runner.LoadCount);
            Assert.AreEqual(0, runner.PullCount, "an archive install pulls nothing");
            using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(result.InstanceRoot, "state", "deployment", "image-distribution.json")));
            var record = evidence.RootElement;
            Assert.AreEqual(3, record.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual(release.Version, record.GetProperty("releaseVersion").GetString());
            Assert.AreEqual($"v{release.Version}", record.GetProperty("releaseTag").GetString());
            Assert.AreEqual(ReleaseFixture.Commit, record.GetProperty("releaseCommit").GetString());
            Assert.AreEqual(
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(release.Folder, "release.json")))),
                record.GetProperty("releaseJsonSha256").GetString());
            Assert.AreEqual(release.DigestReference, record.GetProperty("digestReference").GetString());
            Assert.AreEqual(platform, record.GetProperty("platform").GetString());
            Assert.AreEqual("archive", record.GetProperty("source").GetString());
            Assert.AreEqual((string)release.Archive(platform)["name"]!, record.GetProperty("archiveName").GetString());
            Assert.AreEqual(imageId, record.GetProperty("imageId").GetString());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReleaseFromTheRegistry_PullsItsDigestAndRecordsTheRelease()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-registry-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        // Docker's classic store names a pulled image by its configuration digest, which release.json gives.
        var imageId = release.ConfigDigest(platform);
        var instanceId = Guid.Parse("5d0e7a4b-62c1-4f93-8a17-3b9e0c4d2f61");
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with { ImageSource = ReleaseImageSource.Registry };
        var runner = new InstallerProcessRunner(imageId, root, instanceId)
        {
            Labels = release.Labels(),
            RepoDigests = [release.DigestReference],
            Registry = new FakeRegistry(release.RegistryContent)
        };
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            Assert.AreEqual(imageId, result.Image.ImageId);
            Assert.AreEqual("registry", result.Image.Source);
            Assert.AreEqual(release.DigestReference, result.Image.ImmutableReference);
            Assert.IsNull(result.Image.ArchiveSha256);
            // The index, the platform manifest and the configuration are read before the one pull.
            string[] imports = ["registry", "registry", "registry", "pull"];
            CollectionAssert.AreEqual(imports, runner.Imports);
            Assert.AreEqual(1, runner.PullCount);
            Assert.AreEqual(0, runner.LoadCount);
            using var evidence = JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(result.InstanceRoot, "state", "deployment", "image-distribution.json")));
            Assert.AreEqual("registry", evidence.RootElement.GetProperty("source").GetString());
            Assert.AreEqual(JsonValueKind.Null, evidence.RootElement.GetProperty("archiveName").ValueKind);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("org.opencontainers.image.version", "9.9.9")]
    [DataRow("org.opencontainers.image.revision", "ffffffffffffffffffffffffffffffffffffffff")]
    [DataRow("io.hvo.skymonitor.component", "LogicHost")]
    [DataRow("org.opencontainers.image.created", "2026-10-17T18:00:01Z")]
    public async Task InstallAsync_LoadedImageNotLabelledAsTheRelease_IsRemovedBeforeComposeStartsAndRecordsNothing(string label, string value)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-labels-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        var imageId = release.ConfigDigest(platform);
        var instanceId = Guid.Parse("6c1de0b2-3a48-4f7d-8e5b-91c0f2a7d640");
        var labels = release.Labels();
        labels[label] = value;
        // The archive is the release's, so the check of the archive passes; the image Docker then reports from the load,
        // which happens before the install claims anything, says otherwise, and the load is undone.
        var runner = new InstallerProcessRunner(imageId, root, instanceId) { Labels = labels, Held = [] };
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                ReleaseRequest(root, bundle, instanceId, release.Folder), runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(
                $"The CameraAgent image Docker resolved is not labelled as CameraAgent {release.Version} from commit " +
                $"{ReleaseFixture.Commit}, created {ReleaseFixture.Created}, as release v{release.Version} requires.",
                exception.Message);
            Assert.AreEqual(0, runner.ComposeUpCount);
            Assert.AreEqual(1, runner.LoadCount);
            Assert.IsTrue(exception.IsRefusal, "the load is undone before anything is claimed, so it is a refusal (exit 3)");
            CollectionAssert.Contains(runner.Removed, imageId);
            Assert.IsEmpty(runner.Held!, "the image the refused install loaded is still in Docker");
            // A refused release leaves no state, and so no evidence claiming the instance runs it.
            CollectionAssert.AreEqual(before, Tree(root), "a refused load changed the file system");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_ImageThatIsNotTheReleaseImage_FailsBeforeComposeStarts(bool registry)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-identity-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        var instanceId = Guid.Parse("8e2f4c6a-0b1d-4e3f-9a5c-7d6e8f0a1b2c");
        // Docker resolves an image none of the release's identities name: one the archive loaded, or one pulled under
        // the release's digest reference.
        var runner = new InstallerProcessRunner($"sha256:{new string('d', 64)}", root, instanceId)
        {
            Labels = release.Labels(),
            RepoDigests = [release.DigestReference],
            Held = [],
            // The image the load reports answers to the configuration the archive names, so Docker resolves it there.
            LoadedNames = [release.ConfigDigest(platform)],
            Registry = new FakeRegistry(release.RegistryContent)
        };
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with
        {
            ImageSource = registry ? ReleaseImageSource.Registry : ReleaseImageSource.Archive
        };
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(
                $"The CameraAgent image Docker resolved is not the {platform} image release v{release.Version} names.",
                exception.Message);
            Assert.IsTrue(exception.IsRefusal, "the import is undone before anything is claimed, so it is a refusal (exit 3)");
            Assert.AreEqual(0, runner.ComposeUpCount);
            Assert.IsEmpty(runner.Held!, "the image the refused install imported is still in Docker");
            // A loaded or pulled image is checked before the install claims anything.
            CollectionAssert.AreEqual(before, Tree(root), "a refused import changed the file system");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_TamperedOfflineArchive_IsRefusedBeforeDockerOrTheProductRootIsTouched()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-tampered-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        await File.AppendAllTextAsync(release.ArchivePath(platform), "!");
        var instanceId = Guid.Parse("9f3a5d7b-1c2e-4f40-8b6d-0e7f9a1b2c3d");
        var runner = new InstallerProcessRunner($"sha256:{new string('b', 64)}", root, instanceId);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                ReleaseRequest(root, bundle, instanceId, release.Folder), runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            StringAssert.Contains(exception.Message, "it is not the file the release published", StringComparison.Ordinal);
            Assert.AreEqual(0, runner.Invocations, "a tampered release must fail before Docker is contacted");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "cameraagents")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReleaseJsonNamingAPropertyTwice_IsRefusedBeforeDockerOrTheFileSystem()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-duplicate-{Guid.NewGuid():N}");
        using var release = ReleaseFixture.Create(root);
        // The copy a last-wins reader would take is the real one; the first names another image.
        release.RewriteText(json => json.Replace(
            "\"digest\": \"", $"\"digest\": \"sha256:{new string('e', 64)}\", \"digest\": \"", StringComparison.Ordinal));
        var instanceId = Guid.Parse("0a4b6c8d-2e3f-4a5b-9c7d-1e2f3a4b5c6d");
        var runner = new InstallerProcessRunner(release.ConfigDigest($"linux/{ReleaseAcquirer.HostImageArchitecture()}"), root, instanceId)
        {
            Held = []
        };
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                ReleaseRequest(root, bundle, instanceId, release.Folder), runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(
                "The installer will not use release.json: an object in it names the same property twice.", exception.Message);
            Assert.AreEqual(0, runner.Invocations, "a release.json naming a property twice must be refused before Docker is contacted");
            CollectionAssert.AreEqual(before, Tree(root));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// An offline archive that is the release's file but does not hold the release's image, holds it incompletely (a
    /// layer missing, damaged or of the wrong size, which Docker would reject only while loading), or holds it in a way a
    /// loader could read differently (an entry name a loader cleans to another entry's path, a duplicate, a link, or an
    /// entry no OCI layout has), is refused from the archive itself: before Docker is contacted, before the product root,
    /// state, catalog or staged archive exist, and without loading anything.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("version-label", "holds an image that is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires.")]
    [DataRow("revision-label", "holds an image that is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires.")]
    [DataRow("component-label", "holds an image that is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires.")]
    [DataRow("created-label", "holds an image that is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires.")]
    [DataRow("created-other", "holds an image that is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires.")]
    [DataRow("contract-label", "!The CameraAgent image does not declare the required current configuration and catalog contracts.")]
    [DataRow("catalog-label", "!The CameraAgent image does not declare the required current configuration and catalog contracts.")]
    [DataRow("other-image", "is not the image release v{version} names: its OCI layout names an image other than {digest}.")]
    [DataRow("containerd-name", "is not the image release v{version} names: its OCI layout would name the loaded image something other than {reference}.")]
    [DataRow("classic-tag", "is not the image release v{version} names: its manifest.json does not describe that image under {reference} alone.")]
    [DataRow("classic-config", "is not the image release v{version} names: its manifest.json does not describe that image under {reference} alone.")]
    [DataRow("manifest-of-another-platform", "is not the image release v{version} names: its image index does not list {platform} once, as {manifest}.")]
    [DataRow("tampered-config", "is not the image release v{version} names: its {platform} configuration is not the blob {config} names.")]
    [DataRow("missing-config", "is not the image release v{version} names: it holds no {platform} configuration.")]
    [DataRow("layout-twice", "holds index.json twice.")]
    [DataRow("layout-link", "holds index.json as something other than a file.")]
    [DataRow("config-property-twice", "!The installer will not use the offline archive {name}'s {platform} configuration: an object in it names the same property twice.")]
    [DataRow("missing-layer", "is not the image release v{version} names: it holds no {platform} layer {layer}.")]
    [DataRow("corrupt-layer", "is not the image release v{version} names: its {platform} layer {layer} is not the blob that digest names.")]
    [DataRow("short-layer", "is not the image release v{version} names: its {platform} layer {layer} is not the size its manifest gives.")]
    [DataRow("layer-link", "holds {layer-name} as something other than a file.")]
    [DataRow("extra-blob", "is not the image release v{version} names: it holds {extra}, which its image does not use.")]
    [DataRow("no-classic-manifest", "is not the image release v{version} names: it holds no manifest.json.")]
    [DataRow("no-layout-version", "is not the image release v{version} names: it holds no OCI layout version.")]
    [DataRow("layout-version", "is not the image release v{version} names: its OCI layout is not version 1.0.0.")]
    [DataRow("unknown-entry", "holds an entry named 'repositories', which is not part of an OCI image layout.")]
    [DataRow("index-alias", "holds an entry named 'metadata/../index.json', which is not the canonical spelling of a path.")]
    [DataRow("dot-alias", "holds an entry named './index.json', which is not the canonical spelling of a path.")]
    [DataRow("absolute-alias", "holds an entry named '/index.json', which is not the canonical spelling of a path.")]
    [DataRow("classic-manifest-alias", "holds an entry named 'blobs/../manifest.json', which is not the canonical spelling of a path.")]
    [DataRow("config-alias", "holds an entry named '{config-alias}', which is not the canonical spelling of a path.")]
    [DataRow("layer-alias", "holds an entry named '{layer-alias}', which is not the canonical spelling of a path.")]
    [DataRow("backslash-alias", "holds an entry named '{backslash-alias}', which is not the canonical spelling of a path.")]
    [DataRow("trailing-slash-file", "holds an entry named 'index.json/', which is not the canonical spelling of a path.")]
    [DataRow("repeated-slash-directory", "holds an entry named 'blobs//', which is not the canonical spelling of a path.")]
    [DataRow("classic-repotags-lowercase", "!The installer will not use the offline archive {name}'s manifest.json: an object in it names one property in two spellings, which Docker's JSON decoder reads as the same name.")]
    [DataRow("classic-repotags-long-s", "!The installer will not use the offline archive {name}'s manifest.json: an object in it names a property with a character outside ASCII, which Docker's JSON decoder can read as another property's name.")]
    [DataRow("config-kelvin-alias", "!The installer will not use the offline archive {name}'s {platform} configuration: an object in it names a property with a character outside ASCII, which Docker's JSON decoder can read as another property's name.")]
    [DataRow("classic-unknown-field", "is not the image release v{version} names: its manifest.json has a field 'Parent', which the installer does not accept.")]
    [DataRow("layout-unknown-field", "is not the image release v{version} names: its OCI layout index's image has a field 'urls', which the installer does not accept.")]
    [DataRow("oci-layout-unknown-field", "is not the image release v{version} names: its oci-layout has a field 'extensions', which the installer does not accept.")]
    [DataRow("manifest-unknown-field", "is not the image release v{version} names: its {platform} manifest has a field 'subject', which the installer does not accept.")]
    public async Task InstallAsync_ReleaseArchiveNotHoldingTheReleasesImage_IsRefusedBeforeAnythingIsClaimedOrLoaded(string fault, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-archive-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        var version = "1.2.0-dev.3";
        var labels = ReleaseFixture.ReleaseLabels(version);
        switch (fault)
        {
            case "version-label": labels["org.opencontainers.image.version"] = "1.2.0-dev.4"; break;
            case "revision-label": labels["org.opencontainers.image.revision"] = new string('f', 40); break;
            case "component-label": labels["io.hvo.skymonitor.component"] = "LogicHost"; break;
            case "created-label": labels.Remove("org.opencontainers.image.created"); break;
            case "created-other": labels["org.opencontainers.image.created"] = "2026-10-17T18:00:00+00:00"; break;
            case "contract-label": labels.Remove("io.hvo.skymonitor.configuration-contract"); break;
            case "catalog-label": labels["io.hvo.skymonitor.catalog-contract"] = "no-such-catalog-contract"; break;
        }
        // The layer of the host platform's archive, and the entries a fault adds, which the expected message names.
        string? layerDigest = null;
        string? layerName = null;
        string? extraName = null;
        string? configAlias = null;
        string? layerAlias = null;
        string? backslashAlias = null;
        void Edit(string archivePlatform, List<ArchiveEntry> entries)
        {
            ArchiveEntry Named(string name) => entries.Single(entry => entry.Name == name);
            void Replace(string name, ArchiveEntry replacement) => entries[entries.IndexOf(Named(name))] = replacement;
            string Text(string name) => Encoding.UTF8.GetString(Named(name).Bytes);
            var configName = entries.Single(entry => entry.Name.StartsWith("blobs/", StringComparison.Ordinal) &&
                                                     Encoding.UTF8.GetString(entry.Bytes).Contains("\"Labels\"", StringComparison.Ordinal)).Name;
            var layer = entries.Single(entry => Encoding.UTF8.GetString(entry.Bytes).StartsWith("layer ", StringComparison.Ordinal));
            if (archivePlatform == platform)
            {
                layerDigest = ReleaseFixture.DigestOf(layer.Bytes);
                layerName = layer.Name;
                extraName = ReleaseFixture.BlobName("extra"u8.ToArray());
                configAlias = configName.Replace("blobs/sha256/", "blobs/sha256//", StringComparison.Ordinal);
                layerAlias = layer.Name.Replace("blobs/sha256/", "blobs/sha256/./", StringComparison.Ordinal);
                backslashAlias = layer.Name.Replace('/', '\\');
            }
            switch (fault)
            {
                case "other-image":
                    Replace("index.json", new("index.json", Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Replace(
                        Text("index.json"), "sha256:[0-9a-f]{64}", $"sha256:{new string('c', 64)}"))));
                    break;
                case "containerd-name":
                    Replace("index.json", new("index.json", Encoding.UTF8.GetBytes(Text("index.json").Replace(
                        $"ghcr.io/hualapaivalley/skymonitor-cameraagent:{version}", "docker.io/library/nginx:latest", StringComparison.Ordinal))));
                    break;
                case "classic-tag":
                    Replace("manifest.json", new("manifest.json", Encoding.UTF8.GetBytes(Text("manifest.json").Replace(
                        $"ghcr.io/hualapaivalley/skymonitor-cameraagent:{version}", "docker.io/library/nginx:latest", StringComparison.Ordinal))));
                    break;
                case "classic-config":
                    Replace("manifest.json", new("manifest.json", Encoding.UTF8.GetBytes(Text("manifest.json").Replace(
                        configName, $"blobs/sha256/{new string('c', 64)}", StringComparison.Ordinal))));
                    break;
                case "tampered-config":
                    Replace(configName, new(configName, Encoding.UTF8.GetBytes(Text(configName).Replace("linux", "LINUX", StringComparison.Ordinal))));
                    break;
                case "missing-config":
                    entries.Remove(Named(configName));
                    break;
                case "layout-twice":
                    entries.Add(new("index.json", Named("index.json").Bytes));
                    break;
                case "layout-link":
                    Replace("index.json", new("index.json", Encoding.UTF8.GetBytes("manifest.json"), System.Formats.Tar.TarEntryType.SymbolicLink));
                    break;
                case "missing-layer":
                    entries.Remove(layer);
                    break;
                case "corrupt-layer":
                    // The same size, so only its content tells it from the layer the manifest names.
                    Replace(layer.Name, new(layer.Name, [.. layer.Bytes.Select(static value => (byte)(value ^ 0x20))]));
                    break;
                case "short-layer":
                    Replace(layer.Name, new(layer.Name, layer.Bytes[..^1]));
                    break;
                case "layer-link":
                    Replace(layer.Name, new(layer.Name, Encoding.UTF8.GetBytes(configName), System.Formats.Tar.TarEntryType.SymbolicLink));
                    break;
                case "extra-blob":
                    entries.Add(new(ReleaseFixture.BlobName("extra"u8.ToArray()), "extra"u8.ToArray()));
                    break;
                case "no-classic-manifest":
                    entries.Remove(Named("manifest.json"));
                    break;
                case "no-layout-version":
                    entries.Remove(Named("oci-layout"));
                    break;
                case "layout-version":
                    Replace("oci-layout", new("oci-layout", """{"imageLayoutVersion":"2.0.0"}"""u8.ToArray()));
                    break;
                case "unknown-entry":
                    entries.Add(new("repositories", "{}"u8.ToArray()));
                    break;
                case "index-alias":
                    // containerd cleans this to index.json and lets it replace the one this check read: here it lists a
                    // second image.
                    entries.Add(new("metadata/../index.json", Encoding.UTF8.GetBytes(Text("index.json").Replace(
                        "\"manifests\":[", $"\"manifests\":[{{\"mediaType\":\"{ReleaseFixture.ManifestMediaType}\",\"digest\":\"sha256:{new string('c', 64)}\",\"size\":2}},", StringComparison.Ordinal))));
                    break;
                case "dot-alias":
                    entries.Add(new("./index.json", Named("index.json").Bytes));
                    break;
                case "absolute-alias":
                    entries.Add(new("/index.json", Named("index.json").Bytes));
                    break;
                case "classic-manifest-alias":
                    entries.Add(new("blobs/../manifest.json", Encoding.UTF8.GetBytes(Text("manifest.json").Replace(
                        $"ghcr.io/hualapaivalley/skymonitor-cameraagent:{version}", "docker.io/library/nginx:latest", StringComparison.Ordinal))));
                    break;
                case "config-alias":
                    entries.Add(new(configName.Replace("blobs/sha256/", "blobs/sha256//", StringComparison.Ordinal),
                        Encoding.UTF8.GetBytes(Text(configName).Replace("CameraAgent", "LogicHost", StringComparison.Ordinal))));
                    break;
                case "layer-alias":
                    entries.Add(new(layer.Name.Replace("blobs/sha256/", "blobs/sha256/./", StringComparison.Ordinal), "other layer"u8.ToArray()));
                    break;
                case "backslash-alias":
                    entries.Add(new(layer.Name.Replace('/', '\\'), layer.Bytes));
                    break;
                case "trailing-slash-file":
                    entries.Add(new("index.json/", Named("index.json").Bytes));
                    break;
                case "repeated-slash-directory":
                    entries.Insert(0, new("blobs//", [], System.Formats.Tar.TarEntryType.Directory));
                    break;
                case "classic-repotags-lowercase":
                    // Go's decoder folds repotags into RepoTags, and the later spelling wins: it would tag nginx.
                    Replace("manifest.json", new("manifest.json", Encoding.UTF8.GetBytes(Text("manifest.json").Replace(
                        "\"Layers\":", "\"repotags\":[\"docker.io/library/nginx:latest\"],\"Layers\":", StringComparison.Ordinal))));
                    break;
                case "classic-repotags-long-s":
                    // U+017F folds to s, so Go reads RepoTag\u017f as RepoTags.
                    Replace("manifest.json", new("manifest.json", Encoding.UTF8.GetBytes(Text("manifest.json").Replace(
                        "\"Layers\":", "\"RepoTag\\u017f\":[\"docker.io/library/nginx:latest\"],\"Layers\":", StringComparison.Ordinal))));
                    break;
                case "classic-unknown-field":
                    Replace("manifest.json", new("manifest.json", Encoding.UTF8.GetBytes(Text("manifest.json").Replace(
                        "\"Layers\":", $"\"Parent\":\"sha256:{new string('a', 64)}\",\"Layers\":", StringComparison.Ordinal))));
                    break;
                case "layout-unknown-field":
                    Replace("index.json", new("index.json", Encoding.UTF8.GetBytes(Text("index.json").Replace(
                        "\"annotations\":", "\"urls\":[\"https://example.com/image\"],\"annotations\":", StringComparison.Ordinal))));
                    break;
                case "oci-layout-unknown-field":
                    Replace("oci-layout", new("oci-layout", """{"imageLayoutVersion":"1.0.0","extensions":{}}"""u8.ToArray()));
                    break;
            }
        }
        using var release = ReleaseFixture.Create(
            root,
            version: version,
            labels: labels,
            editArchive: Edit,
            config: fault switch
            {
                "config-property-twice" => static (_, json) => json.Replace("\"os\":\"linux\"", "\"os\":\"windows\",\"os\":\"linux\"", StringComparison.Ordinal),
                // The Kelvin sign folds to k, so Go reads Healthchec\u212a as Healthcheck, which this check never reads.
                "config-kelvin-alias" => static (_, json) => json.Replace(
                    "\"config\":{", "\"config\":{\"Healthchec\\u212a\":{\"Test\":[\"CMD\",\"true\"]},", StringComparison.Ordinal),
                _ => null
            },
            platformManifest: fault == "manifest-unknown-field"
                ? static (_, json) => json.Replace("\"config\":", $"\"subject\":{{\"mediaType\":\"{ReleaseFixture.ManifestMediaType}\",\"digest\":\"sha256:{new string('b', 64)}\",\"size\":2}},\"config\":", StringComparison.Ordinal)
                : null);
        if (fault == "manifest-of-another-platform")
        {
            // release.json names the other platform's manifest, which the index lists, for this platform's archive.
            var other = platform == "linux/amd64" ? "linux/arm64" : "linux/amd64";
            var otherManifest = release.ManifestDigest(other);
            release.Rewrite(manifest => manifest["images"]![ReleaseFixture.ImageKey]!["offlineArchives"]!.AsArray()
                .Single(archive => (string)archive!["platform"]! == platform)!["manifestDigest"] = otherManifest);
        }
        var instanceId = Guid.Parse("1b5c7d9e-3f4a-4b6c-8d0e-2f3a4b5c6d7e");
        var runner = new InstallerProcessRunner(release.ConfigDigest(platform), root, instanceId) { Labels = labels, Held = [] };
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                ReleaseRequest(root, bundle, instanceId, release.Folder), runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            var name = (string)release.Archive(platform)["name"]!;
            var message = expected
                .Replace("{version}", version, StringComparison.Ordinal)
                .Replace("{commit}", ReleaseFixture.Commit, StringComparison.Ordinal)
                .Replace("{created}", ReleaseFixture.Created, StringComparison.Ordinal)
                .Replace("{digest}", release.Digest, StringComparison.Ordinal)
                .Replace("{reference}", $"{release.Repository}:{version}", StringComparison.Ordinal)
                .Replace("{platform}", platform, StringComparison.Ordinal)
                .Replace("{manifest}", release.ManifestDigest(platform), StringComparison.Ordinal)
                .Replace("{config}", release.ConfigDigest(platform), StringComparison.Ordinal)
                .Replace("{layer}", layerDigest, StringComparison.Ordinal)
                .Replace("{layer-name}", layerName, StringComparison.Ordinal)
                .Replace("{extra}", extraName, StringComparison.Ordinal)
                .Replace("{config-alias}", configAlias, StringComparison.Ordinal)
                .Replace("{layer-alias}", layerAlias, StringComparison.Ordinal)
                .Replace("{backslash-alias}", backslashAlias, StringComparison.Ordinal)
                .Replace("{name}", name, StringComparison.Ordinal);
            Assert.AreEqual(message.StartsWith('!') ? message[1..] : $"The offline archive {name} {message}", exception.Message);
            Assert.IsTrue(exception.IsRefusal, "the archive is refused before anything is loaded or claimed, so it is a refusal (exit 3)");
            Assert.AreEqual(0, runner.LoadCount, "a refused archive must never be loaded");
            Assert.AreEqual(0, runner.PullCount);
            Assert.IsEmpty(runner.Held!);
            Assert.IsEmpty(runner.Removed, "nothing was imported, so nothing is removed");
            CollectionAssert.AreEqual(before, Tree(root), "a refused archive changed the file system");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// A registry image is read from its registry by the digest release.json names, and refused there, before anything
    /// is pulled or claimed: Docker and the file system end as they began, with no pull.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("version-label", "The CameraAgent image {reference} in its registry is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires. Nothing was pulled.")]
    [DataRow("created-label", "The CameraAgent image {reference} in its registry is not labelled as CameraAgent {version} from commit {commit}, created {created}, as release v{version} requires. Nothing was pulled.")]
    [DataRow("contract-label", "The CameraAgent image does not declare the required current configuration and catalog contracts.")]
    [DataRow("catalog-label", "The CameraAgent image does not declare the required current configuration and catalog contracts.")]
    [DataRow("other-platform", "The CameraAgent image {reference} in its registry is not the {platform} image release v{version} names: its configuration is not for {platform}. Nothing was pulled.")]
    [DataRow("other-content", "The CameraAgent image {reference} in its registry is not the {platform} image release v{version} names: its image is not the content {digest} names. Nothing was pulled.")]
    [DataRow("other-manifest", "The CameraAgent image {reference} in its registry is not the {platform} image release v{version} names: its {platform} manifest is not the one release.json names. Nothing was pulled.")]
    [DataRow("other-config", "The CameraAgent image {reference} in its registry is not the {platform} image release v{version} names: its {platform} configuration is not the one release.json names. Nothing was pulled.")]
    [DataRow("buildx-too-old", "The installer cannot read {reference} from its registry before pulling it, so it pulls nothing (unknown flag: --format). Install from the release's offline archive, the default image source (leave out --image-source registry), or let this user read the registry with 'docker buildx imagetools inspect' (buildx 0.8 or later).")]
    [DataRow("unavailable", "The installer cannot read {reference} from its registry before pulling it, so it pulls nothing (docker: 'buildx' is not a docker command.). Install from the release's offline archive, the default image source (leave out --image-source registry), or let this user read the registry with 'docker buildx imagetools inspect' (buildx 0.8 or later).")]
    public async Task InstallAsync_RegistryReleaseImageThatIsRefused_IsRefusedBeforeAnythingIsPulledOrClaimed(string fault, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-pull-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        var version = "1.2.0-dev.3";
        var labels = ReleaseFixture.ReleaseLabels(version);
        switch (fault)
        {
            case "version-label": labels["org.opencontainers.image.version"] = "1.2.0-dev.4"; break;
            case "created-label": labels.Remove("org.opencontainers.image.created"); break;
            case "contract-label": labels.Remove("io.hvo.skymonitor.configuration-contract"); break;
            case "catalog-label": labels["io.hvo.skymonitor.catalog-contract"] = "no-such-catalog-contract"; break;
        }
        var architecture = platform["linux/".Length..];
        using var release = ReleaseFixture.Create(
            root,
            version: version,
            labels: labels,
            config: fault == "other-platform"
                ? (configured, json) => configured == platform
                    ? json.Replace($"\"architecture\":\"{architecture}\"", "\"architecture\":\"s390x\"", StringComparison.Ordinal)
                    : json
                : null);
        var other = platform == "linux/amd64" ? "linux/arm64" : "linux/amd64";
        switch (fault)
        {
            case "other-content":
                release.RegistryContent[release.Digest] = "{}"u8.ToArray();
                break;
            case "other-manifest":
                // release.json names the other platform's manifest, which the index lists, for this platform.
                var otherManifest = release.ManifestDigest(other);
                release.Rewrite(manifest => ArchiveOf(manifest, platform)["manifestDigest"] = otherManifest);
                break;
            case "other-config":
                var otherConfig = release.ConfigDigest(other);
                release.Rewrite(manifest => ArchiveOf(manifest, platform)["configDigest"] = otherConfig);
                break;
        }
        var instanceId = Guid.Parse("2c6d8e0f-4a5b-4c7d-9e1f-3a4b5c6d7e8f");
        var runner = new InstallerProcessRunner(release.ConfigDigest(platform), root, instanceId)
        {
            Labels = labels,
            RepoDigests = [release.DigestReference],
            Held = [],
            Registry = new FakeRegistry(release.RegistryContent)
            {
                Unavailable = fault == "unavailable" ? "docker: 'buildx' is not a docker command." : null,
                WithoutFormat = fault == "buildx-too-old"
            }
        };
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with { ImageSource = ReleaseImageSource.Registry };
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(
                expected
                    .Replace("{reference}", release.DigestReference, StringComparison.Ordinal)
                    .Replace("{digest}", release.Digest, StringComparison.Ordinal)
                    .Replace("{platform}", platform, StringComparison.Ordinal)
                    .Replace("{version}", version, StringComparison.Ordinal)
                    .Replace("{commit}", ReleaseFixture.Commit, StringComparison.Ordinal)
                    .Replace("{created}", ReleaseFixture.Created, StringComparison.Ordinal),
                exception.Message);
            Assert.IsGreaterThan(0, runner.Registry!.Reads);
            Assert.IsTrue(exception.IsRefusal, "nothing was pulled or claimed, so it is a refusal (exit 3)");
            Assert.AreEqual(0, runner.PullCount, "a refused registry image must never be pulled");
            Assert.AreEqual(0, runner.LoadCount);
            Assert.IsEmpty(runner.Held!);
            Assert.IsEmpty(runner.Removed, "nothing was pulled, so nothing is removed");
            Assert.AreEqual(0, runner.ComposeUpCount);
            CollectionAssert.AreEqual(before, Tree(root), "a refused registry image changed the file system");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// The checks on the pulled image stay as the second line: a registry whose metadata is the release's, but whose
    /// pulled image Docker then reports otherwise, has that image removed before anything is claimed.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_PulledImageDockerReportsAsAnotherRelease_IsRemovedBeforeAnythingIsClaimed()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-pulled-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        var labels = release.Labels();
        labels["org.opencontainers.image.version"] = "1.2.0-dev.4";
        var instanceId = Guid.Parse("4e8f0a2b-6c7d-4e9f-8a1b-5c6d7e8f9a0b");
        var runner = new InstallerProcessRunner(release.ConfigDigest(platform), root, instanceId)
        {
            Labels = labels,
            RepoDigests = [release.DigestReference],
            Held = [],
            Registry = new FakeRegistry(release.RegistryContent)
        };
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with { ImageSource = ReleaseImageSource.Registry };
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(
                $"The CameraAgent image Docker resolved is not labelled as CameraAgent {release.Version} from commit " +
                $"{ReleaseFixture.Commit}, created {ReleaseFixture.Created}, as release v{release.Version} requires.",
                exception.Message);
            Assert.AreEqual(1, runner.PullCount);
            CollectionAssert.Contains(runner.Removed, release.DigestReference);
            Assert.IsTrue(exception.IsRefusal, "the pull is removed before anything is claimed, so it is a refusal (exit 3)");
            Assert.IsEmpty(runner.Held!, "the refused pull is still in Docker");
            Assert.AreEqual(0, runner.ComposeUpCount);
            CollectionAssert.AreEqual(before, Tree(root), "a refused pull changed the file system");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// A new installation loads its release's archive before it claims anything, so a load Docker rejects leaves no state
    /// that resume or purge would have to recover: the file system and Docker are unchanged, and running the same install
    /// again, once Docker can load, completes it.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ArchiveLoadDockerRejects_ClaimsNothingAndTheSameInstallThenCompletes()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-load-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        var instanceId = Guid.Parse("5f9a1b3c-7d8e-4f0a-9b2c-6d7e8f9a0b1c");
        var runner = new InstallerProcessRunner(release.ConfigDigest(platform), root, instanceId)
        {
            Labels = release.Labels(),
            Held = [],
            RejectNextLoad = true
        };
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder);
        var before = Tree(root);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            StringAssert.StartsWith(exception.Message, "Docker command failed (image load)", StringComparison.Ordinal);
            CollectionAssert.AreEqual(before, Tree(root), "a rejected load left state behind");
            Assert.IsEmpty(runner.Held!);

            var result = await CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            Assert.AreEqual(InstallationOutcome.Installed, result.Outcome);
            Assert.AreEqual(2, runner.LoadCount, "the completed install loads once, before it claims anything");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// An install cancelled while Docker imports the release's image, after the image arrived, removes it again, and has
    /// claimed nothing: Docker and the file system end as they began.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_CancelledWhileTheReleaseImageIsImported_RemovesItAndClaimsNothing(bool registry)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-cancel-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        var imageId = release.ConfigDigest(platform);
        var instanceId = Guid.Parse("6a0b2c4d-8e9f-4a1b-8c3d-7e8f9a0b1c2d");
        using var cancellation = new CancellationTokenSource();
        var runner = new InstallerProcessRunner(imageId, root, instanceId)
        {
            Labels = release.Labels(),
            RepoDigests = [release.DigestReference],
            Held = [],
            Registry = new FakeRegistry(release.RegistryContent),
            CancelDuringImport = cancellation
        };
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with
        {
            ImageSource = registry ? ReleaseImageSource.Registry : ReleaseImageSource.Archive
        };
        var before = Tree(root);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(),
                RuntimeUid, RuntimeGid, cancellation.Token, portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(1, registry ? runner.PullCount : runner.LoadCount);
            CollectionAssert.Contains(runner.Removed, registry ? release.DigestReference : imageId);
            Assert.IsEmpty(runner.Held!, "the cancelled import is still in Docker");
            Assert.AreEqual(0, runner.ComposeUpCount);
            CollectionAssert.AreEqual(before, Tree(root), "a cancelled import changed the file system");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>The offline archive release.json lists for <paramref name="platform"/>.</summary>
    private static JsonNode ArchiveOf(JsonObject manifest, string platform)
        => manifest["images"]![ReleaseFixture.ImageKey]!["offlineArchives"]!.AsArray()
            .Single(archive => (string)archive!["platform"]! == platform)!;

    /// <summary>Every path under <paramref name="root"/>, relative and sorted, for before-and-after comparison.</summary>
    private static string[] Tree(string root)
        => Directory.Exists(root)
            ? [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal)]
            : [];

    private static string RequireCatalogBundle()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the release install contract.");
        }
        return bundle;
    }

    private static InstallRequest ReleaseRequest(string root, string bundle, Guid instanceId, string release) => new()
    {
        AllowTestProductRoot = true,
        InstanceId = instanceId,
        FriendlyName = "Release Camera",
        OwnerEmail = "owner@example.test",
        ProductRoot = root,
        CatalogBundle = bundle,
        Release = release,
        Port = SelectFreePort()
    };

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            SafeFileSystem.MakeTreeOwnerWritable(root);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The post-start health wait and owner bootstrap reach the instance at the address it was bound to, the same URL
    /// the result reports. A LAN-bound instance is not listening on loopback, so probing 127.0.0.1 always failed.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("192.0.2.10", "192.0.2.10", DisplayName = "lan-bind")]
    [DataRow("0.0.0.0", "localhost", DisplayName = "wildcard-bind")]
    public async Task InstallAsync_ProbesHealthAndOwnerBootstrapAtTheBoundAddress(string bindAddress, string expectedHost)
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-bind-{Guid.NewGuid():N}");
        var imageId = $"sha256:{new string('b', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("5d3f0c8e-6a1b-4f27-9e44-2c7d8b9a1e60"),
            FriendlyName = "LAN Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            BindAddress = bindAddress,
            AcknowledgePlaintextHttp = true,
            Port = SelectFreePort()
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        var probe = new RecordingPortProbe();
        var owner = new InstallerOwnerClient();
        var probed = new List<Uri>();
        try
        {
            var result = await CameraAgentInstaller.InstallAsync(
                request,
                runner,
                uri =>
                {
                    probed.Add(uri);
                    return owner;
                },
                RuntimeUid,
                RuntimeGid,
                CancellationToken.None,
                portAvailabilityProbe: probe.Probe);

            var expected = new Uri($"http://{expectedHost}:{request.Port}/");
            Assert.AreEqual(InstallationOutcome.Installed, result.Outcome);
            Assert.AreEqual(expected, result.Url);
            Assert.IsNotEmpty(probed);
            CollectionAssert.AreEqual(Enumerable.Repeat(expected, probed.Count).ToArray(), probed.ToArray());
            Assert.AreEqual(bindAddress, probe.LastBindAddress);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(root);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// A host whose Docker cannot enforce the Compose limits, lacks tar, and is short of space on both filesystems is
    /// refused by both install and install --dry-run with every finding in one report, before anything is created.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_UnmetHostPrerequisites_RefuseInstallAndDryRunBeforeAnythingIsCreated()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-prerequisites-{Guid.NewGuid():N}", "product");
        var imageId = $"sha256:{new string('b', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("9e1c4a7b-2f35-4d68-8b0a-6c3e5f7d9a12"),
            FriendlyName = "Pi Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            Port = SelectFreePort()
        };
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value)
        {
            DockerInfo = "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"pi\",\"ServerVersion\":\"29.0\"," +
                         "\"MemoryLimit\":false,\"CpuCfsQuota\":false,\"CgroupVersion\":\"2\",\"DockerRootDir\":\"/srv/docker\"," +
                         "\"NCPU\":1,\"MemTotal\":2147483648," +
                         "\"Warnings\":[\"WARNING: No memory limit support\",\"WARNING: No cpu cfs quota support\"]}"
        };
        var host = new FakeHostEnvironment
        {
            Tar = false,
            Controllers = "cpuset cpu io pids",
            FileSystems =
            {
                [root] = new HostFileSystemMeasurement("/srv/data", "259:2", 4096),
                ["/srv/docker"] = new HostFileSystemMeasurement("/srv/docker", "259:3", 4096)
            }
        };
        string[] expectedCodes =
        [
            "docker-memory-limit-unsupported", "docker-cpu-quota-unsupported", "docker-pids-limit-unsupported",
            "docker-host-cpus-insufficient", "docker-host-memory-below-limit",
            "tar-missing", "product-root-storage-insufficient", "docker-root-storage-insufficient"
        ];
        try
        {
            foreach (var dryRun in new[] { true, false })
            {
                host.Measured.Clear();
                var refused = await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(() => CameraAgentInstaller.InstallAsync(
                    request with { DryRun = dryRun },
                    runner,
                    _ => throw new AssertFailedException("A refused host must never be probed."),
                    RuntimeUid,
                    RuntimeGid,
                    CancellationToken.None,
                    portAvailabilityProbe: (_, _) => throw new AssertFailedException("A refused host must never admit a port."),
                    hostEnvironment: host));

                var report = refused.Report ?? throw new AssertFailedException("The refusal carried no report.");
                Assert.IsFalse(report.Satisfied, $"dry-run={dryRun}");
                CollectionAssert.AreEqual(expectedCodes, report.Findings.Select(static finding => finding.Code).ToArray(), $"dry-run={dryRun}");
                CollectionAssert.AreEqual(new[] { root, "/srv/docker" }, host.Measured.ToArray(), $"dry-run={dryRun}");
                // A memory limit above the host's memory is advisory, so it is reported with the rest but does not name the
                // refusal; every blocking finding does.
                Assert.IsFalse(report.Findings.Single(static finding => finding.Code == "docker-host-memory-below-limit").Blocking);
                foreach (var code in expectedCodes.Where(static code => code != "docker-host-memory-below-limit"))
                {
                    StringAssert.Contains(refused.Message, code, StringComparison.Ordinal);
                }
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(root)), $"dry-run={dryRun} created the product root.");
                Assert.AreEqual(0, runner.ComposeUpCount);
            }
        }
        finally
        {
            var parent = Path.GetDirectoryName(root)!;
            if (Directory.Exists(parent))
            {
                SafeFileSystem.MakeTreeOwnerWritable(parent);
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    /// <summary>
    /// The product-root threshold the installer computes is twice the catalog bundle and image archive plus 1 GiB:
    /// one byte short is refused naming exactly that requirement, and exactly that much is admitted.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ProductRootStorageThreshold_IsTwiceTheBundleAndArchivePlusHeadroom()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_PRODUCTION_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle) || !Directory.Exists(bundle))
        {
            Assert.Inconclusive("HVO_PRODUCTION_CATALOG_BUNDLE is required for the installer flow contract.");
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"hvo-installer-budget-{Guid.NewGuid():N}");
        var root = Path.Combine(scratch, "product");
        var archive = Path.Combine(scratch, "cameraagent.tar");
        Directory.CreateDirectory(scratch);
        await File.WriteAllBytesAsync(archive, new byte[4096]);
        File.SetUnixFileMode(archive, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var imageId = $"sha256:{new string('b', 64)}";
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            InstanceId = Guid.Parse("3c6f1d2e-8a47-4b59-9d10-7e2f4a6b8c31"),
            FriendlyName = "Budget Camera",
            OwnerEmail = "owner@example.test",
            ProductRoot = root,
            CatalogBundle = bundle,
            ImageReference = imageId,
            ImageArchive = archive,
            ImageArchiveSha256 = await SafeFileSystem.ComputeSha256Async(archive, CancellationToken.None),
            DryRun = true,
            Port = SelectFreePort()
        };
        var bundleBytes = Directory.EnumerateFiles(bundle).Sum(static path => new FileInfo(path).Length);
        var required = (2 * (bundleBytes + 4096)) + HostPrerequisites.StorageHeadroomBytes;
        var runner = new InstallerProcessRunner(imageId, root, request.InstanceId.Value);
        try
        {
            var shortHost = new FakeHostEnvironment { FileSystems = { [root] = new HostFileSystemMeasurement("/srv/data", "259:2", required - 1) } };
            var refused = await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: (_, _) => { }, hostEnvironment: shortHost));
            var finding = refused.Report!.Findings.Single();
            Assert.AreEqual("product-root-storage-insufficient", finding.Code);
            StringAssert.Contains(finding.Expected, $"at least {required} bytes", StringComparison.Ordinal);

            var exactHost = new FakeHostEnvironment { FileSystems = { [root] = new HostFileSystemMeasurement("/srv/data", "259:2", required) } };
            var planned = await CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: (_, _) => { }, hostEnvironment: exactHost);
            Assert.AreEqual(InstallationOutcome.Planned, planned.Outcome);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            SafeFileSystem.MakeTreeOwnerWritable(scratch);
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// Reserves an OS-selected loopback port and releases it, so no fixture depends on a fixed host port.
    /// </summary>
    private static int SelectFreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Records the port admission the installer requested without binding a host socket. These flows assert identity,
    /// resume, and signed-distribution behaviour, so shared-host socket state must not decide their outcome; every
    /// production install still admits its port through the real socket bind.
    /// </summary>
    private sealed class RecordingPortProbe
    {
        public int Invocations { get; private set; }

        public string? LastBindAddress { get; private set; }

        public int LastPort { get; private set; }

        public void Probe(string bindAddress, int port)
        {
            Invocations++;
            LastBindAddress = bindAddress;
            LastPort = port;
        }
    }

    private sealed class InstallerOwnerClient : IOwnerBootstrapClient
    {
        private int stateReadCount;
        public string CurrentInstallationState { get; set; } = "owner-password-change-required";
        public string ExpectedOwnerEmail { get; init; } = "owner@example.test";
        public List<string> Passwords { get; } = [];

        /// <summary>How many times the installation was verified.</summary>
        public int Verifications { get; private set; }

        public Task WaitForHealthAsync(
            ApprovedCatalogContract expectedCatalog, CancellationToken cancellationToken, TimeSpan? timeout = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The installer authenticates by the owner email, which every CameraAgent image resolves.
            Assert.AreEqual(ExpectedOwnerEmail, ownerEmail);
            Assert.IsGreaterThanOrEqualTo(8, password.Length);
            Passwords.Add(password);
            return Task.FromResult(stateReadCount++ == 0
                ? "owner-temporary-password"
                : "owner-password-change-required");
        }

        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult(CurrentInstallationState);

        public Task<string> VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Verifications++;
            Assert.AreEqual("hyg-v42-production", expectation.Catalog.CatalogId);
            Assert.AreEqual(64, expectation.ConfigurationSha256.Length);
            Assert.AreEqual(HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess, expectation.ReplayProfile);
            Assert.IsTrue(OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
                expectation.OwnerBootstrapState,
                CurrentInstallationState,
                expectation.AllowCompletedPasswordReplacement));
            return Task.FromResult(CurrentInstallationState);
        }
    }

    /// <summary>A daemon that enforces every Compose resource limit, as a supported installation host's does.</summary>
    private const string SupportedDockerInfo =
        "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"," +
        "\"MemoryLimit\":true,\"CpuCfsQuota\":true,\"PidsLimit\":true,\"CgroupVersion\":\"2\",\"DockerRootDir\":\"/var/lib/docker\"," +
        "\"NCPU\":4,\"MemTotal\":8442572800,\"Warnings\":null}";

    private sealed class InstallerProcessRunner(string imageId, string root, Guid instanceId) : IProcessRunner
    {
        public int ComposeUpCount { get; private set; }

        /// <summary>The Compose file the last <c>compose up</c> started the instance from: the limits it now runs with.</summary>
        public string? LastComposeUp { get; private set; }

        /// <summary>
        /// The number the failing <c>compose up</c> would have: that one fails as Docker fails one it cannot complete, and
        /// the container keeps what it ran before.
        /// </summary>
        public int FailComposeUpNumber { get; set; } = -1;

        public int Invocations { get; private set; }

        public int LoadCount { get; private set; }

        public int PullCount { get; private set; }

        public string DockerInfo { get; init; } = SupportedDockerInfo;

        /// <summary>
        /// When set, <c>compose config</c> renders the Compose file it was given, so the rendered model follows the
        /// limits the file carries, as Docker's does; otherwise it renders one fixed model.
        /// </summary>
        public bool RenderComposeFile { get; init; }

        /// <summary>The labels the image carries; by default the contract labels of an unreleased build.</summary>
        public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(ReleaseFixture.ContractLabels)
        {
            ["org.opencontainers.image.revision"] = new string('a', 40)
        };

        public IReadOnlyList<string> RepoDigests { get; init; } = [];

        /// <summary>
        /// The names Docker holds the image by, when a test follows what Docker holds: a load or pull adds the image, an
        /// inspect of a name it does not hold fails, and a removal takes the image away by any of its names.
        /// <see langword="null"/> keeps the image always present, as the other flows assume.
        /// </summary>
        public HashSet<string>? Held { get; init; }

        /// <summary>Every name an <c>image rm</c> was asked to remove, in order.</summary>
        public List<string> Removed { get; } = [];

        /// <summary>Further names a load makes the image reachable by, besides its ID.</summary>
        public IReadOnlyList<string> LoadedNames { get; init; } = [];

        /// <summary>The registry a release image is read from before it is pulled.</summary>
        public FakeRegistry? Registry { get; init; }

        /// <summary>Each registry read, load and pull, in order.</summary>
        public List<string> Imports { get; } = [];

        /// <summary>Makes the next load fail as Docker fails a load it cannot complete, importing nothing.</summary>
        public bool RejectNextLoad { get; set; }

        /// <summary>
        /// Cancels this source while a load or pull runs, after Docker has imported the image, as an operator's Ctrl+C
        /// arriving mid-import does.
        /// </summary>
        public CancellationTokenSource? CancelDuringImport { get; init; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations++;
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
            {
                return Success("unix:///var/run/docker.sock");
            }
            if (arguments.Count >= 2 && arguments[0] == "--host") arguments = arguments.Skip(2).ToArray();
            if (arguments.SequenceEqual(["info", "--format", "{{json .}}"], StringComparer.Ordinal))
            {
                return Success(DockerInfo);
            }
            if (arguments.SequenceEqual(["compose", "version", "--short"], StringComparer.Ordinal))
            {
                return Success("2.40.0");
            }
            if (FakeRegistry.IsRead(arguments))
            {
                Imports.Add("registry");
                return Task.FromResult(Registry?.Answer(arguments) ?? throw new AssertFailedException("The test gave no registry."));
            }
            if (arguments is ["image", "load", "--input", ..])
            {
                LoadCount++;
                Imports.Add("load");
                if (RejectNextLoad)
                {
                    RejectNextLoad = false;
                    return Task.FromResult(new ProcessResult(1, string.Empty, "Error processing tar file(exit status 1): unexpected EOF"));
                }
                Held?.UnionWith([imageId, .. LoadedNames]);
                CancelImport();
                return Success($"Loaded image ID: {imageId}\n");
            }
            if (arguments is ["image", "pull", var pulled])
            {
                PullCount++;
                Imports.Add("pull");
                Held?.UnionWith([imageId, pulled]);
                CancelImport();
                return Success(string.Empty);
            }
            if (arguments is ["image", "rm", var removed])
            {
                Removed.Add(removed);
                if (Held is null || !Held.Contains(removed))
                {
                    return Task.FromResult(new ProcessResult(1, string.Empty, $"Error: No such image: {removed}"));
                }
                // The fake holds one image: removing it by any name takes every name away.
                Held.Clear();
                return Success($"Deleted: {imageId}\n");
            }
            if (arguments.Count == 3 && arguments[0] == "image" && arguments[1] == "inspect")
            {
                if (Held is not null && !Held.Contains(arguments[2]))
                {
                    return Task.FromResult(new ProcessResult(1, "[]", $"Error: No such image: {arguments[2]}"));
                }
                return Success(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = imageId,
                        Architecture = "amd64",
                        Os = "linux",
                        RepoDigests,
                        Config = new
                        {
                            Labels
                        }
                    }
                }));
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
                            User = $"{RuntimeUid}:{RuntimeGid}",
                            Labels = new Dictionary<string, string>
                            {
                                ["com.docker.compose.project"] = arguments[2],
                                ["io.hvo.skymonitor.instance-id"] = id
                            }
                        },
                        HostConfig = Limits(),
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
                var file = arguments.ToList().IndexOf("--file");
                return RenderComposeFile && file >= 0
                    ? RenderAsync(arguments[file + 1], cancellationToken)
                    : Success("services:\n  cameraagent:\n    image: immutable\n");
            }
            if (arguments.Contains("up", StringComparer.Ordinal))
            {
                if (ComposeUpCount + 1 == FailComposeUpNumber)
                {
                    FailComposeUpNumber = -1;
                    return Task.FromResult(new ProcessResult(1, string.Empty, "Error response from daemon: cannot recreate the container"));
                }
                ComposeUpCount++;
                var file = arguments.ToList().IndexOf("--file");
                return file >= 0 ? UpAsync(arguments[file + 1], cancellationToken) : Success(string.Empty);
            }
            Assert.Fail($"Unexpected process: {fileName} {string.Join(' ', arguments)}");
            return Success(string.Empty);
        }

        private static Task<ProcessResult> Success(string output)
            => Task.FromResult(new ProcessResult(0, output, string.Empty));

        /// <summary>The container's host configuration, with the limits of the Compose file it was last started from.</summary>
        private object Limits()
            => LastComposeUp is not null && CameraAgentContainerResources.TryReadLimits(LastComposeUp) is { } limits
                ? new
                {
                    ReadonlyRootfs = true,
                    Privileged = false,
                    NanoCpus = (long)(limits.Cpus * 1_000_000_000m),
                    Memory = limits.MemoryBytes,
                    PidsLimit = (long)limits.PidsLimit
                }
                : new { ReadonlyRootfs = true, Privileged = false };

        private static async Task<ProcessResult> RenderAsync(string composeFile, CancellationToken cancellationToken)
            => new(0, await File.ReadAllTextAsync(composeFile, cancellationToken), string.Empty);

        private async Task<ProcessResult> UpAsync(string composeFile, CancellationToken cancellationToken)
        {
            LastComposeUp = await File.ReadAllTextAsync(composeFile, cancellationToken);
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        private void CancelImport()
        {
            if (CancelDuringImport is { } source)
            {
                source.Cancel();
                source.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed class FailingOnceOwnerClient : IOwnerBootstrapClient
    {
        private int stateCount;

        public Task WaitForHealthAsync(
            ApprovedCatalogContract expectedCatalog, CancellationToken cancellationToken, TimeSpan? timeout = null)
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

        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");

        public Task<string> VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
            => Task.FromResult("owner-password-change-required");
    }

}
