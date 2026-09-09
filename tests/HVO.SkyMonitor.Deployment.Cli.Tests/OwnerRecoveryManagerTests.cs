using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using System.Net.Sockets;
using System.Text.Json;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
public sealed class OwnerRecoveryManagerTests
{
    [TestMethod]
    public async Task Execute_GeneratesPrivateCredentialAndUsesAuthenticatedSocketPath()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var recoveryClient = new FakeRecoveryClient();

        var result = await OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            socketPath =>
            {
                var paths = InstallationPaths.Create(fixture.Root, fixture.InstanceId, ProductionCatalog.CatalogId);
                Assert.AreEqual(OwnerRecoverySocket.PathFor(paths), socketPath);
                return recoveryClient;
            },
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual("owner-password-change-required", result.OwnerBootstrapState);
        Assert.AreEqual(recoveryClient.OperationId, result.OperationId);
        Assert.IsTrue(File.Exists(result.PasswordFile));
        Assert.AreEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(result.PasswordFile));
        var json = JsonSerializer.Serialize(result, DeploymentJsonContext.Default.OwnerRecoveryResult);
        Assert.IsFalse(json.Contains(result.PasswordFile, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("passwordFile", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Execute_ResumeReusesOperationAndCredentialAfterLostAcknowledgement()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var recoveryClient = new FakeRecoveryClient { RejectNext = true };

        await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);
        var operationId = recoveryClient.OperationId;
        var password = recoveryClient.Password;

        var result = await OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(resume: true),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(operationId, result.OperationId);
        Assert.AreEqual(operationId, recoveryClient.OperationId);
        Assert.AreEqual(password, recoveryClient.Password);
    }

    [TestMethod]
    public async Task Execute_ResumeRejectsMissingStagedCredential()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var recoveryClient = new FakeRecoveryClient { RejectNext = true };
        await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);
        var paths = InstallationPaths.Create(fixture.Root, fixture.InstanceId, ProductionCatalog.CatalogId);
        var stagedCredential = Path.Combine(
            paths.OperationsRoot,
            "owner-recovery",
            recoveryClient.OperationId.ToString("D"),
            "temporary-password");
        File.Delete(stagedCredential);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(resume: true),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(1, recoveryClient.RequestCount);

        var replacement = await OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(recoveryClient.OperationId, Guid.Empty);
        Assert.AreEqual("completed", replacement.Outcome);
    }

    [TestMethod]
    public async Task Execute_DefinitiveRejectionAllowsFreshOperation()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var recoveryClient = new FakeRecoveryClient { RejectDefinitively = true };

        var rejection = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);
        var rejectedOperationId = recoveryClient.OperationId;

        Assert.AreEqual(OwnerRecoveryFailureDisposition.FreshOperationRequired, rejection.Disposition);

        var result = await OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreNotEqual(rejectedOperationId, result.OperationId);
        Assert.AreEqual("completed", result.Outcome);
    }

    [TestMethod]
    public async Task Execute_UnsupportedRecoveryRetainsOperationForResume()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var recoveryClient = new FakeRecoveryClient { UnsupportedNext = true };

        var unsupported = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);
        var operationId = recoveryClient.OperationId;

        Assert.AreEqual(OwnerRecoveryFailureDisposition.Unsupported, unsupported.Disposition);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);

        var resumed = await OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(resume: true),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(operationId, resumed.OperationId);
    }

    [TestMethod]
    public async Task Execute_MissingRuntimeSocketIsUnsupportedAndRetainsOperationForResume()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        fixture.RemoveRecoverySocket();

        var unsupported = await Assert.ThrowsExactlyAsync<OwnerRecoveryProtocolException>(() =>
            OwnerRecoveryManager.ExecuteAsync(
                fixture.Request(),
                _ => throw new AssertFailedException("A client must not be created without the runtime socket."),
                fixture.Uid,
                fixture.Gid,
                CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual(OwnerRecoveryFailureDisposition.Unsupported, unsupported.Disposition);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => new FakeRecoveryClient(),
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Execute_ReadyReceiptDoesNotReportStaleCredential()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var recoveryClient = new FakeRecoveryClient { ResultState = "owner-ready" };

        var result = await OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => recoveryClient,
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("already-completed", result.Outcome);
        Assert.AreEqual("owner-ready", result.OwnerBootstrapState);
        Assert.IsNull(result.PasswordFile);
    }

    [TestMethod]
    public async Task Execute_CorruptRetainedStateReturnsBoundedError()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);
        var paths = InstallationPaths.Create(fixture.Root, fixture.InstanceId, ProductionCatalog.CatalogId);
        var statePath = Path.Combine(paths.OperationsRoot, $"cameraagent-{fixture.InstanceId:D}.owner-recovery.json");
        SafeFileSystem.WriteTextAtomic(statePath, "{ invalid retained state");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(resume: true),
            _ => new FakeRecoveryClient(),
            fixture.Uid,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);

        Assert.AreEqual("Owner recovery could not read or update its private deployment state.", exception.Message);
        Assert.IsFalse(exception.Message.Contains(fixture.Root, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Execute_RejectsRootOrDifferentRuntimeIdentityBeforeRecovery()
    {
        using var fixture = await RecoveryFixture.CreateAsync().ConfigureAwait(false);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => new FakeRecoveryClient(),
            0,
            0,
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => new FakeRecoveryClient(),
            fixture.Uid + 1,
            fixture.Gid,
            CancellationToken.None)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => OwnerRecoveryManager.ExecuteAsync(
            fixture.Request(),
            _ => new FakeRecoveryClient(),
            fixture.Uid,
            fixture.Gid + 1,
            CancellationToken.None)).ConfigureAwait(false);
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly Socket _recoverySocket;

        private RecoveryFixture(
            string root,
            Guid instanceId,
            uint uid,
            uint gid,
            Socket recoverySocket)
        {
            Root = root;
            InstanceId = instanceId;
            Uid = uid;
            Gid = gid;
            _recoverySocket = recoverySocket;
        }

        internal string Root { get; }
        internal Guid InstanceId { get; }
        internal uint Uid { get; }
        internal uint Gid { get; }

        internal OwnerRecoveryRequest Request(bool resume = false)
            => new(InstanceId, Root, null, GeneratePassword: true, Resume: resume, Json: false)
            {
                AllowTestProductRoot = true
            };

        internal void RemoveRecoverySocket()
        {
            _recoverySocket.Dispose();
            File.Delete(OwnerRecoverySocket.PathFor(
                InstallationPaths.Create(Root, InstanceId, ProductionCatalog.CatalogId)));
        }

        internal static async Task<RecoveryFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-or-{Guid.NewGuid():N}"[..15]);
            var instanceId = Guid.NewGuid();
            var paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.CatalogId);
            foreach (var directory in new[]
            {
                paths.ProductRoot,
                Path.Combine(paths.ProductRoot, "cameraagents"),
                paths.InstanceRoot,
                paths.ConfigRoot,
                paths.StateRoot,
                paths.DeploymentStateRoot,
                Path.Combine(paths.StateRoot, "identity"),
                paths.OperationsRoot
            })
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }

            var uid = NativeLinux.getuid();
            var gid = NativeLinux.getgid();
            var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
            var catalog = new CatalogInstallationIdentity(
                ProductionCatalog.CatalogId,
                ProductionCatalog.PackageVersion,
                "2",
                "3",
                ProductionCatalog.DatabaseSha256,
                ProductionCatalog.DatabaseLength,
                ProductionCatalog.RowCount,
                paths.CatalogRoot,
                new string('a', 64),
                "local-offline");
            var image = new ImageInstallationIdentity(
                "registry",
                $"cameraagent@sha256:{new string('b', 64)}",
                $"sha256:{new string('c', 64)}",
                "amd64",
                null,
                UpgradeCompatibility: "backward-compatible",
                SourceRevision: new string('8', 40),
                Component: "CameraAgent",
                ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: "hyg-v42-production-p3-s2");
            var installationId = Guid.NewGuid();
            var configurationSha = new string('e', 64);
            var rigSha = new string('f', 64);
            var scheduleSha = new string('1', 64);
            var composeSha = ComposeDeployment.ComputeSha256("services: {}\n");
            var manifest = new InstanceManifest(
                1,
                "HVO.SkyMonitor",
                "cameraagent-install-v1",
                DeploymentComponent.CameraAgent,
                instanceId,
                "Test Camera",
                instanceId,
                $"installer-{instanceId:D}",
                1,
                new string('d', 64),
                "owner@example.test",
                0,
                0,
                0,
                "UTC",
                installationId,
                uid,
                gid,
                paths.ProductRoot,
                paths.ConfigRoot,
                paths.StateRoot,
                ComposeDeployment.TemplateVersion,
                configurationSha,
                rigSha,
                scheduleSha,
                "default",
                "1",
                "1",
                "active",
                ComposeDeployment.ComputeSha256("verification-token"),
                composeSha,
                catalog,
                image,
                null,
                daemon,
                "backward-compatible",
                DateTimeOffset.UtcNow,
                LifecycleCondition: InstanceLifecycleCondition.Installed,
                Port: 5130,
                LifecycleControlTokenSha256: ComposeDeployment.ComputeSha256("lifecycle-token"));
            var result = new InstallationResult(
                1,
                InstallationOutcome.Installed,
                installationId,
                instanceId,
                instanceId,
                "Test Camera",
                new Uri("http://127.0.0.1:5130"),
                "owner@example.test",
                "/tmp/password",
                paths.ProductRoot,
                paths.InstanceRoot,
                paths.ConfigRoot,
                paths.StateRoot,
                uid,
                gid,
                ComposeDeployment.TemplateVersion,
                0,
                0,
                0,
                "UTC",
                configurationSha,
                rigSha,
                scheduleSha,
                "default",
                "1",
                "1",
                "active",
                composeSha,
                catalog,
                image,
                daemon,
                true,
                true,
                "owner-password-change-required",
                DateTimeOffset.UtcNow);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ManifestPath,
                manifest,
                DeploymentJsonContext.Default.InstanceManifest,
                CancellationToken.None).ConfigureAwait(false);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath,
                result,
                DeploymentJsonContext.Default.InstallationResult,
                CancellationToken.None).ConfigureAwait(false);
            SafeFileSystem.WriteTextAtomic(
                Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"),
                "lifecycle-token");
            SafeFileSystem.WriteTextAtomic(
                Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token"),
                "lifecycle-token");
            var socketPath = OwnerRecoverySocket.PathFor(paths);
            var recoverySocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            recoverySocket.Bind(new UnixDomainSocketEndPoint(socketPath));
            recoverySocket.Listen(1);
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new RecoveryFixture(root, instanceId, uid, gid, recoverySocket);
        }

        public void Dispose()
        {
            _recoverySocket.Dispose();
            if (Directory.Exists(Root))
            {
                SafeFileSystem.MakeTreeOwnerWritable(Root);
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeRecoveryClient : IOwnerRecoveryClient
    {
        internal bool RejectNext { get; set; }
        internal bool RejectDefinitively { get; set; }
        internal bool UnsupportedNext { get; set; }
        internal string ResultState { get; set; } = "owner-password-change-required";
        internal Guid OperationId { get; private set; }
        internal string? Password { get; private set; }
        internal int RequestCount { get; private set; }

        public Task<string> RecoverAsync(
            Guid operationId,
            string lifecycleControlToken,
            string temporaryPassword,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.AreEqual("lifecycle-token", lifecycleControlToken);
            OperationId = operationId;
            Password = temporaryPassword;
            if (RejectNext)
            {
                RejectNext = false;
                throw new OwnerRecoveryProtocolException(
                    OwnerRecoveryFailureDisposition.ResumeRequired,
                    "simulated lost recovery acknowledgement");
            }
            if (RejectDefinitively)
            {
                RejectDefinitively = false;
                throw new OwnerRecoveryProtocolException(
                    OwnerRecoveryFailureDisposition.FreshOperationRequired,
                    "simulated definitive rejection");
            }
            if (UnsupportedNext)
            {
                UnsupportedNext = false;
                throw new OwnerRecoveryProtocolException(
                    OwnerRecoveryFailureDisposition.Unsupported,
                    "simulated unsupported recovery");
            }
            return Task.FromResult(ResultState);
        }
    }
}
