using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Input;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;

/// <summary>
/// The wizard's screens, each page at 80 x 24 and 120 x 32, and the fallbacks (no colour, the 16 ANSI colours and ASCII
/// frames), saved as ANSI (<see cref="TerminalRenders"/>) for the docs' screenshots: CI draws each as an SVG with
/// <c>tests/cli/ansi-to-svg.py</c>. Each render is also checked: it fits, its bars and buttons are on the screen, and no
/// line runs past the edge.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class InstallWizardRenderTests
{
    /// <summary>An operator's terminal, as the screenshots show it: the answers go to their own config folder.</summary>
    private static readonly Dictionary<string, string?> Operator = new(StringComparer.Ordinal)
    {
        ["HOME"] = "/home/operator",
        ["XDG_CONFIG_HOME"] = "/home/operator/.config"
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(80, 24, "80x24")]
    [DataRow(120, 32, "120x32")]
    public void EveryPage_FitsAndIsRendered(int width, int height, string size)
    {
        using var host = new WizardHost(Operator);
        using var driver = WizardDriver.Start(host, [], width, height);

        Fits(driver, width, height);
        driver.Render(TestContext, $"install-{size}-1-camera");
        driver.Replace("Allsky East");
        driver.NextTo<SoftwarePage>();
        Fits(driver, width, height);
        driver.Type("/home/operator/hvo-sky-0.9.0");
        driver.Press(Key.Tab);
        driver.Press(Key.Tab);
        driver.Type("/home/operator/.cache/hvo-sky/hyg-v4.2-p3-s2-r1.bundle");
        driver.Render(TestContext, $"install-{size}-2-software");
        driver.NextTo<NetworkPage>();
        Fits(driver, width, height);
        driver.Render(TestContext, $"install-{size}-3-network");
        driver.NextTo<LocationPage>();
        Fits(driver, width, height);
        driver.Render(TestContext, $"install-{size}-4-location");
        driver.NextTo<LimitsPage>();
        Fits(driver, width, height);
        driver.Render(TestContext, $"install-{size}-5-limits");
        driver.NextTo<OwnerPage>();
        Fits(driver, width, height);
        driver.Render(TestContext, $"install-{size}-6-owner");
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        Fits(driver, width, height);
        driver.Render(TestContext, $"install-{size}-7-review");
        TestContext.WriteLine(driver.Screen);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("nocolor", "NO_COLOR", "1")]
    [DataRow("ansi16", "COLORTERM", null)]
    [DataRow("ascii", "LANG", "C")]
    public void TheFallbacks_AreRendered(string mode, string variable, string? value)
    {
        var environment = new Dictionary<string, string?>(Operator) { [variable] = value };
        if (mode == "ansi16")
        {
            environment["TERM"] = "xterm";
        }

        using var host = new WizardHost(environment);
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--release", "/home/operator/hvo-sky-0.9.0", "--catalog-bundle", "/home/operator/.cache/hvo-sky/hyg-v4.2-p3-s2-r1.bundle"]);

        driver.Replace("Allsky East");
        Fits(driver, 80, 24);
        driver.Render(TestContext, $"install-80x24-1-camera-{mode}");
        driver.NextTo<OwnerPage>();
        Fits(driver, 80, 24);
        driver.Render(TestContext, $"install-80x24-6-owner-{mode}");
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        Fits(driver, 80, 24);
        driver.Render(TestContext, $"install-80x24-7-review-{mode}");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(80, 24, "80x24")]
    [DataRow(120, 32, "120x32")]
    public void AProblem_AndARefusedPlan_AreRendered(int width, int height, string size)
    {
        using var host = new WizardHost(Operator)
        {
            PlanFailure = _ => InstallerException.Refused("Port 5130 on 127.0.0.1 is already in use by another program.")
        };
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", "Allsky East", "--release", "/home/operator/hvo-sky-0.9.0", "--catalog-bundle", "/home/operator/.cache/hvo-sky/hyg-v4.2-p3-s2-r1.bundle"], width, height);

        driver.Replace("0.0.0.0");
        driver.Press(Key.Enter);
        Fits(driver, width, height);
        StringAssert.Contains(driver.Screen, "[ERROR] Other computers reach this address over plain HTTP", StringComparison.Ordinal);
        driver.Render(TestContext, $"install-{size}-3-network-problem");
        driver.Replace("127.0.0.1");
        driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");
        Fits(driver, width, height);
        StringAssert.Contains(driver.Screen, "[REFUSED] Port 5130", StringComparison.Ordinal);
        driver.Render(TestContext, $"install-{size}-7-review-refused");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(80, "80x24")]
    [DataRow(120, "120x32")]
    public async Task ThePanelsAroundTheInstall_AreRendered(int width, string size)
    {
        using var host = new WizardHost();
        var services = host.Services;
        var session = new InstallWizardSession(["cameraagent", "install", "--friendly-name", "Allsky East", "--release", "/home/operator/hvo-sky-0.9.0", "--catalog-bundle", "/home/operator/.cache/hvo-sky/hyg-v4.2-p3-s2-r1.bundle"], services, services.Terminal, WizardHost.Version, WizardHost.InstanceId)
        {
            AnswersPath = "/home/operator/.config/hvo-sky/install-answers.json"
        };
        var text = new HvoConsoleText(HvoTheme.For(TerminalCapabilities.Full));
        var command = $"hvo-sky-install {InstallWizardRunner.Quote(session.Arguments(session.AnswersPath))}";
        var answers = Path.Combine(host.Root, "answers.json");
        await File.WriteAllTextAsync(answers, session.AnswersJson());
        var (_, output, _) = await InstallWizardGateTests.RunWithoutInputAsync([.. session.Arguments(answers)], services);
        var installing = text.Panel("Installing", InstallWizardRunner.Installing(session, command), width) + command + "\n\n";

        var done = installing + output + InstallWizardRunner.Outcome(text, 0, session, command, width);
        var stopped = installing + "Installation failed: CameraAgent did not become healthy within 120 seconds.\n" + InstallWizardRunner.Outcome(text, 1, session, command, width);

        // Every row of a panel is the terminal's width; the command under it is whole, to be copied.
        var framed = Plain(done).Split('\n').Where(line => line.Length > 0 && "┌│└".Contains(line[0], StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(framed);
        Assert.IsTrue(framed.All(line => line.Length == width), string.Join('\n', framed));
        CollectionAssert.Contains(Plain(done).Split('\n'), command);
        TestContext.AddResultFile(TerminalRenders.SaveText(done, $"install-{size}-8-done", Fallback));
        TestContext.AddResultFile(TerminalRenders.SaveText(stopped, $"install-{size}-8-stopped", Fallback));
    }

    private string Fallback => Path.Combine(TestContext.TestRunResultsDirectory ?? Path.GetTempPath(), "terminal-renders");

    private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", string.Empty);

    private static void Fits(WizardDriver driver, int width, int height)
    {
        var rows = driver.Rows;
        Assert.AreEqual(height, rows.Length, driver.Screen);
        Assert.IsTrue(rows.All(row => row.Length <= width), driver.Screen);
        StringAssert.Contains(rows[0], "HVO SKYMONITOR INSTALLER", StringComparison.Ordinal);
        StringAssert.Contains(rows[height - 4], "[ Back ]", StringComparison.Ordinal);
        StringAssert.Contains(rows[height - 1], "Esc cancel", StringComparison.Ordinal);
    }
}
