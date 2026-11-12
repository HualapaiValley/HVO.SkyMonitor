using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(200)]
    [DataRow(503)]
    [DataRow(401)]
    public async Task CachedUnhealthyCannotAdmitRecoveryWhenTheCurrentAliveEndpointResponds(int status)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var (_, path, _) = await PendingControlAsync(fixture, terminal: false);
        var before = await File.ReadAllBytesAsync(path);
        var runner = new FreshAliveRunner(status);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.EnsureControlSettledAsync(
            fixture.Paths, CancellationToken.None, LifecycleOperationKind.Uninstall, new DockerClient(runner)));
        Assert.IsTrue(runner.Probes > 0, "A cached health result must not substitute for a current liveness read.");
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(7, "000", true)]
    [DataRow(28, "000", true)]
    [DataRow(125, "", false)]
    [DataRow(127, "", false)]
    [DataRow(0, "unparseable", false)]
    [DataRow(1, "000", false)]
    public async Task OfflineAdmissionRequiresAConfirmedCurrentConnectionFailure(int exitCode, string output, bool allowed)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var (_, path, _) = await PendingControlAsync(fixture, terminal: false);
        var before = await File.ReadAllBytesAsync(path);
        var runner = new FreshAliveRunner(0, exitCode, output);
        Task Admission() => CameraAgentLifecycleManager.EnsureControlSettledAsync(fixture.Paths, CancellationToken.None,
            LifecycleOperationKind.Reinstall, new DockerClient(runner));
        if (allowed) await Admission();
        else await Assert.ThrowsExactlyAsync<InstallerException>(Admission);
        Assert.IsTrue(runner.Probes > 0);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
    }

    private sealed class FreshAliveRunner(int status, int exitCode = 0, string? output = null) : IProcessRunner
    {
        private static readonly string[] StaleHealthCommand = ["CMD", "/bin/false"];
        internal int Probes;
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", ""));
            if (arguments is ["--host", _, ..]) arguments = arguments.Skip(2).ToArray();
            if (arguments is ["container", "inspect", _, ..])
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[] { new
                {
                    Image = "sha256:test",
                    State = new { Running = true, Health = new { Status = "unhealthy", Log = new[] { new
                    { End = DateTimeOffset.UtcNow.AddHours(-1), ExitCode = 1 } } } },
                    Config = new { Healthcheck = new { Test = StaleHealthCommand } }
                } }), ""));
            Assert.AreEqual("exec", arguments[0]);
            Assert.IsTrue(arguments.Contains("http://127.0.0.1:8080/alive", StringComparer.Ordinal));
            Assert.IsTrue(arguments.Contains("--max-time", StringComparer.Ordinal));
            Probes++;
            return Task.FromResult(new ProcessResult(exitCode, output ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture), ""));
        }
    }
}
