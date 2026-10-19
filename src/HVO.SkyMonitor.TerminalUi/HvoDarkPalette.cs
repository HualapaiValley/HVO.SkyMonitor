using Terminal.Gui.Drawing;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// One colour of the terminal palette: the semantic role it plays in a terminal program, the CameraAgent stylesheet
/// token it comes from (<c>src/HVO.SkyMonitor.CameraAgent/wwwroot/css/themes/hvo-dark.css</c>), its value as
/// <c>#rrggbb</c>, and the colour of the 16 standard ANSI colours a terminal without true colour draws instead. A token
/// the stylesheet gives with transparency is here as it looks over the page background.
/// </summary>
/// <param name="Role">The role, as the README's token contract names it.</param>
/// <param name="CssToken">The <c>hvo-dark.css</c> custom property the value comes from.</param>
/// <param name="Hex">The value, <c>#rrggbb</c> in lower case.</param>
/// <param name="Ansi16">The ANSI-16 fallback.</param>
/// <param name="Use">What the role is for.</param>
public sealed record HvoColourToken(string Role, string CssToken, string Hex, ColorName16 Ansi16, string Use);

/// <summary>
/// HVO Dark for terminals: SkyMonitor CameraAgent's clean dark styling, which RoofController #158 adopts for every
/// interface, mapped onto the roles the terminal prototype uses (surface, border, text, muted, accent and focus,
/// success, warning, danger and info). Each value is the CameraAgent stylesheet's, and <c>HvoDarkPaletteTests</c> fails
/// when the stylesheet changes until this follows it. The README holds the same table, CSS token to terminal role to
/// ANSI-16 fallback, and is tested against <see cref="Tokens"/> too.
/// </summary>
public static class HvoDarkPalette
{
    /// <summary>The page: everything without a surface of its own.</summary>
    public static HvoColourToken Background { get; } = new("background", "--hvo-body-bg", "#05070d", ColorName16.Black, "The screen behind everything else.");

    /// <summary>Panels and the header, step and key bars.</summary>
    public static HvoColourToken Surface { get; } = new("surface", "--hvo-surface", "#0d1420", ColorName16.Black, "Panels and the bars at the top and foot of the screen.");

    /// <summary>What stands on a surface: buttons and input fields.</summary>
    public static HvoColourToken SurfaceRaised { get; } = new("surface-raised", "--hvo-surface-elevated", "#111c2a", ColorName16.Black, "Buttons and input fields.");

    /// <summary>Panel frames and rules.</summary>
    public static HvoColourToken Border { get; } = new("border", "--hvo-border-strong", "#3a4a60", ColorName16.DarkGray, "Panel frames and rules.");

    /// <summary>Text.</summary>
    public static HvoColourToken Text { get; } = new("text", "--hvo-body-color", "#f7f9fc", ColorName16.White, "Text and values.");

    /// <summary>Labels, hints and anything secondary.</summary>
    public static HvoColourToken Muted { get; } = new("muted", "--hvo-muted", "#98a7bb", ColorName16.Gray, "Field labels, hints and the key bar's names.");

    /// <summary>Panel titles, keys and links.</summary>
    public static HvoColourToken Accent { get; } = new("accent", "--hvo-accent", "#58a6ff", ColorName16.BrightCyan, "Panel titles, keys, and the focused control.");

    /// <summary>The selected item and the primary action, under <see cref="Text"/>.</summary>
    public static HvoColourToken Focus { get; } = new("focus", "--hvo-accent-strong", "#2563eb", ColorName16.Blue, "The selected item and the primary action's background.");

    /// <summary>Something that worked.</summary>
    public static HvoColourToken Success { get; } = new("success", "--hvo-success-strong", "#3fb950", ColorName16.BrightGreen, "A check that passed; a change made.");

    /// <summary>Something to read before going on.</summary>
    public static HvoColourToken Warning { get; } = new("warning", "--hvo-warning-strong", "#d29922", ColorName16.BrightYellow, "Something to read before going on.");

    /// <summary>An error or a refusal.</summary>
    public static HvoColourToken Danger { get; } = new("danger", "--hvo-danger-strong", "#f85149", ColorName16.BrightRed, "An error, a refusal, a step that failed.");

    /// <summary>Information.</summary>
    public static HvoColourToken Info { get; } = new("info", "--hvo-info-strong", "#58a6ff", ColorName16.BrightCyan, "Information and progress.");

    /// <summary>A success notice's band.</summary>
    public static HvoColourToken SuccessBackground { get; } = new("success-background", "--hvo-success-bg", "#0c3721", ColorName16.Green, "A success notice's band.");

    /// <summary>A success notice's text.</summary>
    public static HvoColourToken SuccessText { get; } = new("success-text", "--hvo-success-fg", "#bbf7d0", ColorName16.Black, "A success notice's text.");

    /// <summary>A warning notice's band.</summary>
    public static HvoColourToken WarningBackground { get; } = new("warning-background", "--hvo-warning-bg", "#42380f", ColorName16.Yellow, "A warning notice's band.");

    /// <summary>A warning notice's text.</summary>
    public static HvoColourToken WarningText { get; } = new("warning-text", "--hvo-warning-fg", "#fef9c3", ColorName16.Black, "A warning notice's text.");

    /// <summary>An error notice's band.</summary>
    public static HvoColourToken DangerBackground { get; } = new("danger-background", "--hvo-danger-bg", "#422226", ColorName16.Red, "An error notice's band.");

    /// <summary>An error notice's text.</summary>
    public static HvoColourToken DangerText { get; } = new("danger-text", "--hvo-danger-fg", "#fecaca", ColorName16.White, "An error notice's text.");

    /// <summary>An information notice's band.</summary>
    public static HvoColourToken InfoBackground { get; } = new("info-background", "--hvo-info-bg", "#123548", ColorName16.Cyan, "An information notice's band.");

    /// <summary>An information notice's text.</summary>
    public static HvoColourToken InfoText { get; } = new("info-text", "--hvo-info-fg", "#58a6ff", ColorName16.Black, "An information notice's text.");

    /// <summary>Every token, in the README's order.</summary>
    public static IReadOnlyList<HvoColourToken> Tokens { get; } =
    [
        Background, Surface, SurfaceRaised, Border, Text, Muted, Accent, Focus, Success, Warning, Danger, Info,
        SuccessBackground, SuccessText, WarningBackground, WarningText, DangerBackground, DangerText, InfoBackground,
        InfoText
    ];
}
