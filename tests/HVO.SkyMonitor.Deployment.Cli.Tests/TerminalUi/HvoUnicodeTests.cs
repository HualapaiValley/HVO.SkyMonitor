using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;
using HVO.SkyMonitor.Deployment.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Wcwidth;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// Text that is not one cell per UTF-16 unit (PR-1371-R1 F4): CJK and supplementary-plane characters take two cells,
/// a combining mark none. Lines wrap and clip by display cells, never inside a text element, so nothing runs past its
/// box and nothing is lost where it cannot be scrolled to. Widths here are measured with <c>Wcwidth</c>, the table
/// Terminal.Gui and the project use.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class HvoUnicodeTests
{
    /// <summary>A camera name in three scripts: CJK, a combining accent, and supplementary-plane characters.</summary>
    internal const string Name = "東京の全天カメラ 𠀋 Café 😀 北の空 東京の全天カメラ 𠀋 Café 😀";

    [TestMethod]
    [DataRow("東京の全天カメラ 北の空 東京の全天カメラ", 9)]
    [DataRow("𠀋𠀋𠀋 😀😀 𠀋𠀋𠀋𠀋𠀋 😀", 7)]
    [DataRow("Café Café Café Café Café", 6)]
    [DataRow("  create     folder     /var/lib/hvo-sky/東京の全天カメラ/𠀋𠀋/Café", 40)]
    [DataRow(Name, 20)]
    [DataRow(Name, 72)]
    [DataRow("東", 1)]
    public void Wrapping_IsByDisplayCells_AndNeverSplitsATextElement(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rows = HvoTextLines.Wrap(line, width).ToArray();

        foreach (var row in rows)
        {
            Assert.IsTrue(Cells(row) <= Math.Max(width, 2), $"'{row}' is {Cells(row)} cells, wider than {width}");
            Assert.IsFalse(row.Length > 0 && char.IsLowSurrogate(row[0]), $"'{row}' starts inside a surrogate pair");
            Assert.IsFalse(row.Length > 0 && char.IsHighSurrogate(row[^1]), $"'{row}' ends inside a surrogate pair");
            Assert.IsFalse(row.TrimStart().Length > 0 && CharUnicodeInfo.GetUnicodeCategory(row.TrimStart(), 0) == UnicodeCategory.NonSpacingMark,
                $"'{row}' starts with a combining mark cut from its letter");
        }

        // A break drops the spaces it falls on and a continuation row starts with the hanging indent: every other element
        // is there, in order.
        CollectionAssert.AreEqual(Printed(line), Printed(string.Concat(rows)), "nothing is lost or reordered");
    }

    [TestMethod]
    [DataRow("abc def ghi", 7, "abc def|ghi")]
    [DataRow("東京 北の 空", 4, "東京|北の|空")]
    public void AWordThatEndsAtTheEdge_StaysOnItsLine(string line, int width, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        CollectionAssert.AreEqual(expected.Split('|'), HvoTextLines.Wrap(line, width).ToArray());
    }

    [TestMethod]
    [DataRow("東京東京", 3, "東")]
    [DataRow("東京東京", 4, "東京")]
    [DataRow("Café 東", 6, "Café ")]
    [DataRow("Café", 4, "Café")]
    [DataRow("a😀b", 2, "a")]
    [DataRow("a😀b", 3, "a😀")]
    [DataRow("𠀋x", 2, "𠀋")]
    public void Clipping_IsByDisplayCells(string text, int width, string expected)
        => Assert.AreEqual(expected, HvoChoiceList.Fit(text, width));

    [TestMethod]
    [DataRow(30)]
    [DataRow(80)]
    public void ARow_IsExactlyItsWidthInCells(int width)
    {
        Assert.AreEqual(width, Cells(HvoHeaderBar.Row(Name, "STEP 1 OF 7", width)));
        Assert.AreEqual(width, Cells(HvoHeaderBar.Row("北の空", "東京", width)));

        var text = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(TerminalColours.None, TerminalBorders.Unicode)));
        var panel = Plain(text.Panel(Name, [new(Name), new(HvoConsoleText.Field("Camera", Name, 10))], width)).TrimEnd('\n').Split('\n');
        Assert.IsTrue(panel.All(row => Cells(row) == width), string.Join('\n', panel));
        var notice = Plain(text.Notice(HvoNotice.Warning, Name, width)).TrimEnd('\n').Split('\n');
        Assert.IsTrue(notice.All(row => Cells(row) == width), string.Join('\n', notice));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(80, 24)]
    [DataRow(120, 32)]
    public void TheWizard_ShowsEveryCellOfANameInAnyScript(int width, int height)
    {
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", Name, "--release", "/srv/hvo/release", "--catalog-bundle", "/srv/hvo/catalog.bundle"], width, height);
        var review = driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");

        var plan = review.Plan;
        Assert.IsTrue(plan.Shown.All(row => Cells(row) <= plan.Viewport.Width), string.Join('\n', plan.Shown));
        var header = review.Describe()[0];
        StringAssert.Contains(header, Name, StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            Elements(header).Where(element => element != " ").ToArray(),
            plan.Shown.TakeWhile(row => row.Length > 0).SelectMany(Elements).Where(element => element != " ").ToArray(),
            "the whole first line can be read, wrapped");
        if (driver.Wizard.SummaryShown)
        {
            var summary = driver.Wizard.Summary();
            StringAssert.Contains(summary[0].Text, Name, StringComparison.Ordinal);
        }

        Assert.IsTrue(driver.Rows.All(row => Cells(row) <= width), driver.Screen);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ThePanelsAroundTheInstall_KeepEveryRowToTheTerminalsWidth()
    {
        using var host = new WizardHost();
        var services = host.Services;
        var session = new InstallWizardSession(["cameraagent", "install", "--friendly-name", Name, "--release", "/srv/hvo/release", "--catalog-bundle", "/srv/hvo/catalog.bundle"], services, services.Terminal, WizardHost.Version, WizardHost.InstanceId);
        var text = new HvoConsoleText(HvoTheme.For(TerminalCapabilities.Full));

        foreach (var width in new[] { 80, 120 })
        {
            var rows = Plain(text.Panel("Installing", InstallWizardRunner.Installing(session, "hvo-sky-install cameraagent install"), width)).TrimEnd('\n').Split('\n');
            Assert.IsTrue(rows.All(row => Cells(row) == width), string.Join('\n', rows));
        }
    }

    /// <summary>The display width of <paramref name="text"/>: each text element as wide as its first scalar, as Wcwidth says.</summary>
    internal static int Cells(string text)
        => Elements(text).Sum(element => Math.Max(0, UnicodeCalculator.GetWidth(Rune.GetRuneAt(element, 0))));

    private static string[] Elements(string text)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        return [.. elements];
    }

    private static string[] Printed(string text) => [.. Elements(text).Where(element => element != " ")];

    private static string Plain(string text) => Escape().Replace(text, string.Empty);

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex Escape();
}
