using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_OpenNgcCrossImageResetRefusesWithoutChangingState(bool dryRun)
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        try
        {
            var (paths, runner, _) = await UpgradeSelectAndUninstallOpenNgcAsync(request);
            var before = FileSystemActivity.Snapshot(root);
            var error = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentStateResetManager.ExecuteAsync(
                new(request.InstanceId, request.InstanceId, root, dryRun, false) { AllowTestProductRoot = true },
                runner, RuntimeUid, RuntimeGid, CancellationToken.None));
            Assert.IsTrue(error.IsRefusal);
            StringAssert.Contains(error.Message, "deep-sky", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, "disabled", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, "reinstall", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, "upgrade", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, "reselect", StringComparison.Ordinal);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "Refused reset must not change any retained state or evidence.");
            Assert.IsTrue(File.Exists(Path.Combine(paths.StateRoot, "raw", "reset-sentinel")));
            Assert.IsTrue(File.Exists(paths.ResultPath));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    private static InstallerProcessRunner OpenNgcLegacyInstallerRunner(InstallRequest request)
    {
        var labels = OpenNgcImageLabels();
        labels["io.hvo.skymonitor.catalog-contract"] = CameraAgentImageContract.ComposedCatalogContract;
        labels["io.hvo.skymonitor.catalog-manifest-version"] = "3";
        return new(request.ImageReference, request.ProductRoot, request.InstanceId!.Value)
        { RenderComposeFile = true, Labels = labels };
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_OpenNgcPendingPlanAndRunRefuseTheSameInvalidSelection(bool unsupportedImage)
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        try
        {
            var (paths, selected) = await PrepareLegacyOpenNgcPendingInstallAsync(request, unsupportedImage);
            if (!unsupportedImage)
            {
                File.Delete(Path.Combine(selected.InstallRoot, "current"));
                var version = Path.Combine(selected.InstallRoot, "versions", selected.PackageVersion);
                SafeFileSystem.MakeTreeOwnerWritable(version);
                Directory.Delete(version, true);
            }
            var runner = unsupportedImage ? OpenNgcLegacyInstallerRunner(request) : OpenNgcInstallerRunner(request);
            var before = FileSystemActivity.Snapshot(root);
            var planned = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                InstallResourceRequestAsync(request with { DryRun = true }, runner, new InstallerOwnerClient()));
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "Refused planning must not write.");
            var applied = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                InstallResourceRequestAsync(request, runner, new InstallerOwnerClient()));
            Assert.IsTrue(planned.IsRefusal);
            Assert.IsTrue(applied.IsRefusal);
            Assert.AreEqual(planned.Message, applied.Message);
            StringAssert.Contains(planned.Message, unsupportedImage ? "cannot resolve the selected catalog" : "retained deep-sky catalog snapshot", StringComparison.Ordinal);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "Admission refusal must precede writes in plan and run.");
            Assert.AreEqual(0, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_OpenNgcPendingPlanReportsTheRetainedSelectionWithoutWriting()
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        try
        {
            var (_, selected) = await PrepareLegacyOpenNgcPendingInstallAsync(request, unsupportedImage: false);
            var before = FileSystemActivity.Snapshot(root);
            var plan = await InstallResourceRequestAsync(request with { DryRun = true }, OpenNgcInstallerRunner(request), new InstallerOwnerClient());
            Assert.AreEqual(InstallationOutcome.Planned, plan.Outcome);
            Assert.AreEqual(selected, plan.DeepSkyCatalog);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "Planning a retained selection must not write.");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    private static async Task<(InstallationPaths Paths, CatalogInstallationIdentity Selected)> PrepareLegacyOpenNgcPendingInstallAsync(
        InstallRequest request, bool unsupportedImage)
    {
        InstallationPaths paths;
        CatalogInstallationIdentity selected;
        if (unsupportedImage)
        {
            (paths, _, selected) = await UpgradeSelectAndUninstallOpenNgcAsync(request);
        }
        else
        {
            var runner = OpenNgcInstallerRunner(request);
            var first = await InstallResourceRequestAsync(request, runner, new InstallerOwnerClient());
            (paths, selected) = await RetainOpenNgcSelectionAsync(request, first, runner);
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
            await CameraAgentLifecycleManager.ExecuteAsync(
                new(LifecycleOperationKind.Uninstall, first.InstanceId, request.ProductRoot, false, false, false)
                { AllowTestProductRoot = true }, OpenNgcLifecycleRunner(paths, manifest),
                _ => new OpenNgcInstallerLifecycleClient(), null, RuntimeUid, RuntimeGid, CancellationToken.None);
        }
        await RetainLegacyOpenNgcResetAsync(paths);
        return (paths, selected);
    }

    private static async Task<(InstallationPaths Paths, LifecycleContractTests.FakeRunner Runner, CatalogInstallationIdentity Selected)>
        UpgradeSelectAndUninstallOpenNgcAsync(InstallRequest request)
    {
        var owner = new InstallerOwnerClient();
        var first = await InstallResourceRequestAsync(request, OpenNgcLegacyInstallerRunner(request), owner);
        Assert.AreEqual(CameraAgentImageContract.ComposedCatalogContract, first.Image.CatalogContract);
        var paths = InstallationPaths.Create(request.ProductRoot, first.InstanceId, first.Catalog.CatalogId);
        var state = await ReadOpenNgcInstallationStateAsync(paths);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        var runner = OpenNgcLifecycleRunner(paths, manifest);
        var lifecycle = new OpenNgcInstallerLifecycleClient();
        var operation = new LifecycleRequest(LifecycleOperationKind.Upgrade, first.InstanceId, request.ProductRoot, false, false, false)
        { AllowTestProductRoot = true, ImageReference = OpenNgcUpgradeReference, MigrationBackwardCompatible = true };
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(operation, runner, _ => lifecycle, _ => owner,
            RuntimeUid, RuntimeGid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(CameraAgentImageContract.IndependentCatalogContract, manifest.Image.CatalogContract);
        Assert.AreNotEqual(first.Image.ImageId, manifest.Image.ImageId);
        Assert.AreEqual(state.RequestSha256, (await ReadOpenNgcInstallationStateAsync(paths)).RequestSha256);
        var bundle = Environment.GetEnvironmentVariable("HVO_OPENNGC_CATALOG_BUNDLE");
        if (string.IsNullOrWhiteSpace(bundle)) Assert.Inconclusive("HVO_OPENNGC_CATALOG_BUNDLE is required for the public OpenNGC installer flow contract.");
        var installed = await CatalogLifecycleManager.ExecuteAsync(operation with
        { Operation = LifecycleOperationKind.CatalogInstall, InstanceId = null, ImageReference = null, MigrationBackwardCompatible = false, CatalogBundle = bundle },
            runner, RuntimeUid, RuntimeGid, CancellationToken.None);
        var selected = installed.Catalog!;
        var selection = await CameraAgentLifecycleManager.ExecuteAsync(operation with
        { Operation = LifecycleOperationKind.CatalogSelect, ImageReference = null, MigrationBackwardCompatible = false, CatalogVersion = selected.PackageVersion },
            runner, _ => lifecycle, _ => owner, RuntimeUid, RuntimeGid, CancellationToken.None);
        Assert.AreEqual(selected, selection.DeepSkyCatalog);
        manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
        Assert.IsNull(manifest.PreviousImage, "The incompatible retained image rollback must have been retired.");
        SafeFileSystem.WriteTextAtomic(Path.Combine(paths.StateRoot, "raw", "reset-sentinel"), "Disposable runtime state.");
        var uninstall = await CameraAgentLifecycleManager.ExecuteAsync(operation with
        { Operation = LifecycleOperationKind.Uninstall, ImageReference = null, MigrationBackwardCompatible = false }, runner, _ => lifecycle, null,
            RuntimeUid, RuntimeGid, CancellationToken.None);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, uninstall.LifecycleCondition);
        return (paths, runner, selected);
    }

    private static async Task<InstallationState> ReadOpenNgcInstallationStateAsync(InstallationPaths paths)
        => JsonSerializer.Deserialize(await File.ReadAllTextAsync(paths.StatePath), DeploymentJsonContext.Default.InstallationState)!;

    private static async Task RetainLegacyOpenNgcResetAsync(InstallationPaths paths, InstallationPhase phase = InstallationPhase.Preflight)
    {
        // A pending install left by a reset from an older installer. New resets must refuse an active selection.
        var state = await ReadOpenNgcInstallationStateAsync(paths);
        await SafeFileSystem.WriteJsonAtomicAsync(paths.StatePath, state with
        { Phase = phase, Status = InstallationStatus.Pending }, DeploymentJsonContext.Default.InstallationState, CancellationToken.None);
        File.Delete(paths.ResultPath);
        File.Delete(Path.Combine(paths.StateRoot, "raw", "reset-sentinel"));
    }
}
