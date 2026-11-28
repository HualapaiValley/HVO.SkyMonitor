using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Admission and lifecycle operations on a Silo instance through the real lifecycle manager.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SiloLayout_TwoBaselineBuildsUpgradeReinstallAndRollBackWithoutRewindingStore()
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var originalCompose = await File.ReadAllTextAsync(composePath);
        var model = originalCompose + SiloRuntimeProfile.ComposeService + "\n";
        await File.WriteAllTextAsync(composePath, model);
        var manifest = fixture.Manifest with { ComposeModelSha256 = ComposeDeployment.ComputeSha256(model) };
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ManifestPath, manifest,
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        var install = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None, allowSiloLayout: true);
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.ResultPath, install with { ComposeModelSha256 = manifest.ComposeModelSha256 },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        Directory.CreateDirectory(manifest.Silo!.DataRoot);
        var payload = Path.Combine(manifest.Silo.DataRoot, "sample");
        await File.WriteAllTextAsync(payload, "initial");
        var lifecycle = new FakeLifecycleClient();

        var upgraded = await RunAsync(UpgradeRequest(fixture));
        Assert.AreEqual(CandidateImageId, upgraded.Image!.ImageId);
        await File.AppendAllTextAsync(payload, "-after-upgrade");
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, (await RunAsync(fixture.Request(LifecycleOperationKind.Uninstall))).LifecycleCondition);
        Assert.AreEqual(CandidateImageId, (await RunAsync(fixture.Request(LifecycleOperationKind.Reinstall))).Image!.ImageId);
        var rollback = await RunAsync(fixture.Request(LifecycleOperationKind.Rollback));

        Assert.AreEqual("completed", rollback.Outcome);
        Assert.AreEqual(manifest.Image, rollback.Image);
        Assert.AreEqual("initial-after-upgrade", await File.ReadAllTextAsync(payload));
        var settled = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None, allowSiloLayout: true);
        Assert.AreEqual(manifest.Silo, settled.Silo);
        StringAssert.Contains(await File.ReadAllTextAsync(composePath), SiloRuntimeProfile.ComposeService, StringComparison.Ordinal);

        Task<LifecycleResult> RunAsync(LifecycleRequest request) => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("cameraagent-state-v2", false)]
    [DataRow("foreign-state-v9", false)]
    [DataRow(null, false)]
    [DataRow(SiloRuntimeProfile.StateContract, true)]
    public async Task PreflightAndLifecycleRefuseOtherContractsAndPreBaselineSiloBeforeMutation(string? contract, bool preBaselineSilo)
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Uninstalled);
        var document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(fixture.Paths.ManifestPath))!;
        document["stateContract"] = contract;
        if (preBaselineSilo)
        {
            // The old Silo manifest already names the store binding. Only the baseline
            // manifest and image identity identify a new-contract lifecycle installation.
            document["schemaVersion"] = 1;
            document["image"]!.AsObject().Remove("stateContract");
        }
        await File.WriteAllTextAsync(fixture.Paths.ManifestPath, document.ToJsonString());
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var preflight = new CameraAgentStatePreflightRequest(fixture.InstanceId, fixture.Root, null, Json: true)
        {
            AllowTestProductRoot = true
        };

        await RefusedAsync(() => CameraAgentStatePreflightManager.ExecuteAsync(preflight, fixture.Runner, CancellationToken.None));
        await RefusedAsync(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall), fixture.Runner, null, null,
            fixture.Uid, fixture.Gid, CancellationToken.None));
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "refused lifecycle changed the product root");

        static async Task RefusedAsync(Func<Task> execute)
        {
            var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(execute);
            Assert.IsTrue(refusal.IsRefusal);
            StringAssert.Contains(refusal.Message, "fresh install required", StringComparison.Ordinal);
            StringAssert.Contains(refusal.Message, "0.10.0-beta.1 or later", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Backup, InstanceLifecycleCondition.Installed, false, false)]
    public async Task SiloLayout_UnsupportedOperation_RefusesBeforeMutation(
        LifecycleOperationKind kind, InstanceLifecycleCondition condition, bool plan, bool resume)
    {
        using var fixture = await SiloFixtureAsync(condition);
        var request = fixture.Request(kind) with { DryRun = plan, Resume = resume };
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var lifecycle = new FakeLifecycleClient();

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        Assert.AreEqual(CameraAgentLifecycleManager.SiloLayoutUnsupported, refused.Message);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), $"{kind} changed a Silo-layout instance");
        Assert.AreEqual(0, fixture.Runner.InvocationCount);
        Assert.AreEqual(0, lifecycle.PauseCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SiloLayout_ManifestIsRefusedByDefaultAndValidatedWhenAdmitted()
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None));
        Assert.IsTrue(refused.IsRefusal);
        var admitted = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None, allowSiloLayout: true);
        Assert.AreEqual(SiloRuntimeProfile.ComponentSchemaVersion, admitted.ComponentSchemaVersion);

        // A Silo block that names another bucket, or one on a filesystem-layout manifest, is invalid either way.
        Assert.ThrowsExactly<InstallerException>(() => CameraAgentLifecycleManager.ValidateCanonicalManifest(
            admitted with { Silo = admitted.Silo! with { Bucket = SiloRuntimeProfile.BucketFor(Guid.NewGuid()) } }, allowSiloLayout: true));
        Assert.ThrowsExactly<InstallerException>(() => CameraAgentLifecycleManager.ValidateCanonicalManifest(
            fixture.Manifest with { Silo = admitted.Silo }));
        Assert.ThrowsExactly<InstallerException>(() => CameraAgentLifecycleManager.ValidateCanonicalManifest(
            admitted with { Silo = admitted.Silo! with { DiskSyncOnWrite = false } }, allowSiloLayout: true));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SiloLayout_UninstallThenPurge_RemoveContainersThenEveryInstanceRootIncludingTheSiloDataRoot()
    {
        using var fixture = await SiloFixtureAsync(InstanceLifecycleCondition.Installed);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        var dataRoot = SiloRuntimeProfile.DataRoot(fixture.Paths);
        // Silo writes under the container's 0022 umask; the installer creates the data root owner-only.
        const UnixFileMode Directory0755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                           UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Directory.CreateDirectory(dataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(dataRoot, ".minio.sys"), Directory0755);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, ".minio.sys", "format.json"), "{}");
        var lifecycle = new FakeLifecycleClient();

        var uninstalled = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", uninstalled.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, uninstalled.LifecycleCondition);
        Assert.AreEqual(1, fixture.Runner.ComposeDownCount);
        Assert.IsTrue(File.Exists(Path.Combine(dataRoot, ".minio.sys", "format.json")), "Uninstall keeps the Silo data root.");

        var purged = await CameraAgentLifecycleManager.ExecuteAsync(
            PurgeRequest(fixture), fixture.Runner, _ => lifecycle, null, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", purged.Outcome);
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot));
        Assert.IsFalse(Directory.Exists(dataRoot));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SiloLayout_ComposeModel_JoinsOnlyTheInternalNetworkAndKeepsEveryLineWellFormed()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        var request = new InstallRequest
        {
            AllowTestProductRoot = true,
            ProductRoot = fixture.Root,
            InstanceId = fixture.InstanceId,
            FriendlyName = fixture.Manifest.FriendlyName,
            StorageLayout = CameraAgentStorageLayout.Silo
        };
        var firstBoot = Guid.NewGuid();
        // The verified local image ID the image phase resolved: here the one a staged amd64 archive loads as.
        var imageId = SiloImagePin.Platforms[0].ManifestDigest;
        var silo = new SiloComposeInputs(imageId, "linux/amd64", SiloRuntimeProfile.DataRoot(fixture.Paths), firstBoot);
        var rendered = ComposeDeployment.Render(request, fixture.Paths, fixture.InstanceId, fixture.ApplicationIdentity,
            fixture.Uid, fixture.Gid, fixture.Manifest.Image.ImageId, fixture.Manifest.Catalog.PackageVersion, result.PasswordFile,
            "verification-token", "lifecycle-token", false, fixture.Manifest.Resources, silo: silo);
        var lines = rendered.ComposeText.Split('\n');
        // Each service and top-level key starts its own line; nothing is joined onto a preceding value.
        CollectionAssert.Contains(lines, "    networks:");
        CollectionAssert.Contains(lines, "      silo: {}");
        CollectionAssert.Contains(lines, "    read_only: true");
        CollectionAssert.Contains(lines, "  silo:");
        CollectionAssert.Contains(lines, "networks:");
        CollectionAssert.Contains(lines, "    internal: true");
        Assert.IsFalse(lines.Any(static line => line.Contains("{}    ", StringComparison.Ordinal) || line.Contains("\"3\"networks", StringComparison.Ordinal)));
        Assert.AreEqual(1, lines.Count(static line => line == "    ports:"), "Only CameraAgent publishes a port.");
        StringAssert.Contains(rendered.EnvironmentText, $"HVO_SILO_IMAGE={imageId}\n", StringComparison.Ordinal);
        StringAssert.Contains(rendered.EnvironmentText, "HVO_SILO_PLATFORM=linux/amd64\n", StringComparison.Ordinal);
        CollectionAssert.Contains(lines, "    platform: ${HVO_SILO_PLATFORM:?silo platform required}");
        CollectionAssert.Contains(lines, "      io.hvo.skymonitor.network: silo");
        StringAssert.Contains(rendered.EnvironmentText, $"HVO_SILO_DATA_ROOT={SiloRuntimeProfile.DataRoot(fixture.Paths)}\n", StringComparison.Ordinal);
        Assert.AreEqual(firstBoot.ToString("D"), rendered.Settings["CameraAgent__FirstBootPause__Id"]);
        Assert.IsFalse(rendered.Settings.ContainsKey(SiloRuntimeProfile.WriterSecretKeySetting), "The writer secret is written only after bootstrap.");

        var filesystem = ComposeDeployment.Render(request with { StorageLayout = CameraAgentStorageLayout.Filesystem }, fixture.Paths,
            fixture.InstanceId, fixture.ApplicationIdentity, fixture.Uid, fixture.Gid, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Catalog.PackageVersion, result.PasswordFile, "verification-token", "lifecycle-token", false, fixture.Manifest.Resources);
        Assert.IsFalse(filesystem.ComposeText.Contains("silo", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(filesystem.Settings.Keys.Any(static key => key.Contains("Silo", StringComparison.Ordinal) || key.Contains("FirstBoot", StringComparison.Ordinal)));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned fixture shares the created fixture's root and runner and is disposed by the caller.")]
    private static async Task<LifecycleFixture> SiloFixtureAsync(
        InstanceLifecycleCondition condition, string catalogId = HygV44.CatalogId, bool seedCatalogSelection = true)
    {
        var fixture = await LifecycleFixture.CreateAsync(
            condition, seedCatalogSelection: seedCatalogSelection, catalogId: catalogId);
        var manifest = fixture.Manifest;
        var instanceId = manifest.InstanceId;
        var storeId = SiloRuntimeProfile.StoreIdFor(instanceId);
        var bucket = SiloRuntimeProfile.BucketFor(instanceId);
        var silo = new SiloInstallationIdentity(
            SiloRuntimeProfile.StateContract,
            SiloRuntimeProfile.ImageReference,
            SiloRuntimeProfile.Release,
            SiloRuntimeProfile.IndexDigest,
            SiloRuntimeProfile.PlatformDigestFor(manifest.DockerDaemon.Architecture)!,
            SiloRuntimeProfile.IndexDigest,
            SiloRuntimeProfile.SourceRevision,
            SiloRuntimeProfile.DataRoot(fixture.Paths),
            "ext4",
            "8:1",
            storeId,
            bucket,
            SiloRuntimeProfile.StorageBindingIdFor(instanceId, storeId, bucket),
            SiloRuntimeProfile.WriterGeneration,
            "AKIAEXAMPLEWRITER001",
            "3a70979c-3128-47c3-8653-7cdc283f4f05",
            new string('a', 64),
            "4b81a8ad-4239-48d4-9764-8ded394f5f16",
            new string('b', 64),
            Guid.NewGuid(),
            DiskSyncOnWrite: true);
        var converted = manifest with
        {
            ComponentSchemaVersion = SiloRuntimeProfile.ComponentSchemaVersion,
            Image = manifest.Image with { FirstBootContract = SiloInstallation.FirstBootContract },
            ComposeTemplateVersion = SiloRuntimeProfile.ComposeTemplateVersion,
            PreviousImage = null,
            PreviousComposeModelSha256 = null,
            Silo = silo
        };
        await File.WriteAllTextAsync(fixture.Paths.ManifestPath,
            JsonSerializer.Serialize(converted, DeploymentJsonContext.Default.InstanceManifest));
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await File.WriteAllTextAsync(fixture.Paths.ResultPath, JsonSerializer.Serialize(
            result with { ComposeTemplateVersion = SiloRuntimeProfile.ComposeTemplateVersion, Image = converted.Image },
            DeploymentJsonContext.Default.InstallationResult));
        return fixture.WithManifest(converted);
    }
}
