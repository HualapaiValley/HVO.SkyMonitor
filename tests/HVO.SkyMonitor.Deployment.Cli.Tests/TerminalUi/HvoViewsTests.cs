using HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// The shared views on Terminal.Gui's in-memory ANSI driver: panels and their ASCII frames, choices and their marks,
/// bounded and scrolled text, and the bars, each laid out within the screen whatever it is given.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class HvoViewsTests
{
    private static readonly HvoTheme Full = HvoTheme.For(TerminalCapabilities.Full);

    private static readonly HvoTheme Ascii = HvoTheme.For(new TerminalCapabilities(TerminalColours.None, TerminalBorders.Ascii));

    [TestMethod]
    public void APanel_IsFramedAndTitled_InBoxDrawingOrAscii()
    {
        var unicode = Draw(Full, 30, 5, theme => new HvoPanel(theme, "Camera") { Width = 20, Height = 4 });
        Assert.AreEqual("┌─ CAMERA ─────────┐", unicode[0][..20]);
        Assert.AreEqual("│", unicode[1][..1]);
        Assert.AreEqual("└──────────────────┘", unicode[3][..20]);

        var ascii = Draw(Ascii, 30, 5, theme => new HvoPanel(theme, "Camera") { Width = 20, Height = 4 });
        Assert.AreEqual("+- CAMERA ---------+", ascii[0][..20]);
        Assert.AreEqual("|", ascii[1][..1]);
        Assert.AreEqual("+------------------+", ascii[3][..20]);
        Assert.IsFalse(string.Concat(ascii).Any(c => c > 127), "nothing outside ASCII");
    }

    [TestMethod]
    [DataRow("Camera", 20, " CAMERA ")]
    [DataRow("A very long panel title", 14, " A VERY L")]
    [DataRow("Camera", 5, "")]
    [DataRow("", 20, "")]
    public void APanelsTitle_FitsBetweenItsCorners(string caption, int width, string expected)
    {
        ArgumentNullException.ThrowIfNull(caption);
        ArgumentNullException.ThrowIfNull(expected);
        var title = HvoPanel.TitleText(caption, width);
        Assert.AreEqual(expected, title[..Math.Min(expected.Length, title.Length)]);
    }

    [TestMethod]
    public void APanelsCaption_CanChange()
    {
        using var panel = new HvoPanel(Full, "One");
        panel.Caption = "Two";
        Assert.AreEqual("Two", panel.Caption);
        panel.Caption = null!;
        Assert.AreEqual(string.Empty, panel.Caption);
    }

    [TestMethod]
    public void AChoiceList_MarksTheChoiceAndUpDownHomeEndMoveIt()
    {
        using var app = WizardDriver.CreateApplication(40, 6);
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        using var choices = new HvoChoiceList(Ascii, ["one", "two", "three"], 1);
        var changes = 0;
        choices.ValueChanged += (_, _) => changes++;
        window.Add(choices);
        var token = app.Begin(window);
        try
        {
            choices.SetFocus();
            string[] lines = ["( ) one", "(*) two", "( ) three"];
            CollectionAssert.AreEqual(lines, choices.Lines.ToArray());
            Assert.AreEqual("(*) two", Row(app, 1).TrimEnd());

            app.InjectKey(Key.CursorDown);
            Assert.AreEqual(2, choices.Value);
            app.InjectKey(Key.CursorDown);
            Assert.AreEqual(2, choices.Value, "the last stays chosen");
            app.InjectKey(Key.Home);
            Assert.AreEqual(0, choices.Value);
            app.InjectKey(Key.End);
            Assert.AreEqual(2, choices.Value);
            app.InjectKey(Key.CursorUp);
            Assert.AreEqual(1, choices.Value);
            choices.Value = 9;
            Assert.AreEqual(2, choices.Value);
            Assert.AreEqual(5, changes);
            Assert.AreEqual(Ascii.Choice.Focus, Cell(app, 2, 0));
        }
        finally
        {
            app.End(token!);
        }

        Assert.ThrowsExactly<ArgumentException>(() => new HvoChoiceList(Full, []));
    }

    [TestMethod]
    public void AToggle_IsTickedAndClearedWithSpace()
    {
        using var app = WizardDriver.CreateApplication(40, 3);
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        using var toggle = new HvoToggle(Full, "I know");
        var changes = 0;
        toggle.CheckedChanged += (_, _) => changes++;
        window.Add(toggle);
        var token = app.Begin(window);
        try
        {
            toggle.SetFocus();
            Assert.AreEqual("[ ] I know", toggle.Line);
            app.InjectKey(Key.Space);
            Assert.IsTrue(toggle.Checked);
            Assert.AreEqual("[x] I know", Row(app, 0).TrimEnd());
            app.InjectKey(Key.Space);
            Assert.IsFalse(toggle.Checked);
            toggle.Checked = false;
            Assert.AreEqual(2, changes);
        }
        finally
        {
            app.End(token!);
        }
    }

    [TestMethod]
    [DataRow("short", 20, new[] { "short" })]
    [DataRow("one two three four", 9, new[] { "one two", "three", "four" })]
    [DataRow("  create     folder     /var/lib/hvo-sky/cameraagents/6f9b3c1e-2d4a/x", 60, new[] { "  create     folder     /var/lib/hvo-sky/cameraagents/6f9b3c", "                        1e-2d4a/x" })]
    [DataRow("  create     folder     /var/lib/hvo-sky/cameraagents/x", 40, new[] { "  create     folder", "             /var/lib/hvo-sky/cameraagen", "             ts/x" })]
    [DataRow("Label:    a value that is too long to fit", 24, new[] { "Label:    a value that", "          is too long to", "          fit" })]
    [DataRow("abcdefghij", 4, new[] { "abcd", "efgh", "ij" })]
    [DataRow("anything", 0, new[] { "anything" })]
    public void Lines_WrapUnderTheirColumn(string line, int width, string[] expected)
        => CollectionAssert.AreEqual(expected, HvoTextLines.Wrap(line, width).ToArray());

    [TestMethod]
    public void Lines_MoreThanFit_SayWhereTheReaderIsAndScroll()
    {
        using var app = WizardDriver.CreateApplication(30, 6);
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        using var lines = new HvoTextLines(Full, HvoGround.Page) { Width = 30, Height = 4 };
        window.Add(lines);
        var token = app.Begin(window);
        try
        {
            lines.Show(Enumerable.Range(1, 10).Select(i => $"line {i}"));
            lines.SetFocus();
            app.LayoutAndDraw(true);
            Assert.IsTrue(lines.Overflows);
            Assert.AreEqual("line 1", Row(app, 0).TrimEnd());
            StringAssert.StartsWith(Row(app, 3), "-- lines 1-3 of 10", StringComparison.Ordinal);

            app.InjectKey(Key.CursorDown);
            Assert.AreEqual(1, lines.Top);
            app.InjectKey(Key.PageDown);
            Assert.AreEqual(4, lines.Top);
            app.InjectKey(Key.End);
            Assert.AreEqual(7, lines.Top);
            StringAssert.StartsWith(Row(app, 3), "-- lines 8-10 of 10", StringComparison.Ordinal);
            app.InjectKey(Key.PageUp);
            Assert.AreEqual(4, lines.Top);
            app.InjectKey(Key.CursorUp);
            Assert.AreEqual(3, lines.Top);
            app.InjectKey(Key.Home);
            Assert.AreEqual(0, lines.Top);

            lines.Reveal(6);
            app.LayoutAndDraw(true);
            Assert.AreEqual("line 6", Row(app, 0).TrimEnd(), "the line before the revealed one is at the top");

            lines.Show([new HvoLine("only", HvoTone.Danger, Bold: true)]);
            app.LayoutAndDraw(true);
            Assert.IsFalse(lines.Overflows);
            string[] only = ["only"];
            CollectionAssert.AreEqual(only, lines.Shown.ToArray());
            Assert.AreEqual(Full.Label(HvoTone.Danger, HvoGround.Page, bold: true).Normal, Cell(app, 0, 0));
        }
        finally
        {
            app.End(token!);
        }
    }

    [TestMethod]
    [DataRow("TITLE", "BADGE", 20, " TITLE        BADGE ")]
    [DataRow("A LONG TITLE HERE", "BADGE", 20, " A LONG TITLE HERE  ")]
    [DataRow("TITLE", "", 10, " TITLE    ")]
    [DataRow("TITLE", "B", 2, "  ")]
    public void AHeaderRow_KeepsItsLeftAndDropsItsRightWhenNarrow(string left, string right, int width, string expected)
        => Assert.AreEqual(expected, HvoHeaderBar.Row(left, right, width));

    [TestMethod]
    public void TheHeaderBar_DrawsItsNameInCapitalsAndItsBadge()
    {
        var rows = Draw(Full, 40, 3, theme => new HvoHeaderBar(theme) { Heading = "hvo installer", Badge = "STEP 1 OF 2", BadgeTone = HvoTone.Success, Context = "context", Note = "note" });
        Assert.AreEqual(" HVO INSTALLER".PadRight(28) + "STEP 1 OF 2 ", rows[0]);
        Assert.AreEqual(" context".PadRight(35) + "note ", rows[1]);
    }

    [TestMethod]
    public void TheKeyBar_LeavesOutKeysThatDoNotFit()
    {
        using var bar = new HvoKeyBar(Full) { Keys = [("Enter", "next"), ("Esc", "cancel")], Right = "80x24" };
        Assert.AreEqual(" Enter next  Esc cancel          80x24 ", bar.Render(39));
        Assert.AreEqual(" Enter next  ", bar.Render(13));
        Assert.AreEqual("80x24", bar.Right);
        Assert.AreEqual(2, bar.Keys.Count);
    }

    [TestMethod]
    public void TheTabBar_BracketsTheCurrentTabAndShortensTheOthersWhenNarrow()
    {
        using var bar = new HvoTabBar(Full) { Tabs = ["1 Camera", "2 Software", "3 Review"], Current = 1, Right = "F10 Quit" };
        Assert.AreEqual(1, bar.Current);
        Assert.AreEqual("F10 Quit", bar.Right);
        string[] full = [" 1 Camera ", "[2 Software]", " 3 Review "];
        string[] shortened = [" 1 ", "[2 Software]", " 3 "];
        CollectionAssert.AreEqual(full, bar.Layout(60).Select(cell => cell.Label).ToArray());
        CollectionAssert.AreEqual(shortened, bar.Layout(30).Select(cell => cell.Label).ToArray());
        Assert.IsTrue(bar.Layout(30).Single(cell => cell.Current).Label.StartsWith('['));
        Assert.IsTrue(bar.Layout(12).All(cell => cell.Column + cell.Label.Length <= 11));

        var rows = Draw(Full, 50, 2, theme => new HvoTabBar(theme) { Tabs = ["1 One", "2 Two"], Current = 0, Right = "F10 Quit" });
        Assert.AreEqual(" [1 One]  2 Two                         F10 Quit  ", rows[0]);
    }

    [TestMethod]
    public void ANotice_StartsWithItsWordAndTakesTheRowsItNeeds()
    {
        using var notice = new HvoNoticeBar(Full);
        var changes = 0;
        notice.Changed += (_, _) => changes++;
        Assert.AreEqual(string.Empty, notice.Message);
        Assert.AreEqual(1, notice.RowsFor(40));

        notice.Show(HvoNotice.Warning, "Check this.");
        Assert.AreEqual("[WARNING] Check this.", notice.Message);
        Assert.AreEqual(HvoNotice.Warning, notice.Kind);
        Assert.AreEqual("Check this.", notice.Body);
        notice.Show(HvoNotice.Danger, "No.", "[REFUSED]");
        Assert.AreEqual("[REFUSED] No.", notice.Message);
        notice.Show(HvoNotice.Danger, new string('x', 30) + " " + new string('y', 30) + " " + new string('z', 30));
        Assert.AreEqual(2, notice.RowsFor(40), "at most two rows");
        Assert.AreEqual(3, notice.RowsFor(40, most: 3));
        notice.Clear();
        Assert.AreEqual(string.Empty, notice.Message);
        Assert.AreEqual(4, changes);

        var rows = Draw(Full, 30, 3, theme =>
        {
            var bar = new HvoNoticeBar(theme) { Height = 2 };
            bar.Show(HvoNotice.Success, "Saved the answers to the file.");
            return bar;
        });
        Assert.AreEqual(" [OK] Saved the answers to".PadRight(30), rows[0]);
        Assert.AreEqual(" the file.".PadRight(30), rows[1]);
    }

    private static string[] Draw(HvoTheme theme, int width, int height, Func<HvoTheme, View> create)
    {
        using var app = WizardDriver.CreateApplication(width, height);
        using var window = new Window { BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        var view = create(theme);
        window.Add(view);
        var token = app.Begin(window);
        try
        {
            app.LayoutAndDraw(true);
            return [.. (app.Driver!.ToString() ?? string.Empty).TrimEnd('\n').Split('\n').Select(row => row.TrimEnd('\r'))];
        }
        finally
        {
            app.End(token!);
        }
    }

    private static string Row(IApplication app, int row)
    {
        app.LayoutAndDraw(true);
        return (app.Driver!.ToString() ?? string.Empty).Split('\n')[row].TrimEnd('\r');
    }

    private static Terminal.Gui.Drawing.Attribute? Cell(IApplication app, int row, int column)
    {
        app.LayoutAndDraw(true);
        return app.Driver!.Contents![row, column].Attribute;
    }
}
