using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.TerminalUi;

/// <summary>
/// What a terminal can draw, decided from its environment, and the theme for each: HVO Dark in true colour, each role's
/// ANSI-16 fallback, and no colour at all under <c>NO_COLOR</c>, where style alone marks the focus and the selection.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class HvoThemeTests
{
    [TestMethod]
    [DataRow("xterm-256color", "truecolor", "C.UTF-8", null, null, TerminalColours.TrueColour, TerminalBorders.Unicode)]
    [DataRow("xterm-256color", null, "en_US.UTF-8", null, null, TerminalColours.Ansi16, TerminalBorders.Unicode)]
    [DataRow("xterm-direct", null, "en_US.utf8", null, null, TerminalColours.Ansi16, TerminalBorders.Unicode)]
    [DataRow("xterm-256color", "yes", "C.UTF-8", null, null, TerminalColours.Ansi16, TerminalBorders.Unicode)]
    [DataRow("screen-256color", "24bit", "C.UTF-8", null, null, TerminalColours.TrueColour, TerminalBorders.Unicode)]
    [DataRow("xterm", "24bit", "C.UTF-8", null, null, TerminalColours.TrueColour, TerminalBorders.Unicode)]
    [DataRow("linux", null, "C.UTF-8", null, null, TerminalColours.Ansi16, TerminalBorders.Unicode)]
    [DataRow("xterm", null, "C.UTF-8", null, null, TerminalColours.Ansi16, TerminalBorders.Unicode)]
    [DataRow(null, null, null, null, null, TerminalColours.Ansi16, TerminalBorders.Ascii)]
    [DataRow("xterm-256color", "truecolor", "C", null, null, TerminalColours.TrueColour, TerminalBorders.Ascii)]
    [DataRow("vt100", null, "C.UTF-8", null, null, TerminalColours.Ansi16, TerminalBorders.Ascii)]
    [DataRow("dumb", null, "C.UTF-8", null, null, TerminalColours.None, TerminalBorders.Ascii)]
    [DataRow("xterm-256color", "truecolor", "C.UTF-8", "1", null, TerminalColours.None, TerminalBorders.Unicode)]
    [DataRow("xterm-256color", "truecolor", "C.UTF-8", "", null, TerminalColours.TrueColour, TerminalBorders.Unicode)]
    [DataRow("xterm-256color", "truecolor", "C.UTF-8", null, "ansi16,ascii", TerminalColours.Ansi16, TerminalBorders.Ascii)]
    [DataRow("linux", null, "C", null, "truecolor, unicode", TerminalColours.TrueColour, TerminalBorders.Unicode)]
    [DataRow("xterm-256color", null, "C.UTF-8", null, "nocolor", TerminalColours.None, TerminalBorders.Unicode)]
    [DataRow("xterm-256color", null, "C.UTF-8", "yes", "truecolor", TerminalColours.None, TerminalBorders.Unicode)]
    public void Detect_ReadsTheTerminalFromItsEnvironment(
        string? term, string? colourTerm, string? lang, string? noColor, string? overrides, TerminalColours colours, TerminalBorders borders)
    {
        var environment = new Dictionary<string, string?>
        {
            ["TERM"] = term,
            ["COLORTERM"] = colourTerm,
            ["LANG"] = lang,
            ["NO_COLOR"] = noColor,
            [TerminalCapabilities.OverrideVariable] = overrides
        };

        var detected = TerminalCapabilities.Detect(name => environment.GetValueOrDefault(name));

        Assert.AreEqual(new TerminalCapabilities(colours, borders), detected);
    }

    [TestMethod]
    public void Detect_ReadsTheLocaleInItsOrder()
    {
        var environment = new Dictionary<string, string?> { ["TERM"] = "xterm-256color", ["LC_ALL"] = "C", ["LC_CTYPE"] = "C.UTF-8", ["LANG"] = "C.UTF-8" };

        Assert.AreEqual(TerminalBorders.Ascii, TerminalCapabilities.Detect(name => environment.GetValueOrDefault(name)).Borders);
        environment.Remove("LC_ALL");
        Assert.AreEqual(TerminalBorders.Unicode, TerminalCapabilities.Detect(name => environment.GetValueOrDefault(name)).Borders);
    }

    [TestMethod]
    public void TrueColour_DrawsThePalettesValues()
    {
        var theme = HvoTheme.For(TerminalCapabilities.Full);

        Assert.AreEqual(new Color(HvoDarkPalette.Text.Hex), theme.Page.Normal.Foreground);
        Assert.AreEqual(new Color(HvoDarkPalette.Background.Hex), theme.Page.Normal.Background);
        Assert.AreEqual(new Color(HvoDarkPalette.Surface.Hex), theme.Panel.Normal.Background);
        Assert.AreEqual(new Color(HvoDarkPalette.Border.Hex), theme.PanelFrame.Foreground);
        Assert.AreEqual(new Color(HvoDarkPalette.Accent.Hex), theme.PanelTitle.Foreground);
        Assert.AreEqual(new Color(HvoDarkPalette.Focus.Hex), theme.PrimaryButton.Normal.Background);
        Assert.AreEqual(new Color(HvoDarkPalette.Focus.Hex), theme.Selected.Normal.Background);
        Assert.AreEqual(new Color(HvoDarkPalette.DangerBackground.Hex), theme.Notice(HvoNotice.Danger).Normal.Background);
        Assert.AreEqual(new Color(HvoDarkPalette.Muted.Hex), theme.Label(HvoTone.Muted, HvoGround.Surface).Normal.Foreground);
        Assert.AreEqual(new Color(HvoDarkPalette.Surface.Hex), theme.Label(HvoTone.Muted, HvoGround.Surface).Normal.Background);
        Assert.AreSame(theme, HvoTheme.For(TerminalCapabilities.Full));
        Assert.AreSame(HvoBoxGlyphs.Unicode, theme.Glyphs);
    }

    [TestMethod]
    public void Ansi16_DrawsEachRolesFallback()
    {
        var theme = HvoTheme.For(new TerminalCapabilities(TerminalColours.Ansi16, TerminalBorders.Ascii));

        Assert.AreEqual(new Color(ColorName16.White), theme.Page.Normal.Foreground);
        Assert.AreEqual(new Color(ColorName16.Black), theme.Page.Normal.Background);
        Assert.AreEqual(new Color(ColorName16.DarkGray), theme.PanelFrame.Foreground);
        Assert.AreEqual(new Color(ColorName16.BrightCyan), theme.PanelTitle.Foreground);
        Assert.AreEqual(new Color(ColorName16.Blue), theme.Selected.Normal.Background);
        Assert.AreEqual(new Color(ColorName16.Red), theme.Notice(HvoNotice.Danger).Normal.Background);
        Assert.AreEqual(new Color(ColorName16.Yellow), theme.Notice(HvoNotice.Warning).Normal.Background);
        Assert.AreSame(HvoBoxGlyphs.Ascii, theme.Glyphs);

        // The raised surface is black like the panel in 16 colours, so an input field is underlined there.
        Assert.IsTrue(theme.Field.Editable.Style.HasFlag(TextStyle.Underline));
        Assert.IsFalse(HvoTheme.For(TerminalCapabilities.Full).Field.Editable.Style.HasFlag(TextStyle.Underline));
    }

    [TestMethod]
    public void NoColour_UsesTheTerminalsColoursAndMarksStatesWithStyle()
    {
        var theme = HvoTheme.For(name => name == "NO_COLOR" ? "1" : name == "TERM" ? "xterm-256color" : null);
        Assert.AreEqual(TerminalColours.None, theme.Capabilities.Colours);

        var attributes = new[]
        {
            theme.Page.Normal, theme.Page.Focus, theme.Panel.Normal, theme.PanelFrame, theme.PanelTitle, theme.Bar.Normal,
            theme.Selected.Normal, theme.Field.Normal, theme.FieldFocused.Normal, theme.Choice.Focus, theme.Button.Normal,
            theme.Button.Focus, theme.PrimaryButton.Normal, theme.Key.Normal, theme.KeyName.Normal,
            theme.Notice(HvoNotice.Info).Normal, theme.Notice(HvoNotice.Success).Normal, theme.Notice(HvoNotice.Warning).Normal,
            theme.Notice(HvoNotice.Danger).Normal, theme.Label(HvoTone.Danger).Normal, theme.Label(HvoTone.Muted).Normal
        };
        Assert.IsTrue(attributes.All(attribute => attribute.Foreground == Color.None && attribute.Background == Color.None));

        Assert.IsTrue(theme.Selected.Normal.Style.HasFlag(TextStyle.Reverse), "the selection");
        Assert.IsTrue(theme.FieldFocused.Normal.Style.HasFlag(TextStyle.Reverse), "the focused field");
        Assert.IsTrue(theme.Choice.Focus.Style.HasFlag(TextStyle.Reverse), "the focused choice");
        Assert.IsTrue(theme.Button.Focus.Style.HasFlag(TextStyle.Reverse), "the focused button");
        Assert.IsTrue(theme.PrimaryButton.Normal.Style.HasFlag(TextStyle.Reverse), "the primary action");
        Assert.IsTrue(theme.Field.Normal.Style.HasFlag(TextStyle.Underline), "an input field");
        Assert.IsTrue(theme.Button.Disabled.Style.HasFlag(TextStyle.Faint), "a disabled button");
        Assert.IsTrue(theme.Notice(HvoNotice.Danger).Normal.Style.HasFlag(TextStyle.Reverse), "an error");
        Assert.IsTrue(theme.PanelTitle.Style.HasFlag(TextStyle.Bold), "a panel's title");
    }

    [TestMethod]
    [DataRow(HvoNotice.Info, "[INFO]")]
    [DataRow(HvoNotice.Success, "[OK]")]
    [DataRow(HvoNotice.Warning, "[WARNING]")]
    [DataRow(HvoNotice.Danger, "[ERROR]")]
    public void ANoticesKind_IsAlsoAWord(HvoNotice kind, string word) => Assert.AreEqual(word, HvoTheme.NoticeWord(kind));

    [TestMethod]
    public void Tones_AndGrounds_AreTheirPaletteRoles()
    {
        Assert.AreEqual(HvoDarkPalette.Text, HvoTheme.Token(HvoTone.Text));
        Assert.AreEqual(HvoDarkPalette.Muted, HvoTheme.Token(HvoTone.Muted));
        Assert.AreEqual(HvoDarkPalette.Accent, HvoTheme.Token(HvoTone.Accent));
        Assert.AreEqual(HvoDarkPalette.Border, HvoTheme.Token(HvoTone.Border));
        Assert.AreEqual(HvoDarkPalette.Success, HvoTheme.Token(HvoTone.Success));
        Assert.AreEqual(HvoDarkPalette.Warning, HvoTheme.Token(HvoTone.Warning));
        Assert.AreEqual(HvoDarkPalette.Danger, HvoTheme.Token(HvoTone.Danger));
        Assert.AreEqual(HvoDarkPalette.Info, HvoTheme.Token(HvoTone.Info));
        Assert.AreEqual(HvoDarkPalette.Background, HvoTheme.Ground(HvoGround.Page));
        Assert.AreEqual(HvoDarkPalette.Surface, HvoTheme.Ground(HvoGround.Surface));
        Assert.AreEqual(HvoDarkPalette.SurfaceRaised, HvoTheme.Ground(HvoGround.Raised));
        Assert.AreSame(HvoTheme.For(TerminalCapabilities.Full).Label(HvoTone.Warning, HvoGround.Raised, bold: true), HvoTheme.For(TerminalCapabilities.Full).Label(HvoTone.Warning, HvoGround.Raised, bold: true));
    }

    [TestMethod]
    public void AButton_IsBracketedWithoutTerminalGuisDecorationsOrShadow()
    {
        var theme = HvoTheme.For(TerminalCapabilities.Full);
        using var button = theme.CreateButton("_Next", primary: true);
        using var plain = theme.CreateButton("Back");

        Assert.AreEqual("[ _Next ]", button.Text);
        Assert.IsTrue(button.NoDecorations);
        Assert.IsTrue(button.NoPadding);
        Assert.AreEqual(Terminal.Gui.ViewBase.ShadowStyles.None, button.ShadowStyle);
        Assert.AreSame(theme.PrimaryButton, button.GetScheme());
        Assert.AreSame(theme.Button, plain.GetScheme());
    }

    [TestMethod]
    public void Prepare_WritesSixteenColoursOnlyForAnAnsi16Terminal()
    {
        using var ansi = Wizard.WizardDriver.CreateApplication(80, 24);
        // Start each isolated driver in true colour, independent of the invoking terminal.
        ansi.Driver!.GetOutput().IsLegacyConsole = false;
        ansi.Driver!.Force16Colors = false;
        HvoTheme.For(new TerminalCapabilities(TerminalColours.Ansi16, TerminalBorders.Unicode)).Prepare(ansi);
        Assert.IsTrue(ansi.Driver!.Force16Colors);

        using var full = Wizard.WizardDriver.CreateApplication(80, 24);
        full.Driver!.GetOutput().IsLegacyConsole = false;
        full.Driver!.Force16Colors = false;
        HvoTheme.For(TerminalCapabilities.Full).Prepare(full);
        Assert.IsFalse(full.Driver!.Force16Colors);
    }

    [TestMethod]
    public void AStyledField_ShowsItsFocusAndItsLabelFollows()
    {
        var theme = HvoTheme.For(TerminalCapabilities.Full);
        using var app = Wizard.WizardDriver.CreateApplication(40, 5);
        using var window = new Window();
        using var caption = new Label { Text = "Name" };
        using var field = new TextField { Y = 1, Width = 10 };
        theme.StyleField(field, caption);
        using var other = new TextField { Y = 2, Width = 10 };
        window.Add(caption, field, other);
        var token = app.Begin(window);
        try
        {
            other.SetFocus();
            Assert.AreSame(theme.Field, field.GetScheme());
            Assert.AreSame(theme.Label(HvoTone.Muted, HvoGround.Surface), caption.GetScheme());

            field.SetFocus();
            Assert.AreSame(theme.FieldFocused, field.GetScheme());
            Assert.AreSame(theme.Label(HvoTone.Accent, HvoGround.Surface, bold: true), caption.GetScheme());
        }
        finally
        {
            app.End(token!);
        }
    }
}
