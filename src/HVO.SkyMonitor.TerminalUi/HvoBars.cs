using Terminal.Gui.ViewBase;

namespace HVO.SkyMonitor.TerminalUi;

/// <summary>
/// The top two rows of a screen, as the terminal prototype has them: the program's name in bold capitals and a badge on
/// the right of a bar on the surface, then a line of context, muted, with a note on the right. The badge and the context
/// keep their room first; the note is left out, then cut, when the screen is narrow.
/// </summary>
public sealed class HvoHeaderBar : View
{
    private readonly HvoTheme _theme;
    private string _title = string.Empty;
    private string _badge = string.Empty;
    private string _context = string.Empty;
    private string _note = string.Empty;
    private HvoTone _badgeTone = HvoTone.Accent;

    /// <summary>A header bar across the top of the screen.</summary>
    public HvoHeaderBar(HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = 2;
    }

    /// <summary>The program's name, drawn in capitals.</summary>
    public string Heading
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>The badge on the right of the bar, such as <c>STEP 2 OF 6</c> or <c>[LIVE]</c>.</summary>
    public string Badge
    {
        get => _badge;
        set => Set(ref _badge, value);
    }

    /// <summary>The badge's colour.</summary>
    public HvoTone BadgeTone
    {
        get => _badgeTone;
        set
        {
            _badgeTone = value;
            SetNeedsDraw();
        }
    }

    /// <summary>The line under the bar: what the program works on.</summary>
    public string Context
    {
        get => _context;
        set => Set(ref _context, value);
    }

    /// <summary>A note on the right of the context line.</summary>
    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    /// <summary>The two rows as text, <paramref name="width"/> wide.</summary>
    public IReadOnlyList<string> Rows(int width) => [Row(Heading.ToUpperInvariant(), Badge, width), Row(Context, Note, width)];

    /// <summary>
    /// <paramref name="left"/> from the second column and <paramref name="right"/> ending one column from the edge, with
    /// at least two spaces between; <paramref name="right"/> is dropped, then <paramref name="left"/> cut, to fit.
    /// </summary>
    public static string Row(string left, string right, int width)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (width <= 2)
        {
            return new string(' ', Math.Max(0, width));
        }

        var room = width - 2;
        var leftText = left.Length > room ? left[..room] : left;
        var row = " " + leftText;
        if (right.Length > 0 && leftText.Length + 2 + right.Length <= room)
        {
            row = row.PadRight(width - 1 - right.Length) + right;
        }

        return row.PadRight(width);
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var bar = _theme.Bar.Normal;
        SetAttribute(bar);
        AddStr(0, 0, new string(' ', Math.Max(0, width)));
        var top = Row(Heading.ToUpperInvariant(), Badge, width);
        AddStr(0, 0, top);
        if (Badge.Length > 0 && top.TrimEnd().EndsWith(Badge, StringComparison.Ordinal))
        {
            SetAttribute(_theme.Label(BadgeTone, HvoGround.Surface, bold: true).Normal);
            AddStr(width - 1 - Badge.Length, 0, Badge);
        }

        if (Viewport.Height > 1)
        {
            SetAttribute(_theme.Label(HvoTone.Muted).Normal);
            AddStr(0, 1, Row(Context, Note, width));
        }

        return true;
    }

    private void Set(ref string field, string? value)
    {
        field = value ?? string.Empty;
        SetNeedsDraw();
    }
}

/// <summary>
/// A notice: a band across the screen in its kind's colours, starting with a word in brackets (<c>[ERROR]</c>,
/// <c>[WARNING]</c>, <c>[OK]</c>, <c>[INFO]</c>) so its kind never depends on colour. It wraps over as many rows as it is
/// high; nothing is drawn when it has no text.
/// </summary>
public sealed class HvoNoticeBar : View
{
    private readonly HvoTheme _theme;
    private string _text = string.Empty;
    private string? _word;
    private HvoNotice _kind = HvoNotice.Info;

    /// <summary>A notice band.</summary>
    public HvoNoticeBar(HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        Width = Dim.Fill();
        Height = 1;
    }

    /// <summary>The notice's kind.</summary>
    public HvoNotice Kind => _kind;

    /// <summary>The notice's text, without its word.</summary>
    public string Body => _text;

    /// <summary>The notice as drawn: its word and its text.</summary>
    public string Message => _text.Length == 0 ? string.Empty : $"{_word ?? HvoTheme.NoticeWord(_kind)} {_text}";

    /// <summary>Raised when the notice changes, so a screen can give it the rows it needs.</summary>
    public event EventHandler? Changed;

    /// <summary>Shows <paramref name="text"/> as a notice of <paramref name="kind"/>, with <paramref name="word"/> in place of the kind's own.</summary>
    public void Show(HvoNotice kind, string text, string? word = null)
    {
        _kind = kind;
        _text = text ?? string.Empty;
        _word = word;
        SetNeedsDraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The rows the notice takes wrapped at <paramref name="width"/>, at most <paramref name="most"/>; one when there is none.</summary>
    public int RowsFor(int width, int most = 2)
        => _text.Length == 0 ? 1 : Math.Clamp(HvoTextLines.Wrap(Message, Math.Max(1, width - 2)).Count(), 1, Math.Max(1, most));

    /// <summary>Takes the notice away.</summary>
    public void Clear() => Show(HvoNotice.Info, string.Empty);

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_text.Length == 0)
        {
            return false;
        }

        var width = Viewport.Width;
        SetAttribute(_theme.Notice(_kind).Normal);
        var rows = HvoTextLines.Wrap(Message, Math.Max(1, width - 2)).Take(Math.Max(1, Viewport.Height)).ToArray();
        for (var row = 0; row < Viewport.Height; row++)
        {
            var text = row < rows.Length ? " " + rows[row] : string.Empty;
            AddStr(0, row, text.PadRight(width)[..width]);
        }

        return true;
    }
}

/// <summary>
/// The key bar at the foot of a screen: each key in bold text and what it does, muted, two spaces apart, with optional
/// text on the right. Keys that do not fit are left out from the end.
/// </summary>
public sealed class HvoKeyBar : View
{
    private readonly HvoTheme _theme;
    private IReadOnlyList<(string Key, string Name)> _keys = [];
    private string _right = string.Empty;

    /// <summary>A key bar.</summary>
    public HvoKeyBar(HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        Width = Dim.Fill();
        Height = 1;
    }

    /// <summary>The keys and what each does.</summary>
    public IReadOnlyList<(string Key, string Name)> Keys
    {
        get => _keys;
        set
        {
            _keys = value ?? [];
            SetNeedsDraw();
        }
    }

    /// <summary>Text on the right of the bar.</summary>
    public string Right
    {
        get => _right;
        set
        {
            _right = value ?? string.Empty;
            SetNeedsDraw();
        }
    }

    /// <summary>The bar as text, <paramref name="width"/> wide.</summary>
    public string Render(int width) => HvoHeaderBar.Row(string.Join("  ", Fitting(width - 2).Select(key => $"{key.Key} {key.Name}")), Right, width);

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        SetAttribute(_theme.KeyName.Normal);
        AddStr(0, 0, Render(width));
        var column = 1;
        SetAttribute(_theme.Key.Normal);
        foreach (var (key, name) in Fitting(width - 2))
        {
            AddStr(column, 0, key);
            column += key.Length + 1 + name.Length + 2;
        }

        return true;
    }

    private IEnumerable<(string Key, string Name)> Fitting(int room)
    {
        var used = 0;
        foreach (var key in _keys)
        {
            var length = key.Key.Length + 1 + key.Name.Length + (used == 0 ? 0 : 2);
            if (used + length > room)
            {
                yield break;
            }

            used += length;
            yield return key;
        }
    }
}

/// <summary>
/// A row of tabs on the surface: a wizard's steps, or a program's pages. The current one is on the strong accent (in
/// reverse video without colour) and also in brackets, so it never depends on colour. When the full labels do not fit,
/// the others are shortened to their first word (a step's number, a page's key).
/// </summary>
public sealed class HvoTabBar : View
{
    private readonly HvoTheme _theme;
    private IReadOnlyList<string> _tabs = [];
    private int _current;
    private string _right = string.Empty;

    /// <summary>A tab bar.</summary>
    public HvoTabBar(HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        Width = Dim.Fill();
        Height = 1;
    }

    /// <summary>The tabs' labels.</summary>
    public IReadOnlyList<string> Tabs
    {
        get => _tabs;
        set
        {
            _tabs = value ?? [];
            SetNeedsDraw();
        }
    }

    /// <summary>The index of the current tab.</summary>
    public int Current
    {
        get => _current;
        set
        {
            _current = value;
            SetNeedsDraw();
        }
    }

    /// <summary>A label on the right of the bar, such as <c>F10 Quit</c>.</summary>
    public string Right
    {
        get => _right;
        set
        {
            _right = value ?? string.Empty;
            SetNeedsDraw();
        }
    }

    /// <summary>The bar's cells: each tab's label as drawn and where it starts.</summary>
    public IReadOnlyList<(string Label, int Column, bool Current)> Layout(int width)
    {
        var room = width - 2 - (_right.Length > 0 ? _right.Length + 3 : 0);
        var full = Cells(shorten: false);
        var cells = full.Sum(cell => cell.Length + 1) - 1 <= room ? full : Cells(shorten: true);
        var result = new List<(string, int, bool)>();
        var column = 1;
        for (var index = 0; index < cells.Count; index++)
        {
            if (column + cells[index].Length > width - 1)
            {
                break;
            }

            result.Add((cells[index], column, index == _current));
            column += cells[index].Length + 1;
        }

        return result;
    }

    /// <inheritdoc/>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        SetAttribute(_theme.Panel.Normal);
        AddStr(0, 0, new string(' ', Math.Max(0, width)));
        foreach (var (label, column, current) in Layout(width))
        {
            SetAttribute(current ? _theme.Selected.Normal : _theme.Panel.Normal);
            AddStr(column, 0, label);
        }

        if (_right.Length > 0 && width > _right.Length + 3)
        {
            SetAttribute(_theme.Label(HvoTone.Muted, HvoGround.Surface).Normal);
            AddStr(width - 1 - _right.Length - 2, 0, $" {_right} ");
        }

        return true;
    }

    private List<string> Cells(bool shorten)
        => [.. _tabs.Select((tab, index) => index == _current ? $"[{tab}]" : $" {(shorten ? tab.Split(' ')[0] : tab)} ")];
}
