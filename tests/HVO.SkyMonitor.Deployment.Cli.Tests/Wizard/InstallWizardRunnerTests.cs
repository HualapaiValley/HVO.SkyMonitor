using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;

/// <summary>
/// The wizard and the install it produces, end to end through <see cref="Program.RunAsync"/>: leaving the wizard exits
/// 130 with nothing changed, and Install runs <c>cameraagent install --answers FILE</c> through the same path, output
/// and exit codes as that command typed, with a panel before it and one after saying how to carry on.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class InstallWizardRunnerTests
{
    private const string Release = "/srv/hvo/release";
    private const string Catalog = "/srv/hvo/catalog.bundle";

    /// <summary>An install as install.sh hands it over: the release and the catalog, and no camera name yet.</summary>
    private static readonly string[] Args = ["cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("esc")]
    [DataRow("ctrl+c")]
    public async Task Leaving_Exits130WithNothingChanged(string how)
    {
        using var host = new WizardHost();
        var (exitCode, output, error) = await RunAsync(host, [], driver =>
        {
            driver.Press(how == "esc" ? Key.Esc : Key.C.WithCtrl);
        });

        Assert.AreEqual(130, exitCode);
        StringAssert.Contains(error, "[CANCELLED] Cancelled: nothing was changed.", StringComparison.Ordinal);
        Assert.AreEqual(string.Empty, output);
        Assert.IsEmpty(host.Requests);
        Assert.IsFalse(File.Exists(host.AnswersPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task LeavingAtTheReview_Exits130WithOnlyThePlanChecked()
    {
        using var host = new WizardHost();
        var (exitCode, _, _) = await RunAsync(host, Args, driver =>
        {
            driver.Replace("North");
            driver.NextTo<ReviewPage>();
            driver.WaitIdle("the plan check");
            driver.Press(Key.Esc);
        });

        Assert.AreEqual(130, exitCode);
        Assert.IsTrue(host.Requests.All(request => request.DryRun));
        Assert.HasCount(1, host.Requests);
        Assert.IsFalse(File.Exists(host.AnswersPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ASignal_ClosesTheWizard_Exit130()
    {
        using var host = new WizardHost();
        using var cancellation = new CancellationTokenSource();
        var (exitCode, _, error) = await RunAsync(host, [], driver =>
        {
            cancellation.Cancel();
            driver.WaitIdle("the signal", () => driver.Closed);
        }, cancellationToken: cancellation.Token);

        Assert.AreEqual(130, exitCode);
        StringAssert.Contains(error, "nothing was changed", StringComparison.Ordinal);
        Assert.IsEmpty(host.Requests);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Install_RunsTheSavedAnswersThroughTheInstallersOwnPath()
    {
        using var host = new WizardHost();
        var (exitCode, output, _) = await RunAsync(host, Args, Install);

        Assert.AreEqual(0, exitCode, output);
        var install = host.Installs.Single();
        var expected = host.Services.Parse(["cameraagent", "install", "--answers", host.AnswersPath, "--release", Release, "--catalog-bundle", Catalog]) is InstallDeploymentCommand command
            ? command.Request
            : throw new AssertFailedException("an install");
        Assert.AreEqual(expected, install);
        var installing = output.IndexOf("INSTALLING", StringComparison.Ordinal);
        var outcome = output.IndexOf("Outcome: Installed", StringComparison.Ordinal);
        var done = output.IndexOf("[OK] The install finished.", StringComparison.Ordinal);
        Assert.IsTrue(installing >= 0 && outcome > installing && done > outcome, output);
        StringAssert.Contains(output, "sudo", StringComparison.Ordinal);
        Assert.AreEqual(2, output.Split('\n').Count(line => line == $"hvo-sky-install cameraagent install --answers {host.AnswersPath} --release {Release} --catalog-bundle {Catalog}"), "the command, whole, under each panel");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("failed", 1, "[STOPPED] A step failed: see above.")]
    [DataRow("refused", 3, "[REFUSED] The installer refused: nothing was changed.")]
    [DataRow("interrupted", 130, "[STOPPED] Interrupted.")]
    public async Task AnInstallThatStops_SaysHowToCarryOn(string failure, int code, string notice)
    {
        using var host = new WizardHost();
        var plan = host.Services.Install;
        var services = host.Services with
        {
            Install = (request, token) => request.DryRun
                ? plan(request, token)
                : failure switch
                {
                    "failed" => throw new InstallerException("Docker did not answer."),
                    "refused" => throw InstallerException.Refused("Port 5130 is in use."),
                    _ => throw new OperationCanceledException()
                }
        };

        var (exitCode, output, error) = await RunAsync(host, Args, Install, services: services);

        Assert.AreEqual(code, exitCode, error);
        StringAssert.Contains(output, notice, StringComparison.Ordinal);
        StringAssert.Contains(output, $"\nhvo-sky-install cameraagent install --answers {host.AnswersPath} --release {Release} --catalog-bundle {Catalog}\n", StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(host.AnswersPath), "the answers stay, to carry on with");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AWizardThatCannotStart_AsksNothing_AndTheCommandLineSaysWhatItLacks()
    {
        using var host = new WizardHost();
        var interactive = Interactive(host, _ => throw new AssertFailedException("never runs")) with
        {
            CreateApplication = () => throw new InvalidOperationException("no terminal driver")
        };

        var (exitCode, _, error) = await RunAsync(host, ["cameraagent", "install", "--release", Release, "--catalog-bundle", Catalog], interactive);

        Assert.AreEqual(2, exitCode);
        StringAssert.Contains(error, "The wizard could not start on this terminal: no terminal driver", StringComparison.Ordinal);
        StringAssert.Contains(error, "--friendly-name is required.", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("--port", "many", "--port must be an integer.")]
    [DataRow("--unknown", "x", "Unknown option '--unknown'.")]
    [DataRow("--image-source", "disk", "--image-source must be archive or registry.")]
    public async Task AMistakeOnTheCommandLine_IsReportedBeforeTheWizardOpens(string option, string value, string message)
    {
        using var host = new WizardHost();
        var interactive = Interactive(host, _ => throw new AssertFailedException("The wizard must not open."));

        var (exitCode, _, error) = await RunAsync(host, ["cameraagent", "install", option, value], interactive);

        Assert.AreEqual(2, exitCode);
        StringAssert.StartsWith(error, message, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ThePanels_SayWhatRunsAndHowToCarryOn_ForEveryExitCode()
    {
        using var host = new WizardHost();
        var services = host.Services;
        var session = new InstallWizardSession([.. Args, "--friendly-name", "North"], services, services.Terminal, WizardHost.Version, WizardHost.InstanceId);
        var text = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(TerminalColours.None, TerminalBorders.Ascii)));
        const string command = "hvo-sky-install cameraagent install --answers /a.json";

        var installing = InstallWizardRunner.Installing(session, command).Select(line => line.Text).ToArray();
        StringAssert.StartsWith(installing[0], $"Carrying out the plan you reviewed for {session.Answers.FriendlyName}", StringComparison.Ordinal);
        CollectionAssert.Contains(installing, "The command, under this panel, is the one to run again.");
        StringAssert.EndsWith(InstallWizardRunner.Outcome(text, 0, session, command, 80), $"\n{command}\n", StringComparison.Ordinal);
        StringAssert.Contains(InstallWizardRunner.Outcome(text, 0, session, command, 80), "[OK] The install finished.", StringComparison.Ordinal);
        StringAssert.Contains(InstallWizardRunner.Outcome(text, 2, session, command, 80), "The answers are not valid", StringComparison.Ordinal);
        StringAssert.Contains(InstallWizardRunner.Outcome(text, 3, session, command, 80), "[REFUSED]", StringComparison.Ordinal);
        StringAssert.Contains(InstallWizardRunner.Outcome(text, 130, session, command, 80), "[STOPPED] Interrupted.", StringComparison.Ordinal);
        StringAssert.Contains(InstallWizardRunner.Outcome(text, 1, session, command, 80), "--resume where the message says so", StringComparison.Ordinal);
        Assert.IsTrue(InstallWizardRunner.Outcome(text, 1, session, command, 80).Split('\n').All(line => line.Replace("\u001b[0m", string.Empty, StringComparison.Ordinal).Length <= 80 + 12));
    }

    [TestMethod]
    [DataRow(new[] { "cameraagent", "install" }, "cameraagent install")]
    [DataRow(new[] { "--answers", "/a b.json" }, "--answers '/a b.json'")]
    [DataRow(new[] { "it's" }, "'it'\\''s'")]
    [DataRow(new[] { "" }, "''")]
    [DataRow(new[] { "/x/y.bundle", "a=b@c:d,e+f" }, "/x/y.bundle a=b@c:d,e+f")]
    public void Arguments_AreQuotedAsAShellTakesThemBack(string[] arguments, string quoted)
        => Assert.AreEqual(quoted, InstallWizardRunner.Quote(arguments));

    private static void Install(WizardDriver driver)
    {
        driver.Replace("North");
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        driver.Press(Key.Enter);
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAsync(
        WizardHost host, string[] args, Action<WizardDriver> drive, ProgramServices? services = null, CancellationToken cancellationToken = default)
        => RunAsync(host, args, Interactive(host, drive), services, cancellationToken);

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        WizardHost host, string[] args, InstallWizardRunner.Interactive interactive, ProgramServices? services = null, CancellationToken cancellationToken = default)
    {
        var output = Console.Out;
        var error = Console.Error;
        using var captured = new StringWriter();
        using var errors = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(errors);
        try
        {
            var running = (services ?? host.Services) with
            {
                Wizard = (a, s, token) => InstallWizardRunner.RunAsync(a, s, interactive with { Out = captured, Error = errors }, token)
            };
            var exitCode = await Program.RunAsync(args, running, cancellationToken);
            return (exitCode, captured.ToString(), errors.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    private static InstallWizardRunner.Interactive Interactive(WizardHost host, Action<WizardDriver> drive)
        => new(
            () => WizardDriver.CreateApplication(80, 24),
            (app, wizard) =>
            {
                wizard.Session.ScratchRoot = host.Root;
                using var driver = WizardDriver.Attach(app, wizard);
                drive(driver);
                Assert.IsTrue(driver.Closed, "the wizard closed");
            },
            TextWriter.Null,
            TextWriter.Null,
            () => 80);
}
