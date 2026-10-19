using System.Globalization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;
using HVO.SkyMonitor.Deployment.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// Text that is not one cell per UTF-16 unit (PR-1371-R1 F4, R2 F8): CJK and supplementary-plane characters, emoji,
/// flags, ZWJ sequences and emoji with a skin tone take two cells, a letter with a combining mark one. Lines wrap and
/// clip by cells, never inside a grapheme, so nothing runs past its box and nothing is lost where it cannot be scrolled
/// to. The oracle is Terminal.Gui's renderer itself: <see cref="Cells"/> draws the text and reads how far it went.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class HvoUnicodeTests
{
    /// <summary>
    /// A camera name in several scripts: CJK, a combining accent, supplementary-plane characters, two flags, a ZWJ family
    /// and an emoji with a skin tone.
    /// </summary>
    internal const string Name = "東京の全天カメラ 𠀋 Cafe\u0301 😀 🇺🇸🇯🇵 👨\u200D👩\u200D👧 👍🏽 北の空 東京の全天カメラ 𠀋 Cafe\u0301";

    /// <summary>A name of flags, ZWJ families and emoji with a skin tone, each two cells as the renderer draws them.</summary>
    internal const string Flags = "🇺🇸🇯🇵🇬🇧🇫🇷🇩🇪🇮🇹🇪🇸🇨🇦 👨\u200D👩\u200D👧👨\u200D👩\u200D👧 👍🏽👍🏽 🇧🇷🇦🇺";

    private static readonly Lock RendererGate = new();
    private static IApplication? s_renderer;

    [TestMethod]
    [DataRow("🇺🇸")]
    [DataRow("🇺🇸🇯🇵🇬🇧")]
    [DataRow("👨\u200D👩\u200D👧")]
    [DataRow("👍🏽")]
    [DataRow("a👍🏽b🇺🇸c")]
    [DataRow("Cafe\u0301")]
    [DataRow("東京")]
    [DataRow("𠀋😀")]
    [DataRow("plain ASCII")]
    [DataRow(Name)]
    public void EachGrapheme_IsMeasuredAsTheRendererDrawsIt(string text)
        => Assert.AreEqual(Cells(text), TerminalText.Width(text), text);

    [TestMethod]
    [DataRow("東京の全天カメラ 北の空 東京の全天カメラ", 9)]
    [DataRow("𠀋𠀋𠀋 😀😀 𠀋𠀋𠀋𠀋𠀋 😀", 7)]
    [DataRow("Café Café Café Café Café", 6)]
    [DataRow("  create     folder     /var/lib/hvo-sky/東京の全天カメラ/𠀋𠀋/Café", 40)]
    [DataRow("🇺🇸🇯🇵🇬🇧 👨\u200D👩\u200D👧👨\u200D👩\u200D👧 👍🏽👍🏽👍🏽", 5)]
    [DataRow("a🇺🇸 b🇯🇵 c👍🏽 d👨\u200D👩\u200D👧", 3)]
    [DataRow(Name, 20)]
    [DataRow(Name, 72)]
    [DataRow("東", 1)]
    public void Wrapping_IsByDisplayCells_AndNeverSplitsATextElement(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rows = HvoTextLines.Wrap(line, width).ToArray();
        var whole = Elements(line).ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            Assert.IsTrue(Cells(row) <= Math.Max(width, 2), $"'{row}' is {Cells(row)} cells, wider than {width}");
            var cut = Elements(row).FirstOrDefault(element => element != " " && !whole.Contains(element));
            Assert.IsNull(cut, $"'{row}' holds part of a grapheme: '{cut}'");
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
    [DataRow("🇺🇸🇯🇵", 3, "🇺🇸")]
    [DataRow("a🇺🇸", 2, "a")]
    [DataRow("a👨\u200D👩\u200D👧b", 2, "a")]
    [DataRow("a👨\u200D👩\u200D👧b", 3, "a👨\u200D👩\u200D👧")]
    [DataRow("👍🏽x", 2, "👍🏽")]
    [DataRow("👍🏽x", 1, "")]
    public void Clipping_IsByDisplayCells(string text, int width, string expected)
        => Assert.AreEqual(expected, HvoChoiceList.Fit(text, width));

    [TestMethod]
    [DataRow(30)]
    [DataRow(80)]
    public void ARow_IsExactlyItsWidthInCells(int width)
    {
        Assert.AreEqual(width, Cells(HvoHeaderBar.Row(Name, "STEP 1 OF 7", width)));
        Assert.AreEqual(width, Cells(HvoHeaderBar.Row("北の空", "東京", width)));
        Assert.AreEqual(width, Cells(HvoHeaderBar.Row("🇺🇸", string.Empty, width)));
        Assert.AreEqual(width, Cells(HvoHeaderBar.Row("👨\u200D👩\u200D👧 👍🏽", "🇯🇵", width)));

        var text = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(TerminalColours.None, TerminalBorders.Unicode)));
        var panel = Plain(text.Panel(Name, [new(Name), new(HvoConsoleText.Field("Camera", Name, 10))], width)).TrimEnd('\n').Split('\n');
        Assert.IsTrue(panel.All(row => Cells(row) == width), string.Join('\n', panel));
        var notice = Plain(text.Notice(HvoNotice.Warning, Name, width)).TrimEnd('\n').Split('\n');
        Assert.IsTrue(notice.All(row => Cells(row) == width), string.Join('\n', notice));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(80, 24, Name)]
    [DataRow(120, 32, Name)]
    [DataRow(80, 24, Flags)]
    [DataRow(120, 32, Flags)]
    public void TheWizard_ShowsEveryCellOfANameInAnyScript(int width, int height, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var host = new WizardHost();
        using var driver = WizardDriver.Start(host, ["cameraagent", "install", "--friendly-name", name, "--release", "/srv/hvo/release", "--catalog-bundle", "/srv/hvo/catalog.bundle"], width, height);
        var review = driver.NextTo<ReviewPage>();
        driver.WaitIdle("the plan check");

        var plan = review.Plan;
        Assert.IsTrue(plan.Shown.All(row => Cells(row) <= plan.Viewport.Width), string.Join('\n', plan.Shown));
        var header = review.Describe()[0];
        StringAssert.Contains(header, name, StringComparison.Ordinal);
        CollectionAssert.AreEqual(
            Elements(header).Where(element => element != " ").ToArray(),
            plan.Shown.TakeWhile(row => row.Length > 0).SelectMany(Elements).Where(element => element != " ").ToArray(),
            "the whole first line can be read, wrapped");
        if (driver.Wizard.SummaryShown)
        {
            StringAssert.Contains(driver.Wizard.Summary()[0].Text, name, StringComparison.Ordinal);
            var answers = driver.Wizard.SummaryLines;
            Assert.IsTrue(answers.Shown.All(row => Cells(row) <= answers.Viewport.Width), string.Join('\n', answers.Shown));
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

    /// <summary>
    /// The cells Terminal.Gui's renderer gives <paramref name="text"/>: it draws the text from the first column of a wide
    /// screen and says how far the cursor moved.
    /// </summary>
    internal static int Cells(string text)
    {
        lock (RendererGate)
        {
            s_renderer ??= WizardDriver.CreateApplication(1024, 1);
            var driver = s_renderer.Driver!;
            driver.Move(0, 0);
            driver.AddStr(text);
            return driver.Col;
        }
    }

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
