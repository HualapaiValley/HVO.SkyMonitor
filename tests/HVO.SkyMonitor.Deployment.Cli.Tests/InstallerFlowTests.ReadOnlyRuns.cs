using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// What an install plan, and a re-run with nothing to change, leave on disk (#1315 PR-1355-R1): nothing at all, not even
/// a temporary file, a lock or a file rewritten with the same content (F1, F3). A plan admits exactly what the run admits
/// (F2). A refusal exits 3 only when nothing was changed, and is never journaled as a failed installation (F4). A limits
/// change is restored, or settled by the next run, wherever it is interrupted (F5).
/// </summary>
public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_PlanOfAFreshInstall_CreatesNoFileAnywhereNotEvenATemporaryOne()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("4d5e6f70-8192-4a3b-9c4d-5e6f708192a3"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        try
        {
            var (planned, created) = await FileSystemActivity.WithPrivateTemporaryDirectoryAsync(
                () => InstallResourceRequestAsync(request with { DryRun = true }, runner, new InstallerOwnerClient()));

            Assert.AreEqual(InstallationOutcome.Planned, planned.Outcome);
            Assert.IsTrue(planned.Plan!.HasChanges);
            Assert.IsEmpty(created, "a plan created temporary files: " + string.Join(", ", created));
            Assert.IsFalse(Directory.Exists(root));
            Assert.AreEqual(0, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// <c>install.sh</c>'s default hand-over (#1311): a release from the registry, which Docker does not hold before the
    /// install pulls it. The plan reads and checks the image from the registry, as the install does before it pulls,
    /// and pulls and writes nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_PlanOfARegistryReleaseDockerDoesNotHoldYet_IsPlannedFromTheRegistryAndPullsNothing()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-registry-plan-{Guid.NewGuid():N}");
        var platform = $"linux/{ReleaseAcquirer.HostImageArchitecture()}";
        using var release = ReleaseFixture.Create(root);
        var instanceId = Guid.Parse("d6e7f809-1a2b-43c4-85d6-e7f8091a2b3c");
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with
        {
            ImageSource = ReleaseImageSource.Registry,
            DryRun = true
        };
        var runner = new InstallerProcessRunner(release.ConfigDigest(platform), root, instanceId)
        {
            Labels = release.Labels(),
            RepoDigests = [release.DigestReference],
            Held = [],
            Registry = new FakeRegistry(release.RegistryContent)
        };
        try
        {
            var (planned, created) = await FileSystemActivity.WithPrivateTemporaryDirectoryAsync(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe));

            Assert.AreEqual(InstallationOutcome.Planned, planned.Outcome);
            Assert.AreEqual(release.ConfigDigest(platform), planned.Image.ImageId);
            // The index, the platform manifest and the configuration are read; nothing is pulled.
            Assert.IsTrue(runner.Imports.Count > 0 && runner.Imports.All(static import => import == "registry"), string.Join(", ", runner.Imports));
            Assert.AreEqual(0, runner.PullCount);
            Assert.IsEmpty(runner.Held!);
            Assert.IsEmpty(created);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "cameraagents")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// <c>docker info</c> of a daemon backed by the containerd image store, which names an image by its index digest
    /// rather than by its configuration digest.
    /// </summary>
    private const string ContainerdDockerInfo =
        "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"," +
        "\"Driver\":\"overlayfs\",\"DriverStatus\":[[\"driver-type\",\"io.containerd.snapshotter.v1\"]]," +
        "\"MemoryLimit\":true,\"CpuCfsQuota\":true,\"PidsLimit\":true,\"CgroupVersion\":\"2\",\"DockerRootDir\":\"/var/lib/docker\"," +
        "\"NCPU\":4,\"MemTotal\":8442572800,\"Warnings\":null}";

    /// <summary>
    /// The plan names the image the install will report (#1384). Under Docker's containerd image store the install, its
    /// status and the install record name a release's image by the release's image digest, which is the ID Docker gives
    /// it; a plan, which imports nothing, used to name it by its configuration digest instead. From the archive or the
    /// registry alike.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallAsync_PlanUnderTheContainerdImageStore_NamesTheImageTheInstallReports(bool registry)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-release-containerd-plan-{Guid.NewGuid():N}");
        using var release = ReleaseFixture.Create(root);
        var instanceId = Guid.Parse("e7f8091a-2b3c-44d5-96e7-f8091a2b3c4d");
        var request = ReleaseRequest(root, bundle, instanceId, release.Folder) with
        {
            ImageSource = registry ? ReleaseImageSource.Registry : ReleaseImageSource.Archive
        };
        var runner = new InstallerProcessRunner(release.Digest, root, instanceId)
        {
            DockerInfo = ContainerdDockerInfo,
            Labels = release.Labels(),
            RepoDigests = [release.DigestReference],
            Held = [],
            Registry = new FakeRegistry(release.RegistryContent)
        };
        try
        {
            var planned = await CameraAgentInstaller.InstallAsync(
                request with { DryRun = true }, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);
            Assert.AreEqual(0, runner.LoadCount + runner.PullCount, "a plan imports nothing");
            var installed = await CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            Assert.AreEqual(InstallationOutcome.Planned, planned.Outcome);
            Assert.AreEqual(InstallationOutcome.Installed, installed.Outcome);
            Assert.AreEqual(release.Digest, installed.Image.ImageId);
            Assert.AreEqual(installed.Image.ImageId, planned.Image.ImageId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_PlanOfAReRun_CreatesNoFileDirectoryLockOrTemporaryFile()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("5e6f7081-92a3-4b4c-8d5e-6f708192a3b4"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            _ = await InstallResourceRequestAsync(request, runner, owner);
            var before = FileSystemActivity.Snapshot(root);
            var composeUps = runner.ComposeUpCount;

            var (same, sameCreated) = await FileSystemActivity.WithPrivateTemporaryDirectoryAsync(
                () => InstallResourceRequestAsync(request with { DryRun = true }, runner, owner));
            var (changed, changedCreated) = await FileSystemActivity.WithPrivateTemporaryDirectoryAsync(
                () => InstallResourceRequestAsync(request with { DryRun = true, Memory = "6g" }, runner, owner));

            Assert.IsFalse(same.Plan!.HasChanges);
            Assert.IsTrue(changed.Plan!.HasChanges);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "a plan changed the product root");
            Assert.IsEmpty(sameCreated, "a plan created temporary files: " + string.Join(", ", sameCreated));
            Assert.IsEmpty(changedCreated, "a plan created temporary files: " + string.Join(", ", changedCreated));
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunWithNothingToChange_LeavesEveryFileUntouchedAndDeploysNothing()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("6f708192-a3b4-4c5d-9e6f-708192a3b4c5"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            _ = await InstallResourceRequestAsync(request, runner, owner);
            var before = FileSystemActivity.Snapshot(root);
            var composeUps = runner.ComposeUpCount;
            var verifications = owner.Verifications;

            var (again, created) = await FileSystemActivity.WithPrivateTemporaryDirectoryAsync(
                () => InstallResourceRequestAsync(request, runner, owner));

            Assert.IsFalse(again.Plan!.HasChanges, string.Join("; ", again.Plan.Items));
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "a re-run with nothing to change wrote to the product root");
            Assert.IsEmpty(created, "a re-run with nothing to change created temporary files: " + string.Join(", ", created));
            Assert.AreEqual(composeUps, runner.ComposeUpCount, "a re-run with nothing to change ran Compose up");
            Assert.AreEqual(verifications + 1, owner.Verifications, "the instance is still verified before nothing is changed");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunAndItsPlanWithAnUnreadableIdentityDatabase_AreBothRefusedWithNothingChanged()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("708192a3-b4c5-4d6e-8f70-8192a3b4c5d6"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var identity = Path.Combine(first.StateRoot, "identity", "cameraagent_identity.db");
            await File.WriteAllBytesAsync(identity, new byte[4096]);
            File.SetUnixFileMode(identity, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var before = FileSystemActivity.Snapshot(root);
            var composeUps = runner.ComposeUpCount;

            var planned = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request with { DryRun = true }, runner, owner));
            var run = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(request, runner, owner));

            Assert.IsTrue(planned.IsRefusal, planned.Message);
            Assert.IsTrue(run.IsRefusal, run.Message);
            StringAssert.StartsWith(planned.Message, "CameraAgent state preflight rejected", StringComparison.Ordinal);
            StringAssert.StartsWith(run.Message, "CameraAgent state preflight rejected", StringComparison.Ordinal);
            // A refusal says nothing changed, so nothing did: no report, no configuration, and the installation stays
            // completed rather than being journaled as failed.
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(root), "a refusal changed the product root");
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
            var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(first.StateRoot, "deployment", "installation-state.json")))!;
            Assert.AreEqual("Completed", (string?)state["status"]);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_RefusalAfterTheInstallHasWritten_IsAFailureNotARefusal()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("8192a3b4-c5d6-4e7f-9081-92a3b4c5d6e7"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        var original = CameraAgentInstaller.StepCompleted;
        try
        {
            // A gate that decides not to go ahead once configuration is written can no longer say nothing changed.
            CameraAgentInstaller.StepCompleted = step =>
            {
                if (step == "configuration-written") throw InstallerException.Refused("injected late refusal");
            };

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request, runner, new InstallerOwnerClient()));

            Assert.IsFalse(failure.IsRefusal);
            StringAssert.Contains(failure.Message, "injected late refusal", StringComparison.Ordinal);
            var state = JsonNode.Parse(await File.ReadAllTextAsync(
                Path.Combine(root, "cameraagents", request.InstanceId.Value.ToString("D"), "state", "deployment", "installation-state.json")))!;
            Assert.AreEqual("Failed", (string?)state["status"], "a failure after writes is journaled, so a re-run resumes it");
        }
        finally
        {
            CameraAgentInstaller.StepCompleted = original;
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_LimitsChangeCancelledRightAfterItIsVerified_RestoresTheRecordedLimits()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("92a3b4c5-d6e7-4f80-8192-a3b4c5d6e7f8"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        var original = CameraAgentInstaller.StepCompleted;
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var composeBefore = await File.ReadAllTextAsync(composePath);
            var manifestBefore = await File.ReadAllTextAsync(manifestPath);
            using var cancellation = new CancellationTokenSource();
            CameraAgentInstaller.StepCompleted = step =>
            {
                if (step == "limits-verified") cancellation.Cancel();
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentInstaller.InstallAsync(
                request with { Memory = "6g" }, runner, _ => owner, RuntimeUid, RuntimeGid, cancellation.Token,
                portAvailabilityProbe: new RecordingPortProbe().Probe, hostEnvironment: new FakeHostEnvironment()));

            // The container was moved back, and the records never left the recorded limits: they agree.
            Assert.AreEqual(composeBefore, await File.ReadAllTextAsync(composePath));
            Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(manifestPath));
            Assert.AreEqual(4 * ResourceGibibyte, CameraAgentContainerResources.TryReadLimits(runner.LastComposeUp!)!.MemoryBytes);
        }
        finally
        {
            CameraAgentInstaller.StepCompleted = original;
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_LimitsChangeCancelledAtRecordPublication_IsCommittedAndTheNextRunSettlesTheRecord()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("a3b4c5d6-e7f8-4091-82a3-b4c5d6e7f809"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        var original = CameraAgentInstaller.StepCompleted;
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var resultPath = Path.Combine(first.StateRoot, "deployment", "installation-result.json");
            var location = InstallRecordLocation.For(root, request.ProductionProductRoot);
            using var cancellation = new CancellationTokenSource();
            CameraAgentInstaller.StepCompleted = step =>
            {
                if (step == "limits-result-written") cancellation.Cancel();
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentInstaller.InstallAsync(
                request with { Memory = "6g" }, runner, _ => owner, RuntimeUid, RuntimeGid, cancellation.Token,
                portAvailabilityProbe: new RecordingPortProbe().Probe, hostEnvironment: new FakeHostEnvironment()));
            CameraAgentInstaller.StepCompleted = original;

            // The manifest is the commit point: the container and the instance's own records agree on the new limits.
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None);
            var result = await CameraAgentLifecycleManager.ReadResultAsync(resultPath, CancellationToken.None);
            Assert.AreEqual(6 * ResourceGibibyte, manifest.Resources!.MemoryBytes);
            Assert.AreEqual(manifest.ComposeModelSha256, result.ComposeModelSha256);
            Assert.AreEqual(6 * ResourceGibibyte, CameraAgentContainerResources.TryReadLimits(runner.LastComposeUp!)!.MemoryBytes);
            Assert.AreEqual(4 * ResourceGibibyte, (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single().Resources!.MemoryBytes);

            // The next run, given nothing new, settles the install record and nothing else.
            var composeUps = runner.ComposeUpCount;
            var settled = await InstallResourceRequestAsync(request, runner, owner);

            Assert.AreEqual(DeploymentPlanAction.Change, settled.Plan!.Items.Single(static item => item.Kind == "record").Action);
            Assert.IsTrue(settled.Plan.Items.Where(static item => item.Kind != "record").All(static item => item.Action == DeploymentPlanAction.Unchanged));
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
            Assert.AreEqual(6 * ResourceGibibyte, (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single().Resources!.MemoryBytes);
        }
        finally
        {
            CameraAgentInstaller.StepCompleted = original;
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_LimitsChangeLostBeforeItsManifest_IsSettledByTheNextRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("b4c5d6e7-f809-4192-a3b4-c5d6e7f8091a"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var recordedCompose = await File.ReadAllTextAsync(composePath);
            // A power loss after the container was moved to 6 GiB and before the manifest recorded it: no handler ran.
            await File.WriteAllTextAsync(composePath, recordedCompose.Replace("mem_limit: 4G", "mem_limit: 6G", StringComparison.Ordinal));

            var planned = await InstallResourceRequestAsync(request with { DryRun = true }, runner, owner);
            var settled = await InstallResourceRequestAsync(request, runner, owner);

            // The records decide: the container is put back to the limits they hold, and the plan said so.
            Assert.AreEqual(DeploymentPlanAction.Change, planned.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(DeploymentPlanAction.Change, settled.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(recordedCompose, await File.ReadAllTextAsync(composePath));
            Assert.AreEqual(4 * ResourceGibibyte, (await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None)).Resources!.MemoryBytes);
            Assert.AreEqual(4 * ResourceGibibyte, CameraAgentContainerResources.TryReadLimits(runner.LastComposeUp!)!.MemoryBytes);
            Assert.IsFalse((await InstallResourceRequestAsync(request, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// A limits change whose verification fails, and whose restore Docker then fails, leaves the container on the new
    /// limits while the manifest and the Compose file hold the old ones (#1315 PR-1355-R2 R2-4). The next plan and run
    /// read the limits the container actually has, report the difference, and redeploy it from the records.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_LimitsChangeWhoseRestoreFails_IsRedeployedFromTheRecordsByTheNextRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("e7f8091a-2b3c-44d5-96e7-f8091a2b3c4d"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new VerificationFailingOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            var recordedCompose = await File.ReadAllTextAsync(composePath);
            // The instance is verified where it stands, then at 6 GiB, which fails; the restore's own start then fails.
            owner.FailOnVerification = owner.Verifications + 2;
            runner.FailComposeUpNumber = runner.ComposeUpCount + 2;

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner));

            StringAssert.Contains(failure.Message, "restoring the recorded limits also failed", StringComparison.Ordinal);
            Assert.AreEqual(recordedCompose, await File.ReadAllTextAsync(composePath));
            Assert.AreEqual(6 * ResourceGibibyte, CameraAgentContainerResources.TryReadLimits(runner.LastComposeUp!)!.MemoryBytes);

            var planned = await InstallResourceRequestAsync(request with { DryRun = true }, runner, owner);
            var settled = await InstallResourceRequestAsync(request, runner, owner);

            Assert.AreEqual(DeploymentPlanAction.Change, planned.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(DeploymentPlanAction.Change, settled.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(4 * ResourceGibibyte, CameraAgentContainerResources.TryReadLimits(runner.LastComposeUp!)!.MemoryBytes);
            Assert.IsFalse((await InstallResourceRequestAsync(request, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// A fresh plan admits the catalog bundle exactly as the run does, its manifest included (#1315 PR-1355-R2 R2-2): a
    /// manifest of a version the installer does not read, or one that is not JSON, is refused by both, the same way, and
    /// before either writes anything.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("unsupported")]
    [DataRow("malformed")]
    public async Task InstallAsync_CatalogBundleWithABadManifest_IsRefusedByThePlanAsByTheRun(string fault)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("f8091a2b-3c4d-45e6-87f8-091a2b3c4d5e"));
        var copy = Path.Combine(Path.GetTempPath(), $"hvo-catalog-bundle-{Guid.NewGuid():N}");
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.EnumerateFiles(bundle))
            {
                // File.Copy keeps the source's mode, and the shared bundle's files may be read-only (#1374): only the
                // copy is made writable, so the test can damage its manifest without touching the shared bundle.
                var copied = Path.Combine(copy, Path.GetFileName(file));
                File.Copy(file, copied);
                File.SetUnixFileMode(copied, File.GetUnixFileMode(copied) | UnixFileMode.UserWrite);
            }
            var manifestPath = Path.Combine(copy, "manifest.json");
            if (fault == "unsupported")
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
                manifest["manifestVersion"] = 1;
                await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            }
            else
            {
                await File.WriteAllTextAsync(manifestPath, "{\"manifestVersion\": 2,");
            }
            var bad = request with { CatalogBundle = copy };

            var planned = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(bad with { DryRun = true }, runner, new InstallerOwnerClient()));
            var run = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(bad, runner, new InstallerOwnerClient()));

            Assert.AreEqual(run.Message, planned.Message);
            Assert.AreEqual(run.IsRefusal, planned.IsRefusal);
            Assert.IsFalse(Directory.Exists(root), "the catalog is admitted before anything is written");
            Assert.AreEqual(0, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(copy);
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallRecord_HoldsNoSecretBearingFieldOrValue()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("c5d6e7f8-091a-42a3-b4c5-d6e7f8091a2b"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        try
        {
            var installed = await InstallResourceRequestAsync(request, runner, new InstallerOwnerClient());
            var record = await File.ReadAllTextAsync(InstallRecordLocation.For(root, request.ProductionProductRoot).Path);
            string Secret(string relative) => File.ReadAllText(Path.Combine(installed.ConfigRoot, relative)).Trim();

            foreach (var name in JsonNames(JsonNode.Parse(record)!))
            {
                foreach (var word in new[] { "password", "secret", "token", "email", "key", "credential" })
                {
                    Assert.IsFalse(name.Contains(word, StringComparison.OrdinalIgnoreCase), $"the install record has a field named '{name}'");
                }
            }
            foreach (var value in new[]
                     {
                         Secret("installation-verification/token"),
                         Secret("lifecycle-control/token"),
                         Secret("owner-bootstrap/temporary-password"),
                         installed.PasswordFile,
                         request.OwnerEmail
                     })
            {
                Assert.DoesNotContain(value, record, "the install record holds a secret or personal value");
            }
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    private static IEnumerable<string> JsonNames(JsonNode node) => node switch
    {
        JsonObject value => value.SelectMany(static member => member.Value is null
            ? [member.Key]
            : JsonNames(member.Value).Prepend(member.Key)),
        JsonArray items => items.Where(static item => item is not null).SelectMany(static item => JsonNames(item!)),
        _ => []
    };
}
