using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PublicOpenNgcBundleInstallsAndSelectsThroughTheCatalogCommands()
    {
        var bundle = Environment.GetEnvironmentVariable("HVO_OPENNGC_CATALOG_BUNDLE");
        if (string.IsNullOrEmpty(bundle)) Assert.Inconclusive("Set HVO_OPENNGC_CATALOG_BUNDLE for temporary public-source artifact evidence.");
        using var fixture = await DeepSkyFixtureAsync();
        var install = fixture.Request(LifecycleOperationKind.CatalogInstall) with { InstanceId = null, CatalogBundle = bundle };
        var installed = await CameraAgentLifecycleManager.ExecuteAsync(install, fixture.Runner, null, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("openngc-production", installed.Catalog!.CatalogId);
        var select = fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = installed.Catalog.PackageVersion };
        var selected = await CameraAgentLifecycleManager.ExecuteAsync(select, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(installed.Catalog, selected.DeepSkyCatalog);
        Assert.AreEqual(fixture.Manifest.Catalog, selected.Catalog);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task StarSelectionPreservesTheIndependentDeepSkySelectionAndMount()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var deepSky = DeepSkyIdentity(fixture);
        var lifecycle = new FakeLifecycleClient();
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        await DeepSkyTransitionAsync(fixture, deepSky, lifecycle, owner);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var candidate = manifest.Catalog with { PackageVersion = "hyg-v4.2-p3-s2-r2" };
        var request = fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogVersion = candidate.PackageVersion };
        var compose = CameraAgentLifecycleManager.ComposeFrom(fixture.Paths, manifest, result);
        var composeBefore = await File.ReadAllTextAsync(compose.ComposeFile);
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, fixture.Paths, request.Operation!.Value, manifest, CancellationToken.None);
        var selected = await CatalogLifecycleManager.SelectAsync(request, fixture.Paths, manifest, result, compose,
            new DockerClient(fixture.Runner), _ => lifecycle, _ => owner, "lifecycle-token", "verification-token",
            candidate, operation, CancellationToken.None);
        Assert.AreEqual(candidate, selected.Catalog);
        Assert.AreEqual(deepSky, selected.DeepSkyCatalog);
        Assert.AreEqual(deepSky, (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None)).DeepSkyCatalog);
        Assert.AreEqual(composeBefore, await File.ReadAllTextAsync(compose.ComposeFile));
        Assert.AreEqual(deepSky.PackageVersion, await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__RequiredPackageVersion")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcInterruptedRecoveryResumesFromTheRetainedOriginalSelection()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var candidate = DeepSkyIdentity(fixture);
        var failures = 2;
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity)
        {
            InspectVerification = _ =>
            {
                if (failures-- > 0) throw new IOException("Synthetic verification interruption.");
            }
        };
        var lifecycle = new FakeLifecycleClient();
        await Assert.ThrowsExactlyAsync<InstallerException>(() => DeepSkyTransitionAsync(fixture, candidate, lifecycle, owner));
        var interrupted = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.IsTrue(interrupted!.MutationStarted);
        Assert.IsNull(interrupted.OriginalDeepSkyCatalog);
        var resumed = await DeepSkyTransitionAsync(fixture, candidate, lifecycle, owner, resume: true);
        Assert.AreEqual(candidate, resumed.DeepSkyCatalog);
        Assert.AreEqual(fixture.Manifest.Catalog, resumed.Catalog);
        var completed = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Completed, completed!.Status);
        Assert.AreEqual(interrupted.OperationId, completed.OperationId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcSelectionAndRollbackPreserveTheStarSelectionAndIndependentEvidence()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var original = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var candidate = DeepSkyIdentity(fixture);
        var lifecycle = new FakeLifecycleClient();
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        var selected = await DeepSkyTransitionAsync(fixture, candidate, lifecycle, owner);
        Assert.AreEqual(candidate, selected.DeepSkyCatalog);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(original.Catalog, manifest.Catalog);
        Assert.AreEqual(candidate, manifest.DeepSkyCatalog);
        Assert.IsTrue(manifest.HasPreviousDeepSkySelection);
        Assert.IsNull(manifest.PreviousDeepSkyCatalog);
        Assert.AreEqual("true", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__Enabled")));
        Assert.AreEqual(original.Catalog.PackageVersion, await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion")));
        var rolledBack = await DeepSkyTransitionAsync(fixture, null, lifecycle, owner, rollback: true);
        Assert.IsNull(rolledBack.DeepSkyCatalog);
        manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(original.Catalog, manifest.Catalog);
        Assert.IsNull(manifest.DeepSkyCatalog);
        Assert.AreEqual(candidate, manifest.PreviousDeepSkyCatalog);
        Assert.AreEqual("false", await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__Enabled")));
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"))).Contains("/app/deep-sky-catalog", StringComparison.Ordinal));
        Assert.AreEqual(2, lifecycle.ResumeCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcFailureOrPendingProcessingRestoresBothSelections(bool pending)
    {
        using var fixture = await DeepSkyFixtureAsync();
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        var composeBefore = await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"));
        var lifecycle = new FakeLifecycleClient { DrainedProcessingPending = pending ? 1 : 0 };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = !pending };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => DeepSkyTransitionAsync(fixture, DeepSkyIdentity(fixture), lifecycle, owner));
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.AreEqual(composeBefore, await File.ReadAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__Enabled")));
        Assert.AreEqual(1, lifecycle.ResumeCount);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, operation!.Status);
        Assert.IsFalse(operation.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcCommittedSelectionResumesWithoutRepeatingMutation()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var candidate = DeepSkyIdentity(fixture);
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => DeepSkyTransitionAsync(fixture, candidate, lifecycle, owner));
        var before = fixture.Runner.ComposeUpCount;
        await DeepSkyTransitionAsync(fixture, candidate, lifecycle, owner, resume: true);
        Assert.AreEqual(before, fixture.Runner.ComposeUpCount);
        var operation = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Completed, operation!.Status);
        Assert.AreEqual(candidate, operation.CandidateDeepSkyCatalog);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcCancellationRestoresTheOriginalSelection()
    {
        using var fixture = await DeepSkyFixtureAsync();
        using var cancelled = new CancellationTokenSource();
        var first = true;
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity)
        {
            InspectVerification = _ =>
            {
                if (!first) return;
                first = false;
                cancelled.Cancel();
                cancelled.Token.ThrowIfCancellationRequested();
            }
        };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => DeepSkyTransitionAsync(fixture, DeepSkyIdentity(fixture), new(), owner, cancellationToken: cancelled.Token));
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.IsNull(manifest.DeepSkyCatalog);
        Assert.AreEqual(fixture.Manifest.Catalog, manifest.Catalog);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__Enabled")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcCollectionProtectsSelectedPreviousInterruptedAndHistoricalInputs()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var selected = DeepSkyIdentity(fixture);
        var previous = selected with { PackageVersion = "openngc-v20260501-p6-s5-r2" };
        var candidate = selected with { PackageVersion = "openngc-v20260501-p6-s5-r4" };
        var deepPaths = fixture.Paths.WithCatalog(selected.CatalogId);
        SafeFileSystem.CreateOwnerDirectory(deepPaths.CatalogRoot);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(deepPaths.CatalogRoot, "versions"));
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(deepPaths.CatalogRoot, "versions", "openngc-v20260501-p6-s5-r3"));
        Directory.CreateSymbolicLink(Path.Combine(deepPaths.CatalogRoot, "current"), "versions/openngc-v20260501-p6-s5-r3");
        var manifest = (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None)) with
        { DeepSkyCatalog = selected, PreviousDeepSkyCatalog = previous, HasPreviousDeepSkySelection = true };
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, selected.PackageVersion, CancellationToken.None));
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, previous.PackageVersion, CancellationToken.None));
        Assert.IsFalse(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, candidate.PackageVersion, CancellationToken.None));
        var credential = Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__RequiredPackageVersion");
        SafeFileSystem.WriteTextAtomic(credential, candidate.PackageVersion);
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, candidate.PackageVersion, CancellationToken.None));
        File.Delete(credential);
        var request = fixture.Request(LifecycleOperationKind.CatalogSelect) with { CatalogFamily = "deep-sky", CatalogVersion = candidate.PackageVersion };
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, fixture.Paths, request.Operation!.Value, manifest, CancellationToken.None);
        operation = await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            DeepSkyTransition = true,
            CandidateDeepSkyCatalog = candidate,
            CandidateCatalog = manifest.Catalog
        }, CancellationToken.None);
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, candidate.PackageVersion, CancellationToken.None));
        await CameraAgentLifecycleManager.PinCatalogsAsync(fixture.Paths, manifest, operation.OperationId, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, manifest with { DeepSkyCatalog = null, PreviousDeepSkyCatalog = null },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            Phase = LifecycleOperationPhase.Completed,
            Status = InstallationStatus.Completed
        }, CancellationToken.None);
        Assert.IsTrue(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, selected.PackageVersion, CancellationToken.None));
        Assert.IsFalse(await CatalogLifecycleManager.IsReferencedAsync(deepPaths, candidate.PackageVersion, CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OpenNgcCredentialDriftIsRefusedBeforeMutation()
    {
        using var fixture = await DeepSkyFixtureAsync();
        var selected = DeepSkyIdentity(fixture);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        DeepSkyCatalogSelection.WriteSelection(fixture.Paths, CameraAgentLifecycleManager.ComposeFrom(fixture.Paths, manifest, result), selected);
        await CameraAgentLifecycleManager.ValidateDeepSkySelectionSettingAsync(fixture.Paths, selected, null, CancellationToken.None);
        SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.ConfigRoot, "secrets", "DeepSkyCatalog__RequiredPackageVersion"), "openngc-v20260501-p6-s5-r2");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ValidateDeepSkySelectionSettingAsync(fixture.Paths, selected, null, CancellationToken.None));
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
    }

    private static async Task<LifecycleFixture> DeepSkyFixtureAsync()
    {
        var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed,
            installedImage: image => image with { CatalogContract = CameraAgentImageContract.IndependentCatalogContract, CatalogManifestVersion = "4" });
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        File.SetUnixFileMode(Path.Combine(fixture.Paths.ConfigRoot, "compose", "instance.env"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var text = await File.ReadAllTextAsync(composePath) + "    volumes:\n      - ${HVO_CATALOG_ROOT:?catalog root required}:/app/catalog:ro\n";
        SafeFileSystem.WriteTextAtomic(composePath, text);
        var manifest = fixture.Manifest with { ComposeModelSha256 = ComposeDeployment.ComputeSha256(text) };
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ResultPath, result with { ComposeModelSha256 = manifest.ComposeModelSha256 }, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        fixture.Runner.ConfigureRuntime(fixture.Paths, manifest.Image.ImmutableReference, manifest.Image.ImageId,
            manifest.Image.ImmutableReference, manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        return fixture;
    }

    private static CatalogInstallationIdentity DeepSkyIdentity(LifecycleFixture fixture)
    {
        var spec = ProductionCatalog.Get("openngc-production");
        return new(spec.CatalogId, spec.PackageVersionPrefix + "1", spec.SchemaVersion, spec.PreprocessingVersion,
            spec.DatabaseSha256, spec.DatabaseLength, spec.RowCount, fixture.Paths.WithCatalog(spec.CatalogId).CatalogRoot,
            new string('a', 64), "local-offline");
    }

    private static async Task<LifecycleResult> DeepSkyTransitionAsync(LifecycleFixture fixture, CatalogInstallationIdentity? candidate,
        FakeLifecycleClient lifecycle, FakeOwnerClient owner, bool rollback = false, bool resume = false, CancellationToken cancellationToken = default)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var request = fixture.Request(rollback ? LifecycleOperationKind.CatalogRollback : LifecycleOperationKind.CatalogSelect)
            with
        { CatalogFamily = "deep-sky", CatalogVersion = rollback ? null : candidate!.PackageVersion, Resume = resume };
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, fixture.Paths, request.Operation!.Value, manifest, cancellationToken);
        return await DeepSkyCatalogSelection.TransitionAsync(request, fixture.Paths, manifest, result,
            CameraAgentLifecycleManager.ComposeFrom(fixture.Paths, manifest, result), new DockerClient(fixture.Runner),
            _ => lifecycle, _ => owner, "lifecycle-token", "verification-token", candidate, operation, cancellationToken);
    }
}
