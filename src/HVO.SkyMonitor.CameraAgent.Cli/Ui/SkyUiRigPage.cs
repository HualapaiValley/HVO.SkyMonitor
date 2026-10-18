using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// F3 Camera &amp; rig: the active and staged rig revisions, the revisions to stage from, and the camera group of the
/// settings catalogue. Stage previews the chosen revision and asks for the same acknowledgement as the Camera &amp; rig
/// page's checkbox; Apply restarts CameraAgent to apply the staged revision, then checks that it became the active one.
/// </summary>
internal sealed class SkyUiRigPage : SkyUiPage
{
    private readonly SkyUiFields _active;
    private readonly SkyUiFields _staged;
    private readonly SkyUiTable _revisions;
    private readonly SkyUiFields _camera;
    private readonly HvoPanel _activePanel;
    private readonly HvoPanel _stagedPanel;
    private readonly HvoPanel _revisionsPanel;
    private readonly HvoPanel _cameraPanel;
    private readonly Button _stage;
    private readonly Button _apply;
    private readonly Button _changeCamera;

    public SkyUiRigPage(SkyTerminalUi ui)
        : base(ui, "Camera & rig")
    {
        _active = new SkyUiFields(Theme, 10);
        _staged = new SkyUiFields(Theme, 10);
        _revisions = new SkyUiTable(Theme,
            [new SkyUiColumn("REVISION", 22), new SkyUiColumn("PROFILE", 14), new SkyUiColumn("#", 4), new SkyUiColumn("MODULE", 12), new SkyUiColumn("SENSOR", 14), new SkyUiColumn("STATE", 8)]);
        _camera = new SkyUiFields(Theme, 14);
        _activePanel = AddPanel("Active rig", _active);
        _stagedPanel = AddPanel("Staged", _staged);
        _revisionsPanel = AddPanel("Revisions", _revisions);
        _cameraPanel = AddPanel("Camera settings", _camera);
        _revisions.SelectionChanged += (_, _) => UpdateButtons();
        _revisions.RowActivated += (_, _) => Stage();
        _stage = AddButton("Stage", Stage, primary: true);
        _apply = AddButton("Apply", Apply);
        _changeCamera = AddButton("Change camera", ChangeCamera);
        AddButton("Refresh", Refresh);
    }

    public override string Help => ChangeRefusal ?? (Staged is { } staged
        ? $"Rig revision {staged} is staged: Apply restarts CameraAgent to apply it."
        : "Choose a revision and Stage it; it applies when CameraAgent next starts.");

    public SkyUiTable RevisionTable => _revisions;

    private string? Active => Ui.Data.Rigs.HasValue ? SkyJson.Text(Ui.Data.Rigs.Value, "selection", "activeRevisionId") : null;

    private string? Staged => Ui.Data.Rigs.HasValue ? SkyJson.Text(Ui.Data.Rigs.Value, "selection", "pendingRevisionId") : null;

    private SkySettingField? CameraField => Ui.Data.Settings.Value?.FindGroup("camera")?.Fields is { Count: > 0 } fields ? fields[0] : null;

    public override void Refresh()
    {
        Ui.Read(Ui.Data.Rigs, SkyRoutes.RigProfiles, static element => element);
        Ui.Read(Ui.Data.Info, SkyRoutes.Info, SkyApiClient.Read<SkyInfo>);
        Ui.ReadSettings();
    }

    public override void FocusFirst() => _revisions.SetFocus();

    public override void Arrange(int width, int height, bool wide)
    {
        var left = (width - 3) / 2;
        var top = wide ? 8 : 7;
        Place(_activePanel, 1, 0, left, top);
        Place(_stagedPanel, 2 + left, 0, width - 3 - left, top);
        var rows = wide ? Math.Max(6, height - top - 6) : 6;
        Place(_revisionsPanel, 1, top, width - 2, rows);
        Place(_cameraPanel, 1, top + rows, width - 2, 3);
        PlaceButtons(Math.Min(height - 1, top + rows + 4));
    }

    public override void DataChanged()
    {
        var reading = Ui.Data.Rigs;
        _activePanel.Caption = Titled("Active rig", reading);
        _stagedPanel.Caption = Titled("Staged", reading);
        _revisionsPanel.Caption = Titled("Revisions", reading);
        _cameraPanel.Caption = Titled("Camera settings", Ui.Data.Settings);
        _camera.Fields = CameraField is { } field
            ? [new SkyUiField(field.Setting.Title, $"{field.DisplayValue}  ({field.State?.Source ?? "?"}{(field.CanWrite ? string.Empty : $"; read-only: {field.ReadOnlyReason}")})", field.CanWrite ? HvoTone.Text : HvoTone.Muted)]
            : [Pending(Ui.Data.Settings, "the camera settings")];
        if (!reading.HasValue)
        {
            _active.Fields = [Ui.Connection is null ? new SkyUiField(string.Empty, "Not signed in: F5 System.", HvoTone.Warning) : Pending(reading, "the rig")];
            _staged.Fields = [];
            _revisions.Show([]);
            UpdateButtons();
            return;
        }

        var rigs = reading.Value;
        var revisions = SkyJson.Items(rigs, "revisions");
        _active.Fields = Describe(revisions, Active, SkyJson.Text(rigs, "activeRuntimeFailure"));
        _staged.Fields = Staged is null
            ? [new SkyUiField(string.Empty, "No rig revision is staged.", HvoTone.Muted)]
            : Describe(revisions, Staged, SkyJson.Text(rigs, "pendingRuntimeFailure"));
        _revisions.Show([.. revisions.Select(revision =>
        {
            var id = SkyJson.Text(revision, "revisionId") ?? "?";
            var state = id == Active ? "active" : id == Staged ? "staged" : string.Empty;
            return new SkyUiRow(id,
                [id, SkyJson.Text(revision, "profileId") ?? string.Empty, SkyCliFormat.Count(SkyJson.Integer(revision, "revisionNumber")),
                    SkyJson.Text(revision, "moduleType") ?? string.Empty, SkyJson.Text(revision, "sensorName") ?? string.Empty, state],
                state.Length > 0 ? HvoTone.Accent : HvoTone.Text);
        })]);
        UpdateButtons();
    }

    public override string Describe()
        => string.Join("\n\n", Describe(_activePanel.Caption, _active, 60), Describe(_stagedPanel.Caption, _staged, 60),
            Describe(_revisionsPanel.Caption, _revisions, 100), Describe(_cameraPanel.Caption, _camera, 76));

    private static List<SkyUiField> Describe(IReadOnlyList<JsonElement> revisions, string? id, string? failure)
    {
        if (id is null)
        {
            return [new SkyUiField(string.Empty, "None.", HvoTone.Muted)];
        }

        var revision = revisions.FirstOrDefault(candidate => SkyJson.Text(candidate, "revisionId") == id);
        var fields = new List<SkyUiField> { new("Revision", id, HvoTone.Accent) };
        if (revision.ValueKind == JsonValueKind.Object)
        {
            fields.Add(new("Profile", $"{SkyJson.Text(revision, "profileId")} #{SkyCliFormat.Count(SkyJson.Integer(revision, "revisionNumber"))}"));
            fields.Add(new("Module", SkyJson.Text(revision, "moduleType") ?? "unknown"));
            fields.Add(new("Sensor", SkyJson.Text(revision, "sensorName") ?? "unknown"));
        }

        if (failure is not null)
        {
            fields.Add(new("Failure", failure, HvoTone.Danger));
        }

        return fields;
    }

    private void UpdateButtons()
    {
        var chosen = _revisions.SelectedRow?.Key;
        _stage.Enabled = CanChange && Ui.Data.Rigs.HasValue && chosen is not null && chosen != Active && chosen != Staged && Staged is null;
        _apply.Enabled = CanChange && Staged is not null && Ui.Data.Info.Value?.Restart.Supervised == true;
        _changeCamera.Enabled = CanChange && CameraField is { CanWrite: true };
        Ui.Hint(Help);
    }

    private void Stage()
    {
        if (!_stage.Enabled || _revisions.SelectedRow?.Key is not { } id || Ui.Client is not { } client)
        {
            return;
        }

        var version = SkyJson.Integer(Ui.Data.Rigs.Value, "selection", "version");
        Ui.Say(HvoNotice.Info, $"Previewing rig revision {id}...");
        Ui.Run(
            token => client.GetAsync(SkyRoutes.RigPreview + Uri.EscapeDataString(id), token),
            (preview, error) =>
            {
                if (error is not null)
                {
                    Ui.Say(HvoNotice.Danger, $"Rig revision {id} could not be previewed: {SkyCliContext.Classify(error).Message}");
                    return;
                }

                if (SkyJson.Boolean(preview, "valid") != true)
                {
                    Ui.Say(HvoNotice.Danger, $"Rig revision {id} cannot be staged: CameraAgent's preview refused it ({SkyJson.Text(preview, "failure") ?? "no reason given"}).", "[REFUSED]");
                    return;
                }

                Ui.Say(HvoNotice.Info, $"Rig revision {id} passed CameraAgent's preview against the active schedule.");
                Ui.OpenPrompt(new SkyUiPromptSpec($"Stage rig {id}",
                [
                    new($"Rig revision {id} passed the preview against schedule revision {SkyJson.Text(preview, "scheduleRevisionId")}."),
                    new("It is unvalidated at runtime: it takes effect when CameraAgent next starts, and its camera may fail to initialize then.", HvoTone.Warning)
                ], "Stage", _ =>
                {
                    Ui.Run(
                        (operation, token) => operation.SendChangeAsync(client, HttpMethod.Post, SkyRoutes.RigStage, new
                        {
                            revisionId = id,
                            expectedVersion = version,
                            acknowledgeUnvalidated = true,
                            expectedScheduleRevisionId = SkyJson.Text(preview, "scheduleRevisionId"),
                            expectedScheduleProfileSha256 = SkyJson.Text(preview, "scheduleProfileSha256")
                        }, $"the staging of rig revision {id}", token),
                        (_, failure, operation) =>
                        {
                            Ui.SayOutcome(operation, failure,
                                $"Staged rig revision {id}. It takes effect when CameraAgent next starts: Apply restarts it now.",
                                $"Rig revision {id} was not staged");
                            Refresh();
                        });
                    return null;
                })
                {
                    Acknowledge = "Stage it, unvalidated at runtime, for the next CameraAgent start"
                });
            });
    }

    private void Apply()
    {
        if (!_apply.Enabled || Staged is not { } staged)
        {
            return;
        }

        Ui.OpenPrompt(new SkyUiPromptSpec("Apply the staged rig",
        [
            new($"Restart CameraAgent at {Ui.Connection?.Url} to apply rig revision {staged}?"),
            new("Capture stops until it is back. The interface waits for the new process, then until the rig is active or its camera fails.", HvoTone.Muted)
        ], "Restart", _ =>
        {
            // As 'hvo-sky rig apply': the staged rig becomes active only once its camera has initialized, so the rig is read
            // until it is, CameraAgent reports a failure, or the deadline passes.
            Ui.Restart(
                $"the restart that applies rig revision {staged}",
                (operation, client, token) => SkyCli.WaitForRigAsync(operation, client, staged, token),
                (outcome, error, operation) => Ui.SayOutcome(operation, error,
                    error is null ? $"Rig revision {staged} is active. CameraAgent restarted at {SkyCliFormat.Time(outcome!.After!.Host.ProcessStartedUtc)}." : string.Empty,
                    "CameraAgent was not restarted"));
            return null;
        }));
    }

    private void ChangeCamera()
    {
        if (_changeCamera.Enabled && CameraField is { } field)
        {
            SkyUiSettingChange.Open(Ui, field);
        }
    }
}
