using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class HostProductRootPolicyTests
{
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("missing-policy", false)]
    [DataRow("missing-policy", true)]
    [DataRow("different-root", false)]
    [DataRow("different-root", true)]
    [DataRow("missing-root", false)]
    [DataRow("missing-root", true)]
    [DataRow("linked-root", false)]
    [DataRow("linked-root", true)]
    [DataRow("writable-policy", false)]
    [DataRow("writable-policy", true)]
    [DataRow("writable-root", false)]
    [DataRow("writable-root", true)]
    [DataRow("malformed-policy", false)]
    [DataRow("malformed-policy", true)]
    [DataRow("relative-root", false)]
    [DataRow("relative-root", true)]
    [DataRow("write-denied", false)]
    [DataRow("write-denied", true)]
    public async Task HostRepair_PrivilegedTransport_PreservesPublicRefusalsAndWriteFailures(string fault, bool json)
    {
        if (fault == "write-denied" && NativeLinux.getuid() == 0)
            Assert.Inconclusive("The kernel write denial requires a non-root test process.");
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
            case "write-denied": File.SetUnixFileMode(Path.GetDirectoryName(host.Location.PolicyPath)!, SharedDirectory & ~UnixFileMode.UserWrite); break;
        }
        var before = File.Exists(host.Location.PolicyPath) ? await File.ReadAllBytesAsync(host.Location.PolicyPath) : null;
        var runner = new PolicyHelperTransport(host.Location);
        var services = ProgramServices.Production with
        {
            Parse = arguments =>
            {
                var parsed = (HostProductRootRequest)CommandLine.ParseCommand(arguments, new CommandLineContext(false, host.Location));
                // Force the real non-root transport branch. Every helper result must be nonzero, so the caller
                // must return before its production-policy read-back; only the helper uses the temporary location.
                return parsed with { PolicyLocation = HostPolicyLocation.Production };
            },
            HostPolicy = (request, cancellationToken) => PrivilegedHostPolicy.SetProductRootAsync(
                request, runner, uid: 1000, cancellationToken)
        };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var previousOutput = Console.Out;
        var previousError = Console.Error;
        int exit;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            exit = await Program.RunAsync(
                json ? ["host", "set-product-root", requested, "--repair", "--json"] : ["host", "set-product-root", requested, "--repair"],
                services, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
        }
        Assert.AreEqual(1, runner.Calls);
        Assert.AreEqual(string.Empty, output.ToString());
        var refused = fault != "write-denied";
        Assert.AreEqual(refused ? 3 : 1, exit, error.ToString());
        Assert.AreEqual(refused ? 3 : 1, runner.Result!.ExitCode, "The helper must preserve the refusal before the parent maps it.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(runner.Result.StandardError));
        if (json)
        {
            using var result = JsonDocument.Parse(error.ToString());
            Assert.AreEqual(1, result.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual(refused ? "refused" : "installation-failed", result.RootElement.GetProperty("errorCode").GetString());
            var message = result.RootElement.GetProperty("message").GetString();
            Assert.AreEqual(refused
                ? runner.Result.StandardError.Trim()
                : "Privileged host policy update failed: " + runner.Result.StandardError.Trim(), message);
        }
        else
        {
            var message = error.ToString();
            StringAssert.StartsWith(message, refused ? "Installation refused: " : "Installation failed: ", StringComparison.Ordinal);
            StringAssert.Contains(message, runner.Result.StandardError.Trim(), StringComparison.Ordinal);
            Assert.AreEqual(refused, message.Contains("Nothing was changed.", StringComparison.Ordinal));
        }
        if (before is null) Assert.IsFalse(File.Exists(host.Location.PolicyPath));
        else CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(host.Location.PolicyPath));
        if (fault == "different-root") Assert.IsFalse(Directory.Exists(requested));
        if (fault == "missing-root") Assert.IsFalse(Directory.Exists(root));
    }

    private sealed class PolicyHelperTransport(HostPolicyLocation location) : IProcessRunner
    {
        public int Calls { get; private set; }
        public ProcessResult? Result { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual("sudo", fileName);
            CollectionAssert.AreEqual(
                PrivilegedHostPolicy.HelperArguments(arguments[4], repairMount: true).ToArray(), arguments.ToArray());
            using var output = new StringWriter();
            using var error = new StringWriter();
            var previousOutput = Console.Out;
            var previousError = Console.Error;
            int exit;
            try
            {
                Console.SetOut(output);
                Console.SetError(error);
                exit = PrivilegedHostPolicy.RunInternal(arguments.Skip(2).ToArray(), location, uid: 0);
            }
            finally
            {
                Console.SetOut(previousOutput);
                Console.SetError(previousError);
            }
            Result = new ProcessResult(exit, output.ToString(), error.ToString());
            Assert.AreNotEqual(0, exit, "A simulated helper success would reach a real production-policy read-back.");
            return Task.FromResult(Result);
        }
    }
}
