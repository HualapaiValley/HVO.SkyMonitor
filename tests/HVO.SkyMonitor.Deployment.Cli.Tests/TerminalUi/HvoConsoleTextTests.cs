using System.Globalization;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.TerminalUi;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// HVO Dark as plain terminal output: panels and notices bounded to their width, in 24-bit colour, the 16 colours, or
/// no colour, with box-drawing or ASCII frames; and <see cref="TerminalRenders"/>, which saves screens for the docs.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class HvoConsoleTextTests
{
    [TestMethod]
    [DataRow(TerminalColours.TrueColour, TerminalBorders.Unicode)]
    [DataRow(TerminalColours.Ansi16, TerminalBorders.Unicode)]
    [DataRow(TerminalColours.None, TerminalBorders.Ascii)]
    public void APanel_IsBoundedToItsWidth(TerminalColours colours, TerminalBorders borders)
    {
        var text = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(colours, borders)));

        var panel = text.Panel("Installing", [new("A line long enough to wrap onto a second line in a narrow panel."), new(HvoConsoleText.Field("Answers", "/x", 10), HvoTone.Muted)], 40);

        var lines = Plain(panel).TrimEnd('\n').Split('\n');
        Assert.AreEqual(5, lines.Length, panel);
        Assert.IsTrue(lines.All(line => line.Length == 40), string.Join('\n', lines));
        StringAssert.StartsWith(lines[0], borders == TerminalBorders.Ascii ? "+- INSTALLING -" : "┌─ INSTALLING ─", StringComparison.Ordinal);
        StringAssert.StartsWith(lines[3], borders == TerminalBorders.Ascii ? "| Answers   /x" : "│ Answers   /x", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Colours_AreWrittenAsTheTerminalCanDrawThem()
    {
        var full = new HvoConsoleText(HvoTheme.For(TerminalCapabilities.Full)).Notice(HvoNotice.Danger, "No.", 30);
        var ansi = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(TerminalColours.Ansi16, TerminalBorders.Unicode))).Notice(HvoNotice.Danger, "No.", 30);
        var none = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(TerminalColours.None, TerminalBorders.Unicode))).Notice(HvoNotice.Danger, "No.", 30);

        StringAssert.Contains(full, "\u001b[0;38;2;254;202;202;48;2;66;34;38;1m", StringComparison.Ordinal);
        StringAssert.Contains(ansi, "\u001b[0;97;41;1m", StringComparison.Ordinal);
        StringAssert.Contains(none, "\u001b[0;1;7m", StringComparison.Ordinal);
        Assert.IsFalse(HasColour(none), "no colour without colour");
        Assert.IsTrue(HasColour(ansi));
        Assert.AreEqual(" [ERROR] No.".PadRight(30), Plain(none).TrimEnd('\n'));
    }

    [TestMethod]
    public void ANotice_WrapsAndTakesItsWord()
    {
        var text = new HvoConsoleText(HvoTheme.For(new TerminalCapabilities(TerminalColours.None, TerminalBorders.Ascii)));

        var lines = Plain(text.Notice(HvoNotice.Warning, "Interrupted, and this sentence is long enough to wrap.", 30, "[STOPPED]")).TrimEnd('\n').Split('\n');

        Assert.AreEqual(" [STOPPED] Interrupted, and", lines[0].TrimEnd());
        Assert.IsTrue(lines.All(line => line.Length == 30));
        Assert.AreEqual(3, lines.Length);
    }

    [TestMethod]
    public void AField_AlignsItsValue()
    {
        Assert.AreEqual("Answers   /x", HvoConsoleText.Field("Answers", "/x", 10));
        Assert.AreEqual("Very long label  /x", HvoConsoleText.Field("Very long label", "/x", 10));
    }

    [TestMethod]
    [DataRow(HvoNotice.Info)]
    [DataRow(HvoNotice.Success)]
    [DataRow(HvoNotice.Warning)]
    public void EveryKindOfNotice_IsDrawnInItsColours(HvoNotice kind)
    {
        var text = new HvoConsoleText(HvoTheme.For(TerminalCapabilities.Full));
        StringAssert.StartsWith(Plain(text.Notice(kind, "x", 20)), $" {HvoTheme.NoticeWord(kind)} x", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Renders_AreSavedAsAnsiByName()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hvo-renders-{Guid.NewGuid():N}");
        try
        {
            var path = TerminalRenders.SaveText("\u001b[1mx\u001b[0m", "install-80x24-1-camera", folder);

            Assert.AreEqual(Path.Combine(TerminalRenders.Folder(folder), "install-80x24-1-camera.ans"), path);
            Assert.AreEqual("\u001b[1mx\u001b[0m", File.ReadAllText(path));
            File.Delete(path);
            Assert.ThrowsExactly<ArgumentException>(() => TerminalRenders.SaveText("x", "../escape", folder));
            Assert.ThrowsExactly<ArgumentException>(() => TerminalRenders.SaveText("x", "Upper", folder));
            Assert.AreEqual(Environment.GetEnvironmentVariable(TerminalRenders.DirectoryVariable) is { Length: > 0 } configured ? configured : folder, TerminalRenders.Folder(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static string Plain(string text) => Escape().Replace(text, string.Empty);

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex Escape();

    /// <summary>
    /// Whether <paramref name="text"/> sets a colour: an SGR parameter of 30-38, 40-48, 90-97 or 100-107. 39 and 49 set
    /// the terminal's own colours, which is no colour.
    /// </summary>
    internal static bool HasColour(string text)
        => Escape().Matches(text)
            .SelectMany(match => match.Value[2..^1].Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(code => int.Parse(code, CultureInfo.InvariantCulture))
            .Any(code => code is >= 30 and <= 38 or >= 40 and <= 48 or >= 90 and <= 97 or >= 100 and <= 107);
}
