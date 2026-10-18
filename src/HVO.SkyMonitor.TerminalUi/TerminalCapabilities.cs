namespace HVO.SkyMonitor.TerminalUi;

/// <summary>How a terminal program draws its colours.</summary>
public enum TerminalColours
{
    /// <summary>HVO Dark in 24-bit colour.</summary>
    TrueColour,

    /// <summary>The 16 standard ANSI colours, each role's fallback in <see cref="HvoDarkPalette"/>.</summary>
    Ansi16,

    /// <summary>
    /// No colour (<c>NO_COLOR</c>, https://no-color.org): the terminal's own foreground and background, with bold,
    /// underline, faint and reverse video where HVO Dark has a colour.
    /// </summary>
    None
}

/// <summary>How a terminal program draws frames and rules.</summary>
public enum TerminalBorders
{
    /// <summary>Box-drawing characters: <c>┌─┐│└┘</c>.</summary>
    Unicode,

    /// <summary>ASCII only: <c>+-+|++</c>, for a terminal or locale that cannot draw box-drawing characters.</summary>
    Ascii
}

/// <summary>
/// What the terminal can draw, decided once from the environment when a terminal program starts. Nothing a program shows
/// depends on colour alone: every state also has a word or a mark (<c>[ERROR]</c>, <c>(*)</c>, <c>[x]</c>, the current
/// step in brackets), so <see cref="TerminalColours.None"/> loses no information.
/// </summary>
/// <param name="Colours">The colours.</param>
/// <param name="Borders">The frames.</param>
public sealed record TerminalCapabilities(TerminalColours Colours, TerminalBorders Borders)
{
    /// <summary>The environment variable that overrides the detected capabilities: a comma-separated list of <c>truecolor</c>, <c>ansi16</c>, <c>nocolor</c> and <c>ascii</c>.</summary>
    public const string OverrideVariable = "HVO_TERMINAL";

    /// <summary>HVO Dark in true colour with box-drawing frames.</summary>
    public static TerminalCapabilities Full { get; } = new(TerminalColours.TrueColour, TerminalBorders.Unicode);

    /// <summary>
    /// The capabilities <paramref name="environment"/> describes:
    /// <list type="bullet">
    /// <item><c>NO_COLOR</c> set and not empty, or <c>TERM=dumb</c>: no colour.</item>
    /// <item><c>COLORTERM</c> of <c>truecolor</c> or <c>24bit</c>: true colour.</item>
    /// <item>Anything else, a <c>TERM</c> naming 256 colours included: the 16 ANSI colours.</item>
    /// <item>ASCII frames when the locale (<c>LC_ALL</c>, else <c>LC_CTYPE</c>, else <c>LANG</c>) is not UTF-8, or the
    /// terminal is <c>dumb</c> or a VT100.</item>
    /// <item><c>HVO_TERMINAL</c> (<see cref="OverrideVariable"/>) overrides either: <c>truecolor</c>, <c>ansi16</c> or
    /// <c>nocolor</c>, and <c>ascii</c> or <c>unicode</c>; <c>NO_COLOR</c> still wins over a colour it names.</item>
    /// </list>
    /// </summary>
    public static TerminalCapabilities Detect(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var term = environment("TERM") ?? string.Empty;
        var colourTerm = environment("COLORTERM") ?? string.Empty;
        // True colour only when the terminal says so: a 256-colour terminal draws 24-bit colours wrongly or not at all.
        var colours = term == "dumb" ? TerminalColours.None
            : colourTerm is "truecolor" or "24bit" ? TerminalColours.TrueColour
            : TerminalColours.Ansi16;
        var locale = FirstSet(environment, "LC_ALL", "LC_CTYPE", "LANG") ?? string.Empty;
        var utf8 = locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || locale.Contains("utf8", StringComparison.OrdinalIgnoreCase);
        var borders = !utf8 || term is "dumb" or "vt100" ? TerminalBorders.Ascii : TerminalBorders.Unicode;

        foreach (var word in (environment(OverrideVariable) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (word.ToUpperInvariant())
            {
                case "TRUECOLOR":
                    colours = TerminalColours.TrueColour;
                    break;
                case "ANSI16":
                    colours = TerminalColours.Ansi16;
                    break;
                case "NOCOLOR":
                    colours = TerminalColours.None;
                    break;
                case "ASCII":
                    borders = TerminalBorders.Ascii;
                    break;
                case "UNICODE":
                    borders = TerminalBorders.Unicode;
                    break;
            }
        }

        if (!string.IsNullOrEmpty(environment("NO_COLOR")))
        {
            colours = TerminalColours.None;
        }

        return new(colours, borders);
    }

    private static string? FirstSet(Func<string, string?> environment, params string[] names)
        => names.Select(environment).FirstOrDefault(value => !string.IsNullOrEmpty(value));
}
