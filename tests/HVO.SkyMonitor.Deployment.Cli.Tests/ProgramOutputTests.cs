using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// What the installer prints, where, and with which exit code: help and the version on standard output with success, and
/// a command-line mistake, an unreadable answers or configuration file included, as its reason and the usage on standard
/// error with exit code 2. Every command's exit codes are pinned in <see cref="ExitCodeContractTests"/>. Each case runs the real entry point with the console streams captured, so the class does not run in
/// parallel.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class ProgramOutputTests
{
    private const string GeneralUsage = """
        Usage: hvo-sky-install <command> [options]

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
          host set-product-root <absolute-path>

        Run 'hvo-sky-install <command> --help' to list the options of one command.
        Run 'hvo-sky-install --version' to print this installer's version and commit.
        """;

    private const string InstallUsage = """
        Usage: hvo-sky-install cameraagent install [options]

        Options:
          --answers <value>
          --config <value>
          --instance-id <value>
          --friendly-name <value>
          --owner-email <value>
          --bind-address <value>
          --port <value>
          --product-root <value>
          --catalog-bundle <value>
          --release <value>
          --image-source <value>
          --image-ref <value>
          --image-archive <value>
          --image-archive-sha256 <value>
          --password-file <value>
          --latitude <value>
          --longitude <value>
          --elevation <value>
          --time-zone <value>
          --replay-profile <value>
          --cpus <value>
          --memory <value>
          --plan
          --dry-run
          --resume
          --json
          --generate-password
          --acknowledge-plaintext-http
          --no-download
          -h, --help

        --answers reads the install from a JSON answers file with "schemaVersion": 1; only --cpus, --memory, --release, --image-source, --catalog-bundle and --no-download may accompany it. --config is its deprecated predecessor and cannot be combined with the other value options. --plan is --dry-run. A plan writes no product file, lock or Compose file; a database caught mid-transaction is read through a private temporary copy that is removed.
        """;

    private const string HostSetProductRootUsage = """
        Usage: hvo-sky-install host set-product-root <absolute-path> [options]

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
    public async Task Main_Version_PrintsTheStampedVersionAndCommitOnStandardOutputAndSucceeds()
    {
        var (exitCode, output, error) = await RunCommandLineAsync("--version");

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, error);
        var stamped = InstallerBuildIdentity.Of(typeof(Program).Assembly);
        Assert.AreEqual(stamped + Environment.NewLine, output);
        // Install contract section 1: exactly <version>+<40-character commit>, with "unknown" for a build that had no
        // checkout and was given no commit.
        var match = Regex.Match(
            stamped,
            "\\A(?<version>[A-Za-z0-9](?:[A-Za-z0-9.-]{0,126}[A-Za-z0-9])?)\\+(?<commit>[0-9a-f]{40}|unknown)\\z",
            RegexOptions.CultureInvariant);
        Assert.IsTrue(match.Success, stamped);
        // This test assembly comes from the same checkout and build, so the SDK stamped it with the same commit and
        // version: the installer names that commit (unknown when there was none) and that version, which is the product
        // version (#1310): X.Y.Z-dev for a build given none, or the release version a release build is given.
        var reference = typeof(ProgramOutputTests).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var separator = reference.IndexOf('+', StringComparison.Ordinal);
        var (referenceVersion, referenceCommit) = separator < 0
            ? (reference, "unknown")
            : (reference[..separator], reference[(separator + 1)..]);
        Assert.AreEqual(referenceCommit, match.Groups["commit"].Value);
        Assert.AreEqual(referenceVersion, match.Groups["version"].Value);
    }

    [TestMethod]
    public void InstallerVersion_IsTheProductVersionOrAPreReleaseOfIt()
    {
        // One product version (install contract section 1, #1310): the VersionPrefix in Directory.Build.props.
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        var prefixes = Regex.Matches(props, "<VersionPrefix>([^<]*)</VersionPrefix>", RegexOptions.CultureInvariant);
        Assert.HasCount(1, prefixes, "Directory.Build.props declares one VersionPrefix");
        var prefix = prefixes[0].Groups[1].Value;
        Assert.IsTrue(Regex.IsMatch(prefix, "\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant), prefix);

        var stamped = InstallerBuildIdentity.Of(typeof(Program).Assembly);
        var version = stamped[..stamped.IndexOf('+', StringComparison.Ordinal)];
        Assert.IsTrue(version == prefix || version.StartsWith(prefix + "-", StringComparison.Ordinal), stamped);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    [TestMethod]
    [DataRow("bogus", "Unknown command 'bogus'.")]
    [DataRow("", "Missing command.")]
    [DataRow("cameraagent", "Missing command after 'cameraagent'.")]
    [DataRow("catalog bogus", "Unknown command 'catalog bogus'.")]
    [DataRow("help status", "Unexpected argument 'status' after 'help'.")]
    [DataRow("--version status", "Unexpected argument 'status' after '--version'.")]
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
    public async Task Main_ConfigOrAnswersFileThatCannotBeRead_IsInvalidInputWithExitCodeTwo()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"hvo-installer-output-{Guid.NewGuid():N}", "install.json");

        var config = await RunAsync("cameraagent", "install", "--config", missing);
        var answers = await RunAsync("cameraagent", "install", "--answers", missing);

        Assert.AreEqual(2, config.ExitCode);
        Assert.AreEqual(string.Empty, config.Output);
        // The diagnostic line is bounded (Redaction.SafeDiagnostic), so a long temporary path is cut short, never wrapped.
        var diagnostic = Redaction.SafeDiagnostic(
            $"The installer configuration '{missing}' could not be read: Protected file '{missing}' could not be opened without following links.");
        Assert.IsLessThanOrEqualTo(256, diagnostic.Length);
        Assert.AreEqual(diagnostic + Environment.NewLine + Text(InstallUsage), config.Error);
        Assert.AreEqual(2, answers.ExitCode);
        StringAssert.StartsWith(answers.Error, $"The answers file '{missing}' could not be read: ", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Main_LifecycleUsage_NamesThePlanAndTheDeprecatedMigrationAssertion()
    {
        var (exitCode, output, _) = await RunCommandLineAsync("cameraagent upgrade --help");

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "  --plan" + Environment.NewLine, StringComparison.Ordinal);
        StringAssert.Contains(output, "--migration-backward-compatible is deprecated and has no effect.", StringComparison.Ordinal);
        StringAssert.Contains(output, "A plan writes no product file, lock or Compose file; a database caught mid-transaction is read through a private temporary copy that is removed.", StringComparison.Ordinal);
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
