using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// One of several choices, a line each: <c>(*)</c> before the chosen one and <c>( )</c> before the others, in every
/// colour mode (Terminal.Gui's option glyphs are not ASCII). Up and Down choose; with the focus, the chosen line is on
/// the strong accent, or in reverse video without colour. Enter is left to the program (a wizard's Next).
/// </summary>
public sealed class HvoChoiceList : View
{
    private readonly HvoTheme _theme;
    private readonly string[] _labels;
    private int _value;

    /// <summary>The choices <paramref name="labels"/>, <paramref name="value"/> chosen.</summary>
    public HvoChoiceList(HvoTheme theme, IReadOnlyList<string> labels, int value = 0)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0)
        {
            throw new ArgumentException("A choice needs at least one option.", nameof(labels));
        }

        _theme = theme;
        _labels = [.. labels];
        _value = Math.Clamp(value, 0, _labels.Length - 1);
        CanFocus = true;
        Width = Dim.Fill();
        Height = _labels.Length;
        SetScheme(theme.Choice);
        AddCommand(Command.Up, () => Choose(_value - 1));
        AddCommand(Command.Down, () => Choose(_value + 1));
        AddCommand(Command.Start, () => Choose(0));
        AddCommand(Command.End, () => Choose(_labels.Length - 1));
        KeyBindings.Add(Key.CursorUp, Command.Up);
        KeyBindings.Add(Key.CursorDown, Command.Down);
        KeyBindings.Add(Key.Home, Command.Start);
        KeyBindings.Add(Key.End, Command.End);
    }

    /// <summary>Raised when the choice changes.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>The choices.</summary>
    public IReadOnlyList<string> Labels => _labels;

    /// <summary>The index of the chosen line.</summary>
    public int Value
    {
        get => _value;
        set => Choose(value);
    }

    /// <summary>The list as text, a line each, as it is drawn.</summary>
    public IReadOnlyList<string> Lines => [.. _labels.Select((label, index) => Line(label, index == _value))];

    /// <summary>A choice's line: its mark and its label.</summary>
    public static string Line(string label, bool chosen) => $"{(chosen ? "(*)" : "( )")} {label}";

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        for (var index = 0; index < _labels.Length && index < Viewport.Height; index++)
        {
            var chosen = index == _value;
            var line = Line(_labels[index], chosen);
            SetAttribute(chosen && HasFocus ? _theme.Choice.Focus : GetAttributeForRole(Enabled ? VisualRole.Normal : VisualRole.Disabled));
            AddStr(0, index, Fit(line, Viewport.Width));
        }

        return true;
    }

    private bool? Choose(int value)
    {
        var next = Math.Clamp(value, 0, _labels.Length - 1);
        if (next != _value)
        {
            _value = next;
            SetNeedsDraw();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    // The line cut to the width in display cells, between text elements.
    internal static string Fit(string line, int width) => TerminalText.Clip(line, width);
}
