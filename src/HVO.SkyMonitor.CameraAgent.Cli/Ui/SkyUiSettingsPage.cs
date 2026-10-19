using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// F2 Settings: the groups and settings of CameraAgent's settings catalogue (#1316), each with its value, where it comes
/// from and whether it can be changed here; the chosen setting's description and rules; and Change, whose form the
/// catalogue builds.
/// </summary>
internal sealed class SkyUiSettingsPage : SkyUiPage
{
    private readonly SkyUiTable _groups;
    private readonly SkyUiTable _settings;
    private readonly HvoTextLines _about;
    private readonly HvoPanel _groupsPanel;
    private readonly HvoPanel _settingsPanel;
    private readonly HvoPanel _aboutPanel;
    private readonly Button _change;

    public SkyUiSettingsPage(SkyTerminalUi ui)
        : base(ui, "Settings")
    {
        _groups = new SkyUiTable(Theme, [new SkyUiColumn("GROUP", 24)]) { Empty = "Reading the catalogue..." };
        _settings = new SkyUiTable(Theme, [new SkyUiColumn("SETTING", 24), new SkyUiColumn("VALUE", 18), new SkyUiColumn("NOTE", 40)]);
        _about = new HvoTextLines(Theme) { CanFocus = false };
        _groupsPanel = AddPanel("Groups", _groups);
        _settingsPanel = AddPanel("Settings", _settings);
        _aboutPanel = AddPanel("About", _about);
        _groups.SelectionChanged += (_, _) => ShowGroup();
        _settings.SelectionChanged += (_, _) => ShowSetting();
        _settings.RowActivated += (_, _) => Change();
        _change = AddButton("Change", Change, primary: true);
        AddButton("Reload", () => Ui.ReadSettings());
    }

    public override string Help => SelectedField is { } chosen && !chosen.CanWrite
        ? $"{chosen.Key} is read-only here: {chosen.ReadOnlyReason}"
        : ChangeRefusal ?? "Up and Down choose; Enter or Change edits the chosen setting.";

    /// <summary>The setting chosen in the table.</summary>
    public SkySettingField? SelectedField
        => Ui.Data.Settings.Value?.FindField(_settings.SelectedRow?.Key ?? string.Empty, exactOnly: true);

    public SkyUiTable GroupTable => _groups;

    public SkyUiTable SettingTable => _settings;

    public override void Refresh() => Ui.ReadSettings();

    public override void FocusFirst() => _groups.SetFocus();

    public override void Arrange(int width, int height, bool wide)
    {
        var groups = wide ? 30 : 25;
        var top = wide ? 12 : 9;
        Place(_groupsPanel, 1, 0, groups, top);
        Place(_settingsPanel, 2 + groups, 0, width - 3 - groups, top);
        Place(_aboutPanel, 1, top, width - 2, Math.Max(4, Math.Min(wide ? 10 : 7, height - top - 2)));
        _settings.Columns = wide
            ? [new SkyUiColumn("SETTING", 30), new SkyUiColumn("VALUE", 22), new SkyUiColumn("SOURCE", 18), new SkyUiColumn("NOTE", 40)]
            : [new SkyUiColumn("SETTING", 20), new SkyUiColumn("VALUE", 24), new SkyUiColumn("NOTE", 40)];
        PlaceButtons(Math.Min(height - 1, top + (wide ? 10 : 7) + 1));
        ShowGroup();
    }

    public override void DataChanged()
    {
        var reading = Ui.Data.Settings;
        _groupsPanel.Caption = Titled("Groups", reading);
        if (reading.Value is not { } form)
        {
            _groups.Empty = Ui.Connection is null ? "Not signed in: F5 System."
                : reading.Failure is { } failure ? $"Not read: {failure}" : "Reading the catalogue...";
            _groups.Show([]);
            _settings.Show([]);
            _about.Show([]);
            _change.Enabled = false;
            return;
        }

        // A group that cannot be changed here says so in words, in its settings' caption and notes: not by colour alone.
        _groups.Show([.. form.Groups.Select(group => new SkyUiRow(group.Name, [group.Descriptor.Title]))]);
        ShowGroup();
    }

    public override string Describe()
        => string.Join("\n\n", Describe(_groupsPanel.Caption, _groups, 30), Describe(_settingsPanel.Caption, _settings, 76),
            $"[{_aboutPanel.Caption}]\n{string.Join('\n', _about.Lines.Select(line => line.Text))}");

    private void ShowGroup()
    {
        var form = Ui.Data.Settings.Value;
        var group = form?.FindGroup(_groups.SelectedRow?.Key ?? string.Empty);
        if (group is null)
        {
            _settings.Show([]);
            return;
        }

        var readOnly = group.State is { CanWrite: false } ? " / READ-ONLY" : string.Empty;
        _settingsPanel.Caption = Titled(group.Descriptor.Title, Ui.Data.Settings) + readOnly;
        _settings.Show([.. group.Fields.Select(field => new SkyUiRow(field.Key, Ui.Wide
            ? [field.Setting.Title, field.DisplayValue, field.State?.Source ?? "?", SkySettingText.DescribeNotes(field)]
            : [field.Setting.Title, field.DisplayValue, SkySettingText.DescribeNotes(field)],
            field.CanWrite ? HvoTone.Text : HvoTone.Muted))]);
        ShowSetting();
    }

    private void ShowSetting()
    {
        if (SelectedField is not { } field)
        {
            _about.Show([]);
            _change.Enabled = false;
            return;
        }

        _aboutPanel.Caption = $"About {field.Key}";
        var lines = new List<HvoLine>
        {
            new(field.Setting.Description),
            new($"Accepts   {SkySettingText.DescribeRule(field)}", HvoTone.Muted),
            new($"Applies   {SkySettingText.DescribeApplication(field.Setting.Application)}; {SkySettingText.DescribeSafety(field.Setting.Safety)}", HvoTone.Muted),
            new($"Source    {field.State?.Source ?? "not returned"}{(field.State?.SourceDetail is { Length: > 0 } detail ? $" ({detail})" : string.Empty)}", HvoTone.Muted)
        };
        if (!field.CanWrite)
        {
            lines.Add(new($"Read-only {field.ReadOnlyReason}", HvoTone.Warning));
        }

        if (field.State?.Problem is { Length: > 0 } problem)
        {
            lines.Add(new($"Problem   {problem}", HvoTone.Danger));
        }

        if (field.State?.PendingValue is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } pending)
        {
            lines.Add(new($"Pending   {SkySettingText.Describe(pending)}, saved and not in effect yet", HvoTone.Info));
        }

        _about.Show(lines);
        _change.Enabled = field.CanWrite && CanChange;
        Ui.Hint(Help);
    }

    private void Change()
    {
        if (SelectedField is { } field && field.CanWrite && CanChange)
        {
            SkyUiSettingChange.Open(Ui, field);
        }
    }
}

/// <summary>
/// The form that changes one setting, built from its catalogue entry: a field for text and numbers, the choices for an
/// enum, a reason, and an acknowledgement for a change of the location and time basis (safety <c>CaptureGeometry</c>), as
/// <c>hvo-sky config set</c> confirms it. The value is checked against the catalogue before anything is sent; the group
/// is sent with the version read, so a change made meanwhile is refused rather than overwritten.
/// </summary>
internal static class SkyUiSettingChange
{
    public static SkyUiPrompt Open(SkyTerminalUi ui, SkySettingField field)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(field);
        var setting = field.Setting;
        var lines = new List<HvoLine>
        {
            new($"{setting.Key} is {field.DisplayValue} now."),
            new($"It accepts {SkySettingText.DescribeRule(field)}.", HvoTone.Muted),
            new($"A change applies {SkySettingText.DescribeApplication(setting.Application)} and {SkySettingText.DescribeSafety(setting.Safety)}.", HvoTone.Muted)
        };
        var geometry = setting.Safety == SkySettingsContract.CaptureGeometrySafety;
        string[] choices = [];
        var choice = 0;
        if (setting.Type == SkySettingsContract.EnumType && field.Choices is { Count: > 0 } allowed)
        {
            choices = [.. allowed.Select(option => option.Label == option.Value ? option.Value : $"{option.Value}  {option.Label}"), .. setting.Nullable ? new[] { "(none)" } : []];
            var current = field.State?.Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            choice = Math.Max(0, allowed.ToList().FindIndex(option => option.Value == current));
        }

        SkySettingsGroupState group = field.Group.State
            ?? throw new InvalidOperationException($"CameraAgent did not return the {field.Group.Name} group's state.");
        return ui.OpenPrompt(new SkyUiPromptSpec("Setting / Review", lines, "Save", answers =>
        {
            JsonElement value;
            try
            {
                value = choices.Length > 0
                    ? answers.Choice >= field.Choices!.Count ? SkySettingsForm.Null : SkySettingsForm.Check(field, JsonSerializer.SerializeToElement(field.Choices[answers.Choice].Value))
                    : SkySettingsForm.ParseText(field, answers.Fields[0]);
            }
            catch (SkySettingValueException problem)
            {
                return problem.Message;
            }

            var reason = answers.Fields[^1].Trim();
            if (reason.Length > SkySettingsContract.MaximumReasonLength)
            {
                return $"The reason takes at most {SkySettingsContract.MaximumReasonLength} characters.";
            }

            if (SkySettingsForm.SameValue(field.State?.Value, value))
            {
                ui.Say(HvoNotice.Info, $"Nothing to change: {setting.Key} is already {SkySettingText.Describe(value)}.");
                return null;
            }

            Send(ui, field, group, value, reason.Length == 0 ? null : reason);
            return null;
        })
        {
            Fields = choices.Length > 0
                ? [new SkyUiPromptField("Reason")]
                : [new SkyUiPromptField("Value", Initial(field)), new SkyUiPromptField("Reason")],
            Choices = choices,
            Choice = choice,
            Acknowledge = geometry ? "Change the location and time basis future captures are stamped with" : null
        });
    }

    private static string Initial(SkySettingField field) => field.State?.Value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? string.Empty,
        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
        _ => string.Empty
    };

    private static void Send(SkyTerminalUi ui, SkySettingField field, SkySettingsGroupState group, JsonElement value, string? reason)
    {
        if (ui.Client is not { } client)
        {
            return;
        }

        var request = new SkySettingsUpdateRequest(group.Version, new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [field.Key] = value }, reason);
        ui.Say(HvoNotice.Info, $"Saving {field.Key}...");
        ui.Run(
            async (operation, token) => SkyApiClient.Read<SkySettingsWriteResponse>(await operation.SendChangeAsync(
                client, HttpMethod.Put, $"{SkySettingsContract.Route}/{Uri.EscapeDataString(group.Group)}", request,
                $"the {group.Group} settings", token, static answer => SkyJson.Text(answer, "status") != "Unchanged").ConfigureAwait(false)),
            (response, error, operation) =>
            {
                if (error is not null)
                {
                    var details = error is SkyApiException refusal
                        ? string.Join(" ", refusal.Errors.SelectMany(entry => entry.Value).Concat(refusal.Settings.Values))
                        : string.Empty;
                    ui.SayOutcome(operation, error, string.Empty, $"{field.Key} was not saved", details);
                }
                else
                {
                    ui.Say(HvoNotice.Success, response!.Status switch
                    {
                        "PendingRestart" => $"{field.Key} saved; it takes effect at the next CameraAgent start (F5 System, Restart).",
                        "Unchanged" => $"Nothing changed: CameraAgent already had {field.Key} at that value.",
                        _ => $"{field.Key} saved; in effect {SkySettingText.DescribeApplication(field.Setting.Application)}."
                    });
                }

                ui.ReadSettings();
            });
    }
}
