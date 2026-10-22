using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Wizard;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;

/// <summary>
/// When the installer asks (install contract section 5, #1317): only on a terminal, only for an install whose command
/// line leaves a required question, and never with <c>--answers</c>, <c>--config</c>, <c>--plan</c>, <c>--json</c>,
/// <c>--resume</c> or help. Everywhere else it asks nothing, reads nothing from its input, and a required value that is
/// missing is a usage error (exit 2) that names it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class InstallWizardGateTests
{
    private const string Release = "/srv/hvo/release";
    private const string Catalog = "/srv/hvo/catalog.bundle";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(true, new string[0], true)]
    [DataRow(false, new string[0], false)]
    [DataRow(true, new[] { "cameraagent", "install" }, true)]
    [DataRow(true, new[] { "cameraagent", "install", "--friendly-name", "N", "--release", Release }, true)]
    [DataRow(true, new[] { "cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog }, true)]
    [DataRow(true, new[] { "cameraagent", "install", "--friendly-name", "N", "--catalog-bundle", Catalog }, true)]
    [DataRow(true, new[] { "cameraagent", "install", "--friendly-name", "N", "--release", Release, "--catalog-bundle", Catalog }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--friendly-name", "N", "--image-ref", "sha256:x", "--catalog-bundle", Catalog }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--answers", "/a.json" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--config", "/c.json" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--plan" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--dry-run" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--json" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--resume", "--instance-id", "6f9b3c1e-2d4a-4e8b-9c7d-1a2b3c4d5e6f" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--help" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "positional" }, false)]
    [DataRow(true, new[] { "cameraagent", "install", "--friendly-name" }, false)]
    [DataRow(true, new[] { "status" }, false)]
    [DataRow(true, new[] { "cameraagent", "upgrade" }, false)]
    [DataRow(true, new[] { "--help" }, false)]
    [DataRow(false, new[] { "cameraagent", "install" }, false)]
    public void TheWizard_RunsOnlyForAnInstallOnATerminalThatLeavesARequiredQuestion(bool terminal, string[] args, bool wanted)
        => Assert.AreEqual(wanted, InstallWizardGate.Wants(args, new InstallTerminal(terminal, _ => null)));

    [TestMethod]
    public void TheRequiredQuestions_AreNamedByTheirOptions()
    {
        string[] required = ["--friendly-name", "--release", "--catalog-bundle"];
        string[] install = ["cameraagent", "install"];
        CollectionAssert.AreEqual(required, InstallWizardGate.MissingRequired(new Dictionary<string, string?>()).ToArray());
        Assert.IsFalse(InstallWizardGate.MissingRequired(new Dictionary<string, string?> { ["--friendly-name"] = "N", ["--image-ref"] = "i", ["--catalog-bundle"] = "c" }).Any());
        CollectionAssert.AreEqual(install, InstallWizardGate.InstallArguments([]));
        Assert.IsFalse(InstallTerminal.None.IsInteractive);
        Assert.IsNull(InstallTerminal.None.Environment("TERM"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(new[] { "cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog }, "--friendly-name is required.")]
    [DataRow(new[] { "cameraagent", "install", "--friendly-name", "N", "--catalog-bundle", Catalog }, "--release or --image-ref is required.")]
    [DataRow(new[] { "cameraagent", "install", "--friendly-name", "N", "--release", Release }, "--catalog-bundle is required.")]
    public async Task WithoutATerminal_NothingIsAsked_AndAMissingValueIsAUsageErrorThatNamesIt(string[] args, string message)
    {
        using var host = new WizardHost();
        var services = host.Services with { Terminal = InstallTerminal.None, Wizard = Unexpected };

        var (exitCode, _, error) = await RunWithoutInputAsync(args, services);

        Assert.AreEqual((int)InstallerExitCode.Usage, exitCode);
        StringAssert.StartsWith(error, message, StringComparison.Ordinal);
        Assert.IsEmpty(host.Requests);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("--plan")]
    [DataRow("--json")]
    public async Task OnATerminal_APlanOrJson_AsksNothing(string flag)
    {
        using var host = new WizardHost();
        var services = host.Services with { Wizard = Unexpected };

        var (exitCode, _, error) = await RunWithoutInputAsync(["cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog, flag], services);

        Assert.AreEqual((int)InstallerExitCode.Usage, exitCode);
        StringAssert.Contains(error, "--friendly-name is required.", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OnATerminal_AnAnswersFileOrACompleteCommandLine_InstallsWithoutAsking()
    {
        using var host = new WizardHost();
        var services = host.Services with { Wizard = Unexpected };
        var answers = Path.Combine(host.Root, "answers.json");
        await File.WriteAllTextAsync(answers, $$"""{"schemaVersion":1,"instanceId":"{{WizardHost.InstanceId:D}}","friendlyName":"North","release":"{{Release}}","catalogBundle":"{{Catalog}}"}""");

        var fromFile = await RunWithoutInputAsync(["cameraagent", "install", "--answers", answers], services);
        var fromFlags = await RunWithoutInputAsync(["cameraagent", "install", "--friendly-name", "North", "--release", Release, "--catalog-bundle", Catalog], services);

        Assert.AreEqual(0, fromFile.ExitCode, fromFile.Error);
        Assert.AreEqual(0, fromFlags.ExitCode, fromFlags.Error);
        Assert.AreEqual(2, host.Installs.Count);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task OnATerminal_AnInstallLeavingAQuestion_IsHandedToTheWizard()
    {
        using var host = new WizardHost();
        string[]? handed = null;
        var services = host.Services with
        {
            Wizard = (args, _, _) =>
            {
                handed = args;
                return Task.FromResult(130);
            }
        };

        var (exitCode, _, _) = await RunWithoutInputAsync([], services);

        Assert.AreEqual(130, exitCode);
        CollectionAssert.AreEqual(Array.Empty<string>(), handed);
    }

    private static Task<int> Unexpected(string[] args, ProgramServices services, CancellationToken cancellationToken)
        => throw new AssertFailedException("The wizard must not run here.");

    /// <summary>Runs the installer with an input that fails the test if anything reads it.</summary>
    internal static async Task<(int ExitCode, string Output, string Error)> RunWithoutInputAsync(string[] args, ProgramServices services)
    {
        var input = Console.In;
        var output = Console.Out;
        var error = Console.Error;
        using var captured = new StringWriter();
        using var errors = new StringWriter();
        using var unreadable = new UnreadableInput();
        Console.SetIn(unreadable);
        Console.SetOut(captured);
        Console.SetError(errors);
        try
        {
            var exitCode = await Program.RunAsync(args, services, CancellationToken.None);
            return (exitCode, captured.ToString(), errors.ToString());
        }
        finally
        {
            Console.SetIn(input);
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    private sealed class UnreadableInput : TextReader
    {
        public override int Peek() => throw new AssertFailedException("Nothing may be asked here.");

        public override int Read() => throw new AssertFailedException("Nothing may be asked here.");

        public override string ReadLine() => throw new AssertFailedException("Nothing may be asked here.");
    }
}
