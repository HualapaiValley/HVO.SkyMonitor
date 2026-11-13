using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_OpenNgcExplicitDisableAfterTwoVersionsAllowsResetRecovery(bool uninstalled)
    {
        var (request, root) = ResourceRequest(RequireProductionCatalogBundle(), Guid.NewGuid());
        try
        {
            var (paths, runner, r1, r2) = await InstallTwoOpenNgcVersionsAsync(request);
            var owner = new InstallerOwnerClient();
            var lifecycle = new OpenNgcInstallerLifecycleClient();
            var command = new LifecycleRequest(LifecycleOperationKind.Uninstall, request.InstanceId, root, false, false, false)
            { AllowTestProductRoot = true };
            if (uninstalled)
            {
                await CameraAgentLifecycleManager.ExecuteAsync(command, runner, _ => lifecycle, null,
                    RuntimeUid, RuntimeGid, CancellationToken.None);
                var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentStateResetManager.ExecuteAsync(
                    new(request.InstanceId, request.InstanceId, root, false, false) { AllowTestProductRoot = true },
                    runner, RuntimeUid, RuntimeGid, CancellationToken.None));
                StringAssert.Contains(refusal.Message, $"cameraagent reinstall --instance-id {request.InstanceId:D}", StringComparison.Ordinal);
                StringAssert.Contains(refusal.Message, "--catalog-family deep-sky --catalog-version disabled", StringComparison.Ordinal);
                var restored = await CameraAgentLifecycleManager.ExecuteAsync(command with { Operation = LifecycleOperationKind.Reinstall },
                    runner, _ => lifecycle, _ => owner, RuntimeUid, RuntimeGid, CancellationToken.None);
                Assert.AreEqual(InstanceLifecycleCondition.Installed, restored.LifecycleCondition);
            }

            var disable = (LifecycleRequest)CommandLine.ParseCommand([
                "catalog", "select", "--instance-id", request.InstanceId!.Value.ToString("D"), "--product-root", root,
                "--catalog-family", "deep-sky", "--catalog-version", "disabled"
            ], allowTestProductRoot: true);
            var before = FileSystemActivity.Snapshot(root);
            var planned = await CameraAgentLifecycleManager.ExecuteAsync(disable with { DryRun = true }, runner, null, null,
                RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.AreEqual("planned", planned.Outcome);
            Assert.IsNull(planned.DeepSkyCatalog);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "Disable planning must not write.");
            var disabled = await CameraAgentLifecycleManager.ExecuteAsync(disable, runner, _ => lifecycle, _ => owner,
                RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.IsNull(disabled.DeepSkyCatalog);
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
            var result = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, CancellationToken.None);
            Assert.IsNull(manifest.DeepSkyCatalog);
            Assert.IsNull(result.DeepSkyCatalog);
            Assert.AreEqual(r2, manifest.PreviousDeepSkyCatalog);
            Assert.IsTrue(manifest.HasPreviousDeepSkySelection);
            Assert.AreEqual("hyg-v42-production", manifest.Catalog.CatalogId);
            Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "compose.yml"))).Contains(DeepSkyCatalogSelection.Mount, StringComparison.Ordinal));
            Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "compose", "instance.env"))).Contains("HVO_DEEP_SKY_CATALOG_ROOT=", StringComparison.Ordinal));
            foreach (var (key, value) in new[] { ("Enabled", "false"), ("Root", "/app/deep-sky-catalog"),
                         ("RequiredCatalogId", ""), ("RequiredPackageKind", "Production"), ("RequiredPackageVersion", "") })
                Assert.AreEqual(value, await File.ReadAllTextAsync(Path.Combine(paths.ConfigRoot, "secrets", "DeepSkyCatalog__" + key)));
            Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(paths.WithCatalog(r2.CatalogId), r2.PackageVersion, CancellationToken.None));

            await CameraAgentLifecycleManager.ExecuteAsync(command, runner, _ => lifecycle, null, RuntimeUid, RuntimeGid, CancellationToken.None);
            var reset = await CameraAgentStateResetManager.ExecuteAsync(
                new(request.InstanceId, request.InstanceId, root, false, false) { AllowTestProductRoot = true },
                runner, RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.AreEqual("completed", reset.Outcome);
            var installed = await InstallResourceRequestAsync(request, OpenNgcLegacyInstallerRunner(request), new InstallerOwnerClient());
            Assert.IsNull(installed.DeepSkyCatalog);
            Assert.AreEqual(CameraAgentImageContract.ComposedCatalogContract, installed.Image.CatalogContract);
            manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None);
            result = await CameraAgentLifecycleManager.ReadResultAsync(paths.ResultPath, CancellationToken.None);
            CameraAgentLifecycleManager.EnsureCorrelated(paths, installed.InstanceId, manifest, result, false);
            runner = OpenNgcLifecycleRunner(paths, manifest);
            var upgrade = command with { Operation = LifecycleOperationKind.Upgrade, ImageReference = OpenNgcUpgradeReference, MigrationBackwardCompatible = true };
            Assert.AreEqual("status", (await CameraAgentLifecycleManager.ExecuteAsync(command with { Operation = null }, runner, null, null,
                RuntimeUid, RuntimeGid, CancellationToken.None)).Outcome);
            Assert.AreEqual("planned", (await CameraAgentLifecycleManager.ExecuteAsync(upgrade with { DryRun = true }, runner, null, null,
                RuntimeUid, RuntimeGid, CancellationToken.None)).Outcome);
            await CameraAgentLifecycleManager.ExecuteAsync(upgrade, runner, _ => lifecycle, _ => owner, RuntimeUid, RuntimeGid, CancellationToken.None);
            var rollback = command with { Operation = LifecycleOperationKind.CatalogRollback, CatalogFamily = "deep-sky", DryRun = true };
            Assert.AreEqual("planned", (await CameraAgentLifecycleManager.ExecuteAsync(rollback, runner, null, null,
                RuntimeUid, RuntimeGid, CancellationToken.None)).Outcome);
            var reselected = await CameraAgentLifecycleManager.ExecuteAsync(disable with { CatalogVersion = r1.PackageVersion }, runner,
                _ => lifecycle, _ => owner, RuntimeUid, RuntimeGid, CancellationToken.None);
            Assert.AreEqual(r1, reselected.DeepSkyCatalog);
            Assert.AreEqual("planned", (await CameraAgentLifecycleManager.ExecuteAsync(command with { DryRun = true }, runner, null, null,
                RuntimeUid, RuntimeGid, CancellationToken.None)).Outcome);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [DataRow("select")]
    [DataRow("gc")]
    public void InstallAsync_OpenNgcDisabledTargetRequiresExplicitDeepSkySelection(string operation)
    {
        string[] arguments = operation == "select"
            ? ["catalog", "select", "--instance-id", Guid.NewGuid().ToString("D"), "--catalog-version", "disabled"]
            : ["catalog", "gc", "--catalog-version", "disabled"];
        var error = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments));
        StringAssert.Contains(error.Message, "catalog select --catalog-family deep-sky", StringComparison.Ordinal);
    }

    private static async Task<(InstallationPaths Paths, LifecycleContractTests.FakeRunner Runner, CatalogInstallationIdentity R1, CatalogInstallationIdentity R2)>
        InstallTwoOpenNgcVersionsAsync(InstallRequest request)
    {
        var (paths, runner, r1) = await UpgradeSelectAndUninstallOpenNgcAsync(request);
        var lifecycle = new OpenNgcInstallerLifecycleClient();
        var owner = new InstallerOwnerClient();
        var command = new LifecycleRequest(LifecycleOperationKind.Reinstall, request.InstanceId, request.ProductRoot, false, false, false)
        { AllowTestProductRoot = true };
        await CameraAgentLifecycleManager.ExecuteAsync(command, runner, _ => lifecycle, _ => owner,
            RuntimeUid, RuntimeGid, CancellationToken.None);
        var bundle = Environment.GetEnvironmentVariable("HVO_OPENNGC_CATALOG_BUNDLE")!;
        var r2Bundle = Path.Combine(request.ProductRoot, "public-r2-fixture");
        SafeFileSystem.CreateOwnerDirectory(r2Bundle);
        foreach (var file in Directory.EnumerateFiles(bundle))
        {
            var target = Path.Combine(r2Bundle, Path.GetFileName(file));
            File.Copy(file, target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var package = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(r2Bundle, "manifest.json")))!;
        package["package"]!["version"] = "openngc-v20260501-p6-s5-r2";
        SafeFileSystem.WriteTextAtomic(Path.Combine(r2Bundle, "manifest.json"), package.ToJsonString());
        var installed = await CatalogLifecycleManager.ExecuteAsync(command with
        { Operation = LifecycleOperationKind.CatalogInstall, InstanceId = null, CatalogBundle = r2Bundle },
            runner, RuntimeUid, RuntimeGid, CancellationToken.None);
        var r2 = installed.Catalog!;
        var selected = await CameraAgentLifecycleManager.ExecuteAsync(command with
        { Operation = LifecycleOperationKind.CatalogSelect, CatalogFamily = "deep-sky", CatalogVersion = r2.PackageVersion },
            runner, _ => lifecycle, _ => owner, RuntimeUid, RuntimeGid, CancellationToken.None);
        Assert.AreEqual(r2, selected.DeepSkyCatalog);
        Assert.AreNotEqual(r1.PackageVersion, r2.PackageVersion);
        return (paths, runner, r1, r2);
    }
}
