using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The root-owned host product-root policy. Each case states its own policy location below a private directory the
/// test user owns, carried exactly as production carries <see cref="HostPolicyLocation.Production"/>, so the
/// ownership, mode, link, existence, and mount rules run against real files without root and without touching the
/// host's <c>/etc/hvo-sky</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class HostProductRootPolicyTests
{
    private const UnixFileMode SharedDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode SharedFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly string ImageReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Resolve_WithoutAPolicyFile_KeepsTheDefaultProductRoot()
    {
        using var host = new PolicyHost();
        Assert.AreEqual(new ProductionProductRoot(host.DefaultRoot, FromPolicy: false), HostProductRootPolicy.Resolve(host.Location));

        // A policy directory without the file is still no policy.
        host.CreateDirectory("etc/hvo-sky");
        Assert.AreEqual(new ProductionProductRoot(host.DefaultRoot, FromPolicy: false), HostProductRootPolicy.Resolve(host.Location));
        Assert.AreEqual(ProductionProductRoot.Default, new ProductionProductRoot(InstallRequest.DefaultProductRoot, false));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Resolve_ValidPolicy_NamesItsProductRoot()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/hvo-skymonitor");
        host.WritePolicy(root);

        Assert.AreEqual(new ProductionProductRoot(root, FromPolicy: true), HostProductRootPolicy.Resolve(host.Location));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("etc/hvo-sky/host.json", 0b110_110_100, DisplayName = "policy-group-writable")]
    [DataRow("etc/hvo-sky/host.json", 0b110_100_110, DisplayName = "policy-other-writable")]
    [DataRow("etc/hvo-sky", 0b111_111_101, DisplayName = "policy-directory-group-writable")]
    [DataRow("srv/data", 0b111_101_111, DisplayName = "root-component-other-writable")]
    [DataRow("srv/data/hvo-skymonitor", 0b111_111_101, DisplayName = "root-group-writable")]
    public void Resolve_PathWritableByGroupOrOthers_IsRefused(string relative, int mode)
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/hvo-skymonitor");
        host.WritePolicy(root);
        File.SetUnixFileMode(Path.Combine(host.TrustRoot, relative), (UnixFileMode)mode);

        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));

        StringAssert.Contains(refused.Message, Path.Combine(host.TrustRoot, relative), StringComparison.Ordinal);
        StringAssert.Contains(refused.Message, "not writable by group or other users", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Resolve_PathsNotOwnedByThePolicyOwner_AreRefused()
    {
        if (NativeLinux.getuid() == 0) Assert.Inconclusive("The ownership refusal is observable only from a non-root test user.");
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/hvo-skymonitor");
        host.WritePolicy(root);

        // The production rule is root:root; a tree owned by the unprivileged test user does not satisfy it.
        var rootOwned = host.Location with { OwnerUid = 0, OwnerGid = 0 };
        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(rootOwned));
        StringAssert.Contains(refused.Message, $"'{host.TrustRoot}' must be owned by 0:0", StringComparison.Ordinal);

        var otherGroup = host.Location with { OwnerGid = host.Location.OwnerGid + 1 };
        Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(otherGroup));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Resolve_SymbolicLinks_AreRefusedWhereverTheyAppear()
    {
        using var host = new PolicyHost();
        var real = host.CreateDirectory("srv/real/hvo-skymonitor");
        host.CreateDirectory("srv/data");
        File.CreateSymbolicLink(Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor"), real);
        host.WritePolicy(Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor"));

        var rootLink = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        StringAssert.Contains(rootLink.Message, "must not be a symbolic link", StringComparison.Ordinal);

        host.WritePolicy(real);
        Assert.AreEqual(real, HostProductRootPolicy.Resolve(host.Location).Path);

        var policy = host.Location.PolicyPath;
        var target = Path.Combine(host.TrustRoot, "elsewhere.json");
        File.Move(policy, target);
        File.CreateSymbolicLink(policy, target);
        var policyLink = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        StringAssert.Contains(policyLink.Message, "must not be a symbolic link", StringComparison.Ordinal);
        File.Delete(policy);

        var directory = Path.Combine(host.TrustRoot, "etc/hvo-sky");
        Directory.Move(directory, Path.Combine(host.TrustRoot, "etc/hvo-sky-real"));
        File.CreateSymbolicLink(directory, Path.Combine(host.TrustRoot, "etc/hvo-sky-real"));
        var directoryLink = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        StringAssert.Contains(directoryLink.Message, "must not be a symbolic link", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("{\"productRoot\":\"srv/data/hvo-skymonitor\"}", DisplayName = "relative")]
    [DataRow("{\"productRoot\":\"{trust}/srv/../srv/data\"}", DisplayName = "dot-dot")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data/\"}", DisplayName = "trailing-slash")]
    [DataRow("{\"productRoot\":\"{trust}//srv/data\"}", DisplayName = "double-slash")]
    [DataRow("{\"productRoot\":\"/\"}", DisplayName = "filesystem-root")]
    [DataRow("{\"productRoot\":\"\"}", DisplayName = "empty")]
    [DataRow("{\"productRoot\":null}", DisplayName = "null")]
    [DataRow("{}", DisplayName = "missing")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data\",\"owner\":\"root\"}", DisplayName = "unknown-member")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data\",\"productRoot\":\"{trust}/srv/other\"}", DisplayName = "conflicting-duplicate-root")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data\",\"productRoot\":\"{trust}/srv/data\"}", DisplayName = "repeated-duplicate-root")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data\",\"mountPoint\":\"/\",\"mountPoint\":\"{trust}\"}", DisplayName = "duplicate-mount-point")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data\",\"mountPoint\":\"{trust}/var\"}", DisplayName = "mount-point-not-an-ancestor")]
    [DataRow("{\"productRoot\":\"{trust}/srv/$HOME\"}", DisplayName = "compose-interpolation")]
    [DataRow("{\"productRoot\":\"{trust}/srv/data #note\"}", DisplayName = "compose-comment")]
    [DataRow("{\"productRoot\":\"{trust}/srv/a:b\"}", DisplayName = "compose-volume-separator")]
    [DataRow("[\"{trust}/srv/data\"]", DisplayName = "array")]
    [DataRow("productRoot={trust}/srv/data", DisplayName = "not-json")]
    public void Resolve_PolicyThatDoesNotNameAnAbsoluteNormalizedRoot_IsRefused(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var host = new PolicyHost();
        host.CreateDirectory("srv/data");
        host.WritePolicyText(content.Replace("{trust}", host.TrustRoot, StringComparison.Ordinal));

        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));

        StringAssert.Contains(refused.Message, host.Location.PolicyPath, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Resolve_PolicyLargerThanItsBound_IsRefused()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data");
        host.WritePolicyText($"{{\"productRoot\":\"{root}\"}}{new string(' ', HostProductRootPolicy.MaximumPolicyBytes)}");

        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));

        StringAssert.Contains(refused.Message, "exceeds", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Resolve_ProductRootThatDoesNotExistOrLiesOutsideTheTrustRoot_IsRefused()
    {
        using var host = new PolicyHost();
        host.CreateDirectory("srv/data");
        host.WritePolicy(Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor"));

        var missing = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        StringAssert.Contains(missing.Message, "does not exist", StringComparison.Ordinal);
        StringAssert.Contains(missing.Message, "mount its filesystem", StringComparison.Ordinal);

        host.WritePolicy("/srv/data/hvo-skymonitor");
        var outside = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        StringAssert.Contains(outside.Message, "must be below", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EnsureSingleMount_RefusesAMountBeneathTheRootAndAcceptsOneAtIt()
    {
        const string mountInfo =
            "22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw\n" +
            "30 22 259:2 / /srv/data rw,relatime shared:2 - ext4 /dev/nvme0n1p2 rw\n" +
            "31 30 259:3 / /srv/data2 rw,relatime shared:3 - ext4 /dev/nvme0n1p3 rw\n" +
            "32 30 0:50 / /srv/data/hvo\\040sky/state rw,relatime shared:4 - tmpfs tmpfs rw\n";

        // A mount exactly at the root keeps the whole root on that one mount, and a sibling sharing a name prefix is
        // not beneath it.
        HostProductRootPolicy.EnsureSingleMount("/srv/data", mountInfo.Replace(
            "32 30 0:50 / /srv/data/hvo\\040sky/state rw,relatime shared:4 - tmpfs tmpfs rw\n", string.Empty, StringComparison.Ordinal));
        HostProductRootPolicy.EnsureSingleMount("/srv/data/hvo-skymonitor", mountInfo);

        var parent = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.EnsureSingleMount("/srv", mountInfo));
        StringAssert.Contains(parent.Message, "must live on one mount", StringComparison.Ordinal);
        StringAssert.Contains(parent.Message, "'/srv/data'", StringComparison.Ordinal);

        var escaped = Assert.ThrowsExactly<InstallerException>(
            () => HostProductRootPolicy.EnsureSingleMount("/srv/data/hvo sky", mountInfo));
        StringAssert.Contains(escaped.Message, "'/srv/data/hvo sky/state'", StringComparison.Ordinal);

        Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.EnsureSingleMount("/srv/data", "malformed"));
        Assert.AreEqual("a\tb\\c d", HostProductRootPolicy.UnescapeMountPath("a\\011b\\134c\\040d"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Apply_WritesThePolicyCreatesTheFinalRootAndIsIdempotent()
    {
        using var host = new PolicyHost();
        host.CreateDirectory("srv/data");
        var root = Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor");

        var change = HostProductRootPolicy.Apply(host.Location, root);

        Assert.AreEqual(new HostPolicyChange(HostPolicyChange.Updated, host.DefaultRoot, root), change);
        Assert.AreEqual(SharedDirectory, File.GetUnixFileMode(root));
        Assert.AreEqual(SharedFile, File.GetUnixFileMode(host.Location.PolicyPath));
        Assert.AreEqual(SharedDirectory, File.GetUnixFileMode(Path.GetDirectoryName(host.Location.PolicyPath)!));
        using (var policy = JsonDocument.Parse(File.ReadAllText(host.Location.PolicyPath)))
        {
            Assert.AreEqual(root, policy.RootElement.GetProperty("productRoot").GetString());
            // The mount that holds the root is recorded so an unmounted filesystem fails closed later.
            Assert.AreEqual(MountPointHolding(root), policy.RootElement.GetProperty("mountPoint").GetString());
            Assert.AreEqual(2, policy.RootElement.EnumerateObject().Count());
        }
        Assert.AreEqual(new ProductionProductRoot(root, FromPolicy: true), HostProductRootPolicy.Resolve(host.Location));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(host.Location.PolicyPath)!, "*.tmp"));

        Assert.AreEqual(
            new HostPolicyChange(HostPolicyChange.Unchanged, root, root),
            HostProductRootPolicy.Apply(host.Location, root));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("cameraagents", DisplayName = "cameraagent")]
    [DataRow("logichosts", DisplayName = "logichost")]
    public void Apply_RefusesToMoveARootThatAlreadyHoldsInstances(string component)
    {
        using var host = new PolicyHost();
        var current = host.CreateDirectory("srv/data/current");
        var next = host.CreateDirectory("srv/data/next");
        host.WritePolicy(current);
        var namespaceRoot = host.CreateDirectory($"srv/data/current/{component}");

        // An empty namespace holds no instance, so the root may still move.
        Assert.AreEqual(HostPolicyChange.Updated, HostProductRootPolicy.Apply(host.Location, next).Outcome);
        host.WritePolicy(current);
        Directory.CreateDirectory(Path.Combine(namespaceRoot, Guid.NewGuid().ToString("D")));
        var policy = File.ReadAllText(host.Location.PolicyPath);

        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Apply(host.Location, next));

        StringAssert.Contains(refused.Message, "already holds instances", StringComparison.Ordinal);
        Assert.AreEqual(policy, File.ReadAllText(host.Location.PolicyPath));
        // Re-stating the root that holds the instances is not a move: it only records the hand-written policy's
        // missing mount point, and then reports no change.
        Assert.AreEqual(HostPolicyChange.Updated, HostProductRootPolicy.Apply(host.Location, current).Outcome);
        Assert.AreEqual(HostPolicyChange.Unchanged, HostProductRootPolicy.Apply(host.Location, current).Outcome);
        Assert.AreEqual(current, HostProductRootPolicy.Resolve(host.Location).Path);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Apply_RefusesToMoveADefaultRootThatHoldsInstances()
    {
        using var host = new PolicyHost();
        Directory.CreateDirectory(Path.Combine(host.DefaultRoot, "cameraagents", Guid.NewGuid().ToString("D")));
        var next = host.CreateDirectory("srv/data/hvo-skymonitor");

        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Apply(host.Location, next));

        StringAssert.Contains(refused.Message, host.DefaultRoot, StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(host.Location.PolicyPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Apply_RefusesAMissingOrLinkedParentAndAnUnacceptableRoot()
    {
        using var host = new PolicyHost();
        var missingParent = Assert.ThrowsExactly<InstallerException>(
            () => HostProductRootPolicy.Apply(host.Location, Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor")));
        StringAssert.Contains(missingParent.Message, "does not exist", StringComparison.Ordinal);

        var real = host.CreateDirectory("mnt/nvme");
        host.CreateDirectory("srv");
        File.CreateSymbolicLink(Path.Combine(host.TrustRoot, "srv/data"), real);
        var linked = Assert.ThrowsExactly<InstallerException>(
            () => HostProductRootPolicy.Apply(host.Location, Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor")));
        StringAssert.Contains(linked.Message, "must not be a symbolic link", StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Path.Combine(real, "hvo-skymonitor")));

        Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Apply(host.Location, "srv/data"));
        Assert.IsFalse(File.Exists(host.Location.PolicyPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_HostPolicyRoot_IsTheDefaultForEveryCommand()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/hvo-skymonitor");
        host.WritePolicy(root);
        var context = new CommandLineContext(AllowTestProductRoot: false, host.Location);
        var instance = Guid.NewGuid().ToString("D");
        string[][] commands =
        [
            ["cameraagent", "install", "--friendly-name", "North Camera", "--owner-email", "admin@example.test",
                "--catalog-bundle", "/srv/hvo/catalog.bundle", "--image-ref", ImageReference],
            ["status"],
            ["status", "--instance-id", instance],
            ["cameraagent", "upgrade", "--instance-id", instance, "--image-ref", ImageReference],
            ["cameraagent", "rollback", "--instance-id", instance],
            ["cameraagent", "reinstall", "--instance-id", instance],
            ["cameraagent", "uninstall", "--instance-id", instance],
            ["cameraagent", "purge", "--instance-id", instance, "--confirm-instance-id", instance],
            ["cameraagent", "preflight", "--instance-id", instance, "--image-ref", ImageReference],
            ["cameraagent", "reset-state", "--instance-id", instance, "--confirm-instance-id", instance],
            ["cameraagent", "recover-owner", "--instance-id", instance, "--generate-password"],
            ["catalog", "install", "--catalog-bundle", "/srv/hvo/catalog.bundle"],
            ["catalog", "select", "--instance-id", instance, "--catalog-version", "hyg-v4.2-p3-s2-r1"],
            ["catalog", "rollback", "--instance-id", instance],
            ["catalog", "gc", "--dry-run"],
            ["cameraagent", "upgrade", "--instance-id", instance, "--release", "/srv/hvo/release-v1.2.0-dev.3"]
        ];

        foreach (var arguments in commands)
        {
            var command = CommandLine.ParseCommand(arguments, context);
            var productRoot = command switch
            {
                InstallDeploymentCommand install => install.Request.ProductRoot,
                LifecycleRequest lifecycle => lifecycle.ProductRoot,
                CameraAgentStatePreflightRequest preflight => preflight.ProductRoot,
                CameraAgentStateResetRequest reset => reset.ProductRoot,
                OwnerRecoveryRequest recovery => recovery.ProductRoot,
                _ => throw new AssertFailedException($"Unexpected command {command.GetType().Name}.")
            };
            var label = string.Join(' ', arguments.Take(2));
            Assert.AreEqual(root, productRoot, label);
            Assert.AreEqual(root, command.ProductionProductRoot, label);
            if (command is InstallDeploymentCommand installCommand)
            {
                Assert.AreEqual(root, installCommand.Request.ProductionProductRoot);
                Assert.IsTrue(installCommand.Request.IsProductionRoot);
            }
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_HostPolicyRoot_ReplacesTheDefaultRootAsTheOnlyProductionRoot()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data/hvo-skymonitor");
        host.WritePolicy(root);
        string[] status = ["status", "--product-root", InstallRequest.DefaultProductRoot];

        var refused = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.ParseCommand(status, new CommandLineContext(false, host.Location)));
        StringAssert.Contains(refused.Message, root, StringComparison.Ordinal);
        Assert.AreEqual(
            InstallRequest.DefaultProductRoot,
            ((LifecycleRequest)CommandLine.ParseCommand(status, new CommandLineContext(true, host.Location))).ProductRoot);

        // A configuration file that names no product root installs into the policy root; one that names the old
        // default is refused rather than silently redirected.
        var configPath = Path.Combine(host.TrustRoot, "install.json");
        var install = new InstallRequest
        {
            FriendlyName = "North Camera",
            OwnerEmail = "admin@example.test",
            CatalogBundle = "/srv/hvo/catalog.bundle",
            ImageReference = ImageReference
        };
        var named = JsonSerializer.Serialize(install, InstallRequestJsonContext.Default.InstallRequest);
        using (var document = JsonDocument.Parse(named))
        {
            var unnamed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject().Where(static property => property.Name != "productRoot"))
            {
                unnamed[property.Name] = property.Value.Clone();
            }
            File.WriteAllText(configPath, JsonSerializer.Serialize(unnamed));
        }
        var fromConfig = (InstallDeploymentCommand)CommandLine.ParseCommand(
            ["cameraagent", "install", "--config", configPath], new CommandLineContext(false, host.Location));
        Assert.AreEqual(root, fromConfig.Request.ProductRoot);

        File.WriteAllText(configPath, named);
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
            ["cameraagent", "install", "--config", configPath], new CommandLineContext(false, host.Location)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_InvalidHostPolicy_FailsEveryCommandClosed()
    {
        using var host = new PolicyHost();
        host.WritePolicyText("{\"productRoot\":\"relative\"}");

        foreach (var arguments in new[] { new[] { "status" }, ["cameraagent", "uninstall", "--instance-id", Guid.NewGuid().ToString("D")] })
        {
            var refused = Assert.ThrowsExactly<InstallerException>(
                () => CommandLine.ParseCommand(arguments, new CommandLineContext(false, host.Location)));
            StringAssert.Contains(refused.Message, host.Location.PolicyPath, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_Help_IsAnsweredWithoutReadingARefusedHostPolicy()
    {
        using var host = new PolicyHost();
        host.WritePolicyText("{\"productRoot\":\"relative\"}");
        var context = new CommandLineContext(false, host.Location);

        Assert.AreEqual(CommandLine.GeneralUsage(), ((HelpCommand)CommandLine.ParseCommand(["--help"], context)).Usage);
        StringAssert.StartsWith(
            ((HelpCommand)CommandLine.ParseCommand(["status", "--help"], context)).Usage,
            "Usage: hvo-sky-install status [options]",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<InstallerException>(() => CommandLine.ParseCommand(["status"], context));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_Version_IsAnsweredWithoutReadingARefusedHostPolicy()
    {
        using var host = new PolicyHost();
        host.WritePolicyText("{\"productRoot\":\"relative\"}");
        var context = new CommandLineContext(false, host.Location);

        var version = (VersionCommand)CommandLine.ParseCommand(["--version"], context);

        Assert.AreEqual(InstallerBuildIdentity.Current, version.Identity);
        Assert.IsFalse(version.Json);
        // Anything after it is not a version request, so it reaches the policy that is refused here.
        Assert.ThrowsExactly<InstallerException>(() => CommandLine.ParseCommand(["--version", "--json"], context));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_HostSetProductRoot_CarriesThePolicyLocationAndRejectsMalformedUse()
    {
        using var host = new PolicyHost();
        var context = new CommandLineContext(false, host.Location);
        var root = Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor");

        var request = (HostProductRootRequest)CommandLine.ParseCommand(["host", "set-product-root", root, "--json"], context);

        Assert.AreEqual(root, request.ProductRoot);
        Assert.IsTrue(request.Json);
        Assert.AreEqual(host.Location, request.PolicyLocation);
        Assert.AreEqual(host.DefaultRoot, request.ProductionProductRoot);
        Assert.AreEqual(HostPolicyLocation.Production, ((HostProductRootRequest)CommandLine.ParseCommand(
            ["host", "set-product-root", "/srv/data/hvo-skymonitor"], allowTestProductRoot: false)).PolicyLocation);

        foreach (var arguments in new[]
                 {
                     new[] { "host" },
                     ["host", "set-product-root"],
                     ["host", "set-product-root", "--json"],
                     ["host", "show"],
                     ["host", "set-product-root", "srv/data"],
                     ["host", "set-product-root", "/srv/data/"],
                     ["host", "set-product-root", "/"],
                     ["host", "set-product-root", root, "--force"],
                     ["host", "set-product-root", root, "extra"],
                     ["host", "set-product-root", root, "--json", "--json"]
                 })
        {
            Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments, context), string.Join(' ', arguments));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SetProductRootAsync_TestLocation_WritesInProcessAndReportsTheChange()
    {
        using var host = new PolicyHost();
        host.CreateDirectory("srv/data");
        var root = Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor");
        var request = (HostProductRootRequest)CommandLine.ParseCommand(
            ["host", "set-product-root", root], new CommandLineContext(false, host.Location));
        var runner = new RecordingRunner(new ProcessResult(0, string.Empty, string.Empty));

        var result = await PrivilegedHostPolicy.SetProductRootAsync(request, runner, NativeLinux.getuid(), CancellationToken.None);

        Assert.AreEqual(new HostPolicyResult(1, HostPolicyChange.Updated, host.Location.PolicyPath, host.DefaultRoot, root), result);
        Assert.IsEmpty(runner.Invocations);
        Assert.AreEqual(root, HostProductRootPolicy.Resolve(host.Location).Path);
    }

    [TestMethod]
    public async Task SetProductRootAsync_ProductionLocation_EscalatesOnlyThroughTheNarrowHelper()
    {
        var request = new HostProductRootRequest("/srv/data/hvo-skymonitor", Json: false);
        var runner = new RecordingRunner(new ProcessResult(1, string.Empty, "sudo: sorry, user roys may not run this command"));

        var failed = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => PrivilegedHostPolicy.SetProductRootAsync(request, runner, uid: 1000, CancellationToken.None));

        StringAssert.Contains(failed.Message, "Privileged host policy update failed: sudo: sorry, user roys may not run this command", StringComparison.Ordinal);
        var (fileName, arguments) = runner.Invocations.Single();
        Assert.AreEqual("sudo", fileName);
        CollectionAssert.AreEqual(
            new[] { "--", $"/proc/{Environment.ProcessId}/exe", PrivilegedHostPolicy.CommandName, "--product-root", "/srv/data/hvo-skymonitor" },
            arguments.ToArray());

        var silent = new RecordingRunner(new ProcessResult(0, "done", string.Empty));
        var unreported = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => PrivilegedHostPolicy.SetProductRootAsync(request, silent, uid: 1000, CancellationToken.None));
        StringAssert.Contains(unreported.Message, "did not report its outcome", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PrivilegedHostPolicyHelper_ParsesOnlyTheExactCommandAndRequiresRoot()
    {
        var helper = PrivilegedHostPolicy.HelperArguments("/srv/data/hvo-skymonitor").Skip(2).ToArray();

        Assert.IsTrue(PrivilegedHostPolicy.TryParse(helper, out var productRoot));
        Assert.AreEqual("/srv/data/hvo-skymonitor", productRoot);
        foreach (var (index, value) in new (int, string)[]
                 {
                     (0, "internal-prepare"), (1, "--policy"), (2, "srv/data"), (2, "/srv/data/"), (2, "/"), (2, "/srv/../etc")
                 })
        {
            var changed = (string[])helper.Clone();
            changed[index] = value;
            Assert.IsFalse(PrivilegedHostPolicy.TryParse(changed, out _), $"{index}={value}");
        }
        Assert.IsFalse(PrivilegedHostPolicy.TryParse([.. helper, "--policy", "/tmp/host.json"], out _));
        if (NativeLinux.getuid() != 0)
        {
            Assert.AreEqual(2, PrivilegedHostPolicy.RunInternal(helper));
        }
    }

    [TestMethod]
    public void PrivilegedHelpers_DeriveTheirRootOwnedNamespaceFromTheResolvedRoot()
    {
        var policy = new ProductionProductRoot("/srv/data/hvo-skymonitor", FromPolicy: true);

        string[] defaultNamespaces =
        [
            "/var/lib/hvo-sky", "/var/lib/hvo-sky/cameraagents",
            "/var/lib/hvo-sky/logichosts", "/var/lib/hvo-sky/catalogs"
        ];
        string[] policyNamespaces =
        [
            "/srv/data/hvo-skymonitor", "/srv/data/hvo-skymonitor/cameraagents",
            "/srv/data/hvo-skymonitor/logichosts", "/srv/data/hvo-skymonitor/catalogs"
        ];
        CollectionAssert.AreEqual(
            defaultNamespaces, PrivilegedPreparation.RootOwnedNamespaces(InstallRequest.DefaultProductRoot).ToArray());
        CollectionAssert.AreEqual(
            policyNamespaces, PrivilegedPreparation.RootOwnedNamespaces(policy.Path).ToArray());

        Assert.AreSame(PrivilegedPurgeNamespace.DefaultNamespaceComponents, PrivilegedPurgeNamespace.NamespaceComponents(ProductionProductRoot.Default));
        CollectionAssert.AreEqual(
            new[] { ("srv", true), ("data", true), ("hvo-skymonitor", true), ("cameraagents", true) },
            PrivilegedPurgeNamespace.NamespaceComponents(policy).ToArray());

        var identity = new UnixNodeIdentity(1000, 1001, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, 0x4000, 2, 4242, 8, 1, 77);
        var helper = PrivilegedPurgeNamespace.HelperArguments(
            PrivilegedPurgeNamespace.Action.Quarantine, policy.Path, Guid.NewGuid(), Guid.NewGuid(), identity).Skip(2).ToArray();
        Assert.IsTrue(PrivilegedPurgeNamespace.TryParse(helper, policy.Path, out _));
        // The helper accepts only the root the policy resolves, so the default root is refused once a policy names another.
        Assert.IsFalse(PrivilegedPurgeNamespace.TryParse(helper, InstallRequest.DefaultProductRoot, out _));
        helper[4] = InstallRequest.DefaultProductRoot;
        Assert.IsFalse(PrivilegedPurgeNamespace.TryParse(helper, policy.Path, out _));
    }

    [TestMethod]
    [DataRow("/srv/$HOME/hvo", DisplayName = "dollar")]
    [DataRow("/srv/data #comment", DisplayName = "space-hash")]
    [DataRow("/srv/a:b", DisplayName = "colon")]
    [DataRow("/srv/it's", DisplayName = "single-quote")]
    [DataRow("/srv/\"q\"", DisplayName = "double-quote")]
    [DataRow("/srv/a\\b", DisplayName = "backslash")]
    [DataRow("/srv/a=b", DisplayName = "equals")]
    [DataRow("/srv/{x}", DisplayName = "brace")]
    [DataRow("/srv/a*b", DisplayName = "glob")]
    [DataRow("/srv/a;b", DisplayName = "semicolon")]
    [DataRow("/srv/d\u00e9j\u00e0", DisplayName = "non-ascii")]
    [DataRow("/srv/-data", DisplayName = "leading-dash")]
    public void ProductRootTextError_RejectsCharactersComposeWouldNotCarryLiterally(string root)
    {
        StringAssert.Contains(HostProductRootPolicy.ProductRootTextError(root), "segments may contain only", StringComparison.Ordinal);
        Assert.IsNull(HostProductRootPolicy.ProductRootTextError("/srv/data-1/hvo_sky.monitor/A9"));
        Assert.IsNull(HostProductRootPolicy.ProductRootTextError(InstallRequest.DefaultProductRoot));
    }

    /// <summary>
    /// The generated environment file and Compose model carry an accepted root literally: every bind source and every
    /// root variable names exactly the root, with nothing interpolated, commented out, or split.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ComposeRendering_AcceptedRootCharacters_AreCarriedLiterally()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data-1/hvo_sky.monitor-A9");
        Assert.IsNull(HostProductRootPolicy.ProductRootTextError(root));
        var paths = InstallationPaths.Create(root, Guid.NewGuid(), HygV42.CatalogId);

        var compose = WriteCompose(paths);

        var environment = File.ReadAllLines(compose.EnvironmentFile).ToDictionary(
            static line => line[..line.IndexOf('=', StringComparison.Ordinal)],
            static line => line[(line.IndexOf('=', StringComparison.Ordinal) + 1)..],
            StringComparer.Ordinal);
        Assert.AreEqual(paths.ConfigRoot, environment["HVO_CONFIG_ROOT"]);
        Assert.AreEqual(paths.StateRoot, environment["HVO_STATE_ROOT"]);
        Assert.AreEqual(paths.CatalogRoot, environment["HVO_CATALOG_ROOT"]);
        Assert.IsTrue(environment.Values.Where(value => value.StartsWith(root, StringComparison.Ordinal))
            .All(static value => value.All(static character => !char.IsWhiteSpace(character) && character is not ('$' or '#' or ':' or '"' or '\''))));
    }

    /// <summary>
    /// The real Compose implementation renders every bind source of an accepted root exactly. It needs the Docker CLI
    /// and Compose plugin but no daemon, so it runs with the Unit lane's invalid endpoint.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ComposeConfig_AcceptedRootCharacters_RenderEveryBindSourceExactly()
    {
        var runner = new ProcessRunner();
        ProcessResult version;
        try
        {
            version = await runner.RunAsync("docker", ["compose", "version", "--short"], CancellationToken.None);
        }
        catch (InstallerException)
        {
            version = new ProcessResult(127, string.Empty, "docker is unavailable");
        }
        if (version.ExitCode != 0)
        {
            Assert.Inconclusive("The Docker CLI with the Compose plugin is required to render the Compose model.");
        }

        using var host = new PolicyHost();
        var root = host.CreateDirectory("srv/data-1/hvo_sky.monitor-A9");
        var paths = InstallationPaths.Create(root, Guid.NewGuid(), HygV42.CatalogId);
        var compose = WriteCompose(paths);

        var rendered = await runner.RunAsync(
            "docker",
            ["compose", "--project-name", compose.ProjectName, "--env-file", compose.EnvironmentFile, "--file", compose.ComposeFile,
                "config", "--format", "json"],
            CancellationToken.None);

        Assert.AreEqual(0, rendered.ExitCode, rendered.StandardError);
        using var model = JsonDocument.Parse(rendered.StandardOutput);
        var sources = model.RootElement.GetProperty("services").EnumerateObject()
            .SelectMany(static service => service.Value.GetProperty("volumes").EnumerateArray())
            .Where(static volume => volume.GetProperty("type").GetString() == "bind")
            .Select(static volume => volume.GetProperty("source").GetString()!)
            .ToArray();
        string[] expected =
        [
            Path.Combine(paths.ConfigRoot, "secrets"),
            Path.Combine(paths.ConfigRoot, "camera-module.json"),
            paths.CatalogRoot,
            Path.Combine(paths.StateRoot, "identity"),
            Path.Combine(paths.StateRoot, "raw")
        ];
        CollectionAssert.IsSubsetOf(expected, sources);
        Assert.IsTrue(sources.All(source => source.StartsWith(root + "/", StringComparison.Ordinal)), string.Join(", ", sources));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PolicyChangeAndPrivilegedPreparation_ShareOneLock_SoAnInstanceCreatedUnderTheOldRootStopsTheMove()
    {
        using var host = new PolicyHost();
        var current = host.CreateDirectory("srv/data/current");
        var next = host.CreateDirectory("srv/data/next");
        host.WritePolicy(current);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? instance = null;

        // Preparation resolves the policy and creates the instance while holding the lock.
        var preparation = Task.Run(() => PrivilegedPreparation.PrepareUnderHostPolicy(host.Location, current, production =>
        {
            Assert.AreEqual(current, production.Path);
            entered.Set();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(30)), "The test never released the preparation.");
            instance = Path.Combine(current, "cameraagents", Guid.NewGuid().ToString("D"));
            Directory.CreateDirectory(instance);
        }));
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(30)), "Preparation never took the host-policy lock.");

        // The policy change waits for the same lock instead of checking the still-empty root and publishing.
        var change = Task.Run(() => HostProductRootPolicy.Apply(host.Location, next));
        Assert.IsFalse(change.Wait(TimeSpan.FromMilliseconds(500)), "The policy change did not wait for the preparation lock.");
        StringAssert.Contains(File.ReadAllText(host.Location.PolicyPath), current, StringComparison.Ordinal);

        release.Set();
        Assert.AreEqual(0, preparation.GetAwaiter().GetResult());
        var refused = Assert.ThrowsExactly<InstallerException>(() => change.GetAwaiter().GetResult());
        StringAssert.Contains(refused.Message, "already holds instances", StringComparison.Ordinal);
        Assert.AreEqual(current, HostProductRootPolicy.Resolve(host.Location).Path);
        Assert.IsTrue(Directory.Exists(instance));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PrivilegedPreparation_WaitsForAHeldPolicyLockAndRefusesARootThePolicyNoLongerNames()
    {
        using var host = new PolicyHost();
        var current = host.CreateDirectory("srv/data/current");
        var next = host.CreateDirectory("srv/data/next");
        host.WritePolicy(current);
        var prepared = 0;

        Task<int> preparation;
        using (HostProductRootPolicy.AcquireLock(host.Location))
        {
            preparation = Task.Run(() => PrivilegedPreparation.PrepareUnderHostPolicy(
                host.Location, current, _ => Interlocked.Increment(ref prepared)));
            Assert.IsFalse(preparation.Wait(TimeSpan.FromMilliseconds(500)), "Preparation did not wait for the held lock.");
            Assert.AreEqual(0, prepared);
        }
        Assert.AreEqual(0, preparation.GetAwaiter().GetResult());
        Assert.AreEqual(1, prepared);

        // Once the policy names another root, preparation for the old root is refused before anything is created.
        Assert.AreEqual(HostPolicyChange.Updated, HostProductRootPolicy.Apply(host.Location, next).Outcome);
        Assert.AreEqual(2, PrivilegedPreparation.PrepareUnderHostPolicy(
            host.Location, current, _ => throw new AssertFailedException("The old root was prepared.")));
        Assert.AreEqual(0, PrivilegedPreparation.PrepareUnderHostPolicy(host.Location, next, _ => { }));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void AcquireLock_RefusesALockFileOthersCouldOpenOrReplace()
    {
        using var host = new PolicyHost();
        using (HostProductRootPolicy.AcquireLock(host.Location))
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(host.Location.LockPath));
            var busy = Assert.ThrowsExactly<InstallerException>(
                () => HostProductRootPolicy.AcquireLock(host.Location, TimeSpan.FromMilliseconds(200)));
            StringAssert.Contains(busy.Message, "is busy", StringComparison.Ordinal);
        }

        File.SetUnixFileMode(host.Location.LockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var readable = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.AcquireLock(host.Location));
        StringAssert.Contains(readable.Message, "mode 0600", StringComparison.Ordinal);

        File.Delete(host.Location.LockPath);
        File.CreateSymbolicLink(host.Location.LockPath, Path.Combine(host.TrustRoot, "elsewhere.lock"));
        Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.AcquireLock(host.Location));
        Assert.IsFalse(File.Exists(Path.Combine(host.TrustRoot, "elsewhere.lock")));
    }

    [TestMethod]
    public void PrivilegedPreparationHelper_ParsesTheInstanceAndProductFormsExactly()
    {
        var instanceId = Guid.NewGuid();
        var instance = PrivilegedPreparation.HelperArguments(
            new PrivilegedPreparation.Invocation("/srv/data/hvo-skymonitor", instanceId, 1000, 1001)).Skip(2).ToArray();
        var product = PrivilegedPreparation.HelperArguments(
            new PrivilegedPreparation.Invocation("/srv/data/hvo-skymonitor", null, 1000, 1001)).Skip(2).ToArray();
        string[] productForm = ["internal-prepare", "--product-root", "/srv/data/hvo-skymonitor", "--uid", "1000", "--gid", "1001"];

        CollectionAssert.AreEqual(productForm, product);
        Assert.IsTrue(PrivilegedPreparation.TryParse(instance, out var parsedInstance));
        Assert.AreEqual(new PrivilegedPreparation.Invocation("/srv/data/hvo-skymonitor", instanceId, 1000, 1001), parsedInstance);
        Assert.IsTrue(PrivilegedPreparation.TryParse(product, out var parsedProduct));
        Assert.AreEqual(new PrivilegedPreparation.Invocation("/srv/data/hvo-skymonitor", null, 1000, 1001), parsedProduct);
        foreach (var (arguments, index, value) in new (string[], int, string)[]
                 {
                     (product, 0, "internal-purge-namespace"), (product, 1, "--root"), (product, 3, "--instance-id"),
                     (product, 4, "0"), (product, 4, "+1000"), (product, 6, "-1"), (instance, 3, "--uid"),
                     (instance, 4, instanceId.ToString("N"))
                 })
        {
            var changed = (string[])arguments.Clone();
            changed[index] = value;
            Assert.IsFalse(PrivilegedPreparation.TryParse(changed, out _), $"{index}={value}");
        }
        Assert.IsFalse(PrivilegedPreparation.TryParse([.. product, "--force"], out _));
        Assert.IsFalse(PrivilegedPreparation.TryParse(product[..^1], out _));
        // A backup's folder alone (#1353 PR-1369-R3 F20): never with an instance.
        var backups = PrivilegedPreparation.HelperArguments(
            new PrivilegedPreparation.Invocation("/srv/data/hvo-skymonitor", instanceId, 1000, 1001, Backups: true)).Skip(2).ToArray();
        string[] backupsForm = ["internal-prepare", "--product-root", "/srv/data/hvo-skymonitor", "--backups", "--uid", "1000", "--gid", "1001"];
        CollectionAssert.AreEqual(backupsForm, backups);
        Assert.IsTrue(PrivilegedPreparation.TryParse(backups, out var parsedBackups));
        Assert.AreEqual(new PrivilegedPreparation.Invocation("/srv/data/hvo-skymonitor", null, 1000, 1001, Backups: true), parsedBackups);
        Assert.IsFalse(PrivilegedPreparation.TryParse([.. backups[..4], "--instance-id", instanceId.ToString("D"), .. backups[4..]], out _));
        Assert.IsFalse(PrivilegedPreparation.TryParse([.. backups[..3], "--backup", .. backups[4..]], out _));
        if (NativeLinux.getuid() != 0)
        {
            Assert.AreEqual(2, PrivilegedPreparation.RunInternal(product));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Apply_RecordsTheMountHoldingTheRootAndResolveFailsClosedWhenItIsNotMounted()
    {
        using var host = new PolicyHost();
        host.CreateDirectory("srv/data");
        var root = Path.Combine(host.TrustRoot, "srv/data/hvo-skymonitor");
        Assert.AreEqual(HostPolicyChange.Updated, HostProductRootPolicy.Apply(host.Location, root).Outcome);
        Assert.AreEqual(root, HostProductRootPolicy.Resolve(host.Location).Path);

        // A filesystem that was mounted at the root's ancestor and is now absent: the directories remain on the parent
        // filesystem, but nothing is mounted where the policy says the root's filesystem lives.
        var unmounted = Path.Combine(host.TrustRoot, "srv");
        host.WritePolicyText(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["productRoot"] = root,
            ["mountPoint"] = unmounted
        }));
        var refused = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.Resolve(host.Location));
        StringAssert.Contains(refused.Message, $"mounted at '{unmounted}', but nothing is mounted there", StringComparison.Ordinal);

        // A hand-written policy without a mount point is upgraded by restating the same root.
        host.WritePolicy(root);
        Assert.AreEqual(HostPolicyChange.Updated, HostProductRootPolicy.Apply(host.Location, root).Outcome);
        Assert.AreEqual(HostPolicyChange.Unchanged, HostProductRootPolicy.Apply(host.Location, root).Outcome);
        using var policy = JsonDocument.Parse(File.ReadAllText(host.Location.PolicyPath));
        Assert.AreEqual(MountPointHolding(root), policy.RootElement.GetProperty("mountPoint").GetString());
    }

    [TestMethod]
    public void EnsureMountedOn_RequiresTheRootToResolveThroughTheRecordedMount()
    {
        const string mountInfo =
            "22 1 179:2 / / rw,relatime shared:1 - ext4 /dev/mmcblk0p2 rw\n" +
            "30 22 259:2 / /srv rw,relatime shared:2 - ext4 /dev/nvme0n1p2 rw\n" +
            "41 30 259:3 / /srv/data rw,relatime shared:3 - ext4 /dev/nvme0n1p3 rw\n";

        HostProductRootPolicy.EnsureMountedOn("/srv/hvo-skymonitor", 30, "/srv", mountInfo);
        Assert.AreEqual("/srv", HostProductRootPolicy.MountPointOf(30, mountInfo));
        Assert.AreEqual("/", HostProductRootPolicy.MountPointOf(22, mountInfo));

        // /srv unmounted: the root's directory now resolves through the root filesystem.
        var absent = Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.EnsureMountedOn(
            "/srv/hvo-skymonitor", 22, "/srv", mountInfo.Replace("30 22 259:2 / /srv rw", "99 22 0:1 / /mnt rw", StringComparison.Ordinal)));
        StringAssert.Contains(absent.Message, "nothing is mounted there", StringComparison.Ordinal);

        // Mounted, but the root resolves through another mount (something mounted over part of the path).
        var other = Assert.ThrowsExactly<InstallerException>(
            () => HostProductRootPolicy.EnsureMountedOn("/srv/data/hvo-skymonitor", 41, "/srv", mountInfo));
        StringAssert.Contains(other.Message, "is not on the filesystem mounted at '/srv'", StringComparison.Ordinal);
        Assert.ThrowsExactly<InstallerException>(() => HostProductRootPolicy.MountPointOf(77, mountInfo));
    }

    private static string MountPointHolding(string path)
    {
        var mountId = NativeLinux.GetNodeIdentity(path).MountId;
        return HostProductRootPolicy.MountPointOf(mountId, File.ReadAllText(HostProductRootPolicy.MountInfoPath));
    }

    private static ComposeFiles WriteCompose(InstallationPaths paths)
    {
        var request = new InstallRequest
        {
            FriendlyName = "Compose Path",
            OwnerEmail = "owner@example.test",
            ProductRoot = paths.ProductRoot,
            ImageReference = $"sha256:{new string('a', 64)}",
            CatalogBundle = Path.Combine(paths.ProductRoot, "catalog.bundle")
        };
        Directory.CreateDirectory(paths.CatalogRoot);
        return ComposeDeployment.Write(
            request,
            paths,
            Guid.Parse(Path.GetFileName(paths.InstanceRoot)),
            Guid.NewGuid(),
            NativeLinux.getuid(),
            NativeLinux.getgid(),
            $"sha256:{new string('a', 64)}",
            HygV42.PackageVersion,
            Path.Combine(paths.ConfigRoot, "owner-password"),
            "verification-token",
            "lifecycle-token",
            passwordAuthorityEnabled: false,
            CameraAgentContainerResources.Default);
    }

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments));
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A private trust root owned by the test user with mode 0755, standing in for <c>/</c>. Its policy location
    /// requires the test user's ownership exactly as production requires <c>root:root</c>.
    /// </summary>
    private sealed class PolicyHost : IDisposable
    {
        public PolicyHost()
        {
            TrustRoot = Path.Combine(Path.GetTempPath().TrimEnd('/'), $"hvo-host-policy-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TrustRoot);
            File.SetUnixFileMode(TrustRoot, SharedDirectory);
            DefaultRoot = Path.Combine(TrustRoot, "var/lib/hvo-sky");
            Location = new HostPolicyLocation(
                Path.Combine(TrustRoot, "etc/hvo-sky/host.json"),
                TrustRoot,
                NativeLinux.getuid(),
                NativeLinux.getgid(),
                DefaultRoot,
                Path.Combine(TrustRoot, "run/hvo-sky-host-policy.lock"));
            CreateDirectory("run");
        }

        public string TrustRoot { get; }

        public string DefaultRoot { get; }

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

        public void WritePolicy(string productRoot)
            => WritePolicyText(JsonSerializer.Serialize(new Dictionary<string, string> { ["productRoot"] = productRoot }));

        public void WritePolicyText(string content)
        {
            CreateDirectory("etc/hvo-sky");
            File.WriteAllText(Location.PolicyPath, content);
            File.SetUnixFileMode(Location.PolicyPath, SharedFile);
        }

        public void Dispose()
        {
            if (Directory.Exists(TrustRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(TrustRoot, "*", SearchOption.AllDirectories))
                {
                    if (new DirectoryInfo(directory).LinkTarget is null)
                    {
                        File.SetUnixFileMode(directory, SharedDirectory);
                    }
                }
                Directory.Delete(TrustRoot, recursive: true);
            }
        }
    }
}
