using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.Deployment.Wizard;

/// <summary>
/// One page of the wizard: a titled panel with a line or two about what it asks, then its questions, a label in a muted
/// column and the answer beside it. A question the command line answered shows its answer and the option that gave it,
/// and is not asked; a page with no question left (<see cref="Applies"/>) is passed over.
/// </summary>
internal abstract class WizardPage : View
{
    /// <summary>The column answers start in, after their labels.</summary>
    internal const int AnswerColumn = 18;

    private readonly List<string> _description = [];
    private int _row;

    protected WizardPage(InstallWizard wizard, string title, string stepName)
    {
        Wizard = wizard;
        StepName = stepName;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        Panel = new HvoPanel(wizard.Theme, title) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        Add(Panel);
    }

    public InstallWizard Wizard { get; }

    public InstallWizardSession Session => Wizard.Session;

    public WizardAnswers Answers => Session.Answers;

    public HvoTheme Theme => Wizard.Theme;

    /// <summary>The page's panel.</summary>
    public HvoPanel Panel { get; }

    /// <summary>The page's name in the step bar.</summary>
    public string StepName { get; }

    public virtual string NextLabel => "Next";

    public virtual bool CanGoNext => true;

    public virtual bool CanGoBack => true;

    /// <summary>Whether the page has a question left: the wizard passes over one that has none.</summary>
    public abstract bool Applies { get; }

    /// <summary>Called each time the page is shown.</summary>
    public virtual void Showing()
    {
    }

    /// <summary>Takes the page's answers when Next is pressed: null to go on, or why not.</summary>
    public virtual string? Leave() => null;

    /// <summary>The page as text, for tests and the log.</summary>
    public virtual IReadOnlyList<string> Describe() => _description;

    /// <summary>Gives the focus to the page's first question.</summary>
    public void FocusFirst()
    {
        var first = Panel.Body.SubViews.FirstOrDefault(view => view.CanFocus && view.Visible && view.Enabled);
        if (first is not null)
        {
            first.SetFocus();
        }
        else
        {
            Wizard.NextButton.SetFocus();
        }
    }

    /// <summary>Clears the page, for one that is built each time it is shown.</summary>
    protected void Clear()
    {
        foreach (var view in Panel.Body.SubViews.ToArray())
        {
            Panel.Body.Remove(view);
            view.Dispose();
        }

        _description.Clear();
        _row = 0;
    }

    /// <summary>The page's opening lines, muted.</summary>
    protected void Intro(string text)
    {
        var intro = Wrapped(text, 0, HvoTone.Muted, rows: 2);
        Panel.Body.Add(intro);
        _description.Add(text);
        _row = 3;
    }

    /// <summary>A question answered by typing: its label, and a field holding <paramref name="value"/>, with a hint after or under it.</summary>
    protected TextField Field(string label, string value, int width, string? hint = null)
    {
        var caption = Caption(label);
        var room = Math.Max(8, width);
        var field = Theme.StyleField(new TextField { X = AnswerColumn, Y = _row, Width = room, Text = value }, caption);
        field.TextChanged += (_, _) => Wizard.Say(string.Empty);
        Panel.Body.Add(caption, field);
        _description.Add($"{label}: [{value}]");
        _row++;
        if (hint is not null)
        {
            Hint(hint, room);
        }

        return field;
    }

    /// <summary>A question the command line answered: its label, the answer, and the option that gave it.</summary>
    protected void Given(string label, string value, string option)
    {
        var caption = Caption(label);
        var answer = new Label { X = AnswerColumn, Y = _row, Width = Dim.Fill(), Text = $"{value}  ({option})" };
        answer.SetScheme(Theme.Label(HvoTone.Text, HvoGround.Surface));
        Panel.Body.Add(caption, answer);
        _description.Add($"{label}: {value} (given: {option})");
        _row++;
    }

    /// <summary>One of several answers: its label, then a line for each.</summary>
    protected HvoChoiceList Choice(string label, IReadOnlyList<string> options, int value)
    {
        Panel.Body.Add(Caption(label));
        var choice = new HvoChoiceList(Theme, options, value) { X = AnswerColumn, Y = _row };
        choice.ValueChanged += (_, _) => Wizard.Say(string.Empty);
        Panel.Body.Add(choice);
        _description.Add($"{label}: {string.Join(" | ", choice.Lines)}");
        _row += options.Count;
        return choice;
    }

    /// <summary>A yes-or-no question under the answers.</summary>
    protected HvoToggle Toggle(string text, bool value)
    {
        var toggle = new HvoToggle(Theme, text, value) { X = AnswerColumn, Y = _row };
        toggle.CheckedChanged += (_, _) => Wizard.Say(string.Empty);
        Panel.Body.Add(toggle);
        _description.Add(toggle.Line);
        _row++;
        return toggle;
    }

    /// <summary>A muted line or two under the last answer, in the answers' column.</summary>
    protected Label Hint(string text, int width = 0)
    {
        var hint = Wrapped(text, AnswerColumn, HvoTone.Muted, rows: 2);
        Panel.Body.Add(hint);
        _description.Add(text);
        var rows = TerminalText.Width(text) > Math.Max(width, 40) ? 2 : 1;
        _row += rows;
        hint.Height = rows;
        return hint;
    }

    /// <summary>Skips a row between groups of questions.</summary>
    protected void Gap() => _row++;

    /// <summary>The row the next question goes on.</summary>
    protected int Row => _row;

    private Label Caption(string label)
    {
        var caption = new Label { X = 0, Y = _row, Width = AnswerColumn - 2, Text = label };
        caption.SetScheme(Theme.Label(HvoTone.Muted, HvoGround.Surface));
        return caption;
    }

    private Label Wrapped(string text, int column, HvoTone tone, int rows)
    {
        var label = new Label { X = column, Y = _row, Width = Dim.Fill(), Height = rows, Text = text };
        label.TextFormatter.WordWrap = true;
        label.SetScheme(Theme.Label(tone, HvoGround.Surface));
        return label;
    }
}

/// <summary>The camera's name, and the instance ID this installation is known by. Passed over when <c>--friendly-name</c> gave the name.</summary>
internal sealed class CameraPage : WizardPage
{
    private TextField? _name;

    public CameraPage(InstallWizard wizard)
        : base(wizard, "Camera", "Camera")
    {
    }

    public TextField? NameField => _name;

    public override bool Applies => !Session.Given("--friendly-name");

    public override void Showing()
    {
        Clear();
        Intro("Name the camera: CameraAgent's pages call it this. The instance ID tells this installation apart from others here.");
        _name = Field("Camera name", Answers.FriendlyName, 40, "Up to 80 characters, such as Allsky East.");
        Gap();
        Given("Instance ID", Answers.InstanceId.ToString("D"), Session.InstanceIdSource);
    }

    public override string? Leave()
    {
        var name = _name?.Text ?? Answers.FriendlyName;
        if (InstallWizardSession.NameProblem(name) is { } problem)
        {
            return problem;
        }

        Answers.FriendlyName = name.Trim();
        return null;
    }
}

/// <summary>
/// Where CameraAgent comes from: the release folder and how its image reaches Docker, and the star catalog. Passed over
/// when the command line gave the release (or an image) and the catalog, as <c>install.sh</c> does.
/// </summary>
internal sealed class SoftwarePage : WizardPage
{
    private static readonly ReleaseImageSource[] Sources = [ReleaseImageSource.Archive, ReleaseImageSource.Registry];

    private TextField? _release;
    private HvoChoiceList? _source;
    private TextField? _catalog;

    public SoftwarePage(InstallWizard wizard)
        : base(wizard, "Software", "Software")
    {
    }

    public TextField? ReleaseField => _release;

    public HvoChoiceList? SourceChoice => _source;

    public TextField? CatalogField => _catalog;

    public override bool Applies
        => (!Session.Given("--release") && !Session.Given("--image-ref")) || !Session.Given("--catalog-bundle");

    public override void Showing()
    {
        Clear();
        _release = _catalog = null;
        _source = null;
        Intro("Where CameraAgent comes from: a release's folder (release.json, SHA256SUMS and its image archive) and the star catalog.");
        if (Session.GivenValue("--image-ref") is { } image)
        {
            Given("Image", image, "--image-ref");
        }
        else if (Session.GivenValue("--release") is { } release)
        {
            Given("Release folder", release, "--release");
        }
        else
        {
            _release = Field("Release folder", Answers.Release, 48, "The folder holding the release's files, such as /home/you/hvo-sky-0.9.0.");
            if (Session.GivenValue("--image-source") is { } source)
            {
                Given("Image from", source, "--image-source");
            }
            else
            {
                _source = Choice("Image from", ["the release's image archive", "the registry, by its digest"], Array.IndexOf(Sources, Answers.ImageSource));
            }
        }

        Gap();
        if (Session.GivenValue("--catalog-bundle") is { } catalog)
        {
            Given("Catalog bundle", catalog, "--catalog-bundle");
        }
        else
        {
            _catalog = Field("Catalog bundle", Answers.CatalogBundle, 48, "The unpacked catalog's folder, NAME.bundle.");
        }
    }

    public override string? Leave()
    {
        if (_release is not null)
        {
            if (InstallWizardSession.PathProblem(_release.Text, "the release folder") is { } problem)
            {
                return problem;
            }

            Answers.Release = _release.Text.Trim();
        }

        if (_source is not null)
        {
            Answers.ImageSource = Sources[_source.Value];
        }

        if (_catalog is not null)
        {
            if (InstallWizardSession.PathProblem(_catalog.Text, "the catalog bundle") is { } problem)
            {
                return problem;
            }

            Answers.CatalogBundle = _catalog.Text.Trim();
        }

        return null;
    }
}

/// <summary>
/// The address and port CameraAgent's web pages listen on, and, for an address other computers reach, that they reach it
/// over plain HTTP. Passed over when the command line gave both, with <c>--acknowledge-plaintext-http</c> for such an
/// address.
/// </summary>
internal sealed class NetworkPage : WizardPage
{
    private TextField? _address;
    private TextField? _port;
    private HvoToggle? _acknowledge;

    public NetworkPage(InstallWizard wizard)
        : base(wizard, "Network", "Network")
    {
    }

    public TextField? AddressField => _address;

    public TextField? PortField => _port;

    public HvoToggle? Acknowledgement => _acknowledge;

    public override bool Applies
        => !Session.Given("--bind-address") || !Session.Given("--port")
           || (!InstallWizardSession.IsLoopback(Answers.BindAddress) && !Session.Given("--acknowledge-plaintext-http"));

    public override void Showing()
    {
        Clear();
        _address = _port = null;
        _acknowledge = null;
        Intro("Where CameraAgent's web pages listen. 127.0.0.1 keeps them on this machine; another address lets other computers reach them.");
        if (Session.GivenValue("--bind-address") is { } address)
        {
            Given("Address", address, "--bind-address");
        }
        else
        {
            _address = Field("Address", Answers.BindAddress, 18);
            _address.TextChanged += (_, _) => UpdateAcknowledgement();
        }

        if (Session.GivenValue("--port") is { } port)
        {
            Given("Port", port, "--port");
        }
        else
        {
            _port = Field("Port", Answers.Port, 8);
        }

        Gap();
        if (Session.Given("--acknowledge-plaintext-http"))
        {
            Given("Plain HTTP", "acknowledged", "--acknowledge-plaintext-http");
        }
        else
        {
            _acknowledge = Toggle("Other computers reach it over plain HTTP: I know", Answers.AcknowledgePlaintextHttp);
            UpdateAcknowledgement();
        }
    }

    public override string? Leave()
    {
        var address = _address?.Text ?? Answers.BindAddress;
        var port = _port?.Text ?? Answers.Port;
        var acknowledged = Session.Given("--acknowledge-plaintext-http") || (_acknowledge?.Checked ?? false);
        if (InstallWizardSession.NetworkProblem(address, port, acknowledged) is { } problem)
        {
            return problem;
        }

        Answers.BindAddress = address.Trim();
        Answers.Port = port.Trim();
        Answers.AcknowledgePlaintextHttp = acknowledged && !InstallWizardSession.IsLoopback(address);
        return null;
    }

    // The question is asked only for an address other computers reach.
    private void UpdateAcknowledgement()
    {
        if (_acknowledge is not null)
        {
            _acknowledge.Visible = !InstallWizardSession.IsLoopback(_address?.Text ?? Answers.BindAddress);
        }
    }
}

/// <summary>Where the observatory is, for the sky's geometry and the night's schedule. Passed over when the command line gave all four.</summary>
internal sealed class LocationPage : WizardPage
{
    private TextField? _latitude;
    private TextField? _longitude;
    private TextField? _elevation;
    private TextField? _timeZone;

    public LocationPage(InstallWizard wizard)
        : base(wizard, "Location", "Location")
    {
    }

    public TextField? LatitudeField => _latitude;

    public TextField? LongitudeField => _longitude;

    public TextField? ElevationField => _elevation;

    public TextField? TimeZoneField => _timeZone;

    public override bool Applies
        => !Session.Given("--latitude") || !Session.Given("--longitude") || !Session.Given("--elevation") || !Session.Given("--time-zone");

    public override void Showing()
    {
        Clear();
        _latitude = _longitude = _elevation = _timeZone = null;
        Intro("Where the observatory is: CameraAgent works out the sky, and each night's schedule, from it.");
        _latitude = Ask("Latitude", "--latitude", Answers.Latitude, 12, "degrees, north positive");
        _longitude = Ask("Longitude", "--longitude", Answers.Longitude, 12, "degrees, east positive");
        _elevation = Ask("Elevation", "--elevation", Answers.Elevation, 12, "metres above sea level");
        _timeZone = Ask("Time zone", "--time-zone", Answers.TimeZone, 24, "such as America/Phoenix, or UTC");
    }

    public override string? Leave()
    {
        var latitude = _latitude?.Text ?? Answers.Latitude;
        var longitude = _longitude?.Text ?? Answers.Longitude;
        var elevation = _elevation?.Text ?? Answers.Elevation;
        var timeZone = _timeZone?.Text ?? Answers.TimeZone;
        if (InstallWizardSession.LocationProblem(latitude, longitude, elevation, timeZone) is { } problem)
        {
            return problem;
        }

        Answers.Latitude = latitude.Trim();
        Answers.Longitude = longitude.Trim();
        Answers.Elevation = elevation.Trim();
        Answers.TimeZone = timeZone.Trim();
        return null;
    }

    private TextField? Ask(string label, string option, string value, int width, string hint)
    {
        if (Session.GivenValue(option) is { } given)
        {
            Given(label, given, option);
            return null;
        }

        var field = Field(label, value, width);
        var note = new Label { X = AnswerColumn + width + 2, Y = Row - 1, Width = Dim.Fill(), Text = hint };
        note.SetScheme(Theme.Label(HvoTone.Muted, HvoGround.Surface));
        Panel.Body.Add(note);
        return field;
    }
}

/// <summary>
/// CameraAgent's container limits and where archived replays run. An empty limit keeps the installer's default, which an
/// upgrade raises when the default rises; a limit given is the operator's. Passed over when the command line gave all three.
/// </summary>
internal sealed class LimitsPage : WizardPage
{
    private static readonly CameraAgentReplayProfile[] Profiles = [CameraAgentReplayProfile.InProcess, CameraAgentReplayProfile.LocalRunner];

    private TextField? _cpus;
    private TextField? _memory;
    private HvoChoiceList? _replay;

    public LimitsPage(InstallWizard wizard)
        : base(wizard, "Container limits", "Limits")
    {
    }

    public TextField? CpusField => _cpus;

    public TextField? MemoryField => _memory;

    public HvoChoiceList? ReplayChoice => _replay;

    public override bool Applies => !Session.Given("--cpus") || !Session.Given("--memory") || !Session.Given("--replay-profile");

    public override void Showing()
    {
        Clear();
        _cpus = _memory = null;
        _replay = null;
        Intro("CameraAgent's container limits. Leave one empty for the default, at least 2 CPUs and 4 GiB, which upgrades follow.");
        if (Session.GivenValue("--cpus") is { } cpus)
        {
            Given("CPUs", cpus, "--cpus");
        }
        else
        {
            _cpus = Field("CPUs", Answers.Cpus, 8, "empty for 2; at least 2, such as 2.5");
        }

        if (Session.GivenValue("--memory") is { } memory)
        {
            Given("Memory", memory, "--memory");
        }
        else
        {
            _memory = Field("Memory", Answers.Memory, 8, "empty for 4g; at least 4g, such as 6g or 6144m");
        }

        Gap();
        if (Session.GivenValue("--replay-profile") is { } profile)
        {
            Given("Replays run", profile, "--replay-profile");
        }
        else
        {
            _replay = Choice("Replays run", ["in CameraAgent's own process", "in a separate local runner"], Array.IndexOf(Profiles, Answers.ReplayProfile));
        }
    }

    public override string? Leave()
    {
        var cpus = _cpus?.Text ?? Answers.Cpus;
        var memory = _memory?.Text ?? Answers.Memory;
        if (InstallWizardSession.LimitsProblem(cpus, memory) is { } problem)
        {
            return problem;
        }

        Answers.Cpus = cpus.Trim();
        Answers.Memory = memory.Trim();
        if (_replay is not null)
        {
            Answers.ReplayProfile = Profiles[_replay.Value];
        }

        return null;
    }
}

/// <summary>
/// The owner, who signs in to CameraAgent's pages first, and how their first password is set. No password is typed here,
/// shown or saved: the documented one is changed at the first sign-in, a generated one goes to a file only the owner can
/// read, or the operator names a file. Passed over when the command line gave the email and the password's source.
/// </summary>
internal sealed class OwnerPage : WizardPage
{
    private static readonly OwnerPasswordChoice[] Choices = [OwnerPasswordChoice.Default, OwnerPasswordChoice.Generate, OwnerPasswordChoice.File];

    private TextField? _email;
    private HvoChoiceList? _password;
    private TextField? _file;

    public OwnerPage(InstallWizard wizard)
        : base(wizard, "Owner sign-in", "Owner")
    {
    }

    public TextField? EmailField => _email;

    public HvoChoiceList? PasswordChoice => _password;

    public TextField? FileField => _file;

    public override bool Applies
        => !Session.Given("--owner-email") || !(Session.Given("--password-file") || Session.Given("--generate-password"));

    public override void Showing()
    {
        Clear();
        _email = _file = null;
        _password = null;
        Intro("The owner signs in to CameraAgent's pages first. The wizard never asks for a password, and never shows or saves one.");
        if (Session.GivenValue("--owner-email") is { } email)
        {
            Given("Owner email", email, "--owner-email");
        }
        else
        {
            _email = Field("Owner email", Answers.OwnerEmail, 36, $"empty for {OwnerCredentialDefaults.Email}");
        }

        Gap();
        if (Session.GivenValue("--password-file") is { } file)
        {
            Given("First password", file, "--password-file");
        }
        else if (Session.Given("--generate-password"))
        {
            Given("First password", "generated", "--generate-password");
        }
        else
        {
            _password = Choice(
                "First password",
                [$"{OwnerCredentialDefaults.UserName} / {OwnerCredentialDefaults.Password}, changed at first sign-in", "generated into a file only you can read", "read from a file you name"],
                Array.IndexOf(Choices, Answers.Password));
            _file = Field("Password file", Answers.PasswordFile, 40);
            _password.ValueChanged += (_, _) => UpdateFile();
            UpdateFile();
        }
    }

    public override string? Leave()
    {
        var email = _email?.Text ?? Answers.OwnerEmail;
        var choice = _password is null ? Answers.Password : Choices[_password.Value];
        var file = _file?.Text ?? Answers.PasswordFile;
        if (InstallWizardSession.OwnerProblem(email, choice, file) is { } problem)
        {
            return problem;
        }

        Answers.OwnerEmail = email.Trim();
        Answers.Password = choice;
        Answers.PasswordFile = file.Trim();
        return null;
    }

    private void UpdateFile()
    {
        if (_file is not null && _password is not null)
        {
            var shown = Choices[_password.Value] == OwnerPasswordChoice.File;
            _file.Visible = shown;
            foreach (var label in Panel.Body.SubViews.OfType<Label>().Where(label => label.Text == "Password file"))
            {
                label.Visible = shown;
            }
        }
    }
}

/// <summary>
/// The plan: every change the install makes, checked against this machine exactly as
/// <c>hvo-sky-install cameraagent install --answers FILE --plan</c> checks it, then where the answers are saved. Install
/// goes ahead only when the plan can be carried out; a refusal says why, that nothing was changed, and Back changes the
/// answers. Always shown.
/// </summary>
internal sealed class ReviewPage : WizardPage
{
    private readonly HvoTextLines _plan;
    private readonly Label _saveCaption;
    private readonly TextField _savePath;
    private IReadOnlyList<HvoLine> _lines = [];
    private WizardPlanCheck? _check;
    private bool _checking;
    private int _visit;

    public ReviewPage(InstallWizard wizard)
        : base(wizard, "Review the plan", "Review")
    {
        _plan = new HvoTextLines(wizard.Theme) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2) };
        _saveCaption = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = AnswerColumn - 2, Text = "Save answers to" };
        _saveCaption.SetScheme(wizard.Theme.Label(HvoTone.Muted, HvoGround.Surface));
        _savePath = wizard.Theme.StyleField(new TextField { X = AnswerColumn, Y = Pos.AnchorEnd(1), Width = Dim.Fill() }, _saveCaption);
        Panel.Body.Add(_plan, _saveCaption, _savePath);
    }

    public override string NextLabel => "Install";

    public override bool CanGoNext => !_checking && _check is { CanInstall: true };

    public override bool Applies => true;

    public WizardPlanCheck? Check => _check;

    public bool Checking => _checking;

    public HvoTextLines Plan => _plan;

    public TextField SavePath => _savePath;

    public override void Showing()
    {
        _savePath.Text = Session.AnswersPath;
        _check = null;
        _checking = true;
        var visit = ++_visit;
        Show([new($"Checking this machine against the plan, as {InstallerProgram.Current} cameraagent install --answers FILE --plan does...", HvoTone.Muted)]);
        Wizard.Notice.Show(HvoNotice.Info, "Checking the plan. Nothing is changed.", "[CHECKING]");
        Wizard.Run(
            Session.CheckAsync,
            (check, error) =>
            {
                if (visit != _visit)
                {
                    // A check from an earlier visit, for answers that may since have changed.
                    return;
                }

                _checking = false;
                _check = check ?? new WizardPlanCheck(null, Redaction.SafeDiagnostic($"The plan could not be made: {error?.Message}"), WizardPlanCheck.Failure, []);
                Show(Lines(_check));
                if (_check.CanInstall)
                {
                    var changes = _check.Result!.Plan?.HasChanges ?? true;
                    Wizard.Notice.Show(
                        changes ? HvoNotice.Success : HvoNotice.Info,
                        changes ? "The plan can be carried out. Install saves the answers and carries it out." : "Nothing to change: this is already installed as the answers say.");
                }
                else
                {
                    Wizard.Notice.Show(HvoNotice.Danger, $"{_check.Problem} Nothing was changed.", _check.Word);
                }

                Wizard.Refresh();
            });
    }

    public override string? Leave()
    {
        if (InstallWizardSession.PathProblem(_savePath.Text, "where to save the answers") is { } problem)
        {
            return problem;
        }

        try
        {
            Session.SaveAnswers(_savePath.Text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"The answers could not be saved at {_savePath.Text.Trim()}: {Redaction.SafeDiagnostic(exception.Message)}";
        }

        Wizard.Finish();
        return null;
    }

    public override IReadOnlyList<string> Describe() => [.. _lines.Select(line => line.Text)];

    /// <summary>The review as lines: the plan and its warnings, or the problem and what to do.</summary>
    internal IReadOnlyList<HvoLine> Lines(WizardPlanCheck check)
    {
        var lines = new List<HvoLine>();
        if (check.Result is { } result)
        {
            lines.Add(new($"Install {result.FriendlyName}, instance {result.InstanceId:D}, at {result.Url}:", HvoTone.Text, Bold: true));
            lines.Add(new(string.Empty));
            if (result.Plan is { } plan)
            {
                foreach (var item in plan.Items)
                {
                    lines.Add(new(DeploymentPlanText.Lines(new DeploymentPlan([item], string.Empty))[0], Tone(item.Action)));
                }

                lines.Add(new(plan.Summary, HvoTone.Text, Bold: true));
            }

            foreach (var warning in result.Warnings ?? [])
            {
                lines.Add(new($"[WARNING] {Redaction.SafeDiagnostic(warning.Message)}", HvoTone.Warning));
            }

            lines.Add(new(string.Empty));
            lines.Add(new($"Install saves the answers and runs: {InstallerProgram.Current} {InstallWizardRunner.Quote(Session.Arguments(Session.AnswersPath))}", HvoTone.Muted));
        }
        else
        {
            lines.Add(new($"{check.Word} {check.Problem}", HvoTone.Danger, Bold: true));
            lines.AddRange(check.Details.Select(detail => new HvoLine(detail, HvoTone.Text)));
            lines.Add(new(string.Empty));
            lines.Add(new("Nothing was changed. Back changes the answers; Esc cancels.", HvoTone.Muted));
        }

        return lines;
    }

    private static HvoTone Tone(DeploymentPlanAction action) => action switch
    {
        DeploymentPlanAction.Create => HvoTone.Success,
        DeploymentPlanAction.Change => HvoTone.Warning,
        DeploymentPlanAction.Remove => HvoTone.Danger,
        _ => HvoTone.Muted
    };

    private void Show(IReadOnlyList<HvoLine> lines)
    {
        _lines = lines;
        _plan.Show(lines);
    }
}
