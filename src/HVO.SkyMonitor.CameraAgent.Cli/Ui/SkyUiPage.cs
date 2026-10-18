using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// One page of the interface. The shell tells it when it is shown, when its data changed, and how much room it has; the
/// page lays its panels out with absolute cells for that room (80 x 24 at least; more columns from
/// <see cref="SkyTerminalUi.WideWidth"/>).
/// </summary>
internal abstract class SkyUiPage : View
{
    private readonly List<Button> _buttons = [];

    protected SkyUiPage(SkyTerminalUi ui, string name)
    {
        Ui = ui;
        Name = name;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    public string Name { get; }

    protected SkyTerminalUi Ui { get; }

    protected HvoTheme Theme => Ui.Theme;

    public IReadOnlyList<Button> Buttons => _buttons;

    /// <summary>What the line above the tabs says while the page shows.</summary>
    public virtual string Help => string.Empty;

    /// <summary>Reads the page's own data.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Shows what the interface has read.</summary>
    public abstract void DataChanged();

    /// <summary>Lays the page out in <paramref name="width"/> x <paramref name="height"/> cells.</summary>
    public abstract void Arrange(int width, int height, bool wide);

    /// <summary>The page's text, for tests and reading without a screen.</summary>
    public abstract string Describe();

    /// <summary>Gives the focus to the page's first control.</summary>
    public virtual void FocusFirst() => (_buttons.FirstOrDefault(button => button.Enabled) ?? (View)this).SetFocus();

    /// <summary>Presses the button labelled <paramref name="label"/>, as Enter on it would; refused while it is disabled.</summary>
    public void Press(string label)
    {
        var button = _buttons.FirstOrDefault(candidate => candidate.Text == $"[ {label} ]")
            ?? throw new InvalidOperationException($"The {Name} page has no button '{label}'.");
        if (!button.Enabled)
        {
            throw new InvalidOperationException($"The {Name} page's '{label}' is disabled.");
        }

        button.InvokeCommand(Terminal.Gui.Input.Command.Accept);
    }

    protected HvoPanel AddPanel(string title, View body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var panel = new HvoPanel(Theme, title);
        body.X = 0;
        body.Y = 0;
        body.Width = Dim.Fill();
        body.Height = Dim.Fill();
        panel.Body.Add(body);
        Add(panel);
        return panel;
    }

    protected Button AddButton(string label, Action run, bool primary = false)
    {
        var button = Theme.CreateButton(label, primary);
        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        Add(button);
        _buttons.Add(button);
        return button;
    }

    /// <summary>Places <paramref name="view"/> at a cell, with a size.</summary>
    protected static void Place(View view, int x, int y, int width, int height)
    {
        view.X = x;
        view.Y = y;
        view.Width = Math.Max(0, width);
        view.Height = Math.Max(0, height);
    }

    /// <summary>Puts the buttons on row <paramref name="y"/>, two cells apart from column 1.</summary>
    protected void PlaceButtons(int y)
    {
        var x = 1;
        foreach (var button in _buttons)
        {
            button.X = x;
            button.Y = y;
            x += button.Text.Length + 2;
        }
    }

    /// <summary>A panel's title, with <c>LAST KNOWN</c> when the latest read of what it shows failed.</summary>
    protected static string Titled<T>(string title, SkyUiReading<T> reading) => reading.IsLastKnown ? $"{title} / LAST KNOWN" : title;

    /// <summary>What a panel says before its data was read, or after reading it failed with nothing to show.</summary>
    protected static SkyUiField Pending<T>(SkyUiReading<T> reading, string what)
        => reading.Failure is { } failure
            ? new SkyUiField(string.Empty, $"{what} could not be read: {failure}", HvoTone.Warning)
            : new SkyUiField(string.Empty, $"Reading {what}...", HvoTone.Muted);

    protected static string Describe(string title, SkyUiFields fields, int width)
        => $"[{title}]\n{string.Join('\n', fields.Rows(width))}";

    protected static string Describe(string title, SkyUiTable table, int width)
        => $"[{title}]\n{string.Join('\n', table.Lines(width))}";

    /// <summary>Whether a change can be sent: signed in with a key whose access is not known to be read-only.</summary>
    protected bool CanChange => Ui.Connection is not null && Ui.Data.Info.Value?.Caller.Access is not "Read";

    /// <summary>Why a change cannot be sent, when it cannot.</summary>
    protected string? ChangeRefusal => Ui.Connection is null
        ? "Sign in first (F5 System)."
        : Ui.Data.Info.Value?.Caller.Access == "Read" ? "This key is read-only: changes need a ReadWrite key." : null;
}
