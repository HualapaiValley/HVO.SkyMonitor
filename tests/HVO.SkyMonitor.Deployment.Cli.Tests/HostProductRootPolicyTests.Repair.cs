using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class HostProductRootPolicyTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostRepair_StaleMount_PreservesTheRootAndEveryNamespaceEntry(bool json)
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/current");
        var camera = host.CreateDirectory("srv/data/current/cameraagents/purge-tombstone");
        var logic = host.CreateDirectory("srv/data/current/logichosts/unrecognized-entry");
        WriteStaleMount(host, root);
        Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        var runner = new RecordingRunner(new ProcessResult(1, string.Empty, "the test must not invoke sudo"));
        var services = ProgramServices.Production with
        {
            Parse = arguments => CommandLine.ParseCommand(arguments, new CommandLineContext(false, host.Location)),
            HostPolicy = (request, cancellationToken) => PrivilegedHostPolicy.SetProductRootAsync(
                request, runner, NativeLinux.getuid(), cancellationToken)
        };
        using var output = new StringWriter();
        var previous = Console.Out;
        int exit;
        try
        {
            Console.SetOut(output);
            exit = await Program.RunAsync(
                json ? ["host", "set-product-root", root, "--repair", "--json"] : ["host", "set-product-root", root, "--repair"],
                services, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(previous);
        }
        Assert.AreEqual(0, exit, output.ToString());
        Assert.IsEmpty(runner.Invocations);
        if (json)
        {
            using var result = JsonDocument.Parse(output.ToString());
            Assert.AreEqual(root, result.RootElement.GetProperty("previousProductRoot").GetString());
            Assert.AreEqual(root, result.RootElement.GetProperty("productRoot").GetString());
            Assert.AreEqual(HostPolicyChange.Updated, result.RootElement.GetProperty("outcome").GetString());
        }
        else
        {
            StringAssert.Contains(output.ToString(), $"Product root: {root}", StringComparison.Ordinal);
            StringAssert.Contains(output.ToString(), "Outcome: updated", StringComparison.Ordinal);
        }
        using var policy = JsonDocument.Parse(await File.ReadAllTextAsync(host.Location.PolicyPath));
        Assert.AreEqual(root, policy.RootElement.GetProperty("productRoot").GetString());
        Assert.AreEqual(MountPointHolding(root), policy.RootElement.GetProperty("mountPoint").GetString());
        Assert.IsTrue(Directory.Exists(camera));
        Assert.IsTrue(Directory.Exists(logic));
        Assert.AreEqual(SharedFile, File.GetUnixFileMode(host.Location.PolicyPath));
        Assert.AreEqual(HostPolicyChange.Unchanged, HostProductRootPolicy.Apply(host.Location, root).Outcome);
        var status = (LifecycleRequest)CommandLine.ParseCommand(["status"], new CommandLineContext(false, host.Location));
        Assert.AreEqual(root, status.ProductRoot);
        Assert.AreEqual(HostPolicyChange.Unchanged, (await RepairPolicyAsync(host, root)).Outcome);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("missing-policy")]
    [DataRow("different-root")]
    [DataRow("missing-root")]
    [DataRow("linked-root")]
    [DataRow("writable-policy")]
    [DataRow("writable-root")]
    [DataRow("malformed-policy")]
    [DataRow("relative-root")]
    public void HostRepair_UntrustedOrDifferentRoot_IsRefusedWithoutChangingThePolicy(string fault)
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/current");
        WriteStaleMount(host, root);
        var requested = root;
        switch (fault)
        {
            case "missing-policy": File.Delete(host.Location.PolicyPath); break;
            case "different-root": requested = Path.Combine(host.TrustRoot, "srv/data/must-not-be-created"); break;
            case "missing-root": Directory.Delete(root); break;
            case "linked-root":
                var other = host.CreateDirectory("srv/data/other");
                Directory.Delete(root);
                Directory.CreateSymbolicLink(root, other);
                break;
            case "writable-policy": File.SetUnixFileMode(host.Location.PolicyPath, SharedFile | UnixFileMode.GroupWrite); break;
            case "writable-root": File.SetUnixFileMode(root, SharedDirectory | UnixFileMode.GroupWrite); break;
            case "malformed-policy": host.WritePolicyText("{"); break;
            case "relative-root": host.WritePolicyText("{\"productRoot\":\"relative\",\"mountPoint\":\"/\"}"); break;
        }
        var before = File.Exists(host.Location.PolicyPath) ? File.ReadAllBytes(host.Location.PolicyPath) : null;
        var request = ParseRepair(host, requested);
        Assert.ThrowsExactly<InstallerException>(() => PrivilegedHostPolicy.SetProductRootAsync(
            request, new RecordingRunner(new ProcessResult(1, "", "")), NativeLinux.getuid(), CancellationToken.None).GetAwaiter().GetResult());
        if (before is null) Assert.IsFalse(File.Exists(host.Location.PolicyPath));
        else CollectionAssert.AreEqual(before, File.ReadAllBytes(host.Location.PolicyPath));
        if (fault == "different-root") Assert.IsFalse(Directory.Exists(requested));
        if (fault == "missing-root") Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void HostRepair_WaitsForPrivilegedPreparationBeforeReadingAndPublishing()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/current");
        host.WritePolicy(root);
        var request = ParseRepair(host, root);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var repairStarted = new ManualResetEventSlim();
        var instance = Path.Combine(root, "cameraagents", Guid.NewGuid().ToString("D"));
        var before = File.ReadAllBytes(host.Location.PolicyPath);
        var preparation = Task.Run(() => PrivilegedPreparation.PrepareUnderHostPolicy(host.Location, root, _ =>
        {
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(30)));
            Directory.CreateDirectory(instance);
        }));
        Task<HostPolicyResult>? repair = null;
        HostPolicyResult? result = null;
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(30)));
            repair = Task.Run(() =>
            {
                repairStarted.Set();
                return PrivilegedHostPolicy.SetProductRootAsync(
                    request, new RecordingRunner(new ProcessResult(1, "", "")), NativeLinux.getuid(), CancellationToken.None);
            });
            Assert.IsTrue(repairStarted.Wait(TimeSpan.FromSeconds(30)));
            Assert.IsFalse(repair.Wait(TimeSpan.FromMilliseconds(500)), "Repair bypassed the preparation's policy lock.");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(host.Location.PolicyPath));
        }
        finally
        {
            release.Set();
            Assert.AreEqual(0, preparation.GetAwaiter().GetResult());
            if (repair is not null) result = repair.GetAwaiter().GetResult();
        }
        Assert.IsNotNull(result);
        Assert.AreEqual(root, result.ProductRoot);
        Assert.IsTrue(Directory.Exists(instance));
        Assert.AreEqual(root, HostProductRootPolicy.Resolve(host.Location).Path);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void HostRepair_ReReadsThePolicyAfterWaitingAndNeverMovesTheRoot()
    {
        using var host = new PolicyHost();
        var first = host.CreateDirectory("srv/data/first");
        var second = host.CreateDirectory("srv/data/second");
        WriteStaleMount(host, first);
        var request = ParseRepair(host, first);
        using var started = new ManualResetEventSlim();
        Task<HostPolicyResult> repair;
        byte[] changed;
        using (HostProductRootPolicy.AcquireLock(host.Location))
        {
            repair = Task.Run(() =>
            {
                started.Set();
                return PrivilegedHostPolicy.SetProductRootAsync(
                    request, new RecordingRunner(new ProcessResult(1, "", "")), NativeLinux.getuid(), CancellationToken.None);
            });
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)));
            Assert.IsFalse(repair.Wait(TimeSpan.FromMilliseconds(500)));
            WriteStaleMount(host, second);
            changed = File.ReadAllBytes(host.Location.PolicyPath);
        }
        Assert.ThrowsExactly<InstallerException>(() => repair.GetAwaiter().GetResult());
        CollectionAssert.AreEqual(changed, File.ReadAllBytes(host.Location.PolicyPath));
    }

    [TestMethod]
    public void HostRepair_HelperProtocol_AdmitsOnlyTheExactRepairSwitch()
    {
        string[] repair = ["internal-set-product-root", "--product-root", "/srv/data/hvo-skymonitor", "--repair"];
        Assert.IsTrue(PrivilegedHostPolicy.TryParse(repair, out var root));
        Assert.AreEqual("/srv/data/hvo-skymonitor", root);
        CollectionAssert.AreEqual(repair, PrivilegedHostPolicy.HelperArguments(root, repairMount: true).Skip(2).ToArray());
        Assert.IsFalse(PrivilegedHostPolicy.TryParse([.. repair, "--json"], out _));
        Assert.IsFalse(PrivilegedHostPolicy.TryParse([.. repair[..3], "--force"], out _));
        Assert.IsFalse(PrivilegedHostPolicy.TryParse([.. repair[..2], "relative", "--repair"], out _));
    }

    [TestMethod]
    public void HostRepair_HelpNamesTheExplicitRepairOption()
    {
        var help = (HelpCommand)CommandLine.ParseCommand(["host", "set-product-root", "--help"]);
        StringAssert.Contains(help.Usage, "--repair", StringComparison.Ordinal);
    }

    private static HostProductRootRequest ParseRepair(PolicyHost host, string root)
        => (HostProductRootRequest)CommandLine.ParseCommand(
            ["host", "set-product-root", root, "--repair"], new CommandLineContext(false, host.Location));

    private static Task<HostPolicyResult> RepairPolicyAsync(PolicyHost host, string root)
        => PrivilegedHostPolicy.SetProductRootAsync(
            ParseRepair(host, root), new RecordingRunner(new ProcessResult(1, "", "")), NativeLinux.getuid(), CancellationToken.None);

    private static void WriteStaleMount(PolicyHost host, string root)
        => host.WritePolicyText(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["productRoot"] = root,
            ["mountPoint"] = Path.Combine(host.TrustRoot, "srv")
        }));
}
