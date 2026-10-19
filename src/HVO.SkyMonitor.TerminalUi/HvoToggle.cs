using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// A yes-or-no question on one line: <c>[x]</c> before it when ticked and <c>[ ]</c> when not, in every colour mode
/// (Terminal.Gui's check box glyphs are not ASCII). Space ticks and clears it; with the focus it is on the strong accent,
/// or in reverse video without colour.
/// </summary>
public sealed class HvoToggle : View
{
    private readonly HvoTheme _theme;
    private bool _checked;

    /// <summary>The question <paramref name="label"/>, ticked or not.</summary>
    public HvoToggle(HvoTheme theme, string label, bool isChecked = false)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(label);
        _theme = theme;
        Label = label;
        _checked = isChecked;
        CanFocus = true;
        Width = Dim.Fill();
        Height = 1;
        SetScheme(theme.Choice);
        AddCommand(Command.Toggle, () =>
        {
            Checked = !Checked;
            return true;
        });
        KeyBindings.ReplaceCommands(Key.Space, Command.Toggle);
    }

    /// <summary>Raised when the box is ticked or cleared.</summary>
    public event EventHandler? CheckedChanged;

    /// <summary>The question.</summary>
    public string Label { get; }

    /// <summary>Whether it is ticked.</summary>
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                SetNeedsDraw();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The line as drawn.</summary>
    public string Line => $"{(_checked ? "[x]" : "[ ]")} {Label}";

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        SetAttribute(HasFocus ? _theme.Choice.Focus : GetAttributeForRole(Enabled ? VisualRole.Normal : VisualRole.Disabled));
        AddStr(0, 0, HvoChoiceList.Fit(Line, Viewport.Width));
        return true;
    }
}
