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
    public void Show(IReadOnlyList<HvoLine> lines) => Show(lines, preserveScroll: false);

    /// <summary>Shows <paramref name="lines"/>, optionally keeping the scroll position, clamped to the new content.</summary>
    public void Show(IReadOnlyList<HvoLine> lines, bool preserveScroll)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _lines = lines;
        _revealed = null;
        if (!preserveScroll)
        {
            _top = 0;
        }

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
    /// <paramref name="line"/> wrapped at <paramref name="width"/> display cells (<see cref="TerminalText"/>), at spaces
    /// where it can be and never inside a text element, each continuation indented by <see cref="HangingIndent"/>; a
    /// word too long for a line is cut between its elements.
    /// </summary>
    public static IEnumerable<string> Wrap(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (width <= 0 || TerminalText.Width(line) <= width)
        {
            return [line];
        }

        var elements = TerminalText.Elements(line);
        var widths = elements.Select(TerminalText.ElementWidth).ToArray();
        var indent = HangingIndent(line, width);
        var leading = elements.TakeWhile(static element => element == " ").Count();
        var rows = new List<string>();
        var start = 0;
        while (start < elements.Count)
        {
            var first = rows.Count == 0;
            var room = Math.Max(1, width - (first ? 0 : indent));
            var end = start;
            var used = 0;
            while (end < elements.Count && used + widths[end] <= room)
            {
                used += widths[end];
                end++;
            }

            if (end == elements.Count)
            {
                rows.Add((first ? string.Empty : new string(' ', indent)) + string.Concat(elements.Skip(start)));
                break;
            }

            // An element wider than the whole room goes on a line of its own, so the text always moves on.
            end = Math.Max(end, start + 1);

            // The first line breaks only at a space past its own indent and its hanging indent, so a column's long value
            // (a plan step's path) starts on the line with its label and is cut there, rather than moving whole onto a
            // line of its own. A later line breaks at its last space: the space just after the room counts, so a word that
            // ends at the edge stays on its line.
            var minimum = first ? Math.Max(leading, indent) : 0;
            var cut = end;
            var offset = 0;
            for (var index = start; index <= end && index < elements.Count; index++)
            {
                if (elements[index] == " " && offset > minimum && index > start)
                {
                    cut = index;
                }

                offset += widths[index];
            }

            var text = string.Concat(elements.Skip(start).Take(cut - start)).TrimEnd(' ');
            rows.Add((first ? string.Empty : new string(' ', indent)) + text);
            start = cut;
            while (start < elements.Count && elements[start] == " ")
            {
                start++;
            }
        }

        return rows;
    }

    /// <summary>
    /// Where a wrapped line's continuations start, in cells: under its last column (text after two or more spaces) that
    /// begins in the first half of <paramref name="width"/>, or under its first character.
    /// </summary>
    public static int HangingIndent(string line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);
        var elements = TerminalText.Elements(line);
        var offsets = new int[elements.Count + 1];
        for (var index = 0; index < elements.Count; index++)
        {
            offsets[index + 1] = offsets[index] + TerminalText.ElementWidth(elements[index]);
        }

        var limit = width / 2;
        var leading = elements.TakeWhile(static element => element == " ").Count();
        var indent = Math.Min(leading, limit);
        for (var i = indent; i < elements.Count - 2 && offsets[i] < limit; i++)
        {
            if (elements[i] != " " && elements[i + 1] == " " && elements[i + 2] == " ")
            {
                var column = i + 1;
                while (column < elements.Count && elements[column] == " ")
                {
                    column++;
                }

                if (column < elements.Count && offsets[column] <= limit)
                {
                    indent = offsets[column];
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
