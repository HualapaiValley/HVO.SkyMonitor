using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// A re-run of a completed install (install contract sections 5 and 7, #1315): with the same answers it reports every
/// item unchanged and changes nothing; <c>--plan</c> reports what it would do without changing anything; new
/// <c>--cpus</c> or <c>--memory</c> change the container limits, the host check still refuses limits it cannot enforce,
/// and a failed change restores the recorded limits; anything else that differs from the installed instance is refused
/// with nothing changed. Each install also settles the install record.
/// </summary>
public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunWithTheSameAnswers_ReportsNothingToChangeAndWritesNothing()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("1f0e2d3c-4b5a-4968-8776-655443322110"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var records = Records(first, root);
            var before = await Snapshot(records);

            Assert.IsNotNull(first.Plan);
            Assert.IsTrue(first.Plan.Items.Any(static item => item.Action == DeploymentPlanAction.Create && item.Kind == "container"));
            Assert.AreEqual(DeploymentPlanAction.Create, first.Plan.Items.Single(static item => item.Kind == "record").Action);
            var entry = (await InstallRecordStore.ReadAsync(InstallRecordLocation.For(root, request.ProductionProductRoot), CancellationToken.None))!
                .Instances.Single();
            Assert.AreEqual(request.InstanceId, entry.InstanceId);
            Assert.AreEqual(first.Image.ImageId, entry.ImageId);
            Assert.AreEqual(CameraAgentContainerResources.Default, entry.Resources);

            var second = await InstallResourceRequestAsync(request, runner, owner);

            Assert.IsNotNull(second.Plan);
            Assert.IsTrue(second.Plan.Items.All(static item => item.Action == DeploymentPlanAction.Unchanged), string.Join("; ", second.Plan.Items));
            Assert.AreEqual("Nothing to change: the instance is installed as the answers describe.", second.Plan.Summary);
            CollectionAssert.AreEqual(before, await Snapshot(records), "a re-run that changes nothing writes no record");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_PlanOfAFreshInstall_ListsWhatItWouldCreateAndCreatesNothing()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("2a1b0c9d-8e7f-4a6b-9c5d-4e3f2a1b0c9d"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        try
        {
            var planned = await InstallResourceRequestAsync(request with { DryRun = true, Cpus = 3m }, runner, new InstallerOwnerClient());

            Assert.AreEqual(InstallationOutcome.Planned, planned.Outcome);
            var plan = planned.Plan!;
            Assert.IsTrue(plan.HasChanges);
            StringAssert.StartsWith(plan.Summary, "7 to create, 0 to change", StringComparison.Ordinal);
            StringAssert.Contains(
                plan.Items.Single(static item => item.Kind == "container").Detail!,
                "3 CPUs, 4 GiB of memory and 256 PIDs",
                StringComparison.Ordinal);
            Assert.AreEqual(
                Path.Combine(root, "operations", "install.json"),
                plan.Items.Single(static item => item.Kind == "record").Target);
            Assert.IsFalse(Directory.Exists(root));
            Assert.AreEqual(0, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_PlanOfAReRun_SaysWhatWouldChangeAndChangesNothing()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("3b2c1d0e-9f8a-4b7c-8d6e-5f4a3b2c1d0e"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var records = Records(first, root);
            var before = await Snapshot(records);
            var composeUps = runner.ComposeUpCount;

            var same = await InstallResourceRequestAsync(request with { DryRun = true }, runner, owner);
            var changed = await InstallResourceRequestAsync(request with { DryRun = true, Memory = "6g" }, runner, owner);

            Assert.AreEqual(InstallationOutcome.Planned, same.Outcome);
            Assert.AreEqual("Nothing to change: the instance is installed as the answers describe.", same.Plan!.Summary);
            Assert.AreEqual(DeploymentPlanAction.Change, changed.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(DeploymentPlanAction.Change, changed.Plan.Items.Single(static item => item.Kind == "record").Action);
            StringAssert.Contains(
                changed.Plan.Items.Single(static item => item.Kind == "container").Detail!,
                "2 CPUs, 6 GiB of memory and 256 PIDs",
                StringComparison.Ordinal);
            CollectionAssert.AreEqual(before, await Snapshot(records));
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunWithNewLimits_RecreatesTheContainerAndRecordsThem()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("4c3d2e1f-0a9b-4c8d-9e7f-6a5b4c3d2e1f"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            var statePath = Path.Combine(first.StateRoot, "deployment", "installation-state.json");
            var state = await File.ReadAllTextAsync(statePath);
            var composeUps = runner.ComposeUpCount;

            var changed = await InstallResourceRequestAsync(request with { Cpus = 3m, Memory = "6g" }, runner, owner);

            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None);
            var result = await CameraAgentLifecycleManager.ReadResultAsync(Path.Combine(first.StateRoot, "deployment", "installation-result.json"), CancellationToken.None);
            Assert.AreEqual(new ContainerResourceLimits(3m, 6 * ResourceGibibyte, 256, true, true), manifest.Resources);
            StringAssert.Contains(await File.ReadAllTextAsync(composePath), "    cpus: \"3\"\n    mem_limit: 6G\n    pids_limit: 256\n", StringComparison.Ordinal);
            Assert.AreEqual(manifest.ComposeModelSha256, result.ComposeModelSha256);
            Assert.AreNotEqual(first.ComposeModelSha256, result.ComposeModelSha256);
            Assert.AreEqual(state, await File.ReadAllTextAsync(statePath), "a re-run never moves a completed installation's state");
            // The re-run verifies the instance at its recorded limits, then recreates it with the new ones.
            Assert.AreEqual(composeUps + 2, runner.ComposeUpCount);
            Assert.AreEqual(DeploymentPlanAction.Change, changed.Plan!.Items.Single(static item => item.Kind == "container").Action);
            var entry = (await InstallRecordStore.ReadAsync(InstallRecordLocation.For(root, request.ProductionProductRoot), CancellationToken.None))!
                .Instances.Single();
            Assert.AreEqual(manifest.Resources, entry.Resources);

            var again = await InstallResourceRequestAsync(request with { Cpus = 3m, Memory = "6144m" }, runner, owner);
            Assert.IsFalse(again.Plan!.HasChanges);

            // A limit left out keeps what the instance records.
            _ = await InstallResourceRequestAsync(request with { Memory = "8g" }, runner, owner);
            Assert.AreEqual(
                new ContainerResourceLimits(3m, 8 * ResourceGibibyte, 256, true, true),
                (await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None)).Resources);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunGivingALimitToAnInstanceRecordedWithoutLimits_MovesTheOtherToTheDefault()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("5d4e3f2a-1b0c-4d9e-8f7a-6b5c4d3e2f1a"));
        // One fixed rendered model, so the manifest edited to look like an earlier installer's still validates.
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            // An earlier installer recorded no limits; the re-run first restores its original lines to match.
            var earlier = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            Assert.IsTrue(earlier.Remove("resources"));
            await File.WriteAllTextAsync(manifestPath, earlier.ToJsonString());
            _ = await InstallResourceRequestAsync(request, runner, owner);

            _ = await InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner);

            Assert.AreEqual(
                new ContainerResourceLimits(2m, 6 * ResourceGibibyte, 256, false, true),
                (await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None)).Resources);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunWithLimitsTheHostCannotEnforce_IsRefusedBeforeAnythingChanges()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("6e5f4a3b-2c1d-4e0f-9a8b-7c6d5e4f3a2b"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var records = Records(first, root);
            var before = await Snapshot(records);
            var composeUps = runner.ComposeUpCount;

            // The test daemon reports 4 CPUs (#1285): Docker would refuse a larger quota, so the installer refuses first.
            var refused = await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(
                () => InstallResourceRequestAsync(request with { Cpus = 8m }, runner, owner));

            StringAssert.Contains(refused.Message, "docker-host-cpus-insufficient", StringComparison.Ordinal);
            CollectionAssert.AreEqual(before, await Snapshot(records));
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_LimitsChangeThatFailsVerification_RestoresTheRecordedLimitsAndAReRunCarriesOn()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("7f6a5b4c-3d2e-4f1a-8b9c-0d1e2f3a4b5c"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new VerificationFailingOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var records = Records(first, root);
            var before = await Snapshot(records);
            var composePath = Path.Combine(first.ConfigRoot, "compose", "compose.yml");
            var composeBefore = await File.ReadAllTextAsync(composePath);
            // The re-run verifies the instance at its recorded limits first; the next verification is the new limits'.
            owner.FailOnVerification = owner.Verifications + 2;

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner));

            StringAssert.Contains(failure.Message, "so the recorded limits were restored", StringComparison.Ordinal);
            Assert.IsFalse(failure.IsRefusal);
            CollectionAssert.AreEqual(before, await Snapshot(records));
            Assert.AreEqual(composeBefore, await File.ReadAllTextAsync(composePath));

            var carriedOn = await InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner);
            Assert.IsTrue(carriedOn.Plan!.HasChanges);
            Assert.AreEqual(
                6 * ResourceGibibyte,
                (await CameraAgentLifecycleManager.ReadManifestAsync(Path.Combine(first.InstanceRoot, "instance-manifest.json"), CancellationToken.None))
                    .Resources!.MemoryBytes);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_LimitsChangeInterruptedAfterItsManifest_IsFinishedByTheNextReRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("8a7b6c5d-4e3f-4a2b-9c0d-1e2f3a4b5c6d"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var resultPath = Path.Combine(first.StateRoot, "deployment", "installation-result.json");
            var resultBefore = await File.ReadAllTextAsync(resultPath);
            _ = await InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner);
            // A power loss between the manifest and the result leaves the result naming the old Compose model.
            await File.WriteAllTextAsync(resultPath, resultBefore);

            var finished = await InstallResourceRequestAsync(request with { Memory = "6g" }, runner, owner);

            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(Path.Combine(first.InstanceRoot, "instance-manifest.json"), CancellationToken.None);
            var result = await CameraAgentLifecycleManager.ReadResultAsync(resultPath, CancellationToken.None);
            Assert.AreEqual(manifest.ComposeModelSha256, result.ComposeModelSha256);
            Assert.AreEqual(DeploymentPlanAction.Unchanged, finished.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(DeploymentPlanAction.Unchanged, finished.Plan.Items.Single(static item => item.Target.EndsWith("instance-manifest.json", StringComparison.Ordinal)).Action);
            Assert.AreEqual(DeploymentPlanAction.Change, finished.Plan.Items.Single(static item => item.Target.EndsWith("installation-result.json", StringComparison.Ordinal)).Action);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_ReRunDescribingADifferentInstance_IsRefusedAndNamesWhatDiffers()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("9b8c7d6e-5f4a-4b3c-8d1e-2f3a4b5c6d7e"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value) { RenderComposeFile = true };
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var records = Records(first, root);
            var before = await Snapshot(records);
            var composeUps = runner.ComposeUpCount;

            var renamed = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request with { FriendlyName = "Other Camera", TimeZoneId = "America/Phoenix" }, runner, owner));
            var planned = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => InstallResourceRequestAsync(request with { FriendlyName = "Other Camera", DryRun = true }, runner, owner));
            var otherImage = $"sha256:{new string('9', 64)}";
            var reImaged = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(
                request with { ImageReference = otherImage },
                new InstallerProcessRunner(otherImage, root, request.InstanceId.Value) { RenderComposeFile = true },
                owner));

            Assert.IsTrue(renamed.IsRefusal);
            StringAssert.Contains(renamed.Message, "--friendly-name (Limits Camera, not Other Camera)", StringComparison.Ordinal);
            StringAssert.Contains(renamed.Message, "--time-zone (UTC, not America/Phoenix)", StringComparison.Ordinal);
            Assert.IsTrue(planned.IsRefusal);
            Assert.IsTrue(reImaged.IsRefusal);
            StringAssert.Contains(reImaged.Message, "cameraagent upgrade", StringComparison.Ordinal);
            CollectionAssert.AreEqual(before, await Snapshot(records));
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_AsRoot_IsRefusedBeforeAnythingIsCreated()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("0c9d8e7f-6a5b-4c4d-9e2f-3a4b5c6d7e8f"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        try
        {
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), 0, 0, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe, hostEnvironment: new FakeHostEnvironment()));

            Assert.IsTrue(refused.IsRefusal);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstallAsync_CompletedReRunKeepsTheRetainedSnapshotAndAnyOtherInstallDropsIt()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("1d2e3f4a-5b6c-4d7e-8f9a-0b1c2d3e4f5a"));
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value);
        var owner = new InstallerOwnerClient();
        try
        {
            var first = await InstallResourceRequestAsync(request, runner, owner);
            var manifestPath = Path.Combine(first.InstanceRoot, "instance-manifest.json");
            var statePath = Path.Combine(first.StateRoot, "deployment", "installation-state.json");
            // A migrating upgrade left a database snapshot that a rollback would put back.
            var withSnapshot = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            withSnapshot["previousStateSnapshot"] = new JsonObject
            {
                ["snapshotId"] = Guid.NewGuid().ToString("D"),
                ["operationId"] = Guid.NewGuid().ToString("D"),
                ["manifestSha256"] = new string('a', 64),
                ["recordedUtc"] = "2026-10-18T03:00:00+00:00"
            };
            await File.WriteAllTextAsync(manifestPath, withSnapshot.ToJsonString());

            // A completed re-run describes the manifest as it is, so it validates and keeps the snapshot.
            var reRun = await InstallResourceRequestAsync(request, runner, owner);
            Assert.IsFalse(reRun.Plan!.HasChanges);
            Assert.IsNotNull((await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None)).PreviousStateSnapshot);

            // An install that is not a completed re-run, here one resuming a failed run, rebuilds the manifest without it,
            // so a later rollback can never bring back databases from before (the reinstall after reset-state is one).
            var failed = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!.AsObject();
            failed["status"] = "Failed";
            await File.WriteAllTextAsync(statePath, failed.ToJsonString());
            _ = await InstallResourceRequestAsync(request, runner, owner);
            Assert.IsNull((await CameraAgentLifecycleManager.ReadManifestAsync(manifestPath, CancellationToken.None)).PreviousStateSnapshot);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>The records a re-run may change, including the install record.</summary>
    private static string[] Records(InstallationResult installed, string root) =>
    [
        Path.Combine(installed.InstanceRoot, "instance-manifest.json"),
        Path.Combine(installed.StateRoot, "deployment", "installation-result.json"),
        Path.Combine(installed.StateRoot, "deployment", "installation-state.json"),
        Path.Combine(installed.ConfigRoot, "compose", "compose.yml"),
        Path.Combine(root, "operations", "install.json")
    ];

    private static async Task<string[]> Snapshot(string[] paths)
        => await Task.WhenAll(paths.Select(static path => File.ReadAllTextAsync(path)));

    /// <summary>An owner client whose verification fails once, at the call numbered <see cref="FailOnVerification"/>.</summary>
    private sealed class VerificationFailingOwnerClient : IOwnerBootstrapClient
    {
        private readonly InstallerOwnerClient inner = new();

        public int Verifications { get; private set; }

        public int FailOnVerification { get; set; } = -1;

        public Task WaitForHealthAsync(ApprovedCatalogContract expectedCatalog, CancellationToken cancellationToken, TimeSpan? timeout = null)
            => inner.WaitForHealthAsync(expectedCatalog, cancellationToken, timeout);

        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
            => inner.ReadStateAsync(ownerEmail, password, cancellationToken);

        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => inner.ReadInstallationStateAsync(verificationToken, cancellationToken);

        public Task<string> VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            if (++Verifications == FailOnVerification)
            {
                throw new InstallerException("injected verification failure");
            }
            return inner.VerifyInstallationAsync(verificationToken, expectation, cancellationToken);
        }
    }
}
