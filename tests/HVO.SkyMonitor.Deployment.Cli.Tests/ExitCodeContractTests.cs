using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The installer's exit codes, pinned for every command (install contract section 5, #1315): 0 done, 1 a step failed,
/// 2 the command line or answers invalid, 3 refused with nothing changed, 130 interrupted. Each row runs the real
/// <see cref="Program.RunAsync"/>. Codes 2 and 130, and the refusal under a host policy that cannot be trusted, run every
/// command's real parser and real executor; codes 0, 1, 3 and the interruption of a running step substitute the executor
/// with one that returns or throws, so the one mapping every command shares is pinned without a Docker host. Where each
/// real gate refuses is pinned by the flow and lifecycle tests.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ExitCodeContractTests
{
    private const UnixFileMode SharedDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const string Id = "5a3f7c1e-2b4d-4e6f-8a9b-0c1d2e3f4a5b";
    private const string Archive = "hvo-sky-5a3f7c1e2b4d4e6f8a9b0c1d2e3f4a5b-20261018T073000Z-11223344.tar.gz";
    private static readonly string ImageRef = $"sha256:{new string('b', 64)}";

    /// <summary>Every command the parser accepts, with arguments it accepts.</summary>
    private static readonly (string Name, string[] Arguments)[] Commands =
    [
        ("partial-prepare-cleanup", ["partial-prepare-cleanup", "--inventory", "/private/inventory.json", "--mode", "isolated", "--run-id", "failed-run", "--dry-run"]),
        ("status", ["status", "--instance-id", Id]),
        ("cameraagent install",
            ["cameraagent", "install", "--friendly-name", "North Camera", "--catalog-bundle", "/srv/hvo/catalog.bundle", "--image-ref", ImageRef]),
        ("cameraagent recover-owner", ["cameraagent", "recover-owner", "--instance-id", Id, "--generate-password"]),
        ("cameraagent preflight", ["cameraagent", "preflight", "--instance-id", Id, "--image-ref", ImageRef]),
        ("cameraagent reset-state", ["cameraagent", "reset-state", "--instance-id", Id, "--confirm-instance-id", Id]),
        ("cameraagent upgrade", ["cameraagent", "upgrade", "--instance-id", Id, "--image-ref", ImageRef]),
        ("cameraagent rollback", ["cameraagent", "rollback", "--instance-id", Id]),
        ("cameraagent reinstall", ["cameraagent", "reinstall", "--instance-id", Id]),
        ("cameraagent uninstall", ["cameraagent", "uninstall", "--instance-id", Id]),
        ("cameraagent purge", ["cameraagent", "purge", "--instance-id", Id, "--confirm-instance-id", Id]),
        ("cameraagent backup", ["cameraagent", "backup", "--instance-id", Id]),
        ("cameraagent restore", ["cameraagent", "restore", Archive, "--instance-id", Id]),
        ("catalog install", ["catalog", "install", "--catalog-bundle", "/srv/hvo/catalog.bundle"]),
        ("catalog select", ["catalog", "select", "--instance-id", Id, "--catalog-version", "hyg-v4.2-p3-s2-r1"]),
        ("catalog rollback", ["catalog", "rollback", "--instance-id", Id]),
        ("catalog gc", ["catalog", "gc", "--dry-run"]),
        ("host set-product-root", ["host", "set-product-root", "/srv/data/hvo-skymonitor"])
    ];

    /// <summary>
    /// Every command the parser accepts has its rows below: the matrix and the real-path tests list each one (a
    /// <c>DataRow</c> per command and code, so the category audit discovers every case), and this test fails when a
    /// command is added without them.
    /// </summary>
    [TestMethod]
    public void Commands_CoverEveryCommandTheParserAccepts()
        => CollectionAssert.AreEqual(
            CommandLine.Commands.Select(static command => command.Name).ToArray(),
            Commands.Select(static command => command.Name).ToArray());

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("partial-prepare-cleanup", "done", 0)]
    [DataRow("partial-prepare-cleanup", "failed", 1)]
    [DataRow("partial-prepare-cleanup", "refused", 3)]
    [DataRow("partial-prepare-cleanup", "interrupted", 130)]
    [DataRow("status", "done", 0)]
    [DataRow("status", "failed", 1)]
    [DataRow("status", "refused", 3)]
    [DataRow("status", "interrupted", 130)]
    [DataRow("cameraagent install", "done", 0)]
    [DataRow("cameraagent install", "failed", 1)]
    [DataRow("cameraagent install", "refused", 3)]
    [DataRow("cameraagent install", "interrupted", 130)]
    [DataRow("cameraagent recover-owner", "done", 0)]
    [DataRow("cameraagent recover-owner", "failed", 1)]
    [DataRow("cameraagent recover-owner", "refused", 3)]
    [DataRow("cameraagent recover-owner", "interrupted", 130)]
    [DataRow("cameraagent preflight", "done", 0)]
    [DataRow("cameraagent preflight", "failed", 1)]
    [DataRow("cameraagent preflight", "refused", 3)]
    [DataRow("cameraagent preflight", "interrupted", 130)]
    [DataRow("cameraagent reset-state", "done", 0)]
    [DataRow("cameraagent reset-state", "failed", 1)]
    [DataRow("cameraagent reset-state", "refused", 3)]
    [DataRow("cameraagent reset-state", "interrupted", 130)]
    [DataRow("cameraagent upgrade", "done", 0)]
    [DataRow("cameraagent upgrade", "failed", 1)]
    [DataRow("cameraagent upgrade", "refused", 3)]
    [DataRow("cameraagent upgrade", "interrupted", 130)]
    [DataRow("cameraagent rollback", "done", 0)]
    [DataRow("cameraagent rollback", "failed", 1)]
    [DataRow("cameraagent rollback", "refused", 3)]
    [DataRow("cameraagent rollback", "interrupted", 130)]
    [DataRow("cameraagent reinstall", "done", 0)]
    [DataRow("cameraagent reinstall", "failed", 1)]
    [DataRow("cameraagent reinstall", "refused", 3)]
    [DataRow("cameraagent reinstall", "interrupted", 130)]
    [DataRow("cameraagent uninstall", "done", 0)]
    [DataRow("cameraagent uninstall", "failed", 1)]
    [DataRow("cameraagent uninstall", "refused", 3)]
    [DataRow("cameraagent uninstall", "interrupted", 130)]
    [DataRow("cameraagent purge", "done", 0)]
    [DataRow("cameraagent backup", "done", 0)]
    [DataRow("cameraagent restore", "done", 0)]
    [DataRow("cameraagent purge", "failed", 1)]
    [DataRow("cameraagent backup", "failed", 1)]
    [DataRow("cameraagent restore", "failed", 1)]
    [DataRow("cameraagent purge", "refused", 3)]
    [DataRow("cameraagent backup", "refused", 3)]
    [DataRow("cameraagent restore", "refused", 3)]
    [DataRow("cameraagent purge", "interrupted", 130)]
    [DataRow("cameraagent backup", "interrupted", 130)]
    [DataRow("cameraagent restore", "interrupted", 130)]
    [DataRow("catalog install", "done", 0)]
    [DataRow("catalog install", "failed", 1)]
    [DataRow("catalog install", "refused", 3)]
    [DataRow("catalog install", "interrupted", 130)]
    [DataRow("catalog select", "done", 0)]
    [DataRow("catalog select", "failed", 1)]
    [DataRow("catalog select", "refused", 3)]
    [DataRow("catalog select", "interrupted", 130)]
    [DataRow("catalog rollback", "done", 0)]
    [DataRow("catalog rollback", "failed", 1)]
    [DataRow("catalog rollback", "refused", 3)]
    [DataRow("catalog rollback", "interrupted", 130)]
    [DataRow("catalog gc", "done", 0)]
    [DataRow("catalog gc", "failed", 1)]
    [DataRow("catalog gc", "refused", 3)]
    [DataRow("catalog gc", "interrupted", 130)]
    [DataRow("host set-product-root", "done", 0)]
    [DataRow("host set-product-root", "failed", 1)]
    [DataRow("host set-product-root", "refused", 3)]
    [DataRow("host set-product-root", "interrupted", 130)]
    public async Task RunAsync_EveryCommandEndsWithTheContractExitCode(string command, string outcome, int expected)
    {
        using var host = new PolicyHost();
        var services = Substituted(host, outcome);

        var (exitCode, _, error) = await RunAsync(Arguments(command), services, CancellationToken.None);

        Assert.AreEqual(expected, exitCode, error);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("partial-prepare-cleanup")]
    [DataRow("status")]
    [DataRow("cameraagent install")]
    [DataRow("cameraagent recover-owner")]
    [DataRow("cameraagent preflight")]
    [DataRow("cameraagent reset-state")]
    [DataRow("cameraagent upgrade")]
    [DataRow("cameraagent rollback")]
    [DataRow("cameraagent reinstall")]
    [DataRow("cameraagent uninstall")]
    [DataRow("cameraagent purge")]
    [DataRow("cameraagent backup")]
    [DataRow("cameraagent restore")]
    [DataRow("catalog install")]
    [DataRow("catalog select")]
    [DataRow("catalog rollback")]
    [DataRow("catalog gc")]
    [DataRow("host set-product-root")]
    public async Task RunAsync_EveryCommandWithAnOptionItDoesNotAccept_ExitsTwoBeforeAnythingRuns(string command)
    {
        using var host = new PolicyHost();

        var (exitCode, output, _) = await RunAsync(
            [.. Arguments(command), "--no-such-option", "value"], Real(host), CancellationToken.None);

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(string.Empty, output);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("partial-prepare-cleanup")]
    [DataRow("status")]
    [DataRow("cameraagent install")]
    [DataRow("cameraagent recover-owner")]
    [DataRow("cameraagent preflight")]
    [DataRow("cameraagent reset-state")]
    [DataRow("cameraagent upgrade")]
    [DataRow("cameraagent rollback")]
    [DataRow("cameraagent reinstall")]
    [DataRow("cameraagent uninstall")]
    [DataRow("cameraagent purge")]
    [DataRow("cameraagent backup")]
    [DataRow("cameraagent restore")]
    [DataRow("catalog install")]
    [DataRow("catalog select")]
    [DataRow("catalog rollback")]
    [DataRow("catalog gc")]
    [DataRow("host set-product-root")]
    public async Task RunAsync_EveryCommandInterruptedBeforeItBegins_ExitsOneHundredThirty(string command)
    {
        using var host = new PolicyHost();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (exitCode, output, _) = await RunAsync(Arguments(command), Real(host), cancelled.Token);

        Assert.AreEqual(130, exitCode);
        Assert.AreEqual(string.Empty, output);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("partial-prepare-cleanup")]
    [DataRow("status")]
    [DataRow("cameraagent install")]
    [DataRow("cameraagent recover-owner")]
    [DataRow("cameraagent preflight")]
    [DataRow("cameraagent reset-state")]
    [DataRow("cameraagent upgrade")]
    [DataRow("cameraagent rollback")]
    [DataRow("cameraagent reinstall")]
    [DataRow("cameraagent uninstall")]
    [DataRow("cameraagent purge")]
    [DataRow("cameraagent backup")]
    [DataRow("cameraagent restore")]
    [DataRow("catalog install")]
    [DataRow("catalog select")]
    [DataRow("catalog rollback")]
    [DataRow("catalog gc")]
    [DataRow("host set-product-root")]
    public async Task RunAsync_EveryCommandUnderAHostPolicyThatCannotBeTrusted_IsRefusedWithExitThree(string command)
    {
        using var host = new PolicyHost();
        host.WritePolicyText("{\"productRoot\": \"relative/root\"}");

        var (exitCode, output, error) = await RunAsync([.. Arguments(command), "--json"], Real(host), CancellationToken.None);

        Assert.AreEqual(3, exitCode, error);
        Assert.AreEqual(string.Empty, output);
        using var document = JsonDocument.Parse(error.Trim());
        Assert.AreEqual("refused", document.RootElement.GetProperty("errorCode").GetString());
    }

    /// <summary>
    /// A release the installer will not use (#1309) is refused like any other gate: it is read and checked before the
    /// Docker daemon is contacted, so nothing has changed, and it exits 3, with a plan or without.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunAsync_InstallFromAFolderThatIsNotARelease_IsRefusedWithExitThree(bool plan)
    {
        using var host = new PolicyHost();
        var folder = Path.Combine(Path.GetTempPath(), $"hvo-exit-codes-no-release-{Guid.NewGuid():N}");
        string[] arguments =
        [
            "cameraagent", "install", "--friendly-name", "North Camera", "--catalog-bundle", "/srv/hvo/catalog.bundle",
            "--release", folder, "--json", .. plan ? ["--plan"] : Array.Empty<string>()
        ];

        var (exitCode, output, error) = await RunAsync(arguments, Real(host), CancellationToken.None);

        Assert.AreEqual(3, exitCode, error);
        Assert.AreEqual(string.Empty, output);
        using var document = JsonDocument.Parse(error.Trim());
        Assert.AreEqual("refused", document.RootElement.GetProperty("errorCode").GetString());
        Assert.AreEqual($"The release folder {folder} does not exist.", document.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RunAsync_HelpAndVersion_ExitZeroAndAnExtraArgumentExitsTwo()
    {
        using var host = new PolicyHost();

        Assert.AreEqual(0, (await RunAsync(["--help"], Real(host), CancellationToken.None)).ExitCode);
        Assert.AreEqual(0, (await RunAsync(["--version"], Real(host), CancellationToken.None)).ExitCode);
        Assert.AreEqual(0, (await RunAsync(["cameraagent", "install", "--help"], Real(host), CancellationToken.None)).ExitCode);
        Assert.AreEqual(2, (await RunAsync(["--version", "status"], Real(host), CancellationToken.None)).ExitCode);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RunAsync_Refusal_SaysNothingWasChangedAndWithJsonWritesTheRefusedErrorCode()
    {
        using var host = new PolicyHost();
        var services = Substituted(host, "refused");

        var human = await RunAsync(Arguments("cameraagent upgrade"), services, CancellationToken.None);
        var json = await RunAsync([.. Arguments("cameraagent upgrade"), "--json"], services, CancellationToken.None);

        Assert.AreEqual("Installation refused: injected refusal Nothing was changed." + Environment.NewLine, human.Error);
        using var document = JsonDocument.Parse(json.Error.Trim());
        Assert.AreEqual("refused", document.RootElement.GetProperty("errorCode").GetString());
        Assert.AreEqual("injected refusal", document.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RunAsync_UnmetHostPrerequisites_AreARefusalWithExitThree()
    {
        using var host = new PolicyHost();
        var report = new HostPrerequisiteReport(1, "refused", false, "/var/lib/hvo-sky",
            [new HostPrerequisiteFinding("docker-memory-limit-unsupported", "docker", true, "docker info", "false", "true", "Enable memory cgroups.")],
            DateTimeOffset.UtcNow);
        var services = Substituted(host, "done") with
        {
            Install = (_, _) => throw new HostPrerequisiteException(report)
        };

        var (exitCode, _, error) = await RunAsync(Arguments("cameraagent install"), services, CancellationToken.None);

        Assert.AreEqual(3, exitCode);
        StringAssert.Contains(error, "docker-memory-limit-unsupported", StringComparison.Ordinal);
        StringAssert.Contains(error, "Nothing was changed.", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RunAsync_IncompatiblePreflight_IsARefusalWithExitThree()
    {
        using var host = new PolicyHost();
        var services = Substituted(host, "done") with
        {
            Preflight = (request, _) => Task.FromResult(Report(request.InstanceId!.Value, compatible: false))
        };

        var (exitCode, output, error) = await RunAsync([.. Arguments("cameraagent preflight"), "--json"], services, CancellationToken.None);

        Assert.AreEqual(3, exitCode);
        StringAssert.Contains(output, "\"compatible\": false", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(error.Trim());
        Assert.AreEqual("state-incompatible", document.RootElement.GetProperty("errorCode").GetString());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RunAsync_UnforeseenException_IsAFailedStepWithExitOneRatherThanACrash()
    {
        using var host = new PolicyHost();
        var services = Substituted(host, "done") with
        {
            Lifecycle = (_, _) => throw new IOException("disk went away")
        };

        var (exitCode, _, error) = await RunAsync(Arguments("cameraagent uninstall"), services, CancellationToken.None);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(error, "Unexpected IOException: disk went away", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RunAsync_DeprecatedOptionWithJson_IsAWarningInTheResultAndWithoutJsonOnStandardError()
    {
        using var host = new PolicyHost();
        var services = Substituted(host, "done");
        string[] upgrade = [.. Arguments("cameraagent upgrade"), "--migration-backward-compatible"];

        var json = await RunAsync([.. upgrade, "--json"], services, CancellationToken.None);
        var human = await RunAsync(upgrade, services, CancellationToken.None);

        Assert.AreEqual(0, json.ExitCode);
        Assert.AreEqual(string.Empty, json.Error);
        using var document = JsonDocument.Parse(json.Output);
        var warning = document.RootElement.GetProperty("warnings")[0];
        Assert.AreEqual("option-deprecated", warning.GetProperty("code").GetString());
        Assert.AreEqual(0, human.ExitCode);
        StringAssert.StartsWith(human.Error, "Warning: --migration-backward-compatible is deprecated", StringComparison.Ordinal);
    }

    private static string[] Arguments(string command) => Commands.Single(entry => entry.Name == command).Arguments;

    private static ProgramServices Real(PolicyHost host)
        => ProgramServices.Production with { Parse = arguments => CommandLine.ParseCommand(arguments, new CommandLineContext(false, host.Location)) };

    private static ProgramServices Substituted(PolicyHost host, string outcome)
    {
        Task<T> Run<T>(Func<T> done) => outcome switch
        {
            "done" => Task.FromResult(done()),
            "failed" => throw new InstallerException("injected failure"),
            "refused" => throw InstallerException.Refused("injected refusal"),
            "interrupted" => throw new OperationCanceledException(),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
        return new ProgramServices(
            arguments => CommandLine.ParseCommand(arguments, new CommandLineContext(false, host.Location)),
            (request, _) => Run(() => Installation(request)),
            (request, _) => Run(() => Report(request.InstanceId!.Value, compatible: true)),
            (request, _) => Run(() => new CameraAgentStateResetResult(
                1, "completed", Guid.NewGuid(), request.InstanceId!.Value, "/var/lib/hvo-sky/cameraagents/x", [], [], "/e", "next",
                DateTimeOffset.UtcNow)),
            (request, _) => Run(() => new HostPolicyResult(1, "updated", "/etc/hvo-sky/host.json", "/var/lib/hvo-sky", request.ProductRoot)),
            (request, _) => Run(() => new OwnerRecoveryResult(
                1, "completed", Guid.NewGuid(), request.InstanceId!.Value, null, "owner-password-change-required", DateTimeOffset.UtcNow)),
            (request, _) => Run(() => new LifecycleResult(
                1, request.Operation, "completed", Guid.NewGuid(), request.InstanceId, null, request.ProductRoot, null, null, null,
                null, null, null, null, null, [], null, DateTimeOffset.UtcNow)))
        {
            PartialPrepare = (_, _) => Run(() => new System.Text.Json.Nodes.JsonObject { ["phaseStatus"] = "passed" })
        };
    }

    private static CameraAgentStatePreflightReport Report(Guid instanceId, bool compatible)
        => new(1, compatible ? "compatible" : "incompatible", instanceId, "/var/lib/hvo-sky/cameraagents/x", ImageRef, null,
            "cameraagent-state-v2", null, "cameraagent-state-v2", compatible, [], DateTimeOffset.UtcNow);

    private static InstallationResult Installation(InstallRequest request)
    {
        var catalog = new CatalogInstallationIdentity(
            HygV42.CatalogId, "hyg-v4.2-p3-s2-r1", "1", "1", new string('a', 64), 1, 1, "/var/lib/hvo-sky/catalogs/x",
            new string('a', 64), "local-offline");
        return new InstallationResult(
            1, InstallationOutcome.Installed, Guid.NewGuid(), request.InstanceId ?? Guid.NewGuid(), Guid.NewGuid(),
            request.FriendlyName, new Uri("http://127.0.0.1:5130/"), request.OwnerEmail, "/p", request.ProductRoot, "/i", "/c", "/s",
            1000, 1000, "v1", 0, 0, 0, "UTC", new string('a', 64), new string('a', 64), new string('a', 64), "default", "1", "1",
            "active", new string('a', 64), catalog, new ImageInstallationIdentity("registry", ImageRef, ImageRef, "amd64", null),
            new DockerDaemonIdentity("d", "h", "amd64", "29"), true, true, "owner-password-change-required", DateTimeOffset.UtcNow);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] arguments,
        ProgramServices services,
        CancellationToken cancellationToken)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await Program.RunAsync(arguments, services, cancellationToken);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    /// <summary>A private trust root owned by the test user whose host policy resolves the default product root.</summary>
    private sealed class PolicyHost : IDisposable
    {
        public PolicyHost()
        {
            TrustRoot = Path.Combine(Path.GetTempPath().TrimEnd('/'), $"hvo-exit-codes-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TrustRoot);
            File.SetUnixFileMode(TrustRoot, SharedDirectory);
            Location = new HostPolicyLocation(
                Path.Combine(TrustRoot, "etc/hvo-sky/host.json"),
                TrustRoot,
                NativeLinux.getuid(),
                NativeLinux.getgid(),
                InstallRequest.DefaultProductRoot,
                Path.Combine(TrustRoot, "run/hvo-sky-host-policy.lock"));
        }

        public string TrustRoot { get; }

        public HostPolicyLocation Location { get; }

        public void WritePolicyText(string content)
        {
            var directory = Path.GetDirectoryName(Location.PolicyPath)!;
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(Path.GetDirectoryName(directory)!, SharedDirectory);
            File.SetUnixFileMode(directory, SharedDirectory);
            File.WriteAllText(Location.PolicyPath, content);
            File.SetUnixFileMode(Location.PolicyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
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
