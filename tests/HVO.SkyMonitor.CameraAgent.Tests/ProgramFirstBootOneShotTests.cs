using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// CameraAgent's one-shot first-boot mode through its entry point (#1966 review): <see cref="Program.Main"/> given the
/// installer's argument persists the configured first-boot pause in the raw-ingress journal, prints its durable status as
/// one JSON line and returns without building the web host; without a configured pause it refuses and exits non-zero.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Hosting")]
[DoNotParallelize]
public sealed class ProgramFirstBootOneShotTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "The installer runs CameraAgent's one-shot mode on Linux hosts only.")]
    public async Task TheOneShotArgument_PersistsThePauseAndReturnsWithoutTheWebHost()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-one-shot-");
        try
        {
            var pause = Guid.NewGuid();
            // A web host would try to listen on port 1 and fail; the one-shot mode returns before any is built.
            string[] args = [FirstBootStateInitializer.Argument, $"--CameraAgent:RawIngressRoot={root.FullName}",
                $"--CameraAgent:FirstBootPause:Id={pause:D}", "--urls=http://127.0.0.1:1"];

            var (exit, output) = await RunMainAsync(args).ConfigureAwait(false);
            var (again, repeated) = await RunMainAsync(args).ConfigureAwait(false);

            Assert.AreEqual(0, exit, output);
            using var status = JsonDocument.Parse(output.Trim());
            Assert.AreEqual("seeded", status.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual(pause, status.RootElement.GetProperty("id").GetGuid());
            Assert.IsTrue(status.RootElement.GetProperty("seeded").GetBoolean());
            Assert.IsTrue(status.RootElement.GetProperty("inForce").GetBoolean());
            Assert.AreEqual("paused", status.RootElement.GetProperty("state").GetString());
            Assert.AreEqual(1, status.RootElement.GetProperty("version").GetInt64());
            Assert.IsTrue(File.Exists(Path.Combine(root.FullName, "journal", "raw-ingress.db")), "The pause is in the raw-ingress journal.");
            Assert.AreEqual(0, again, repeated);
            Assert.AreEqual(output, repeated, "A repeated one-shot run reports the same durable pause and changes nothing.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "The installer runs CameraAgent's one-shot mode on Linux hosts only.")]
    public async Task TheOneShotArgument_WithoutAConfiguredPause_RefusesAndExitsNonZero()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-one-shot-");
        try
        {
            var (exit, output) = await RunMainAsync(
                [FirstBootStateInitializer.Argument, $"--CameraAgent:RawIngressRoot={root.FullName}", "--urls=http://127.0.0.1:1"]).ConfigureAwait(false);

            Assert.AreEqual(2, exit, output);
            Assert.AreEqual("{\"outcome\":\"refused\",\"reason\":\"first-boot-state-unavailable\"}", output.Trim());
            Assert.IsFalse(File.Exists(Path.Combine(root.FullName, "journal", "raw-ingress.db")), "Nothing is persisted.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunMainAsync(string[] args)
    {
        var console = Console.Out;
        var exitCode = Environment.ExitCode;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Environment.ExitCode = 0;
            await Program.Main(args).ConfigureAwait(false);
            return (Environment.ExitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(console);
            Environment.ExitCode = exitCode;
        }
    }
}
