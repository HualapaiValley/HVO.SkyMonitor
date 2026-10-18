using Terminal.Gui.ViewBase;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// A character-cell panel: a frame in the border colour on the surface, its title in capitals and the accent colour
/// between the frame's top corners, and its content in <see cref="Body"/>, one cell inside the frame. The frame is drawn
/// with the theme's <see cref="HvoTheme.Glyphs"/>, so it falls back to ASCII; Terminal.Gui's own borders have no ASCII
/// style.
/// </summary>
public sealed class HvoPanel : View
{
    private readonly HvoTheme _theme;
    private string _caption;

    /// <summary>A panel titled <paramref name="title"/>.</summary>
    public HvoPanel(HvoTheme theme, string title)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(title);
        _theme = theme;
        _caption = title;
        CanFocus = true;
        SetScheme(theme.Panel);
        Body = new View { X = 2, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(1), CanFocus = true };
        Add(Body);
    }

    /// <summary>Where the panel's content goes: inside the frame, with a column of space on each side.</summary>
    public View Body { get; }

    /// <summary>The title, drawn in capitals.</summary>
    public string Caption
    {
        get => _caption;
        set
        {
            _caption = value ?? string.Empty;
            SetNeedsDraw();
        }
    }

    /// <summary>The title as drawn on a panel <paramref name="width"/> cells wide: in capitals, a space each side, cut to fit.</summary>
    internal static string TitleText(string caption, int width)
    {
        if (width < 6 || caption.Length == 0)
        {
            return string.Empty;
        }

        return TerminalText.Clip($" {caption.ToUpperInvariant()} ", width - 4);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var height = Viewport.Height;
        if (width < 2 || height < 2)
        {
            return true;
        }

        var glyphs = _theme.Glyphs;
        var line = new string(glyphs.Horizontal, width - 2);
        SetAttribute(_theme.PanelFrame);
        AddStr(0, 0, glyphs.TopLeft + line + glyphs.TopRight);
        for (var row = 1; row < height - 1; row++)
        {
            AddStr(0, row, glyphs.Vertical.ToString());
            AddStr(width - 1, row, glyphs.Vertical.ToString());
        }

        AddStr(0, height - 1, glyphs.BottomLeft + line + glyphs.BottomRight);
        if (TitleText(_caption, width) is { Length: > 0 } title)
        {
            SetAttribute(_theme.PanelTitle);
            AddStr(2, 0, title);
        }

        return true;
    }
}
