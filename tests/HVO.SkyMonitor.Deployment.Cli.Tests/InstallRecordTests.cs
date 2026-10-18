using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The machine install record (install contract section 5, #1315): where each product root's record lives, the entry each
/// completed operation rebuilds from the instance's own records and how its version history moves, the strict reading
/// that refuses a record this installer could not have written, the root-owned write beside the host policy, and the
/// narrow privileged hand-off that write needs. A write happens only when the record changes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InstallRecordTests
{
    private const UnixFileMode SharedDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid First = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly string ImageA = $"sha256:{new string('a', 64)}";
    private static readonly string ImageB = $"sha256:{new string('b', 64)}";

    // Exactly what an entry says: no secret, no owner email, no password file.
    private static readonly string[] EntryMembers =
    [
        "component", "instanceId", "friendlyName", "condition", "version", "previousVersion", "rolledBackFrom",
        "imageId", "previousImageId", "url", "resources", "updatedUtc", "lastImageChange"
    ];

    private static readonly JsonSerializerOptions Compact = new(InstallRecordJsonContext.Default.Options) { WriteIndented = false };

    [TestMethod]
    public void For_TheProductionRoot_IsTheMachineRecordBesideTheHostPolicy()
    {
        var location = InstallRecordLocation.For("/var/lib/hvo-sky", "/var/lib/hvo-sky");

        Assert.AreEqual("/etc/hvo-sky/install.json", location.Path);
        Assert.IsTrue(location.IsMachineRecord);
        Assert.AreEqual(HostPolicyLocation.Production, location.Trusted);
    }

    [TestMethod]
    public void For_AnAdmittedTestRoot_IsItsOwnRecordInItsOperationsDirectory()
    {
        var location = InstallRecordLocation.For("/tmp/hvo-test/product", "/var/lib/hvo-sky");

        Assert.AreEqual("/tmp/hvo-test/product/operations/install.json", location.Path);
        Assert.IsFalse(location.IsMachineRecord);
    }

    [TestMethod]
    public void Apply_AddsReplacesAndRemovesEntriesInInstanceOrder()
    {
        var record = InstallRecordStore.Apply(null, InstallRecordChange.Of(Entry(Second)), "/var/lib/hvo-sky", "1.0.0+x", Now);
        record = InstallRecordStore.Apply(record, InstallRecordChange.Of(Entry(First)), "/var/lib/hvo-sky", "1.0.0+x", Now);

        CollectionAssert.AreEqual(new[] { First, Second }, record.Instances.Select(static instance => instance.InstanceId).ToArray());
        Assert.AreEqual(InstallRecord.CurrentSchemaVersion, record.SchemaVersion);
        Assert.AreEqual(InstallRecord.ProductName, record.Product);

        var replaced = InstallRecordStore.Apply(
            record, InstallRecordChange.Of(Entry(First) with { Condition = InstanceLifecycleCondition.Uninstalled }), "/var/lib/hvo-sky", "1.0.0+x", Now);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, replaced.Instances[0].Condition);

        var removed = InstallRecordStore.Apply(replaced, InstallRecordChange.Removing(First), "/var/lib/hvo-sky", "1.0.0+x", Now);
        Assert.AreEqual(Second, removed.Instances.Single().InstanceId);
    }

    [TestMethod]
    public void Apply_AnEntryThatSaysTheSameOrARemovalOfNothing_ReturnsTheSameRecordSoNothingIsWritten()
    {
        var record = InstallRecordStore.Apply(null, InstallRecordChange.Of(Entry(First)), "/var/lib/hvo-sky", "1.0.0+x", Now);

        var restated = InstallRecordStore.Apply(
            record, InstallRecordChange.Of(Entry(First) with { UpdatedUtc = Now.AddHours(1) }), "/var/lib/hvo-sky", "2.0.0+y", Now.AddHours(1));
        var removedNothing = InstallRecordStore.Apply(record, InstallRecordChange.Removing(Second), "/var/lib/hvo-sky", "2.0.0+y", Now);

        Assert.AreSame(record, restated);
        Assert.AreSame(record, removedNothing);
    }

    [TestMethod]
    public void Parse_TheRecordAsWritten_RoundTripsAndHoldsNoSecret()
    {
        var record = InstallRecordStore.Apply(null, InstallRecordChange.Of(Entry(First)), "/var/lib/hvo-sky", "1.0.0+x", Now);
        var json = JsonSerializer.Serialize(record, InstallRecordJsonContext.Default.InstallRecord);

        var parsed = InstallRecordStore.Parse(Encoding.UTF8.GetBytes(json), "/etc/hvo-sky/install.json");

        Assert.IsTrue(record.SameAs(parsed));
        var members = JsonNode.Parse(json)!["instances"]![0]!.AsObject().Select(static member => member.Key).ToArray();
        CollectionAssert.AreEquivalent(EntryMembers, members);
    }

    [TestMethod]
    [DataRow("{\"schemaVersion\":2,\"future\":true}", "newer installer (schema 2)")]
    [DataRow("not json", "not valid JSON")]
    [DataRow("{\"schemaVersion\":1,\"product\":\"HVO.SkyMonitor\",\"productRoot\":\"/var/lib/hvo-sky\",\"installerVersion\":\"1\",\"updatedUtc\":\"2026-10-18T03:00:00+00:00\",\"instances\":[],\"extra\":1}", "not valid JSON")]
    [DataRow("{\"schemaVersion\":1,\"product\":\"HVO.RoofController\",\"productRoot\":\"/var/lib/hvo-sky\",\"installerVersion\":\"1\",\"updatedUtc\":\"2026-10-18T03:00:00+00:00\",\"instances\":[]}", "product must be HVO.SkyMonitor")]
    public void Parse_ARecordThisInstallerCouldNotHaveWritten_IsRefused(string json, string reason)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(reason);
        var exception = Assert.ThrowsExactly<InstallerException>(
            () => InstallRecordStore.Parse(Encoding.UTF8.GetBytes(json), "/etc/hvo-sky/install.json"));

        StringAssert.Contains(exception.Message, reason, StringComparison.Ordinal);
    }

    [TestMethod]
    public void EntryProblem_RefusesWhatThisInstallerNeverWrites()
    {
        Assert.IsNull(InstallRecordStore.EntryProblem(Entry(First)));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { Version = "latest" }));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { ImageId = "cameraagent:latest" }));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { FriendlyName = "bad\nname" }));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { Url = new Uri("http://user:pass@localhost:5130/") }));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { Component = "LogicHost" }));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { Resources = CameraAgentContainerResources.Legacy }));
        Assert.IsNotNull(InstallRecordStore.EntryProblem(Entry(First) with { UpdatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-7)) }));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ProjectAsync_ImageNoReleaseNames_HasNoVersionsAndAReRunChangesNothing()
    {
        using var root = new ProductRoot();
        var manifest = root.Manifest(ImageA, previousImage: null);

        var fresh = await InstallRecordStore.ProjectAsync(root.Paths, manifest, InstallRecordEvent.Install, null, CancellationToken.None);
        var reRun = await InstallRecordStore.ProjectAsync(root.Paths, manifest, InstallRecordEvent.Install, fresh, CancellationToken.None);

        Assert.IsNull(fresh.Version);
        Assert.IsNull(fresh.PreviousVersion);
        Assert.IsNull(fresh.RolledBackFrom);
        Assert.IsNull(fresh.LastImageChange);
        Assert.AreEqual(ImageA, fresh.ImageId);
        Assert.AreEqual(new Uri("http://127.0.0.1:5130/"), fresh.Url);
        Assert.IsTrue(fresh.SameAs(reRun));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ProjectAsync_VersionsComeOnlyFromTheReleaseRecordsAndTheEventDecidesWhichIsWhich()
    {
        using var root = new ProductRoot();
        // #1343's records for both images: the release records own the installed version.
        await root.WriteReleaseRecordsAsync((ImageA, "1.0.0"), (ImageB, "1.1.0"));
        // An entry carrying a version of its own is not a source: the projection replaces it from the records.
        var projected = await InstallRecordStore.ProjectAsync(
            root.Paths, root.Manifest(ImageA, null), InstallRecordEvent.Install, null, CancellationToken.None);
        var stale = projected with { Version = "9.9.9" };

        var installed = await InstallRecordStore.ProjectAsync(
            root.Paths, root.Manifest(ImageA, null), InstallRecordEvent.Install, stale, CancellationToken.None);
        var upgraded = await InstallRecordStore.ProjectAsync(
            root.Paths, root.Manifest(ImageB, ImageA), InstallRecordEvent.Upgrade, installed, CancellationToken.None);
        var rolledBack = await InstallRecordStore.ProjectAsync(
            root.Paths, root.Manifest(ImageA, ImageB), InstallRecordEvent.Rollback, upgraded, CancellationToken.None);
        var resumed = await InstallRecordStore.ProjectAsync(
            root.Paths, root.Manifest(ImageA, ImageB), InstallRecordEvent.Rollback, rolledBack, CancellationToken.None);
        var uninstalled = await InstallRecordStore.ProjectAsync(
            root.Paths, root.Manifest(ImageA, ImageB) with { LifecycleCondition = InstanceLifecycleCondition.Uninstalled },
            InstallRecordEvent.Settle, rolledBack, CancellationToken.None);

        Assert.AreEqual("1.0.0", installed.Version);
        Assert.AreEqual("1.1.0", upgraded.Version);
        Assert.AreEqual("1.0.0", upgraded.PreviousVersion);
        Assert.IsNull(upgraded.RolledBackFrom);
        Assert.AreEqual(InstallRecordImageChange.Upgrade, upgraded.LastImageChange);
        Assert.AreEqual("1.0.0", rolledBack.Version);
        Assert.IsNull(rolledBack.PreviousVersion);
        Assert.AreEqual("1.1.0", rolledBack.RolledBackFrom);
        Assert.AreEqual(InstallRecordImageChange.Rollback, rolledBack.LastImageChange);
        // A resumed settlement of the same rollback, and a later uninstall, keep the rollback's meaning.
        Assert.IsTrue(rolledBack.SameAs(resumed));
        Assert.AreEqual("1.1.0", uninstalled.RolledBackFrom);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SettleInstanceAsync_TestRoot_WritesOnceAndThenFindsNothingToChange()
    {
        using var root = new ProductRoot();
        var location = InstallRecordLocation.For(root.Paths.ProductRoot, "/var/lib/hvo-sky");
        var manifest = root.Manifest(ImageA, null);

        var created = await InstallRecordStore.SettleInstanceAsync(location, root.Paths, manifest, InstallRecordEvent.Install, CancellationToken.None);
        var written = await File.ReadAllTextAsync(location.Path);
        var unchanged = await InstallRecordStore.SettleInstanceAsync(location, root.Paths, manifest, InstallRecordEvent.Install, CancellationToken.None);
        var changed = await InstallRecordStore.SettleInstanceAsync(
            location, root.Paths, manifest with { LifecycleCondition = InstanceLifecycleCondition.Uninstalled }, InstallRecordEvent.Settle, CancellationToken.None);
        var removed = await InstallRecordStore.RemoveInstanceAsync(location, manifest.InstanceId, CancellationToken.None);
        var removedAgain = await InstallRecordStore.RemoveInstanceAsync(location, manifest.InstanceId, CancellationToken.None);

        Assert.AreEqual(DeploymentPlanAction.Create, created);
        Assert.AreEqual(DeploymentPlanAction.Unchanged, unchanged);
        Assert.AreEqual(DeploymentPlanAction.Change, changed);
        Assert.AreEqual(DeploymentPlanAction.Remove, removed);
        Assert.AreEqual(DeploymentPlanAction.Unchanged, removedAgain);
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(location.Path));
        StringAssert.Contains(written, "\"schemaVersion\": 1", StringComparison.Ordinal);
        Assert.IsEmpty((await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ApplyTrusted_WritesTheRecordBesideThePolicyOwnedByThePolicyOwnerAndReadableByAll()
    {
        using var host = new PolicyHost();
        var productRoot = host.CreateDirectory("var/lib/hvo-sky");
        var location = InstallRecordLocation.Machine(host.Location, productRoot);

        var outcome = InstallRecordStore.ApplyTrusted(host.Location, productRoot, InstallRecordChange.Of(Entry(First)));
        var again = InstallRecordStore.ApplyTrusted(host.Location, productRoot, InstallRecordChange.Of(Entry(First) with { UpdatedUtc = Now.AddDays(1) }));

        Assert.AreEqual("updated", outcome);
        Assert.AreEqual("unchanged", again);
        Assert.AreEqual(Path.Combine(host.TrustRoot, "etc/hvo-sky/install.json"), location.Path);
        Assert.AreEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(location.Path));
        var read = await InstallRecordStore.ReadAsync(location, CancellationToken.None);
        Assert.AreEqual(productRoot, read!.ProductRoot);
        Assert.IsTrue(Entry(First).SameAs(read.Instances.Single()));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ApplyTrusted_AProductRootThePolicyDoesNotResolve_IsRefusedAndWritesNothing()
    {
        using var host = new PolicyHost();
        var other = host.CreateDirectory("srv/elsewhere");

        var exception = Assert.ThrowsExactly<InstallerException>(
            () => InstallRecordStore.ApplyTrusted(host.Location, other, InstallRecordChange.Of(Entry(First))));

        Assert.IsTrue(exception.IsRefusal);
        Assert.IsFalse(File.Exists(Path.Combine(host.TrustRoot, "etc/hvo-sky/install.json")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReadAsync_ARecordWritableByOthers_IsRefusedLikeTheHostPolicy()
    {
        using var host = new PolicyHost();
        var productRoot = host.CreateDirectory("var/lib/hvo-sky");
        _ = InstallRecordStore.ApplyTrusted(host.Location, productRoot, InstallRecordChange.Of(Entry(First)));
        var location = InstallRecordLocation.Machine(host.Location, productRoot);
        File.SetUnixFileMode(location.Path, File.GetUnixFileMode(location.Path) | UnixFileMode.OtherWrite);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => InstallRecordStore.ReadAsync(location, CancellationToken.None));

        StringAssert.Contains(exception.Message, "not writable by group or other users", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SettleInstanceAsync_ProductionMachineRecordAsTheRuntimeUser_HandsTheOneEntryToTheSudoHelper()
    {
        if (NativeLinux.getuid() == 0)
        {
            Assert.Inconclusive("Root applies the change itself; the hand-off is the runtime user's.");
        }
        // The production record is only read here (it is absent on a test host), and the helper is a recording stand-in,
        // so nothing under /etc is written.
        using var root = new ProductRoot();
        var location = InstallRecordLocation.Machine(HostPolicyLocation.Production, root.Paths.ProductRoot);
        var runner = new RecordingRunner(new ProcessResult(0, "updated\n", string.Empty));
        var original = InstallRecordStore.PrivilegedRunner;
        InstallRecordStore.PrivilegedRunner = runner;
        try
        {
            // The helper reported success, but the record does not hold the entry when it is read back, so the change
            // does not count.
            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallRecordStore.SettleInstanceAsync(
                location, root.Paths, root.Manifest(ImageA, null), InstallRecordEvent.Install, CancellationToken.None));

            StringAssert.Contains(exception.Message, "does not hold the change", StringComparison.Ordinal);
            var (fileName, arguments) = runner.Invocations.Single();
            Assert.AreEqual("sudo", fileName);
            CollectionAssert.AreEqual(
                new[] { "--", $"/proc/{Environment.ProcessId}/exe", InstallRecordStore.CommandName, "--product-root", root.Paths.ProductRoot, "--set" },
                arguments.Take(6).ToArray());
            Assert.IsTrue(InstallRecordStore.TryParse([.. arguments.Skip(2)], out var parsedRoot, out var change));
            Assert.AreEqual(root.Paths.ProductRoot, parsedRoot);
            Assert.AreEqual(root.Manifest(ImageA, null).InstanceId, change.Set!.InstanceId);

            InstallRecordStore.PrivilegedRunner = new RecordingRunner(new ProcessResult(1, string.Empty, "sudo: a password is required"));
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallRecordStore.SettleInstanceAsync(
                location, root.Paths, root.Manifest(ImageA, null), InstallRecordEvent.Install, CancellationToken.None));
            StringAssert.Contains(refused.Message, "could not be updated", StringComparison.Ordinal);
        }
        finally
        {
            InstallRecordStore.PrivilegedRunner = original;
        }
    }

    [TestMethod]
    public void TryParse_AcceptsOnlyOneValidEntryOrOneRemoval()
    {
        var entry = JsonSerializer.Serialize(Entry(First), Compact.GetTypeInfo(typeof(InstallRecordInstance)));
        string[] set = [InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--set", entry];
        string[] remove = [InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--remove", First.ToString("D")];

        Assert.IsTrue(InstallRecordStore.TryParse(set, out _, out var setChange));
        Assert.AreEqual(First, setChange.Set!.InstanceId);
        Assert.IsTrue(InstallRecordStore.TryParse(remove, out _, out var removeChange));
        Assert.AreEqual(First, removeChange.Remove);

        Assert.IsFalse(InstallRecordStore.TryParse([.. set, "extra"], out _, out _));
        Assert.IsFalse(InstallRecordStore.TryParse([InstallRecordStore.CommandName, "--product-root", "relative", "--set", entry], out _, out _));
        Assert.IsFalse(InstallRecordStore.TryParse([InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--set", "{}"], out _, out _));
        Assert.IsFalse(InstallRecordStore.TryParse(
            [InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--set", entry.Replace("}", ",\"secret\":\"x\"}", StringComparison.Ordinal)],
            out _, out _));
        Assert.IsFalse(InstallRecordStore.TryParse(
            [InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--set", new string(' ', InstallRecordStore.MaximumEntryBytes + 1)],
            out _, out _));
        Assert.IsFalse(InstallRecordStore.TryParse(
            [InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--remove", Guid.Empty.ToString("D")], out _, out _));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void RunInternal_NotRoot_RefusesWithUsageAndWritesNothing()
    {
        if (NativeLinux.getuid() == 0)
        {
            Assert.Inconclusive("The non-root refusal cannot be observed as root.");
        }
        Assert.AreEqual(2, InstallRecordStore.RunInternal(
            [InstallRecordStore.CommandName, "--product-root", "/var/lib/hvo-sky", "--remove", First.ToString("D")]));
    }

    private static InstallRecordInstance Entry(Guid instanceId) => new(
        InstallRecordInstance.CameraAgent,
        instanceId,
        "North Camera",
        InstanceLifecycleCondition.Installed,
        "1.0.0",
        null,
        null,
        ImageA,
        null,
        new Uri("http://127.0.0.1:5130/"),
        CameraAgentContainerResources.Default,
        Now);

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments));
            return Task.FromResult(result);
        }
    }

    /// <summary>A disposable admitted test product root with one instance's paths and a manifest builder.</summary>
    private sealed class ProductRoot : IDisposable
    {
        private static readonly Guid Instance = Guid.Parse("33333333-3333-4333-8333-333333333333");

        public ProductRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-install-record-{Guid.NewGuid():N}");
            Paths = InstallationPaths.Create(root, Instance, HygV42.CatalogId);
            foreach (var directory in new[] { Paths.ProductRoot, Paths.InstanceRoot, Paths.DeploymentStateRoot, Paths.OperationsRoot })
            {
                Directory.CreateDirectory(directory);
            }
            File.SetUnixFileMode(Paths.OperationsRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public InstallationPaths Paths { get; }

        /// <summary>Writes #1343's kept release records (<c>image-distribution.records.json</c>) for the given images.</summary>
        public Task WriteReleaseRecordsAsync(params (string ImageId, string Version)[] images)
            => SafeFileSystem.WriteJsonAtomicAsync(
                Path.Combine(Paths.DeploymentStateRoot, ReleaseRecords.RecordsFileName),
                new CameraAgentImageReleaseRecords(
                    DeploymentSchemaVersions.ImageReleaseRecords,
                    [.. images.Select(static (image, index) => new CameraAgentImageReleaseEvidence(
                        DeploymentSchemaVersions.ImageReleaseEvidence, image.Version, $"v{image.Version}", new string('c', 40),
                        new string('d', 64), new string('e', 64), $"ghcr.io/hualapaivalley/skymonitor-cameraagent@sha256:{new string('f', 64)}",
                        "linux/amd64", "archive", null, null, image.ImageId, Now.AddMinutes(index)))]),
                DeploymentJsonContext.Default.CameraAgentImageReleaseRecords,
                CancellationToken.None);

        public InstanceManifest Manifest(string image, string? previousImage)
        {
            static ImageInstallationIdentity Image(string id) => new("registry", $"cameraagent@{id}", id, "amd64", null);
            var catalog = new CatalogInstallationIdentity(
                HygV42.CatalogId, "1", "1", "1", new string('a', 64), 1, 1, Paths.CatalogRoot, new string('a', 64), "local-offline");
            return new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent, Instance, "North Camera",
                Guid.NewGuid(), $"installer-{Instance:D}", 1, new string('d', 64), "owner@example.test", 0, 0, 0, "UTC",
                Guid.NewGuid(), 1000, 1000, Paths.ProductRoot, Paths.ConfigRoot, Paths.StateRoot, "v1", new string('e', 64),
                new string('f', 64), new string('1', 64), "default", "1", "1", "active", new string('2', 64), new string('3', 64),
                catalog, Image(image), previousImage is null ? null : Image(previousImage),
                new DockerDaemonIdentity("daemon", "host", "amd64", "29.0.0"), "cameraagent-state-v2", Now,
                Resources: CameraAgentContainerResources.Default);
        }

        public void Dispose()
        {
            if (Directory.Exists(Paths.ProductRoot))
            {
                Directory.Delete(Paths.ProductRoot, recursive: true);
            }
        }
    }

    /// <summary>A private trust root owned by the test user, standing in for <c>/</c> as the host policy tests use.</summary>
    private sealed class PolicyHost : IDisposable
    {
        public PolicyHost()
        {
            TrustRoot = Path.Combine(Path.GetTempPath().TrimEnd('/'), $"hvo-install-record-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TrustRoot);
            File.SetUnixFileMode(TrustRoot, SharedDirectory);
            Location = new HostPolicyLocation(
                Path.Combine(TrustRoot, "etc/hvo-sky/host.json"),
                TrustRoot,
                NativeLinux.getuid(),
                NativeLinux.getgid(),
                Path.Combine(TrustRoot, "var/lib/hvo-sky"),
                Path.Combine(TrustRoot, "run/hvo-sky-host-policy.lock"));
            CreateDirectory("run");
        }

        public string TrustRoot { get; }

        public HostPolicyLocation Location { get; }

        public string CreateDirectory(string relative)
        {
            var current = TrustRoot;
            foreach (var name in relative.Split('/'))
            {
                current = Path.Combine(current, name);
                if (!Directory.Exists(current))
                {
                    Directory.CreateDirectory(current);
                    File.SetUnixFileMode(current, SharedDirectory);
                }
            }
            return current;
        }

        public void Dispose()
        {
            if (Directory.Exists(TrustRoot))
            {
                Directory.Delete(TrustRoot, recursive: true);
            }
        }
    }
}
