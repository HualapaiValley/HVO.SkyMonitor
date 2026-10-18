using System.Collections.Concurrent;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>The colour a piece of text has: a role of <see cref="HvoDarkPalette"/>.</summary>
public enum HvoTone
{
    /// <summary>Text and values.</summary>
    Text,

    /// <summary>Labels, hints and anything secondary.</summary>
    Muted,

    /// <summary>Titles and keys.</summary>
    Accent,

    /// <summary>Frames and rules.</summary>
    Border,

    /// <summary>Something that worked.</summary>
    Success,

    /// <summary>Something to read before going on.</summary>
    Warning,

    /// <summary>An error or a refusal.</summary>
    Danger,

    /// <summary>Information.</summary>
    Info
}

/// <summary>What text stands on.</summary>
public enum HvoGround
{
    /// <summary>The page.</summary>
    Page,

    /// <summary>A panel or a bar.</summary>
    Surface,

    /// <summary>A button or an input field.</summary>
    Raised
}

/// <summary>The kind of a notice: a band across the screen with a word in brackets before its text.</summary>
public enum HvoNotice
{
    /// <summary><c>[INFO]</c>.</summary>
    Info,

    /// <summary><c>[OK]</c>.</summary>
    Success,

    /// <summary><c>[WARNING]</c>.</summary>
    Warning,

    /// <summary><c>[ERROR]</c>, <c>[REFUSED]</c>.</summary>
    Danger
}

/// <summary>The characters a frame is drawn with.</summary>
/// <param name="TopLeft">The top left corner.</param>
/// <param name="TopRight">The top right corner.</param>
/// <param name="BottomLeft">The bottom left corner.</param>
/// <param name="BottomRight">The bottom right corner.</param>
/// <param name="Horizontal">A horizontal line.</param>
/// <param name="Vertical">A vertical line.</param>
public sealed record HvoBoxGlyphs(char TopLeft, char TopRight, char BottomLeft, char BottomRight, char Horizontal, char Vertical)
{
    /// <summary><c>┌─┐│└┘</c>.</summary>
    public static HvoBoxGlyphs Unicode { get; } = new('┌', '┐', '└', '┘', '─', '│');

    /// <summary><c>+-+|++</c>.</summary>
    public static HvoBoxGlyphs Ascii { get; } = new('+', '+', '+', '+', '-', '|');
}

/// <summary>
/// HVO Dark for Terminal.Gui, as the RoofController #158 terminal prototype draws it: character-cell panels on the page,
/// titles in the accent colour, muted labels beside aligned values, bracketed actions, the primary action and the
/// selection on the strong accent, and notices as coloured bands with a word in brackets. One theme exists for each
/// <see cref="TerminalCapabilities"/>: in true colour, in the 16 ANSI colours (each role's
/// <see cref="HvoColourToken.Ansi16"/>), and without colour, where bold, underline, faint and reverse video stand in for
/// it. Every state also has a word or a mark, so nothing depends on colour alone.
/// </summary>
public sealed class HvoTheme
{
    private static readonly ConcurrentDictionary<TerminalCapabilities, HvoTheme> Themes = new();

    private readonly ConcurrentDictionary<(HvoTone, HvoGround, bool), Scheme> _labels = new();
    private readonly Dictionary<HvoNotice, Scheme> _notices;

    private HvoTheme(TerminalCapabilities capabilities)
    {
        Capabilities = capabilities;
        Glyphs = capabilities.Borders == TerminalBorders.Ascii ? HvoBoxGlyphs.Ascii : HvoBoxGlyphs.Unicode;
        var p = HvoDarkPalette.Background;
        Page = new(Of(HvoDarkPalette.Text, p))
        {
            Focus = Of(HvoDarkPalette.Background, HvoDarkPalette.Accent, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold),
            HotNormal = Of(HvoDarkPalette.Accent, p, TextStyle.Underline, TextStyle.Underline),
            HotFocus = Of(HvoDarkPalette.Background, HvoDarkPalette.Accent, TextStyle.Bold | TextStyle.Underline, TextStyle.Reverse | TextStyle.Bold | TextStyle.Underline),
            Active = Of(HvoDarkPalette.Text, p, TextStyle.Bold, TextStyle.Bold),
            HotActive = Of(HvoDarkPalette.Accent, p, TextStyle.Bold, TextStyle.Bold),
            Highlight = Of(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold),
            Editable = FieldNormal(),
            ReadOnly = Of(HvoDarkPalette.Muted, p),
            Disabled = Of(HvoDarkPalette.Muted, p, TextStyle.Faint, TextStyle.Faint)
        };
        Panel = new(Page)
        {
            Normal = Of(HvoDarkPalette.Text, HvoDarkPalette.Surface),
            HotNormal = Of(HvoDarkPalette.Accent, HvoDarkPalette.Surface, TextStyle.Underline, TextStyle.Underline),
            ReadOnly = Of(HvoDarkPalette.Muted, HvoDarkPalette.Surface),
            Active = Of(HvoDarkPalette.Text, HvoDarkPalette.Surface, TextStyle.Bold, TextStyle.Bold),
            Disabled = Of(HvoDarkPalette.Muted, HvoDarkPalette.Surface, TextStyle.Faint, TextStyle.Faint)
        };
        PanelFrame = Of(HvoDarkPalette.Border, HvoDarkPalette.Surface);
        PanelTitle = Of(HvoDarkPalette.Accent, HvoDarkPalette.Surface, TextStyle.Bold, TextStyle.Bold);
        Bar = new(Panel) { Normal = Of(HvoDarkPalette.Text, HvoDarkPalette.Surface, TextStyle.Bold, TextStyle.Bold) };
        Selected = new(Panel) { Normal = Of(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold) };
        Field = new(Panel)
        {
            Normal = FieldNormal(),
            Editable = FieldNormal(),
            Focus = Of(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.None, TextStyle.Reverse),
            ReadOnly = Of(HvoDarkPalette.Muted, HvoDarkPalette.SurfaceRaised),
            Disabled = Of(HvoDarkPalette.Muted, HvoDarkPalette.SurfaceRaised, TextStyle.Faint, TextStyle.Faint)
        };
        FieldFocused = new(Field)
        {
            Normal = Of(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.None, TextStyle.Reverse),
            Editable = Of(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.None, TextStyle.Reverse)
        };
        Choice = new(Panel)
        {
            Normal = Of(HvoDarkPalette.Text, HvoDarkPalette.Surface),
            Focus = Of(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.Bold, TextStyle.Reverse | TextStyle.Bold),
            Disabled = Of(HvoDarkPalette.Muted, HvoDarkPalette.Surface, TextStyle.Faint, TextStyle.Faint)
        };
        Button = ButtonScheme(HvoDarkPalette.Text, HvoDarkPalette.SurfaceRaised, TextStyle.Bold);
        PrimaryButton = ButtonScheme(HvoDarkPalette.Text, HvoDarkPalette.Focus, TextStyle.Reverse | TextStyle.Bold);
        Key = new(Page) { Normal = Of(HvoDarkPalette.Text, p, TextStyle.Bold, TextStyle.Bold) };
        KeyName = new(Page) { Normal = Of(HvoDarkPalette.Muted, p) };
        _notices = new()
        {
            [HvoNotice.Info] = NoticeScheme(HvoDarkPalette.InfoText, HvoDarkPalette.InfoBackground, TextStyle.Bold),
            [HvoNotice.Success] = NoticeScheme(HvoDarkPalette.SuccessText, HvoDarkPalette.SuccessBackground, TextStyle.Bold),
            [HvoNotice.Warning] = NoticeScheme(HvoDarkPalette.WarningText, HvoDarkPalette.WarningBackground, TextStyle.Reverse | TextStyle.Bold),
            [HvoNotice.Danger] = NoticeScheme(HvoDarkPalette.DangerText, HvoDarkPalette.DangerBackground, TextStyle.Reverse | TextStyle.Bold)
        };
    }

    /// <summary>What the theme draws for.</summary>
    public TerminalCapabilities Capabilities { get; }

    /// <summary>The frame characters: box-drawing, or ASCII.</summary>
    public HvoBoxGlyphs Glyphs { get; }

    /// <summary>The page, and everything on it without a scheme of its own.</summary>
    public Scheme Page { get; }

    /// <summary>A panel: text on the surface.</summary>
    public Scheme Panel { get; }

    /// <summary>A panel's frame.</summary>
    public Attribute PanelFrame { get; }

    /// <summary>A panel's title, in capitals between its frame's corners.</summary>
    public Attribute PanelTitle { get; }

    /// <summary>The header bar and the step bar: bold text on the surface.</summary>
    public Scheme Bar { get; }

    /// <summary>The selected item (the current step): text on the strong accent, or reverse video.</summary>
    public Scheme Selected { get; }

    /// <summary>An input field: text on the raised surface, on the strong accent while it has the focus.</summary>
    public Scheme Field { get; }

    /// <summary>An input field with the focus: text on the strong accent, or reverse video without colour.</summary>
    public Scheme FieldFocused { get; }

    /// <summary>A list of choices (<see cref="HvoChoiceList"/>, <see cref="HvoToggle"/>): the focused line on the strong accent.</summary>
    public Scheme Choice { get; }

    /// <summary>A button: bold text on the raised surface, dark on the accent while it has the focus.</summary>
    public Scheme Button { get; }

    /// <summary>The primary action: text on the strong accent.</summary>
    public Scheme PrimaryButton { get; }

    /// <summary>A key in the key bar.</summary>
    public Scheme Key { get; }

    /// <summary>What a key in the key bar does.</summary>
    public Scheme KeyName { get; }

    /// <summary>The theme for <paramref name="capabilities"/>.</summary>
    public static HvoTheme For(TerminalCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return Themes.GetOrAdd(capabilities, static value => new HvoTheme(value));
    }

    /// <summary>The theme for the terminal <paramref name="environment"/> describes (<see cref="TerminalCapabilities.Detect"/>).</summary>
    public static HvoTheme For(Func<string, string?> environment) => For(TerminalCapabilities.Detect(environment));

    /// <summary>Text in <paramref name="tone"/> on <paramref name="ground"/>, bold if asked.</summary>
    public Scheme Label(HvoTone tone, HvoGround ground = HvoGround.Page, bool bold = false)
        => _labels.GetOrAdd((tone, ground, bold), key => new(Ground(key.Item2) == HvoDarkPalette.Background ? Page : Panel)
        {
            Normal = Of(Token(key.Item1), Ground(key.Item2), key.Item3 ? TextStyle.Bold : TextStyle.None, Plain(key.Item1, key.Item3))
        });

    /// <summary>A notice's band.</summary>
    public Scheme Notice(HvoNotice kind) => _notices[kind];

    /// <summary>The word in brackets a notice starts with, so its kind does not depend on its colour.</summary>
    public static string NoticeWord(HvoNotice kind) => kind switch
    {
        HvoNotice.Success => "[OK]",
        HvoNotice.Warning => "[WARNING]",
        HvoNotice.Danger => "[ERROR]",
        _ => "[INFO]"
    };

    /// <summary>
    /// Readies <paramref name="app"/> to draw this theme: the 16 ANSI colours are written as such
    /// (<c>Force16Colors</c>), so a terminal without true colour is not sent 24-bit colours.
    /// </summary>
    public void Prepare(IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (Capabilities.Colours == TerminalColours.Ansi16 && app.Driver is { } driver)
        {
            driver.Force16Colors = true;
        }
    }

    /// <summary>
    /// <paramref name="label"/> as a button of this theme: <c>[ Label ]</c>, with no shadow and none of Terminal.Gui's
    /// own decorations (whose glyphs are not ASCII), primary or not. An underscore before a letter makes it the hot key.
    /// </summary>
    public Button CreateButton(string label, bool primary = false)
    {
        ArgumentNullException.ThrowIfNull(label);
        var button = new Button { Text = $"[ {label} ]", NoDecorations = true, NoPadding = true, ShadowStyle = ShadowStyles.None };
        button.SetScheme(primary ? PrimaryButton : Button);
        return button;
    }

    /// <summary>
    /// <paramref name="field"/> as an input field of this theme. Terminal.Gui draws a field the same with and without the
    /// focus, and a screenshot has no cursor, so the field with the focus is drawn on the strong accent (reverse video
    /// without colour), and its <paramref name="caption"/>, when given, turns from muted to bold accent.
    /// </summary>
    public TextField StyleField(TextField field, View? caption = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        field.SetScheme(Field);
        field.HasFocusChanged += (_, _) =>
        {
            field.SetScheme(field.HasFocus ? FieldFocused : Field);
            caption?.SetScheme(Label(field.HasFocus ? HvoTone.Accent : HvoTone.Muted, HvoGround.Surface, bold: field.HasFocus));
        };
        caption?.SetScheme(Label(HvoTone.Muted, HvoGround.Surface));
        return field;
    }

    /// <summary>The colours of <paramref name="token"/> on <paramref name="background"/>, in this theme's colours.</summary>
    public Attribute Of(HvoColourToken foreground, HvoColourToken background, TextStyle style = TextStyle.None, TextStyle plain = TextStyle.None)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(background);
        return Capabilities.Colours switch
        {
            TerminalColours.TrueColour => new(new Color(foreground.Hex), new Color(background.Hex), style),
            TerminalColours.Ansi16 => new(new Color(foreground.Ansi16), new Color(background.Ansi16), style),
            _ => new(Color.None, Color.None, plain)
        };
    }

    /// <summary>The palette token of <paramref name="tone"/>.</summary>
    public static HvoColourToken Token(HvoTone tone) => tone switch
    {
        HvoTone.Muted => HvoDarkPalette.Muted,
        HvoTone.Accent => HvoDarkPalette.Accent,
        HvoTone.Border => HvoDarkPalette.Border,
        HvoTone.Success => HvoDarkPalette.Success,
        HvoTone.Warning => HvoDarkPalette.Warning,
        HvoTone.Danger => HvoDarkPalette.Danger,
        HvoTone.Info => HvoDarkPalette.Info,
        _ => HvoDarkPalette.Text
    };

    /// <summary>The palette token of <paramref name="ground"/>.</summary>
    public static HvoColourToken Ground(HvoGround ground) => ground switch
    {
        HvoGround.Surface => HvoDarkPalette.Surface,
        HvoGround.Raised => HvoDarkPalette.SurfaceRaised,
        _ => HvoDarkPalette.Background
    };

    // Without colour: titles, warnings and errors are bold, and the rest is plain.
    private static TextStyle Plain(HvoTone tone, bool bold)
        => bold || tone is HvoTone.Accent or HvoTone.Warning or HvoTone.Danger ? TextStyle.Bold : TextStyle.None;

    // An input field is underlined where the raised surface cannot be told from the panel: without colour, and in the 16
    // colours, which draw both black.
    private Attribute FieldNormal()
        => Capabilities.Colours == TerminalColours.TrueColour
            ? Of(HvoDarkPalette.Text, HvoDarkPalette.SurfaceRaised)
            : Of(HvoDarkPalette.Text, HvoDarkPalette.SurfaceRaised, TextStyle.Underline, TextStyle.Underline);

    private Scheme ButtonScheme(HvoColourToken text, HvoColourToken background, TextStyle plain)
    {
        var focus = Of(HvoDarkPalette.Background, HvoDarkPalette.Accent, TextStyle.Bold | TextStyle.Underline, TextStyle.Reverse | TextStyle.Bold | TextStyle.Underline);
        return new(Panel)
        {
            Normal = Of(text, background, TextStyle.Bold, plain),
            HotNormal = Of(text, background, TextStyle.Bold | TextStyle.Underline, plain | TextStyle.Underline),
            Focus = focus,
            HotFocus = focus,
            Highlight = Of(text, background, TextStyle.Bold, plain),
            Active = focus,
            HotActive = focus,
            Disabled = Of(HvoDarkPalette.Muted, background, TextStyle.Faint, TextStyle.Faint)
        };
    }

    private Scheme NoticeScheme(HvoColourToken text, HvoColourToken background, TextStyle plain)
        => new(Page) { Normal = Of(text, background, TextStyle.Bold, plain) };
}
