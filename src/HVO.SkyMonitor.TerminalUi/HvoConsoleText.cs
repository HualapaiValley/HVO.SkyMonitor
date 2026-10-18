using System.Globalization;
using System.Text;
using Terminal.Gui.Drawing;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// HVO Dark as plain terminal output, for what a program writes outside Terminal.Gui: a panel (a frame, its title in
/// capitals, and wrapped lines) and a notice band, with the same palette, frames, fallbacks and words in brackets as
/// the views, written as text with ANSI colour codes (or none). The installer uses it for what it prints around its
/// wizard, while <c>sudo</c> may need the terminal; a command's human output can use it too. Every line is at most
/// <c>width</c> columns, so it never wraps on the terminal it was sized for.
/// </summary>
public sealed class HvoConsoleText
{
    private const string Reset = "\u001b[0m";

    /// <summary>Text drawn as <paramref name="theme"/> draws it.</summary>
    public HvoConsoleText(HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Theme = theme;
    }

    /// <summary>The theme.</summary>
    public HvoTheme Theme { get; }

    /// <summary>
    /// A panel <paramref name="width"/> columns wide: its frame in the border colour, <paramref name="title"/> in the
    /// accent colour, and <paramref name="lines"/> wrapped inside, each on the surface. Ends with a line break.
    /// </summary>
    public string Panel(string title, IEnumerable<HvoLine> lines, int width)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(lines);
        width = Math.Max(10, width);
        var glyphs = Theme.Glyphs;
        var inner = width - 4;
        var frame = Code(HvoDarkPalette.Border, HvoDarkPalette.Surface, bold: false, plain: false);
        var text = new StringBuilder();
        var top = glyphs.TopLeft + new string(glyphs.Horizontal, width - 2) + glyphs.TopRight;
        var caption = HvoPanel.TitleText(title, width);
        text.Append(frame).Append(top[..2]);
        text.Append(Code(HvoDarkPalette.Accent, HvoDarkPalette.Surface, bold: true, plain: true)).Append(caption);
        text.Append(frame).Append(top[(2 + caption.Length)..]).Append(Reset).Append('\n');
        foreach (var line in lines)
        {
            foreach (var row in HvoTextLines.Wrap(line.Text, inner))
            {
                text.Append(frame).Append(glyphs.Vertical).Append(' ');
                text.Append(Code(HvoTheme.Token(line.Tone), HvoDarkPalette.Surface, line.Bold, line.Bold || line.Tone is HvoTone.Accent or HvoTone.Warning or HvoTone.Danger));
                text.Append(row.PadRight(inner)[..inner]);
                text.Append(frame).Append(' ').Append(glyphs.Vertical).Append(Reset).Append('\n');
            }
        }

        text.Append(frame).Append(glyphs.BottomLeft).Append(new string(glyphs.Horizontal, width - 2)).Append(glyphs.BottomRight).Append(Reset).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// A notice <paramref name="width"/> columns wide: its word in brackets (<paramref name="word"/>, or the kind's own)
    /// and <paramref name="text"/>, wrapped, on its kind's band. Ends with a line break.
    /// </summary>
    public string Notice(HvoNotice kind, string text, int width, string? word = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        width = Math.Max(10, width);
        var (foreground, background, plainReverse) = kind switch
        {
            HvoNotice.Success => (HvoDarkPalette.SuccessText, HvoDarkPalette.SuccessBackground, false),
            HvoNotice.Warning => (HvoDarkPalette.WarningText, HvoDarkPalette.WarningBackground, true),
            HvoNotice.Danger => (HvoDarkPalette.DangerText, HvoDarkPalette.DangerBackground, true),
            _ => (HvoDarkPalette.InfoText, HvoDarkPalette.InfoBackground, false)
        };
        var code = Code(foreground, background, bold: true, plain: true, reverse: plainReverse);
        var output = new StringBuilder();
        foreach (var row in HvoTextLines.Wrap($"{word ?? HvoTheme.NoticeWord(kind)} {text}", width - 2))
        {
            output.Append(code).Append(' ').Append(row.PadRight(width - 1)[..(width - 1)]).Append(Reset).Append('\n');
        }

        return output.ToString();
    }

    /// <summary><paramref name="label"/> in a column <paramref name="column"/> wide, then <paramref name="value"/>: an aligned field.</summary>
    public static string Field(string label, string value, int column)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(value);
        return label.PadRight(Math.Max(label.Length + 2, column)) + value;
    }

    /// <summary>The escape that starts text in <paramref name="foreground"/> on <paramref name="background"/>, or, without colour, its style alone.</summary>
    public string Code(HvoColourToken foreground, HvoColourToken background, bool bold, bool plain, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        ArgumentNullException.ThrowIfNull(background);
        var parts = new List<string> { "0" };
        switch (Theme.Capabilities.Colours)
        {
            case TerminalColours.TrueColour:
                parts.Add($"38;2;{Rgb(foreground.Hex)}");
                parts.Add($"48;2;{Rgb(background.Hex)}");
                if (bold)
                {
                    parts.Add("1");
                }

                break;
            case TerminalColours.Ansi16:
                var fg = Index(foreground.Ansi16);
                var bg = Index(background.Ansi16);
                parts.Add((fg < 8 ? 30 + fg : 82 + fg).ToString(CultureInfo.InvariantCulture));
                parts.Add((bg < 8 ? 40 + bg : 92 + bg).ToString(CultureInfo.InvariantCulture));
                if (bold)
                {
                    parts.Add("1");
                }

                break;
            default:
                if (plain)
                {
                    parts.Add("1");
                }

                if (reverse)
                {
                    parts.Add("7");
                }

                break;
        }

        return $"\u001b[{string.Join(';', parts)}m";
    }

    /// <summary>The standard index (0-15) of a Terminal.Gui 16-colour name, which is in Windows console order.</summary>
    public static int Index(ColorName16 colour) => colour switch
    {
        ColorName16.Black => 0,
        ColorName16.Red => 1,
        ColorName16.Green => 2,
        ColorName16.Yellow => 3,
        ColorName16.Blue => 4,
        ColorName16.Magenta => 5,
        ColorName16.Cyan => 6,
        ColorName16.Gray => 7,
        ColorName16.DarkGray => 8,
        ColorName16.BrightRed => 9,
        ColorName16.BrightGreen => 10,
        ColorName16.BrightYellow => 11,
        ColorName16.BrightBlue => 12,
        ColorName16.BrightMagenta => 13,
        ColorName16.BrightCyan => 14,
        _ => 15
    };

    private static string Rgb(string hex)
        => string.Join(';', Enumerable.Range(0, 3).Select(i => Convert.ToInt32(hex.Substring(1 + (i * 2), 2), 16).ToString(CultureInfo.InvariantCulture)));
}
