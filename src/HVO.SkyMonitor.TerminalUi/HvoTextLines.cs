using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>A line of text and its colour.</summary>
/// <param name="Text">The text.</param>
/// <param name="Tone">Its colour.</param>
/// <param name="Bold">Whether it is bold.</param>
public sealed record HvoLine(string Text, HvoTone Tone = HvoTone.Text, bool Bold = false);

/// <summary>
/// Lines to read in a bounded box: each wrapped at the box's width, its continuation lines indented under the column its
/// text is in (the value after a label, or a plan step's target), so content never runs past the box. When there is
/// more than fits, the last row says which lines show and how to see the rest, in words rather than a scroll bar whose
/// glyphs are not ASCII. Up, Down, Page Up, Page Down, Home and End scroll it; Enter is left to the program.
/// </summary>
public sealed class HvoTextLines : View
{
    private readonly HvoTheme _theme;
    private IReadOnlyList<HvoLine> _lines = [];
    private (HvoLine Line, string Text)[] _shown = [];
    private int _wrappedAt = -1;
    private int _top;
    private int? _revealed;

    /// <summary>Lines on <paramref name="ground"/>.</summary>
    public HvoTextLines(HvoTheme theme, HvoGround ground = HvoGround.Surface)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        Ground = ground;
        CanFocus = true;
        AddCommand(Command.Up, () => ScrollTo(_top - 1));
        AddCommand(Command.Down, () => ScrollTo(_top + 1));
        AddCommand(Command.PageUp, () => ScrollTo(_top - Math.Max(1, Room)));
        AddCommand(Command.PageDown, () => ScrollTo(_top + Math.Max(1, Room)));
        AddCommand(Command.Start, () => ScrollTo(0));
        AddCommand(Command.End, () => ScrollTo(int.MaxValue));
        KeyBindings.Add(Key.CursorUp, Command.Up);
        KeyBindings.Add(Key.CursorDown, Command.Down);
        KeyBindings.Add(Key.PageUp, Command.PageUp);
        KeyBindings.Add(Key.PageDown, Command.PageDown);
        KeyBindings.Add(Key.Home, Command.Start);
        KeyBindings.Add(Key.End, Command.End);
    }

    /// <summary>What the lines stand on.</summary>
    public HvoGround Ground { get; }

    /// <summary>The lines, as given.</summary>
    public IReadOnlyList<HvoLine> Lines => _lines;

    /// <summary>The lines as the view wraps them at its width.</summary>
    public IReadOnlyList<string> Shown => [.. _shown.Select(shown => shown.Text)];

    /// <summary>The first wrapped line in view.</summary>
    public int Top => _top;

    /// <summary>Whether there is more than fits.</summary>
    public bool Overflows => _shown.Length > Math.Max(0, Viewport.Height);

    /// <summary>Shows <paramref name="lines"/> from the top.</summary>
    public void Show(IReadOnlyList<HvoLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _lines = lines;
        _revealed = null;
        _top = 0;
        Rewrap(force: true);
    }

    /// <summary>Shows plain <paramref name="lines"/> in <paramref name="tone"/>.</summary>
    public void Show(IEnumerable<string> lines, HvoTone tone = HvoTone.Text)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Show([.. lines.Select(line => new HvoLine(line, tone))]);
    }

    /// <summary>Scrolls so line <paramref name="line"/> of <see cref="Lines"/> is in view near the top, and keeps it there as the view is resized.</summary>
    public void Reveal(int line)
    {
        _revealed = Math.Clamp(line, 0, Math.Max(0, _lines.Count - 1));
        Rewrap(force: true);
    }

    /// <summary>
    /// <paramref name="line"/> wrapped at <paramref name="width"/>, at spaces where it can be, each continuation indented
    /// by <see cref="HangingIndent"/>; a word too long for a line is cut.
    /// </summary>
    public static IEnumerable<string> Wrap(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (width <= 0 || line.Length <= width)
        {
            yield return line;
            yield break;
        }

        var indent = new string(' ', HangingIndent(line, width));
        var rest = line;
        var first = true;
        while (true)
        {
            var prefix = first ? string.Empty : indent;
            var room = width - prefix.Length;
            if (rest.Length <= room)
            {
                yield return prefix + rest;
                yield break;
            }

            // The first line breaks only at a space past its hanging indent, so a column's long value (a plan step's
            // path) starts on the line with its label and is cut there, rather than moving whole onto a line of its own.
            var start = first ? Math.Max(rest.Length - rest.TrimStart(' ').Length, indent.Length) : 0;
            var space = rest.LastIndexOf(' ', room);
            var end = space > start ? space : room;
            yield return prefix + rest[..end].TrimEnd(' ');
            rest = rest[end..].TrimStart(' ');
            first = false;
            if (rest.Length == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Where a wrapped line's continuations start: under its last column (text after two or more spaces) that begins in
    /// the first half of <paramref name="width"/>, or under its first character.
    /// </summary>
    public static int HangingIndent(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        var limit = width / 2;
        var indent = Math.Min(line.Length - line.TrimStart(' ').Length, limit);
        for (var i = indent; i < line.Length - 2 && i < limit; i++)
        {
            if (line[i] != ' ' && line[i + 1] == ' ' && line[i + 2] == ' ')
            {
                var column = i + 1;
                while (column < line.Length && line[column] == ' ')
                {
                    column++;
                }

                if (column < line.Length && column <= limit)
                {
                    indent = column;
                }

                i = column - 1;
            }
        }

        return indent;
    }

    /// <summary>What the last row says when there is more than fits: the lines in view, of how many, and the keys.</summary>
    public static string Position(int first, int last, int total) => $"-- lines {first}-{last} of {total}: Up, Down, PgUp, PgDn --";

    /// <inheritdoc/>
    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        Rewrap(force: false);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        Rewrap(force: false);
        var height = Viewport.Height;
        var width = Viewport.Width;
        var room = Room;
        for (var row = 0; row < room && _top + row < _shown.Length; row++)
        {
            var (line, text) = _shown[_top + row];
            SetAttribute(_theme.Label(line.Tone, Ground, line.Bold).Normal);
            AddStr(0, row, HvoChoiceList.Fit(text, width));
        }

        if (Overflows && height > 0)
        {
            SetAttribute(_theme.Label(HasFocus ? HvoTone.Accent : HvoTone.Muted, Ground).Normal);
            AddStr(0, height - 1, HvoChoiceList.Fit(Position(_top + 1, Math.Min(_shown.Length, _top + room), _shown.Length), width));
        }

        return true;
    }

    // The rows for lines: all of them, or all but the last, which says where the reader is.
    private int Room => Overflows ? Math.Max(0, Viewport.Height - 1) : Viewport.Height;

    private void Rewrap(bool force)
    {
        var width = Viewport.Width;
        if (!force && width == _wrappedAt)
        {
            return;
        }

        _wrappedAt = width;
        _shown = [.. _lines.SelectMany(line => Wrap(line.Text, width).Select(text => (line, text)))];
        if (_revealed is { } revealed)
        {
            _top = _lines.Take(Math.Max(0, revealed - 1)).Sum(line => Wrap(line.Text, width).Count());
        }

        _top = Clamp(_top);
        SetNeedsDraw();
    }

    private int Clamp(int top) => Math.Clamp(top, 0, Math.Max(0, _shown.Length - Math.Max(1, Room)));

    private bool? ScrollTo(int top)
    {
        _revealed = null;
        _top = Clamp(top);
        SetNeedsDraw();
        return true;
    }
}
