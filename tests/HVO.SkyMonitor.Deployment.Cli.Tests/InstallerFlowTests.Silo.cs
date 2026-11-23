using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The Silo layout through the installer itself (#1966 review: Fable 2 and Sol F2), on a real temporary product root with a
/// scripted Docker CLI. A plan, fresh or of a completed instance, changes nothing and starts no container; an install
/// persists the first-boot pause and proves it from the database before CameraAgent first starts, and never starts
/// CameraAgent when it cannot; an installed instance's layout and inputs are never changed in place.
/// </summary>
public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SiloInstall_PlansWithoutChange_ProvesThePauseBeforeCameraAgentStarts_AndPlansItsReRunReadOnly()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-silo-{Guid.NewGuid():N}");
        RequireSiloDataRootHost(root);
        var instanceId = Guid.NewGuid();
        var request = SiloRequest(root, bundle, instanceId);
        using var runner = new SiloInstallerRunner(SiloCameraAgentImage, root, instanceId);
        var originalClient = SiloCommands.ClientFactory;
        SiloCommands.ClientFactory = uri => new SiloFirstBootClient(uri, runner.CameraAgent);
        try
        {
            Task<InstallationResult> Install(InstallRequest install) => CameraAgentInstaller.InstallAsync(
                install, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);

            // A fresh plan names what the run creates beside CameraAgent, and changes nothing anywhere.
            var plan = await Install(request with { DryRun = true });
            Assert.AreEqual(InstallationOutcome.Planned, plan.Outcome);
            var paths = runner.Paths;
            AssertPlanned(plan.Plan!, "folder", SiloRuntimeProfile.DataRoot(paths), DeploymentPlanAction.Create);
            AssertPlanned(plan.Plan!, "network", SiloRuntimeProfile.NetworkNameFor(instanceId), DeploymentPlanAction.Create);
            AssertPlanned(plan.Plan!, "container", SiloRuntimeProfile.ContainerNameFor(instanceId), DeploymentPlanAction.Create);
            AssertPlanned(plan.Plan!, "silo-store", SiloRuntimeProfile.BucketFor(instanceId), DeploymentPlanAction.Create);
            AssertPlanned(plan.Plan!, "first-boot-pause", "capture admission", DeploymentPlanAction.Create);
            Assert.IsEmpty(runner.Mutations, "A plan changes nothing on the daemon: " + string.Join("; ", runner.Mutations));
            Assert.IsFalse(Directory.Exists(paths.InstanceRoot), "A plan creates nothing on disk.");

            // The install binds the store, persists the pause and proves it, and only then starts CameraAgent.
            var installed = await Install(request);
            Assert.AreEqual(InstallationOutcome.Installed, installed.Outcome);
            var siloUp = runner.Events.IndexOf("silo-up");
            var seeded = runner.Events.IndexOf("first-boot-init");
            var started = runner.Events.IndexOf("cameraagent-up");
            Assert.IsTrue(siloUp >= 0 && seeded > siloUp && started > seeded, "Order: " + string.Join(", ", runner.Events));
            Assert.IsTrue(runner.CameraAgentAbsentAtSeed, "The pause is seeded while the CameraAgent service container does not exist.");
            Assert.IsTrue(runner.PauseInForceAtFirstStart, "The persisted pause is durable and in force before CameraAgent first starts.");
            var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(paths.ManifestPath, CancellationToken.None, allowSiloLayout: true);
            Assert.IsNotNull(manifest.Silo);
            Assert.AreEqual(SiloRuntimeProfile.ComponentSchemaVersion, manifest.ComponentSchemaVersion);
            Assert.AreEqual(runner.FirstBootPauseId, manifest.Silo.FirstBootPauseId);
            Assert.AreEqual(SiloRuntimeProfile.BucketFor(instanceId), manifest.Silo.Bucket);

            // A completed instance's plan reports what the run verifies; it starts no probe or other container.
            var calls = runner.Calls.Count;
            var mutations = runner.Mutations.Count;
            var replan = await Install(request with { DryRun = true });
            Assert.AreEqual(InstallationOutcome.Planned, replan.Outcome);
            Assert.IsFalse(replan.Plan!.HasChanges);
            AssertPlanned(replan.Plan, "container", SiloRuntimeProfile.ContainerNameFor(instanceId), DeploymentPlanAction.Unchanged);
            AssertPlanned(replan.Plan, "silo-store", manifest.Silo.Bucket, DeploymentPlanAction.Unchanged);
            Assert.AreEqual(mutations, runner.Mutations.Count, "The plan changed nothing: " + string.Join("; ", runner.Mutations.Skip(mutations)));
            Assert.IsFalse(runner.Calls.Skip(calls).Any(static call => call is ["run", ..] or ["exec", ..]),
                "The plan ran no container: " + string.Join("; ", runner.Calls.Skip(calls).Select(static call => string.Join(' ', call))));

            // Other inputs, planned or run, and the other layout are refused before anything changes.
            foreach (var dryRun in new[] { true, false })
            {
                var other = await Assert.ThrowsAsync<InstallerException>(() => Install(request with { FriendlyName = "Renamed Camera", DryRun = dryRun }));
                Assert.IsTrue(other.IsRefusal, other.Message);
                StringAssert.Contains(other.Message, "never changes or upgrades it in place", StringComparison.Ordinal);
            }
            var layout = await Assert.ThrowsAsync<InstallerException>(() => Install(request with { StorageLayout = CameraAgentStorageLayout.Filesystem }));
            Assert.IsTrue(layout.IsRefusal, layout.Message);
            StringAssert.Contains(layout.Message, "storage layout is never changed in place", StringComparison.Ordinal);
            Assert.AreEqual(mutations, runner.Mutations.Count, "No refusal changed anything.");
        }
        finally
        {
            SiloCommands.ClientFactory = originalClient;
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("not-persisted")]
    [DataRow("not-in-force")]
    public async Task SiloInstall_NeverStartsCameraAgentWhenThePersistedPauseCannotBeProven(string seeded)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-silo-{Guid.NewGuid():N}");
        RequireSiloDataRootHost(root);
        var instanceId = Guid.NewGuid();
        using var runner = new SiloInstallerRunner(SiloCameraAgentImage, root, instanceId) { Seed = seeded };
        var originalClient = SiloCommands.ClientFactory;
        SiloCommands.ClientFactory = uri => new SiloFirstBootClient(uri, runner.CameraAgent);
        try
        {
            var failure = await Assert.ThrowsAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                SiloRequest(root, bundle, instanceId), runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid,
                CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            StringAssert.Contains(failure.Message, "first-boot pause", StringComparison.Ordinal);
            CollectionAssert.Contains(runner.Events, "first-boot-init");
            CollectionAssert.DoesNotContain(runner.Events, "cameraagent-up", "CameraAgent never starts without a proven pause.");
        }
        finally
        {
            SiloCommands.ClientFactory = originalClient;
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// #1966 round 2 (Sol F1): an install interrupted after CameraAgent started, and run again with the same inputs, keeps
    /// the setting files the running process loaded. Docker keeps a container whose Compose model is unchanged, so a
    /// rewritten bucket or binding file would leave verification refusing a CameraAgent that was never restarted.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SiloInstall_ResumedAfterCameraAgentStarted_KeepsTheSettingsTheRunningProcessLoaded()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-silo-{Guid.NewGuid():N}");
        RequireSiloDataRootHost(root);
        var instanceId = Guid.NewGuid();
        var request = SiloRequest(root, bundle, instanceId);
        using var runner = new SiloInstallerRunner(SiloCameraAgentImage, root, instanceId);
        runner.CameraAgent.FailReads = 1;
        var originalClient = SiloCommands.ClientFactory;
        SiloCommands.ClientFactory = uri => new SiloFirstBootClient(uri, runner.CameraAgent);
        try
        {
            Task<InstallationResult> Install() => CameraAgentInstaller.InstallAsync(
                request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid, CancellationToken.None,
                portAvailabilityProbe: new RecordingPortProbe().Probe);
            _ = await Assert.ThrowsAsync<InstallerException>(Install);
            Assert.IsTrue(runner.CameraAgentStarts > 0, "The interruption came after CameraAgent started.");
            var secrets = Path.Combine(runner.Paths.ConfigRoot, "secrets");
            string[] loaded = [.. SiloRuntimeProfile.BindingSettings(
                    (await CameraAgentLifecycleManager.ReadManifestAsync(runner.Paths.ManifestPath, CancellationToken.None, allowSiloLayout: true)).Silo!)
                .Keys.Append(SiloRuntimeProfile.EndpointSetting)];
            var changed = loaded.ToDictionary(setting => setting, setting => NativeLinux.GetChangeTimeUtc(Path.Combine(secrets, setting)));
            var starts = runner.CameraAgentStarts;

            var resumed = await Install();

            Assert.AreEqual(InstallationOutcome.Installed, resumed.Outcome);
            Assert.AreEqual(starts, runner.CameraAgentStarts, "The same inputs render the same model, so Docker kept CameraAgent running.");
            foreach (var setting in loaded)
                Assert.AreEqual(changed[setting], NativeLinux.GetChangeTimeUtc(Path.Combine(secrets, setting)), $"{setting} was rewritten.");
        }
        finally
        {
            SiloCommands.ClientFactory = originalClient;
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// #1966 round 5 (Sol R5-03): the bootstrap proves its data root this Silo's alone by the replacement's rules before it
    /// starts Silo. Another container that reaches the data root through a link, or through a named volume whose options
    /// bind it there (whatever type it declares), stops the install before Silo or CameraAgent starts.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("link-to-the-data-root", "which is the data root, lies inside it or holds it")]
    [DataRow("bind-volume-of-the-data-root", "a bind of")]
    [DataRow("tmpfs-typed-bind-volume-of-the-data-root", "a bind of")]
    public async Task SiloInstall_NeverBootstrapsWhileAnotherContainerReachesItsDataRoot(string shape, string reason)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-silo-{Guid.NewGuid():N}");
        RequireSiloDataRootHost(root);
        var instanceId = Guid.NewGuid();
        using var runner = new SiloInstallerRunner(SiloCameraAgentImage, root, instanceId);
        var dataRoot = SiloRuntimeProfile.DataRoot(runner.Paths);
        var originalClient = SiloCommands.ClientFactory;
        SiloCommands.ClientFactory = uri => new SiloFirstBootClient(uri, runner.CameraAgent);
        try
        {
            switch (shape)
            {
                case "link-to-the-data-root":
                    // The link names the data root the install creates; it resolves once the install has created it.
                    Directory.CreateDirectory(root);
                    var link = Path.Combine(root, "data-alias");
                    File.CreateSymbolicLink(link, dataRoot);
                    runner.Silo.AddOtherContainer(SiloHarness.BindMount(link));
                    break;
                case "bind-volume-of-the-data-root":
                    runner.Silo.AddOtherContainer(runner.Silo.LocalVolume("bound", ("type", "none"), ("o", "bind"), ("device", dataRoot)));
                    break;
                case "tmpfs-typed-bind-volume-of-the-data-root":
                    runner.Silo.AddOtherContainer(runner.Silo.LocalVolume("bound", ("type", "tmpfs"), ("o", "bind"), ("device", dataRoot)));
                    break;
            }

            var refused = await Assert.ThrowsAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                SiloRequest(root, bundle, instanceId), runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid,
                CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));

            StringAssert.Contains(refused.Message, "Another container references the Silo data root", StringComparison.Ordinal);
            StringAssert.Contains(refused.Message, reason, StringComparison.Ordinal);
            Assert.IsTrue(refused.InnerException is InstallerException { IsRefusal: true } || refused.IsRefusal, "The bootstrap refused: " + refused);
            CollectionAssert.DoesNotContain(runner.Events, "silo-up", "Silo never starts on a data root another container reaches.");
            CollectionAssert.DoesNotContain(runner.Events, "cameraagent-up", "CameraAgent never starts.");
            Assert.IsFalse(runner.Mutations.Any(static mutation => mutation.StartsWith("store:", StringComparison.Ordinal)), "The store is never touched.");
        }
        finally
        {
            SiloCommands.ClientFactory = originalClient;
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// #1966 round 2 (Fable new finding 2): a CameraAgent image that does not declare the first-boot pause contract would
    /// ignore the seeded pause and admit captures, so a Silo-layout install refuses it before anything is created.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(null)]
    [DataRow("cameraagent-first-boot-pause-v0")]
    public async Task SiloInstall_RefusesACameraAgentImageWithoutTheFirstBootContract(string? contract)
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-silo-{Guid.NewGuid():N}");
        RequireSiloDataRootHost(root);
        var instanceId = Guid.NewGuid();
        using var runner = new SiloInstallerRunner(SiloCameraAgentImage, root, instanceId, contract);
        try
        {
            foreach (var dryRun in new[] { true, false })
            {
                var refused = await Assert.ThrowsAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                    SiloRequest(root, bundle, instanceId) with { DryRun = dryRun }, runner, _ => new InstallerOwnerClient(), RuntimeUid,
                    RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));
                Assert.IsTrue(refused.IsRefusal, refused.Message);
                StringAssert.Contains(refused.Message, "does not declare the first-boot pause contract", StringComparison.Ordinal);
            }
            Assert.IsEmpty(runner.Mutations, "Nothing changed on the daemon: " + string.Join("; ", runner.Mutations));
            Assert.IsFalse(Directory.Exists(runner.Paths.InstanceRoot), "Nothing was created on disk.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AnInstalledFilesystemInstance_IsNeverConvertedToTheSiloLayoutInPlace()
    {
        var bundle = RequireCatalogBundle();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-installer-layout-{Guid.NewGuid():N}");
        var instanceId = Guid.NewGuid();
        var request = SiloRequest(root, bundle, instanceId) with { StorageLayout = CameraAgentStorageLayout.Filesystem };
        var runner = new InstallerProcessRunner(SiloCameraAgentImage, root, instanceId);
        try
        {
            _ = await CameraAgentInstaller.InstallAsync(request, runner, _ => new InstallerOwnerClient(), RuntimeUid, RuntimeGid,
                CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe);
            var composeUps = runner.ComposeUpCount;
            var paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.DefaultCatalogId);
            var manifest = await File.ReadAllBytesAsync(paths.ManifestPath);

            foreach (var dryRun in new[] { true, false })
            {
                var refused = await Assert.ThrowsAsync<InstallerException>(() => CameraAgentInstaller.InstallAsync(
                    request with { StorageLayout = CameraAgentStorageLayout.Silo, DryRun = dryRun }, runner, _ => new InstallerOwnerClient(),
                    RuntimeUid, RuntimeGid, CancellationToken.None, portAvailabilityProbe: new RecordingPortProbe().Probe));
                Assert.IsTrue(refused.IsRefusal, refused.Message);
                StringAssert.Contains(refused.Message, "storage layout is never changed in place", StringComparison.Ordinal);
            }
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
            CollectionAssert.AreEqual(manifest, await File.ReadAllBytesAsync(paths.ManifestPath));
            Assert.IsFalse(Directory.Exists(SiloRuntimeProfile.DataRoot(paths)), "No Silo data root was created.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static readonly string SiloCameraAgentImage = $"sha256:{new string('d', 64)}";

    private static InstallRequest SiloRequest(string root, string bundle, Guid instanceId) => new()
    {
        AllowTestProductRoot = true,
        InstanceId = instanceId,
        FriendlyName = "Silo Camera",
        OwnerEmail = "owner@example.test",
        ProductRoot = root,
        CatalogBundle = bundle,
        ImageReference = SiloCameraAgentImage,
        Port = SelectFreePort(),
        StorageLayout = CameraAgentStorageLayout.Silo
    };

    /// <summary>A Silo data root is admitted only on local ext4 with its headroom reserve free; a host without both cannot run this flow.</summary>
    private static void RequireSiloDataRootHost(string root)
    {
        var ancestor = LinuxHostEnvironment.NearestExistingAncestor(root);
        var (fileSystem, _, _) = SiloRuntimeProfile.MountOf(ancestor, File.ReadAllText(HostProductRootPolicy.MountInfoPath));
        if (fileSystem != "ext4" || !SiloRuntimeProfile.HeadroomOf(ancestor).Ready)
            Assert.Inconclusive($"A Silo-layout install needs its test root on local ext4 with the headroom reserve free; {ancestor} is on {fileSystem}.");
    }

    private static void AssertPlanned(DeploymentPlan plan, string kind, string target, DeploymentPlanAction action)
    {
        var item = plan.Items.SingleOrDefault(item => item.Kind == kind && item.Target == target);
        Assert.IsNotNull(item, $"The plan names {kind} {target}: " + string.Join("; ", plan.Items.Select(static i => $"{i.Action} {i.Kind} {i.Target}")));
        Assert.AreEqual(action, item.Action, $"{kind} {target}");
    }

    /// <summary>
    /// The installer's Docker CLI for a Silo-layout instance: the filesystem flow's fake for the CameraAgent image, Compose
    /// and the daemon, and the Silo harness's answers for the Silo container, its network and its store. The pinned Silo
    /// image is already on the daemon (Docker's classic store). The first-boot initialization writes the pause as the app's
    /// schema transaction does; every call, and every call that changes something, is recorded in order.
    /// </summary>
    private sealed class SiloInstallerRunner : IProcessInputRunner, IDisposable
    {
        private const string MarkerVersion = "3a70979c-3128-47c3-8653-7cdc283f4f05";
        private const string FenceVersion = "4b81a8ad-4239-48d4-9764-8ded394f5f16";
        private readonly InstallerProcessRunner inner;
        private readonly SiloHarness silo;

        /// <param name="firstBootContract">The CameraAgent image's first-boot contract label, or none.</param>
        public SiloInstallerRunner(string imageId, string root, Guid instanceId, string? firstBootContract = SiloInstallation.FirstBootContract)
        {
            Paths = InstallationPaths.Create(root, instanceId, ProductionCatalog.DefaultCatalogId);
            var labels = new Dictionary<string, string>(ReleaseFixture.ContractLabels)
            {
                ["org.opencontainers.image.revision"] = new string('a', 40)
            };
            if (firstBootContract is not null) labels[SiloInstallation.FirstBootContractLabel] = firstBootContract;
            inner = new InstallerProcessRunner(imageId, root, instanceId) { Labels = labels };
            silo = new SiloHarness(Paths, instanceId, seed: false).Defaults();
            silo.SiloImage = SiloImagePin.ForArchitecture("amd64")!.ConfigDigest;
            silo.SiloManifest = null;
            silo.CameraAgentExists = false;
            silo.NetworkMembers.Remove(silo.CameraAgentId);
            CameraAgent = new FirstBootStatus(this);
        }

        public InstallationPaths Paths { get; }

        /// <summary>The Docker state the Silo checks see, which a test may add other containers and volumes to.</summary>
        public SiloHarness Silo => silo;

        public List<string[]> Calls { get; } = [];
        public List<string> Mutations { get; } = [];
        public List<string> Events { get; } = [];
        public FirstBootStatus CameraAgent { get; }

        /// <summary>What the first-boot initialization leaves: the pause (default), nothing, or a pause no longer in force.</summary>
        public string Seed { get; init; } = "persisted";

        public bool CameraAgentAbsentAtSeed { get; private set; }

        /// <summary>How many times CameraAgent's container started: Docker restarts it only when its Compose model changes.</summary>
        public int CameraAgentStarts { get; private set; }

        private string? runningModel;
        public bool PauseInForceAtFirstStart { get; private set; }
        public Guid FirstBootPauseId => Guid.Parse(File.ReadAllText(Path.Combine(Paths.ConfigRoot, "secrets", "CameraAgent__FirstBootPause__Id")));

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
            => RunCoreAsync(fileName, arguments, null, cancellationToken);

        public Task<ProcessResult> RunWithInputAsync(string fileName, IReadOnlyList<string> arguments, string standardInput, CancellationToken cancellationToken)
            => RunCoreAsync(fileName, arguments, standardInput, cancellationToken);

        private async Task<ProcessResult> RunCoreAsync(string fileName, IReadOnlyList<string> arguments, string? input, CancellationToken cancellationToken)
        {
            if (fileName != "docker" || arguments is ["context", "inspect", ..])
                return await inner.RunAsync(fileName, arguments, cancellationToken);
            string[] args = arguments.Count >= 2 && arguments[0] == "--host" ? [.. arguments.Skip(2)] : [.. arguments];
            Calls.Add(args);
            if (args is ["image", "inspect", var image] && SiloImagePin.Platforms.Any(candidate => candidate.ConfigDigest == image))
                return ScriptedDocker.Ok(new JsonArray(SiloImage(image)));
            if (args is ["image", "inspect", ..] && args.Any(static arg => arg.Contains(SiloImagePin.IndexDigest, StringComparison.Ordinal) ||
                                                                           SiloImagePin.Platforms.Any(candidate => candidate.ManifestDigest == arg)))
                return ScriptedDocker.NoSuch("image", args[^1]);
            if (args is ["compose", ..])
                return await ComposeAsync(args, cancellationToken);
            if (args is ["exec", "--interactive", _, "bash", "-s"] && input is not null)
            {
                Mutations.Add("store: " + (input.Contains("create_only", StringComparison.Ordinal) ? "bind" : "script"));
                return Store(input);
            }
            if (args is ["run", ..] && input is not null)
            {
                Mutations.Add("probe container");
                return Verified();
            }
            if (args is ["container", "inspect", var name] && (name == silo.Project || name == silo.CameraAgentId))
                return silo.CameraAgentExists ? await CameraAgentContainerAsync(cancellationToken) : ScriptedDocker.NoSuch("container", name);
            if (args is ["image", "pull" or "load" or "rm" or "tag", ..] or ["container", "rm" or "stop" or "start" or "create", ..] or
                ["network", "create" or "rm", ..])
                Mutations.Add(string.Join(' ', args));
            return silo.Answer(args, input) ?? await inner.RunAsync(fileName, arguments, cancellationToken);
        }

        private async Task<ProcessResult> ComposeAsync(string[] args, CancellationToken cancellationToken)
        {
            if (args.Contains("version", StringComparer.Ordinal) || args.Contains("config", StringComparer.Ordinal))
                return await inner.RunAsync("docker", args, cancellationToken);
            if (args.Contains("up", StringComparer.Ordinal) && args[^1] == SiloRuntimeProfile.ServiceName)
            {
                Mutations.Add("compose up silo");
                Events.Add("silo-up");
                return ScriptedDocker.Ok(string.Empty);
            }
            if (args.Contains("run", StringComparer.Ordinal) && args[^1] == "--hvo-initialize-first-boot-state")
            {
                Mutations.Add("compose run first-boot initialization");
                Events.Add("first-boot-init");
                CameraAgentAbsentAtSeed = !silo.CameraAgentExists;
                if (Seed != "not-persisted") SeedPause(inForce: Seed != "not-in-force");
                return ScriptedDocker.Ok("{\"outcome\":\"seeded\"}\n");
            }
            if (args.Contains("up", StringComparer.Ordinal))
            {
                Mutations.Add("compose up");
                if (!Events.Contains("cameraagent-up")) PauseInForceAtFirstStart = await PauseInForceAsync(cancellationToken);
                Events.Add("cameraagent-up");
                silo.CameraAgentExists = true;
                if (!silo.NetworkMembers.Contains(silo.CameraAgentId)) silo.NetworkMembers.Add(silo.CameraAgentId);
                // Docker keeps a running container whose Compose model (the file and its environment) is unchanged.
                var model = await File.ReadAllTextAsync(args[Array.IndexOf(args, "--file") + 1], cancellationToken) +
                            await File.ReadAllTextAsync(args[Array.IndexOf(args, "--env-file") + 1], cancellationToken);
                if (model != runningModel)
                {
                    runningModel = model;
                    CameraAgentStarts++;
                    silo.CameraAgentStarted = DateTimeOffset.UtcNow.AddSeconds(1);
                }
                return await inner.RunAsync("docker", args, cancellationToken);
            }
            Mutations.Add(string.Join(' ', args));
            return await inner.RunAsync("docker", args, cancellationToken);
        }

        private static JsonObject SiloImage(string id) => new()
        {
            ["Id"] = id,
            ["Os"] = "linux",
            ["Architecture"] = SiloImagePin.Platforms.Single(candidate => candidate.ConfigDigest == id).Architecture,
            ["Config"] = new JsonObject
            {
                ["Labels"] = new JsonObject
                {
                    ["org.opencontainers.image.version"] = SiloImagePin.Release,
                    ["org.opencontainers.image.revision"] = SiloImagePin.SourceRevision,
                    ["org.opencontainers.image.licenses"] = SiloImagePin.License
                }
            }
        };

        /// <summary>The CameraAgent container as both the filesystem and the Silo verifications read it.</summary>
        private async Task<ProcessResult> CameraAgentContainerAsync(CancellationToken cancellationToken)
        {
            var answered = await inner.RunAsync("docker", ["container", "inspect", silo.Project], cancellationToken);
            var container = JsonNode.Parse(answered.StandardOutput)!.AsArray()[0]!.DeepClone().AsObject();
            var expected = silo.CameraAgentContainer();
            container["Id"] = expected["Id"]!.DeepClone();
            container["Name"] = expected["Name"]!.DeepClone();
            container["State"]!["StartedAt"] = expected["State"]!["StartedAt"]!.DeepClone();
            container["State"]!["Running"] = expected["State"]!["Running"]!.DeepClone();
            container["State"]!["Pid"] = expected["State"]!["Pid"]!.DeepClone();
            container["Config"]!["Env"] = expected["Config"]!["Env"]!.DeepClone();
            container["NetworkSettings"] = expected["NetworkSettings"]!.DeepClone();
            foreach (var mount in expected["Mounts"]!.AsArray()) container["Mounts"]!.AsArray().Add(mount!.DeepClone());
            return ScriptedDocker.Ok(new JsonArray(container));
        }

        /// <summary>The fresh store a new instance meets: empty, then a bucket and writer created, then the binding written.</summary>
        private ProcessResult Store(string script)
        {
            static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            if (script.Contains("create_only", StringComparison.Ordinal))
                return ScriptedDocker.Ok($"{{\"outcome\":\"bound\",\"markerVersionId\":\"{MarkerVersion}\",\"markerSha256\":\"{Sha(Bytes("instance.v1.json"))}\"," +
                                         $"\"fenceVersionId\":\"{FenceVersion}\",\"fenceSha256\":\"{Sha(Bytes("fence.json"))}\"}}\n");
            if (script.Contains("hvo-bootstrap: inventory", StringComparison.Ordinal))
            {
                var rootUser = File.ReadAllText(Path.Combine(SiloRuntimeProfile.RootSecretsDirectory(Paths), "root-user")).Trim();
                var keys = $"{{\"status\":\"success\",\"user\":\"{rootUser}\",\"stsKeys\":null,\"svcaccs\":null}}";
                return ScriptedDocker.Ok($"{{\"outcome\":\"inventory\",\"buckets\":\"\",\"objects\":\"\",\"versioning\":\"\",\"keys\":\"{B64(keys)}\"}}\n");
            }
            return ScriptedDocker.Ok($"{{\"outcome\":\"created\",\"versioning\":\"Enabled\",\"accessKey\":\"{SiloHarness.Access}\",\"secretKey\":\"{SiloHarness.Secret}\"}}\n");
        }

        /// <summary>The binding read back from CameraAgent's network: the marker and fence the bootstrap wrote.</summary>
        private ProcessResult Verified()
        {
            var marker = Bytes("instance.v1.json");
            var fence = Bytes("fence.json");
            return ScriptedDocker.Ok(
                $"{{\"outcome\":\"verified\",\"markerSha256\":\"{Sha(marker)}\",\"marker\":\"{Convert.ToBase64String(marker)}\"," +
                $"\"fenceVersionId\":\"{FenceVersion}\",\"fence\":\"{Convert.ToBase64String(fence)}\",\"endpoint\":\"{SiloRuntimeProfile.Endpoint}\"," +
                $"\"writerAccessKeyId\":\"{SiloHarness.Access}\",\"writerSecretSha256\":\"{Sha(Encoding.UTF8.GetBytes(SiloHarness.Secret))}\"}}\n");
        }

        private byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(SiloRuntimeProfile.BootstrapDirectory(Paths), name));

        private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        /// <summary>What CameraAgent's initialize-only run writes in its raw-ingress schema transaction.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "PRAGMA user_version takes no parameter; the value is an integer parsed from the image's label.")]
        private void SeedPause(bool inForce)
        {
            var database = CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot);
            var missing = new Stack<string>();
            for (var directory = Path.GetDirectoryName(database)!; !Directory.Exists(directory); directory = Path.GetDirectoryName(directory)!)
                missing.Push(directory);
            while (missing.Count > 0) SafeFileSystem.CreateOwnerDirectory(missing.Pop());
            var id = FirstBootPauseId;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            // The journal CameraAgent creates is at the raw-ingress schema its image declares, which the state preflight of a
            // resumed install checks; the installer reads only the two capture-control tables.
            var schema = int.Parse(ReleaseFixture.ContractLabels["io.hvo.skymonitor.raw-ingress-schema"], System.Globalization.CultureInfo.InvariantCulture);
            command.CommandText = $"PRAGMA user_version = {schema};" + """
                CREATE TABLE capture_control_state (state_key INTEGER PRIMARY KEY, state TEXT NOT NULL, version INTEGER NOT NULL);
                CREATE TABLE capture_control_commands (idempotency_key TEXT NOT NULL, actor TEXT NOT NULL, target_state TEXT NOT NULL,
                    expected_version INTEGER, status TEXT NOT NULL, result_state TEXT, result_version INTEGER, changed INTEGER NOT NULL);
                INSERT INTO capture_control_commands VALUES ($key, $actor, 'paused', 0, 'completed', 'paused', 1, 1);
                INSERT INTO capture_control_state VALUES (1, 'paused', 1);
                """;
            command.Parameters.AddWithValue("$key", $"installer:first-boot:{id:D}:pause");
            command.Parameters.AddWithValue("$actor", $"installer-first-boot:{id:D}");
            command.ExecuteNonQuery();
            if (!inForce)
            {
                using var later = connection.CreateCommand();
                later.CommandText = """
                    INSERT INTO capture_control_commands VALUES ('ui-resume', 'owner-1', 'running', 1, 'completed', 'running', 2, 1);
                    UPDATE capture_control_state SET state = 'running', version = 2 WHERE state_key = 1;
                    """;
                later.ExecuteNonQuery();
            }
        }

        private async Task<bool> PauseInForceAsync(CancellationToken cancellationToken)
        {
            try
            {
                await SiloInstallation.VerifyPersistedFirstBootPauseAsync(Paths.StateRoot, FirstBootPauseId, requireInForce: true, cancellationToken);
                return true;
            }
            catch (InstallerException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            CameraAgent.Dispose();
            silo.Dispose();
        }
    }

    /// <summary>CameraAgent's first-boot status route, answering as the app does for the pause the installer persisted.</summary>
    private sealed class FirstBootStatus(SiloInstallerRunner runner) : HttpMessageHandler
    {
        /// <summary>How many status reads fail (HTTP 500) before the route answers, to interrupt an install after CameraAgent started.</summary>
        public int FailReads { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/api/internal/deployment/lifecycle/first-boot")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (FailReads > 0)
            {
                FailReads--;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            var body = new JsonObject
            {
                ["id"] = runner.FirstBootPauseId.ToString("D"),
                ["seeded"] = true,
                ["inForce"] = true,
                ["resumed"] = false,
                ["state"] = "paused",
                ["version"] = 1
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }
}
