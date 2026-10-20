using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// F4 Schedule: the active and staged capture-schedule revisions, whether capture is admitted now, the revisions, and the
/// next capture windows. Apply staged activates the staged revision, and Activate the chosen one, at the next capture
/// boundary; an older revision goes back by rollback, as <c>hvo-sky schedule apply</c> decides. Staging a revision from a
/// capture profile file stays with <c>hvo-sky schedule stage</c>.
/// </summary>
internal sealed class SkyUiSchedulePage : SkyUiPage
{
    private readonly SkyUiFields _active;
    private readonly SkyUiFields _now;
    private readonly SkyUiTable _revisions;
    private readonly SkyUiTable _upcoming;
    private readonly HvoPanel _activePanel;
    private readonly HvoPanel _nowPanel;
    private readonly HvoPanel _revisionsPanel;
    private readonly HvoPanel _upcomingPanel;
    private readonly Button _applyStaged;
    private readonly Button _activate;

    public SkyUiSchedulePage(SkyTerminalUi ui)
        : base(ui, "Schedule")
    {
        _active = new SkyUiFields(Theme, 10);
        _now = new SkyUiFields(Theme, 12);
        _revisions = new SkyUiTable(Theme, [new SkyUiColumn("REVISION", 22), new SkyUiColumn("#", 4), new SkyUiColumn("STATE", 8)]);
        _upcoming = new SkyUiTable(Theme, [new SkyUiColumn("FROM", 14), new SkyUiColumn("TO", 14), new SkyUiColumn("SETPOINT", 16)]) { Empty = "No capture window ahead." };
        _activePanel = AddPanel("Active schedule", _active);
        _nowPanel = AddPanel("Capture now", _now);
        _revisionsPanel = AddPanel("Revisions", _revisions);
        _upcomingPanel = AddPanel("Upcoming", _upcoming);
        _revisions.SelectionChanged += (_, _) => UpdateButtons();
        _revisions.RowActivated += (_, _) => Activate();
        _applyStaged = AddButton("Apply staged", ApplyStaged, primary: true);
        _activate = AddButton("Activate", Activate);
        AddButton("Refresh", Refresh);
    }

    public override string Help => ChangeRefusal
        ?? "A revision applies at the next capture boundary. 'hvo-sky schedule stage FILE' stages one from a profile.";

    public SkyUiTable RevisionTable => _revisions;

    private JsonElement Schedule => Ui.Data.Schedule.Value;

    private string? ActiveId => Ui.Data.Schedule.HasValue ? SkyJson.Text(Schedule, "activeRevision", "revisionId") : null;

    private string? StagedId => Ui.Data.Schedule.HasValue ? SkyJson.Text(Schedule, "pendingRevision", "revisionId") : null;

    public override void Refresh() => Ui.Read(Ui.Data.Schedule, SkyRoutes.Schedule, static element => element);

    public override void FocusFirst() => _revisions.SetFocus();

    public override void Arrange(int width, int height, bool wide)
    {
        var left = (width - 3) / 2;
        Place(_activePanel, 1, 0, left, 6);
        Place(_nowPanel, 2 + left, 0, width - 3 - left, 6);
        var rows = wide ? Math.Max(8, height - 6 - 3) : 8;
        // The revisions need less room than the windows, whose three columns fit 80 columns only so.
        var revisions = wide ? left : 31;
        Place(_revisionsPanel, 1, 6, revisions, rows);
        Place(_upcomingPanel, 2 + revisions, 6, width - 3 - revisions, rows);
        PlaceButtons(Math.Min(height - 1, 6 + rows + 1));
    }

    public override void DataChanged()
    {
        var reading = Ui.Data.Schedule;
        _activePanel.Caption = Titled("Active schedule", reading);
        _nowPanel.Caption = Titled("Capture now", reading);
        _revisionsPanel.Caption = Titled("Revisions", reading);
        _upcomingPanel.Caption = Titled("Upcoming", reading);
        if (!reading.HasValue)
        {
            _active.Fields = [Ui.Connection is null ? new SkyUiField(string.Empty, "Not signed in: F5 System.", HvoTone.Warning) : Pending(reading, "the schedule")];
            _now.Fields = [];
            _revisions.Show([]);
            _upcoming.Show([]);
            UpdateButtons();
            return;
        }

        var schedule = reading.Value;
        var active = SkyJson.Member(schedule, "activeRevision");
        _active.Fields = active is { } revision
            ?
            [
                new("Revision", SkyJson.Text(revision, "revisionId") ?? "unknown", HvoTone.Accent),
                new("Number", SkyCliFormat.Count(SkyJson.Integer(revision, "revisionNumber"))),
                new("Source", SkyJson.Text(revision, "source") ?? "unknown"),
                new("Created", SkyCliFormat.Time(SkyJson.Time(revision, "createdUtc")))
            ]
            : [new SkyUiField(string.Empty, "No active revision.", HvoTone.Warning)];
        var admitted = SkyJson.Boolean(schedule, "decision", "admitted");
        _now.Fields =
        [
            new("Capture", admitted switch { true => "admitted", false => "not admitted", null => "unknown" }, admitted == true ? HvoTone.Success : HvoTone.Warning),
            new("Next change", SkyCliFormat.Time(SkyJson.Time(schedule, "decision", "nextTransitionUtc"))),
            new("Staged", StagedId ?? "none", StagedId is null ? HvoTone.Muted : HvoTone.Info),
            new("Overrides", SkyCliFormat.Count(SkyJson.Items(schedule, "overrides").Count))
        ];
        var rows = new List<SkyUiRow>();
        if (SkyJson.Member(schedule, "pendingRevision") is { } pending)
        {
            rows.Add(Row(pending, "staged", HvoTone.Info));
        }

        if (active is { } current)
        {
            rows.Add(Row(current, "active", HvoTone.Accent));
        }

        rows.AddRange(SkyJson.Items(schedule, "history").Select(item => Row(item, string.Empty, HvoTone.Text)).Where(row => rows.All(seen => seen.Key != row.Key)));
        _revisions.Show(rows);
        var now = Ui.Now;
        _upcoming.Show([.. SkyJson.Items(schedule, "preview", "intervals")
            .Where(interval => SkyJson.Time(interval, "endUtc") is { } end && end > now)
            .Select((interval, index) => new SkyUiRow($"{index}",
                [Short(SkyJson.Time(interval, "startUtc")), Short(SkyJson.Time(interval, "endUtc")), SkyJson.Text(interval, "setpointProfileId") ?? string.Empty]))]);
        UpdateButtons();
    }

    public override string Describe()
        => string.Join("\n\n", Describe(_activePanel.Caption, _active, 60), Describe(_nowPanel.Caption, _now, 60),
            Describe(_revisionsPanel.Caption, _revisions, 60), Describe(_upcomingPanel.Caption, _upcoming, 60));

    private static SkyUiRow Row(JsonElement revision, string state, HvoTone tone)
    {
        var id = SkyJson.Text(revision, "revisionId") ?? "?";
        return new SkyUiRow(id, [id, SkyCliFormat.Count(SkyJson.Integer(revision, "revisionNumber")), state], tone);
    }

    // A window's time without its year, so FROM and TO fit side by side at 80 columns.
    private static string Short(DateTimeOffset? time) => time is { } at ? SkyCliFormat.Time(at)[5..16].Replace('T', ' ') + "Z" : "?";

    private void UpdateButtons()
    {
        var chosen = _revisions.SelectedRow?.Key;
        _applyStaged.Enabled = CanChange && StagedId is not null;
        _activate.Enabled = CanChange && chosen is not null && chosen != ActiveId;
        Ui.Hint(Help);
    }

    private void ApplyStaged()
    {
        if (_applyStaged.Enabled && StagedId is { } staged)
        {
            Open(staged);
        }
    }

    private void Activate()
    {
        if (_activate.Enabled && _revisions.SelectedRow?.Key is { } chosen)
        {
            Open(chosen);
        }
    }

    private void Open(string id)
    {
        var schedule = Schedule;
        var version = SkyJson.Integer(schedule, "stateVersion");
        bool rollback;
        try
        {
            // Forward or back from the revision numbers CameraAgent reports, never guessed, as 'hvo-sky schedule apply'.
            rollback = SkyScheduleDirection.IsRollback(schedule, id);
        }
        catch (SkyCliOutcomeException refusal)
        {
            Ui.Say(HvoNotice.Danger, refusal.Message, "[REFUSED]");
            return;
        }

        Ui.OpenChangePrompt(new SkyUiPromptSpec("Schedule / Review",
        [
            new($"Activate schedule revision {id}{(rollback ? ", going back from the active revision" : string.Empty)}?"),
            new("It takes effect at the next capture boundary: the capture in progress finishes under the active revision.", HvoTone.Muted)
        ], rollback ? "Roll back" : "Activate", answers =>
        {
            var reason = answers.Fields[0].Trim();
            if (reason.Length > SkySettingsContract.MaximumReasonLength)
            {
                return $"The reason takes at most {SkySettingsContract.MaximumReasonLength} characters.";
            }

            if (Ui.Client is not { } client)
            {
                return "Not signed in.";
            }

            Ui.Run(
                (operation, token) => operation.SendChangeAsync(client, HttpMethod.Post, rollback ? SkyRoutes.ScheduleRollback : SkyRoutes.ScheduleActivate,
                    new { revisionId = id, expectedVersion = version, reason = reason.Length == 0 ? null : reason },
                    SkyScheduleDirection.Describe(id, rollback), token),
                (_, error, operation) =>
                {
                    Ui.SayOutcome(operation, error, $"Schedule revision {id} is active from the next capture boundary.",
                        $"Schedule revision {id} was not activated");
                    Refresh();
                });
            return null;
        })
        {
            Fields = [new SkyUiPromptField("Reason")]
        });
    }
}
