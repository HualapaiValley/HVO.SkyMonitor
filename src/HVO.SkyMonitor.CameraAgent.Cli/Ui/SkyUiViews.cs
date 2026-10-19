using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>A reading: a muted label and its value in a tone, as the terminal prototype's fields are.</summary>
internal sealed record SkyUiField(string Label, string Value, HvoTone Tone = HvoTone.Text);

/// <summary>
/// Readings in a panel: each label muted in a column, its value beside it in its tone, a row each, cut at the panel's
/// edge. A state is never colour alone: the value says it in words.
/// </summary>
internal sealed class SkyUiFields : View
{
    private readonly HvoTheme _theme;
    private IReadOnlyList<SkyUiField> _fields = [];

    public SkyUiFields(HvoTheme theme, int labelWidth)
    {
        _theme = theme;
        LabelWidth = labelWidth;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = false;
    }

    public int LabelWidth { get; set; }

    /// <summary>What the fields are drawn on: a panel's surface (the default), or the page, below the panels.</summary>
    public HvoGround Ground { get; set; } = HvoGround.Surface;

    public IReadOnlyList<SkyUiField> Fields
    {
        get => _fields;
        set
        {
            _fields = value ?? [];
            SetNeedsDraw();
        }
    }

    /// <summary>The rows as drawn on a panel <paramref name="width"/> wide, for tests and screen readers.</summary>
    public IReadOnlyList<string> Rows(int width) => [.. _fields.Select(field => Cut(Row(field), width))];

    private string Row(SkyUiField field) => field.Label.Length == 0 ? field.Value : TerminalText.Pad(field.Label, LabelWidth) + field.Value;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        for (var row = 0; row < _fields.Count && row < Viewport.Height; row++)
        {
            var field = _fields[row];
            if (field.Label.Length > 0)
            {
                SetAttribute(_theme.Label(HvoTone.Muted, Ground).Normal);
                AddStr(0, row, Cut(TerminalText.Pad(field.Label, LabelWidth), width));
            }

            var column = field.Label.Length == 0 ? 0 : LabelWidth;
            if (column < width)
            {
                SetAttribute(_theme.Label(field.Tone, Ground, bold: field.Tone is HvoTone.Warning or HvoTone.Danger).Normal);
                AddStr(column, row, Cut(field.Value, width - column));
            }
        }

        return true;
    }

    // Cut to the width in display cells, so a wide character never pushes text past the panel's edge.
    internal static string Cut(string text, int width) => width <= 0 ? string.Empty : TerminalText.Clip(text, width);
}

/// <summary>A column of a <see cref="SkyUiTable"/>: its heading and the most it takes; the last column takes the rest.</summary>
internal sealed record SkyUiColumn(string Heading, int Most);

/// <summary>One row of a <see cref="SkyUiTable"/>: its cells, an identity that survives a refresh, and its tone.</summary>
internal sealed record SkyUiRow(string Key, IReadOnlyList<string> Cells, HvoTone Tone = HvoTone.Text);

/// <summary>
/// A table to choose a row from: a muted heading, then the rows, the chosen one marked with <c>&gt;</c> (so it never
/// depends on colour) and, while the table has the focus, on the strong accent. Up, Down, Page Up, Page Down, Home and End
/// move the choice and scroll it into view; Enter is <see cref="RowActivated"/>. The choice survives a refresh by its key.
/// </summary>
internal sealed class SkyUiTable : View
{
    private readonly HvoTheme _theme;
    private IReadOnlyList<SkyUiColumn> _columns;
    private IReadOnlyList<SkyUiRow> _rows = [];
    private int _selected;
    private int _top;

    public SkyUiTable(HvoTheme theme, IReadOnlyList<SkyUiColumn> columns)
    {
        _theme = theme;
        _columns = columns;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        AddCommand(Command.Up, () => Choose(_selected - 1));
        AddCommand(Command.Down, () => Choose(_selected + 1));
        AddCommand(Command.PageUp, () => Choose(_selected - Math.Max(1, Room)));
        AddCommand(Command.PageDown, () => Choose(_selected + Math.Max(1, Room)));
        AddCommand(Command.Start, () => Choose(0));
        AddCommand(Command.End, () => Choose(_rows.Count - 1));
        AddCommand(Command.Accept, () =>
        {
            RowActivated?.Invoke(this, EventArgs.Empty);
            return true;
        });
        KeyBindings.Add(Key.CursorUp, Command.Up);
        KeyBindings.Add(Key.CursorDown, Command.Down);
        KeyBindings.Add(Key.PageUp, Command.PageUp);
        KeyBindings.Add(Key.PageDown, Command.PageDown);
        KeyBindings.Add(Key.Home, Command.Start);
        KeyBindings.Add(Key.End, Command.End);
        KeyBindings.ReplaceCommands(Key.Enter, Command.Accept);
    }

    /// <summary>The chosen row changed.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Enter was pressed on the table.</summary>
    public event EventHandler? RowActivated;

    /// <summary>What the table says when it has no rows.</summary>
    public string Empty { get; set; } = "Nothing to show.";

    public IReadOnlyList<SkyUiColumn> Columns
    {
        get => _columns;
        set
        {
            _columns = value;
            SetNeedsDraw();
        }
    }

    public IReadOnlyList<SkyUiRow> Rows => _rows;

    public int Selected => _rows.Count == 0 ? -1 : _selected;

    public SkyUiRow? SelectedRow => _rows.Count == 0 ? null : _rows[_selected];

    /// <summary>Shows <paramref name="rows"/>, keeping the chosen row by its key when it is still there.</summary>
    public void Show(IReadOnlyList<SkyUiRow> rows, string? select = null)
    {
        var keep = select ?? SelectedRow?.Key;
        _rows = rows ?? [];
        var index = keep is null ? -1 : _rows.ToList().FindIndex(row => row.Key == keep);
        _selected = index >= 0 ? index : Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        Reveal();
        SetNeedsDraw();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The table as drawn <paramref name="width"/> wide: its heading, then each row with its marker.</summary>
    public IReadOnlyList<string> Lines(int width)
    {
        var widths = Widths(width);
        var lines = new List<string> { SkyUiFields.Cut("  " + Join(_columns.Select(column => column.Heading).ToArray(), widths), width) };
        lines.AddRange(_rows.Select((row, index) => SkyUiFields.Cut((index == _selected ? "> " : "  ") + Join(row.Cells, widths), width)));
        return lines;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var widths = Widths(width);
        SetAttribute(_theme.Label(HvoTone.Muted, HvoGround.Surface).Normal);
        AddStr(0, 0, SkyUiFields.Cut("  " + Join(_columns.Select(column => column.Heading).ToArray(), widths), width));
        if (_rows.Count == 0)
        {
            if (Viewport.Height > 1)
            {
                AddStr(2, 1, SkyUiFields.Cut(Empty, width - 2));
            }

            return true;
        }

        for (var row = 0; row < Room && _top + row < _rows.Count; row++)
        {
            var index = _top + row;
            var chosen = index == _selected;
            var text = (chosen ? "> " : "  ") + Join(_rows[index].Cells, widths);
            SetAttribute(chosen && HasFocus
                ? _theme.Selected.Normal
                : _theme.Label(_rows[index].Tone, HvoGround.Surface, bold: chosen).Normal);
            AddStr(0, row + 1, TerminalText.Pad(SkyUiFields.Cut(text, width), chosen && HasFocus ? width : 0));
        }

        if (_rows.Count > Room && Viewport.Height > 1)
        {
            SetAttribute(_theme.Label(HvoTone.Muted, HvoGround.Surface).Normal);
            var more = $"{_top + 1}-{Math.Min(_rows.Count, _top + Room)} of {_rows.Count}";
            AddStr(Math.Max(0, width - more.Length), 0, more);
        }

        return true;
    }

    // The rows below the heading.
    private int Room => Math.Max(0, Viewport.Height - 1);

    private int[] Widths(int width)
    {
        var room = Math.Max(0, width - 2);
        var widths = new int[_columns.Count];
        var used = 0;
        for (var index = 0; index < _columns.Count; index++)
        {
            var last = index == _columns.Count - 1;
            var widest = _rows.Select(row => index < row.Cells.Count ? TerminalText.Width(row.Cells[index]) : 0).Append(TerminalText.Width(_columns[index].Heading)).Max();
            widths[index] = last ? Math.Max(0, room - used) : Math.Min(widest, _columns[index].Most);
            used += widths[index] + 2;
        }

        return widths;
    }

    private static string Join(IReadOnlyList<string> cells, int[] widths)
        => string.Join("  ", widths.Select((width, index) => TerminalText.Pad(SkyUiFields.Cut(index < cells.Count ? cells[index] : string.Empty, width), width))).TrimEnd();

    private bool? Choose(int index)
    {
        if (_rows.Count == 0)
        {
            return true;
        }

        var next = Math.Clamp(index, 0, _rows.Count - 1);
        if (next != _selected)
        {
            _selected = next;
            Reveal();
            SetNeedsDraw();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        return true;
    }

    private void Reveal()
    {
        var room = Math.Max(1, Room);
        if (_selected < _top)
        {
            _top = _selected;
        }
        else if (_selected >= _top + room)
        {
            _top = _selected - room + 1;
        }

        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - room));
    }
}

/// <summary>
/// A titled rule across the page, as the terminal prototype heads its lists below the panels (<c>RECENT NOTICES</c>):
/// a line in the panels' frame colour, with the theme's glyph, so it falls back to ASCII, and the title in capitals.
/// </summary>
internal sealed class SkyUiRule : View
{
    private readonly HvoTheme _theme;
    private string _title;

    public SkyUiRule(HvoTheme theme, string title)
    {
        _theme = theme;
        _title = title;
        Height = 1;
        CanFocus = false;
    }

    public string Heading
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            SetNeedsDraw();
        }
    }

    /// <summary>The rule as drawn <paramref name="width"/> cells wide.</summary>
    public string Line(int width)
    {
        var glyph = _theme.Glyphs.Horizontal;
        if (width <= 2 || _title.Length == 0)
        {
            return new string(glyph, Math.Max(0, width));
        }

        var title = SkyUiFields.Cut($" {_title.ToUpperInvariant()} ", width - 1);
        return glyph + title + new string(glyph, Math.Max(0, width - 1 - TerminalText.Width(title)));
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        SetAttribute(_theme.PanelFrame);
        AddStr(0, 0, new string(_theme.Glyphs.Horizontal, Math.Max(0, width)));
        if (_title.Length > 0 && width > 2)
        {
            SetAttribute(_theme.Label(HvoTone.Muted, HvoGround.Page, bold: true).Normal);
            AddStr(1, 0, SkyUiFields.Cut($" {_title.ToUpperInvariant()} ", width - 1));
        }

        return true;
    }
}
