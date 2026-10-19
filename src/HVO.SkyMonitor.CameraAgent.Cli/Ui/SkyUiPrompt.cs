using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>A text field of a prompt: its label, what it starts with, and whether it is secret (shown as <c>*</c>).</summary>
internal sealed record SkyUiPromptField(string Label, string Initial = "", bool Secret = false);

/// <summary>What the person gave a prompt: each field's text, the choice, and whether they ticked the acknowledgement.</summary>
internal sealed record SkyUiPromptAnswers(IReadOnlyList<string> Fields, int Choice, bool Acknowledged);

/// <summary>
/// A prompt: what it says, what it asks (text fields, or one of several choices), an acknowledgement that must be ticked
/// before its action can run, and the action. <see cref="Run"/> returns a problem to show in the prompt, which stays
/// open, or null, when the prompt closes.
/// </summary>
internal sealed record SkyUiPromptSpec(string Title, IReadOnlyList<HvoLine> Lines, string Action, Func<SkyUiPromptAnswers, string?> Run)
{
    public IReadOnlyList<SkyUiPromptField> Fields { get; init; } = [];

    public IReadOnlyList<string> Choices { get; init; } = [];

    public int Choice { get; init; }

    /// <summary>When set, the action runs only once this is ticked, as the web UI's checkboxes require.</summary>
    public string? Acknowledge { get; init; }
}

/// <summary>
/// A prompt drawn over the page in a titled panel, not a modal dialog: Esc closes it, and the interface's keys keep
/// working. The first field, choice or toggle has the focus; Enter in a field runs the action.
/// </summary>
internal sealed class SkyUiPrompt : IDisposable
{
    private readonly SkyUiPromptSpec _spec;
    private readonly Action<SkyUiPrompt> _close;
    private readonly List<TextField> _fields = [];
    private readonly HvoChoiceList? _choices;
    private readonly HvoToggle? _acknowledge;
    private readonly Label _error;
    private readonly Button _action;
    private readonly Button _cancel;

    public SkyUiPrompt(HvoTheme theme, SkyUiPromptSpec spec, int screenWidth, Action<SkyUiPrompt> close)
    {
        Panel = new HvoPanel(theme, spec.Title);
        _spec = spec;
        _close = close;
        // As the terminal prototype's review: at most 66 cells, and eight fewer than the screen.
        var width = Math.Clamp(screenWidth - 8, 30, 66);
        var inner = width - 4;
        var lines = spec.Lines.SelectMany(line => HvoTextLines.Wrap(line.Text, inner).Select(text => line with { Text = text })).ToArray();
        var text = new HvoTextLines(theme) { X = 0, Y = 0, Width = Dim.Fill(), Height = lines.Length, CanFocus = false };
        text.Show(lines);
        Panel.Body.Add(text);
        var y = lines.Length;
        if (spec.Fields.Count > 0)
        {
            y++;
            var labelWidth = spec.Fields.Max(field => field.Label.Length) + 2;
            foreach (var field in spec.Fields)
            {
                var caption = new Label { Text = field.Label, X = 0, Y = y, Width = labelWidth };
                var input = new TextField { Text = field.Initial, Secret = field.Secret, X = labelWidth, Y = y, Width = Dim.Fill() };
                theme.StyleField(input, caption);
                input.Accepting += (_, e) =>
                {
                    e.Handled = true;
                    Choose();
                };
                Panel.Body.Add(caption, input);
                _fields.Add(input);
                y++;
            }
        }

        if (spec.Choices.Count > 0)
        {
            y++;
            _choices = new HvoChoiceList(theme, spec.Choices, spec.Choice) { X = 0, Y = y };
            Panel.Body.Add(_choices);
            y += spec.Choices.Count;
        }

        if (spec.Acknowledge is { } acknowledge)
        {
            y++;
            _acknowledge = new HvoToggle(theme, acknowledge) { X = 0, Y = y };
            _acknowledge.CheckedChanged += (_, _) => _action!.Enabled = _acknowledge.Checked;
            Panel.Body.Add(_acknowledge);
            y++;
        }

        _error = new Label { X = 0, Y = y + 1, Width = Dim.Fill(), Height = 2 };
        _error.TextFormatter.WordWrap = true;
        _error.SetScheme(theme.Label(HvoTone.Danger, HvoGround.Surface, bold: true));
        _action = theme.CreateButton(spec.Action, primary: true);
        _action.X = 0;
        _action.Y = y + 3;
        _action.Enabled = _acknowledge is null;
        _action.Accepting += (_, e) =>
        {
            e.Handled = true;
            Choose();
        };
        _cancel = theme.CreateButton("Cancel");
        _cancel.X = Pos.Right(_action) + 2;
        _cancel.Y = y + 3;
        _cancel.Accepting += (_, e) =>
        {
            e.Handled = true;
            _close(this);
        };
        Panel.Body.Add(_error, _action, _cancel);
        Panel.X = Pos.Center();
        Panel.Y = Pos.Center();
        Panel.Width = width;
        Panel.Height = y + 4 + 2;
    }

    /// <summary>The panel the prompt is drawn in, which the interface puts over the page.</summary>
    public HvoPanel Panel { get; }

    public SkyUiPromptSpec Spec => _spec;

    public IReadOnlyList<TextField> Fields => _fields;

    public HvoChoiceList? Choices => _choices;

    public HvoToggle? Acknowledgement => _acknowledge;

    public Button ActionButton => _action;

    public string Error => _error.Text;

    /// <summary>Shows a problem in the prompt, which stays open.</summary>
    public void ShowError(string text) => _error.Text = text ?? string.Empty;

    public void FocusFirst()
        => ((View?)_fields.FirstOrDefault() ?? (View?)_choices ?? (View?)_acknowledge ?? _action).SetFocus();

    /// <summary>Runs the action, as its button does: refused while the acknowledgement is not ticked.</summary>
    public void Choose()
    {
        if (!_action.Enabled)
        {
            ShowError($"Tick \"{_spec.Acknowledge}\" first.");
            return;
        }

        var problem = _spec.Run(new SkyUiPromptAnswers(
            [.. _fields.Select(field => field.Text ?? string.Empty)], _choices?.Value ?? 0, _acknowledge?.Checked ?? true));
        if (problem is null)
        {
            _close(this);
        }
        else
        {
            ShowError(problem);
        }
    }

    /// <summary>The prompt's text as drawn, for tests: its lines, fields (secrets as <c>*</c>), choices, toggle and error.</summary>
    public string Describe()
        => string.Join('\n', _spec.Lines.Select(line => line.Text)
            .Concat(_spec.Fields.Select((field, index) => $"{field.Label}: {(field.Secret ? new string('*', (_fields[index].Text ?? string.Empty).Length) : _fields[index].Text)}"))
            .Concat(_choices?.Lines ?? [])
            .Concat(_acknowledge is null ? [] : [_acknowledge.Line])
            .Append(Error));

    /// <summary>Disposes the panel, and with it every field, choice and button on it.</summary>
    public void Dispose() => Panel.Dispose();
}
