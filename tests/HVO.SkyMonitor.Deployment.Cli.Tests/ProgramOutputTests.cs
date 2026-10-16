using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// What the installer prints, where, and with which exit code: help on standard output with success, a command-line
/// mistake as its reason and the usage on standard error with exit code 2, and an ordinary failure unchanged. Each
/// case runs the real entry point with the console streams captured, so the class does not run in parallel.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProgramOutputTests
{
    private const string GeneralUsage = """
        Usage: hvo-skymonitor <command> [options]

        Commands:
          status
          cameraagent install
          cameraagent recover-owner
          cameraagent preflight
          cameraagent reset-state
          cameraagent upgrade
          cameraagent rollback
          cameraagent reinstall
          cameraagent uninstall
          cameraagent purge
          catalog install
          catalog select
          catalog rollback
          catalog gc
          catalog check
          host set-product-root <absolute-path>

        Run 'hvo-skymonitor <command> --help' to list the options of one command.
        """;

    private const string InstallUsage = """
        Usage: hvo-skymonitor cameraagent install [options]

        Options:
          --config <value>
          --instance-id <value>
          --friendly-name <value>
          --owner-email <value>
          --bind-address <value>
          --port <value>
          --product-root <value>
          --catalog-bundle <value>
          --catalog-manifest <value>
          --catalog-index <value>
          --catalog-version <value>
          --asset-base-url <value>
          --channel <value>
          --image-ref <value>
          --image-archive <value>
          --image-archive-sha256 <value>
          --image-manifest <value>
          --image-index <value>
          --image-version <value>
          --password-file <value>
          --latitude <value>
          --longitude <value>
          --elevation <value>
          --time-zone <value>
          --replay-profile <value>
          --dry-run
          --resume
          --json
          --generate-password
          --acknowledge-plaintext-http
          --no-download
          -h, --help

        --config reads the request from a JSON file and cannot be combined with the other value options.
        """;

    private const string HostSetProductRootUsage = """
        Usage: hvo-skymonitor host set-product-root <absolute-path> [options]

        Options:
          --json
          -h, --help
        """;

    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("help")]
    public async Task Main_HelpAlone_PrintsTheGeneralUsageOnStandardOutputAndSucceeds(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var (exitCode, output, error) = await RunCommandLineAsync(arguments);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(Text(GeneralUsage), output);
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    [DataRow("cameraagent install --help")]
    [DataRow("cameraagent install -h")]
    [DataRow("cameraagent install --dry-run --help")]
    public async Task Main_CommandHelp_PrintsThatCommandsOptionsOnStandardOutputAndSucceeds(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var (exitCode, output, error) = await RunCommandLineAsync(arguments);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(Text(InstallUsage), output);
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    [DataRow("bogus", "Unknown command 'bogus'.")]
    [DataRow("", "Missing command.")]
    [DataRow("cameraagent", "Missing command after 'cameraagent'.")]
    [DataRow("catalog bogus", "Unknown command 'catalog bogus'.")]
    [DataRow("help status", "Unexpected argument 'status' after 'help'.")]
    public async Task Main_NoKnownCommand_ReportsTheReasonAndTheGeneralUsageOnStandardError(string arguments, string reason)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var (exitCode, output, error) = await RunCommandLineAsync(arguments);

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(string.Empty, output);
        Assert.AreEqual(reason + Environment.NewLine + Text(GeneralUsage), error);
    }

    [TestMethod]
    public async Task Main_MissingArgument_ReportsTheReasonAndThatCommandsUsageOnStandardError()
    {
        var (exitCode, output, error) = await RunCommandLineAsync("host set-product-root");

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(string.Empty, output);
        Assert.AreEqual(
            "host set-product-root requires <absolute-path>." + Environment.NewLine + Text(HostSetProductRootUsage),
            error);
    }

    [TestMethod]
    public async Task Main_OptionWithoutItsValue_ReportsTheReasonAndThatCommandsUsageOnStandardError()
    {
        var (exitCode, output, error) = await RunCommandLineAsync("cameraagent install --port");

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(string.Empty, output);
        Assert.AreEqual("Option '--port' requires a value." + Environment.NewLine + Text(InstallUsage), error);
    }

    [TestMethod]
    public async Task Main_UsageErrorWithJson_WritesOneJsonErrorObjectOnStandardError()
    {
        var (exitCode, output, error) = await RunCommandLineAsync("bogus --json");

        Assert.AreEqual(2, exitCode);
        Assert.AreEqual(string.Empty, output);
        var lines = error.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length, error);
        using var document = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("usage", document.RootElement.GetProperty("errorCode").GetString());
        Assert.AreEqual("Unknown command 'bogus'.", document.RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Main_OrdinaryFailure_KeepsTheInstallationFailedMessageAndExitCode()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"hvo-installer-output-{Guid.NewGuid():N}", "install.json");

        var (exitCode, output, error) = await RunAsync("cameraagent", "install", "--config", missing);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, output);
        Assert.AreEqual(
            $"Installation failed: Protected file '{missing}' could not be opened without following links.{Environment.NewLine}",
            error);
    }

    private static string Text(string usage) => usage.ReplaceLineEndings() + Environment.NewLine;

    private static Task<(int ExitCode, string Output, string Error)> RunCommandLineAsync(string commandLine)
        => RunAsync(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await Program.Main(arguments);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
