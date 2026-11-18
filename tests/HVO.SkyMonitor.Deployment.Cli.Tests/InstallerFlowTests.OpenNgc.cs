using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_OpenNgcLegacyResetResumeKeepsFirstComposeWrite(bool missingSnapshot)
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        var runner = OpenNgcInstallerRunner(request);
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, new InstallerOwnerClient());
            var (paths, selected) = await RetainOpenNgcSelectionAsync(request, first, runner);
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
            var lifecycleRunner = OpenNgcLifecycleRunner(paths, manifest);
            var uninstall = new LifecycleRequest(LifecycleOperationKind.Uninstall, first.InstanceId, root, false, false, false)
            { AllowTestProductRoot = true };
            var removed = await CameraAgentLifecycleManager.ExecuteAsync(uninstall, lifecycleRunner,
                _ => new OpenNgcInstallerLifecycleClient(), null, RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, removed.LifecycleCondition);
            await RetainLegacyOpenNgcResetAsync(paths, InstallationPhase.OwnerSeeded);
            Assert.IsFalse(File.Exists(paths.ResultPath));

            var expectations = new List<InstallationVerificationExpectation>();
            var reinstallRunner = OpenNgcInstallerRunner(request);
            var owner = new InstallerOwnerClient { InspectVerification = expectations.Add, OwnerAlreadySeeded = true };
            if (missingSnapshot)
            {
                var version = Path.Combine(selected.InstallRoot, "versions", selected.PackageVersion);
                File.Delete(Path.Combine(selected.InstallRoot, "current"));
                SafeFileSystem.MakeTreeOwnerWritable(version);
                Directory.Delete(version, recursive: true);
                var missing = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(request, reinstallRunner, owner));
                Assert.IsInstanceOfType<DirectoryNotFoundException>(missing.GetBaseException());
                StringAssert.Contains(missing.GetBaseException().Message, "Catalog snapshot directory is missing", StringComparison.Ordinal);
                Assert.AreEqual(0, reinstallRunner.ComposeUpCount, "A missing retained snapshot must stop installation before container startup.");
                Assert.AreEqual(selected, (await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None)).DeepSkyCatalog);
                return;
            }
            var restored = await InstallResourceRequestAsync(request, reinstallRunner, owner);
            Assert.AreEqual(first.ApplicationIdentity, restored.ApplicationIdentity);
            Assert.AreEqual(selected, restored.DeepSkyCatalog, "Reset preserves the configured deep-sky selection in the result.");
            await AssertOpenNgcInstallationAsync(paths, restored, selected);
            Assert.IsTrue(expectations.Count > 0);
            Assert.IsTrue(expectations.All(expectation => expectation.DeepSkyCatalog == selected));
            Assert.AreEqual(1, reinstallRunner.ComposeUpCount, "Resuming past OwnerSeeded must start the first Compose write without an owner-bootstrap rewrite.");
            StringAssert.Contains(reinstallRunner.LastComposeUp!, DeepSkyCatalogSelection.Mount, StringComparison.Ordinal);
            StringAssert.Contains(reinstallRunner.LastComposeUpEnvironment!, $"HVO_DEEP_SKY_CATALOG_ROOT={selected.InstallRoot}\n", StringComparison.Ordinal);

            manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
            lifecycleRunner = OpenNgcLifecycleRunner(paths, manifest);
            var rollback = await CameraAgentLifecycleManager.ExecuteAsync(
                uninstall with { Operation = LifecycleOperationKind.CatalogRollback, DryRun = true, CatalogFamily = "deep-sky" },
                lifecycleRunner, null, null, RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.AreEqual("planned", rollback.Outcome);
            var upgrade = await CameraAgentLifecycleManager.ExecuteAsync(
                uninstall with
                {
                    Operation = LifecycleOperationKind.Upgrade,
                    DryRun = true,
                    ImageReference = OpenNgcUpgradeReference,
                    NoDownload = true
                },
                lifecycleRunner, null, null, RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.AreEqual("planned", upgrade.Outcome);
            await AssertOpenNgcInstallationAsync(paths, restored, selected);
            expectations.Clear();
            await CameraAgentLifecycleManager.ExecuteAsync(
                uninstall with
                {
                    Operation = LifecycleOperationKind.Upgrade,
                    ImageReference = OpenNgcUpgradeReference,
                    NoDownload = true,
                    MigrationBackwardCompatible = true
                },
                lifecycleRunner, _ => new OpenNgcInstallerLifecycleClient(), _ => owner,
                RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.IsTrue(expectations.Count > 1, "Upgrade must verify the current installation before mutation and the candidate after startup.");
            Assert.IsTrue(expectations.All(expectation => expectation.DeepSkyCatalog == selected),
                "Pre-mutation and candidate verification must both retain the active deep-sky identity.");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_OpenNgcLimitsReRunKeepsTheStartedMountAndRemainsIdempotent()
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        var runner = OpenNgcInstallerRunner(request);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var (paths, selected) = await RetainOpenNgcSelectionAsync(request, first, runner);
            var before = runner.ComposeUpCount;
            var changed = request with { Cpus = 3m, Memory = "6g" };
            var result = await InstallResourceRequestAsync(changed, runner, owner);
            Assert.IsTrue(runner.ComposeUpCount > before);
            StringAssert.Contains(runner.LastComposeUp!, DeepSkyCatalogSelection.Mount, StringComparison.Ordinal);
            StringAssert.Contains(runner.LastComposeUpEnvironment!, $"HVO_DEEP_SKY_CATALOG_ROOT={selected.InstallRoot}\n", StringComparison.Ordinal);
            await AssertOpenNgcInstallationAsync(paths, result, selected);
            var same = await InstallResourceRequestAsync(changed, runner, owner);
            Assert.IsFalse(same.Plan!.HasChanges);
            Assert.AreEqual("Nothing to change: the instance is installed as the answers describe.", same.Plan.Summary);
            await AssertOpenNgcInstallationAsync(paths, same, selected);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    private const string OpenNgcUpgradeReference = "ghcr.io/example/cameraagent@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private static Dictionary<string, string> OpenNgcImageLabels() => new(ReleaseFixture.ContractLabels)
    {
        ["io.hvo.skymonitor.catalog-contract"] = CameraAgentImageContract.IndependentCatalogContract,
        ["io.hvo.skymonitor.catalog-manifest-version"] = "4",
        ["org.opencontainers.image.revision"] = new string('a', 40)
    };

    private static InstallerProcessRunner OpenNgcInstallerRunner(InstallRequest request)
        => new(request.ImageReference, request.ProductRoot, request.InstanceId!.Value)
        { RenderComposeFile = true, Labels = OpenNgcImageLabels() };

    private static LifecycleContractTests.FakeRunner OpenNgcLifecycleRunner(InstallationPaths paths, InstanceManifest manifest)
    {
        var runner = new LifecycleContractTests.FakeRunner(manifest.DockerDaemon)
        { CatalogContract = CameraAgentImageContract.IndependentCatalogContract };
        runner.ConfigureRuntime(paths, manifest.Image.ImmutableReference, manifest.Image.ImageId,
            OpenNgcUpgradeReference, $"sha256:{new string('d', 64)}", RuntimeUid, RuntimeGid);
        foreach (var label in OpenNgcImageLabels()) runner.CandidateLabels[label.Key] = label.Value;
        return runner;
    }

    private static async Task<(InstallationPaths Paths, CatalogInstallationIdentity Selected)> RetainOpenNgcSelectionAsync(
        InstallRequest request, InstallationResult first, InstallerProcessRunner runner)
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_OPENNGC_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle)) Assert.Inconclusive("HVO_OPENNGC_CATALOG_BUNDLE is required for the public OpenNGC installer flow contract.");
        var paths = InstallationPaths.Create(request.ProductRoot, first.InstanceId, first.Catalog.CatalogId);
        var installed = await CatalogLifecycleManager.ExecuteAsync(
            new(LifecycleOperationKind.CatalogInstall, null, request.ProductRoot, false, false, false)
            { AllowTestProductRoot = true, CatalogBundle = bundle }, runner, RuntimeUid, RuntimeGid, CancellationToken.None);
        var selected = installed.Catalog!;
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        var recorded = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, CancellationToken.None);
        var compose = CameraAgentLifecycleManager.ComposeFrom(paths, manifest, recorded);
        DeepSkyCatalogSelection.WriteSelection(paths, compose, selected);
        var model = await new DockerClient(runner).ComposeAsync(compose.ComposeFile, compose.EnvironmentFile,
            compose.ProjectName, ["config"], CancellationToken.None);
        var hash = ComposeDeployment.ComputeSha256(model.StandardOutput);
        // Selection itself has separate lifecycle coverage. Start the same retained selection this regression receives.
        await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, manifest with
        { DeepSkyCatalog = selected, HasPreviousDeepSkySelection = true, ComposeModelSha256 = hash },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(paths.ResultPath, recorded with { DeepSkyCatalog = selected, ComposeModelSha256 = hash },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        await new DockerClient(runner).ComposeAsync(compose.ComposeFile, compose.EnvironmentFile,
            compose.ProjectName, ["up", "--detach"], CancellationToken.None);
        return (paths, selected);
    }

    private static async Task AssertOpenNgcInstallationAsync(InstallationPaths paths, InstallationResult result, CatalogInstallationIdentity selected)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        var recorded = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, CancellationToken.None);
        Assert.AreEqual(selected, manifest.DeepSkyCatalog);
        Assert.AreEqual(selected, result.DeepSkyCatalog);
        Assert.AreEqual(selected, recorded.DeepSkyCatalog);
        Assert.IsTrue(manifest.HasPreviousDeepSkySelection);
        Assert.IsNull(manifest.PreviousDeepSkyCatalog);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "compose.yml")), DeepSkyCatalogSelection.Mount, StringComparison.Ordinal);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "instance.env")),
            $"HVO_DEEP_SKY_CATALOG_ROOT={selected.InstallRoot}\n", StringComparison.Ordinal);
        foreach (var (key, value) in new[] { ("Enabled", "true"), ("Root", "/app/deep-sky-catalog"),
                     ("RequiredCatalogId", selected.CatalogId), ("RequiredPackageKind", "Production"), ("RequiredPackageVersion", selected.PackageVersion) })
            Assert.AreEqual(value, await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "secrets", "DeepSkyCatalog__" + key)));
        CameraAgentLifecycleManager.EnsureCorrelated(paths, result.InstanceId, manifest, recorded, allowTransactionalDrift: false);
    }

    private sealed class OpenNgcInstallerLifecycleClient : ICameraAgentLifecycleClient
    {
        private static readonly LifecycleContinuity Drained = new("Paused", 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, ReplayCatalogIndependent: true);
        public Task<LifecycleContinuity> PauseAndDrainAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken) => Task.FromResult(Drained);
        public Task<LifecycleContinuity> ConfirmDrainedAsync(string verificationToken, CancellationToken cancellationToken) => Task.FromResult(Drained);
        public Task ResumeAsync(Guid operationId, string verificationToken, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<LifecycleContinuity> ReadContinuityAsync(string verificationToken, CancellationToken cancellationToken) => Task.FromResult(Drained);
        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(Guid operationId, Guid commandId, long expectedVersion, string verificationToken, CancellationToken cancellationToken)
            => throw new AssertFailedException("Recovery is not expected in the successful uninstall/reset flow.");
    }
}
