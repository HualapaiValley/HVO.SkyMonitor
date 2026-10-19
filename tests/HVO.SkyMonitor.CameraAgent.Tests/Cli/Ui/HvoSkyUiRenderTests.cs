using System.Runtime.Versioning;
using HVO.SkyMonitor.CameraAgent.Cli.Ui;
using HVO.SkyMonitor.TerminalUi;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli.Ui;

/// <summary>
/// <c>hvo-sky ui</c>'s screens: each page at 80 x 24 and 120 x 32, the fallbacks (no colour, the 16 ANSI colours and
/// ASCII frames), and the screens that mark what is not current, saved as ANSI (<see cref="TerminalRenders"/>) for the
/// docs' screenshots, which <c>scripts/docs:terminal-screenshots</c> draws as SVGs. Each render is also checked: it fits,
/// its bars are on the screen, and no line runs past the edge.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[UnsupportedOSPlatform("windows")]
public sealed class HvoSkyUiRenderTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A harness signed in through the environment, so the screens show the same paths on every machine: an operator's
    /// config folder, which holds no file, and the key from <c>HVO_SKY_API_KEY</c>.
    /// </summary>
    private static CliHarness Operator()
    {
        var harness = new CliHarness(signedIn: false);
        harness.Environment["XDG_CONFIG_HOME"] = "/home/operator/.config";
        harness.Environment["HVO_SKY_URL"] = CliHarness.Url;
        harness.Environment["HVO_SKY_API_KEY"] = CliHarness.Key;
        return harness;
    }

    [TestMethod]
    [DataRow(80, 24, "80x24")]
    [DataRow(120, 32, "120x32")]
    public void EveryPage_FitsAndIsRendered(int width, int height, string size)
    {
        ArgumentNullException.ThrowIfNull(size);
        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness, width, height);

        Fits(driver, width, height);
        driver.Render(TestContext, $"ui-{size}-1-status");
        TestContext.WriteLine(driver.Screen);
        foreach (var (index, name) in new[] { (1, "2-settings"), (2, "3-rig"), (3, "4-schedule"), (4, "5-system") })
        {
            driver.ShowPage(index);
            Fits(driver, width, height);
            driver.Render(TestContext, $"ui-{size}-{name}");
            TestContext.WriteLine(driver.Screen);
        }
    }

    [TestMethod]
    [DataRow(80, 24, "80x24")]
    [DataRow(120, 32, "120x32")]
    public void AReview_IsRenderedOverItsPage(int width, int height, string size)
    {
        ArgumentNullException.ThrowIfNull(size);
        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness, width, height);
        driver.ShowPage(4);

        driver.Ui.Pages[4].Press("Restart");

        Fits(driver, width, height);
        StringAssert.Contains(driver.Screen, "RESTART / REVIEW", StringComparison.Ordinal);
        driver.Render(TestContext, $"ui-{size}-5-system-restart-review");
    }

    [TestMethod]
    [DataRow("nocolor", "NO_COLOR", "1")]
    [DataRow("ansi16", "COLORTERM", null)]
    [DataRow("ascii", "LANG", "C")]
    public void TheFallbacks_AreRendered(string mode, string variable, string? value)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(variable);
        var environment = new Dictionary<string, string?>(SkyUiDriver.Operator) { [variable] = value };
        if (mode == "ansi16")
        {
            environment["TERM"] = "xterm";
        }

        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness, environment: environment);

        Fits(driver, 80, 24);
        driver.Render(TestContext, $"ui-80x24-1-status-{mode}");
        driver.ShowPage(1);
        Fits(driver, 80, 24);
        driver.Render(TestContext, $"ui-80x24-2-settings-{mode}");
        TestContext.WriteLine(driver.Screen);
        if (mode == "ascii")
        {
            // A terminal that is not UTF-8 gets nothing but ASCII: frames, separators and markers alike.
            foreach (var index in new[] { 0, 2, 3, 4 })
            {
                driver.ShowPage(index);
                Assert.IsTrue(driver.Screen.All(char.IsAscii), driver.Screen);
            }
        }
    }

    [TestMethod]
    [DataRow(80, 24, "80x24")]
    [DataRow(120, 32, "120x32")]
    public void WhatIsNotCurrent_IsMarkedAndRendered(int width, int height, string size)
    {
        ArgumentNullException.ThrowIfNull(size);
        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness, width, height);
        harness.Api.Unreachable = true;
        ((SkyUiClock)harness.Time).Now = SkyUiClock.Observed.AddSeconds(40);

        driver.Tick();

        Fits(driver, width, height);
        StringAssert.Contains(driver.Rows[0], "[OFFLINE]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "LAST KNOWN", StringComparison.Ordinal);
        driver.Render(TestContext, $"ui-{size}-1-status-offline");
        TestContext.WriteLine(driver.Screen);
    }

    private static void Fits(SkyUiDriver driver, int width, int height)
    {
        var rows = driver.Rows;
        Assert.HasCount(height, rows, driver.Screen);
        Assert.IsTrue(rows.All(row => TerminalText.Width(row) <= width), driver.Screen);
        StringAssert.Contains(rows[0], "HVO SKYMONITOR", StringComparison.Ordinal);
        StringAssert.Contains(rows[height - 2], "[F", StringComparison.Ordinal);
        StringAssert.Contains(rows[height - 2], "F10 Quit", StringComparison.Ordinal);
        foreach (var name in width >= SkyTerminalUi.WideWidth ? SkyTerminalUi.PageNames : SkyTerminalUi.NarrowPageNames)
        {
            StringAssert.Contains(rows[height - 2], name, StringComparison.Ordinal);
        }

        StringAssert.Contains(rows[height - 1], "Esc close prompt", StringComparison.Ordinal);
        StringAssert.EndsWith(rows[height - 1].TrimEnd(), $"{width}x{height}", StringComparison.Ordinal);
    }
}
