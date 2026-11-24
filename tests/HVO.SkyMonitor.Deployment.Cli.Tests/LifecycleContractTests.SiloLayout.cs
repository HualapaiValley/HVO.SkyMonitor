using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The Silo-layout fence of #1966 through the real lifecycle manager: every operation the first Silo release does not
/// support refuses a Silo-layout instance before mutation, and uninstall and purge are admitted.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Rollback, InstanceLifecycleCondition.Installed)]
    [DataRow(LifecycleOperationKind.Reinstall, InstanceLifecycleCondition.Uninstalled)]
    [DataRow(LifecycleOperationKind.Upgrade, InstanceLifecycleCondition.Installed)]
    [DataRow(LifecycleOperationKind.CatalogRollback, InstanceLifecycleCondition.Installed)]
    public async Task SiloLayout_UnsupportedOperation_RefusesBeforeMutation(LifecycleOperationKind kind, InstanceLifecycleCondition condition)
    {
        using var fixture = await SiloFixtureAsync(condition);
        var request = fixture.Request(kind);
        if (kind == LifecycleOperationKind.Upgrade)
            request = request with { ImageReference = $"sha256:{new string('5', 64)}", NoDownload = true };
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var lifecycle = new FakeLifecycleClient();

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        Assert.AreEqual(CameraAgentLifecycleManager.SiloLayoutUnsupported, refused.Message);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), $"{kind} changed a Silo-layout instance");
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(0, fixture.Runner.ComposeDownCount);
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
    private static async Task<LifecycleFixture> SiloFixtureAsync(InstanceLifecycleCondition condition)
    {
        var fixture = await LifecycleFixture.CreateAsync(condition);
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
            ComposeTemplateVersion = SiloRuntimeProfile.ComposeTemplateVersion,
            PreviousImage = null,
            PreviousComposeModelSha256 = null,
            Silo = silo
        };
        await File.WriteAllTextAsync(fixture.Paths.ManifestPath,
            JsonSerializer.Serialize(converted, DeploymentJsonContext.Default.InstanceManifest));
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await File.WriteAllTextAsync(fixture.Paths.ResultPath, JsonSerializer.Serialize(
            result with { ComposeTemplateVersion = SiloRuntimeProfile.ComposeTemplateVersion },
            DeploymentJsonContext.Default.InstallationResult));
        return fixture.WithManifest(converted);
    }
}
