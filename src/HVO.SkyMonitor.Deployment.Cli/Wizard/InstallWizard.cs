using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.Deployment.Wizard;

/// <summary>How the wizard ended.</summary>
internal enum InstallWizardResult
{
    /// <summary>Esc, Ctrl+C or a signal: nothing was changed.</summary>
    Cancelled,

    /// <summary>Install: the answers are saved, and the installer runs them.</summary>
    Install
}

/// <summary>
/// The installer's wizard (install contract section 5, #1317), in HVO Dark as the RoofController #158 terminal prototype
/// draws it: the installer's name and the step on a bar at the top, a notice band under it, the page in a titled panel
/// (with every answer so far in a second panel on a terminal 110 or more columns wide), Back and Next under it, the steps
/// on a bar, and the keys at the foot. It fits 80 x 24 and uses the room 120 x 32 gives.
/// <para>
/// Its pages are the install's questions, the review of the plan, and nothing else: a page whose every question the
/// command line answered is passed over. It changes nothing. Install saves the answers and closes the wizard, and the
/// installer then runs <c>cameraagent install --answers FILE</c> on the terminal, where <c>sudo</c> may ask for a
/// password; Esc or Ctrl+C on any page closes it with nothing changed.
/// </para>
/// </summary>
internal sealed class InstallWizard : IDisposable
{
    /// <summary>The width from which the answers so far show beside the page.</summary>
    internal const int SummaryWidth = 110;

    /// <summary>The keys of the key bar, and what each does.</summary>
    internal static readonly (string Key, string Name)[] Keys = [("Enter", "next"), ("Alt+B", "back"), ("Tab", "move"), ("Esc", "cancel")];

    private readonly IApplication _app;
    private readonly HvoHeaderBar _header;
    private readonly HvoNoticeBar _notice;
    private readonly View _pageArea;
    private readonly HvoPanel _summary;
    private readonly HvoTextLines _summaryLines;
    private readonly Button _back;
    private readonly Button _next;
    private readonly Label _message;
    private readonly HvoTabBar _steps;
    private readonly List<WizardPage> _pages;
    private readonly CancellationTokenSource _closing = new();
    private int _noticeRows = 1;
    private int _index = -1;
    private int _pending;
    private bool _closed;

    public InstallWizard(IApplication app, InstallWizardSession session, HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(theme);
        _app = app;
        Session = session;
        Theme = theme;

        Window = new Window { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), BorderStyle = LineStyle.None };
        Window.SetScheme(theme.Page);
        _header = new HvoHeaderBar(theme)
        {
            Heading = "HVO SkyMonitor installer",
            Context = $"hvo-sky-install {InstallerVersion(session.Version)} installs CameraAgent on this machine",
            Note = "Nothing changes until Install"
        };
        _notice = new HvoNoticeBar(theme) { X = 0, Y = 2 };
        _pageArea = new View { X = 1, Y = 3, Width = Dim.Fill(1), Height = Dim.Fill(4), CanFocus = true };
        _summary = new HvoPanel(theme, "Answers") { X = Pos.AnchorEnd(41), Y = 3, Width = 40, Height = Dim.Fill(4), Visible = false };
        _summaryLines = new HvoTextLines(theme) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = false };
        _summary.Body.CanFocus = false;
        _summary.CanFocus = false;
        _summary.Body.Add(_summaryLines);
        _back = theme.CreateButton("_Back");
        _back.X = 2;
        _back.Y = Pos.AnchorEnd(4);
        _back.Accepting += (_, e) =>
        {
            e.Handled = true;
            Back();
        };
        _next = theme.CreateButton("_Next", primary: true);
        _next.X = Pos.Right(_back) + 2;
        _next.Y = Pos.AnchorEnd(4);
        _next.Accepting += (_, e) =>
        {
            e.Handled = true;
            Next();
        };
        _message = new Label { X = Pos.Right(_next) + 2, Y = Pos.AnchorEnd(4), Width = Dim.Fill(1), Height = 2 };
        _message.TextFormatter.WordWrap = true;
        _message.SetScheme(theme.Label(HvoTone.Muted));
        _steps = new HvoTabBar(theme) { X = 0, Y = Pos.AnchorEnd(2) };
        var keys = new HvoKeyBar(theme) { X = 0, Y = Pos.AnchorEnd(1), Keys = Keys };
        Window.Add(_header, _notice, _pageArea, _summary, _back, _next, _message, _steps, keys);
        Window.FrameChanged += (_, _) => Arrange();
        _notice.Changed += (_, _) => Arrange();

        _pages =
        [
            new CameraPage(this),
            new SoftwarePage(this),
            new NetworkPage(this),
            new LocationPage(this),
            new LimitsPage(this),
            new OwnerPage(this),
            new ReviewPage(this)
        ];
        _app.Keyboard.KeyDown += OnKeyDown;
        if (session.SavedProblem is { } problem)
        {
            _notice.Show(HvoNotice.Warning, $"The saved answers could not be read, so the questions start from the defaults: {problem}");
        }

        ShowPage(Following(-1) ?? _pages.Count - 1);
    }

    public Window Window { get; }

    public InstallWizardSession Session { get; }

    public HvoTheme Theme { get; }

    /// <summary>How the wizard ended: cancelled until Install.</summary>
    public InstallWizardResult Result { get; private set; } = InstallWizardResult.Cancelled;

    /// <summary>Work still on its way (tests wait for none).</summary>
    public int PendingOperations => Volatile.Read(ref _pending);

    public WizardPage Page => _pages[_index];

    public int PageIndex => _index;

    public IReadOnlyList<WizardPage> Pages => _pages;

    public string Message => _message.Text;

    public HvoNoticeBar Notice => _notice;

    public HvoHeaderBar Header => _header;

    public HvoTabBar Steps => _steps;

    public Button NextButton => _next;

    public Button BackButton => _back;

    /// <summary>Whether the answers so far show beside the page.</summary>
    public bool SummaryShown => _summary.Visible;

    /// <summary>The answers so far as the panel beside the page shows them, wrapped to its width.</summary>
    internal HvoTextLines SummaryLines => _summaryLines;

    /// <summary>The rows the notice band has: two when its text needs them, so it is never cut.</summary>
    public int NoticeRows => _noticeRows;

    /// <summary>Ended when the wizard closes: work it started stops.</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>The pages that apply to the answers so far, the shown one always among them.</summary>
    public IReadOnlyList<WizardPage> Applying => [.. _pages.Where(page => page == Page || page.Applies)];

    /// <summary>The installer's version without its commit.</summary>
    public static string InstallerVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Split('+')[0];
    }

    /// <summary>Shows page <paramref name="index"/>, which gets ready for the answers so far.</summary>
    public void ShowPage(int index)
    {
        if (_index >= 0)
        {
            _pageArea.Remove(Page);
        }

        _index = index;
        Say(string.Empty);
        if (Session.SavedProblem is null || _index > 0)
        {
            _notice.Clear();
        }

        _pageArea.Add(Page);
        Page.Showing();
        Refresh();
        Page.FocusFirst();
    }

    /// <summary>Brings the bars and buttons up to date with the page.</summary>
    public void Refresh()
    {
        var shown = Applying;
        var step = shown.ToList().IndexOf(Page);
        _header.Badge = $"STEP {step + 1} OF {shown.Count}";
        _steps.Tabs = [.. shown.Select((page, i) => $"{i + 1} {page.StepName}")];
        _steps.Current = step;
        _next.Text = $"[ {Page.NextLabel} ]";
        _next.Enabled = Page.CanGoNext;
        _back.Enabled = Previous(_index) is not null && Page.CanGoBack;
        _summaryLines.Show(Summary());
        Arrange();
    }

    public void Next()
    {
        if (!Page.CanGoNext)
        {
            return;
        }

        var problem = Page.Leave();
        if (problem is not null)
        {
            Say(problem, HvoNotice.Danger);
            return;
        }

        if (Following(_index) is { } next)
        {
            ShowPage(next);
        }
    }

    public void Back()
    {
        if (Previous(_index) is { } previous && Page.CanGoBack)
        {
            ShowPage(previous);
        }
    }

    /// <summary>Closes the wizard with nothing changed.</summary>
    public void Cancel()
    {
        Result = InstallWizardResult.Cancelled;
        Stop();
    }

    /// <summary>Closes the wizard to install: the answers are saved at <see cref="InstallWizardSession.AnswersPath"/>.</summary>
    public void Finish()
    {
        Result = InstallWizardResult.Install;
        Stop();
    }

    /// <summary>
    /// Says <paramref name="text"/> beside the buttons; a problem is also a notice band, its kind in words, so that it
    /// does not depend on colour.
    /// </summary>
    public void Say(string text, HvoNotice? problem = null)
    {
        _message.Text = text;
        if (problem is { } kind)
        {
            _notice.Show(kind, text);
            _message.Text = string.Empty;
        }
    }

    /// <summary>Runs <paramref name="work"/> away from the interface's thread, then <paramref name="done"/> on it with the result.</summary>
    public void Run<T>(Func<CancellationToken, Task<T>> work, Action<T?, Exception?> done)
    {
        Interlocked.Increment(ref _pending);
        var token = _closing.Token;
        _ = Task.Run(async () =>
        {
            T? result = default;
            Exception? failure = null;
            try
            {
                result = await work(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = error;
            }

            if (!Post(() =>
                {
                    try
                    {
                        done(result, failure);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }))
            {
                Interlocked.Decrement(ref _pending);
            }
        });
    }

    /// <summary>Runs <paramref name="action"/> on the interface's thread; false when the wizard has closed.</summary>
    public bool Post(Action action)
    {
        if (_closed)
        {
            return false;
        }

        try
        {
            _app.Invoke(() =>
            {
                if (!_closed)
                {
                    action();
                }
            });
            return true;
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The next page after <paramref name="index"/> that applies, or null.</summary>
    public int? Following(int index)
    {
        for (var i = index + 1; i < _pages.Count; i++)
        {
            if (_pages[i].Applies)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Every answer so far, a line each, with where a given one came from.</summary>
    public IReadOnlyList<HvoLine> Summary()
    {
        var a = Session.Answers;
        var lines = new List<HvoLine>();
        void Add(string label, string value, string option)
        {
            var given = Session.Given(option);
            lines.Add(new($"{label,-10}  {(value.Length == 0 ? "-" : value)}{(given ? " *" : string.Empty)}", given ? HvoTone.Muted : HvoTone.Text));
        }

        Add("Camera", a.FriendlyName, "--friendly-name");
        Add("Release", Session.GivenValue("--image-ref") ?? a.Release, "--release");
        if (!Session.Given("--image-ref"))
        {
            Add("Image from", a.ImageSource == ReleaseImageSource.Registry ? "the registry" : "the archive", "--image-source");
        }

        Add("Catalog", a.CatalogBundle, "--catalog-bundle");
        Add("Address", $"{a.BindAddress}:{a.Port}", "--port");
        Add("Location", $"{a.Latitude}, {a.Longitude}, {a.Elevation} m", "--latitude");
        Add("Time zone", a.TimeZone, "--time-zone");
        Add("CPUs", InstallWizardSession.Blank(a.Cpus) ?? "default", "--cpus");
        Add("Memory", InstallWizardSession.Blank(a.Memory) ?? "default", "--memory");
        Add("Owner", InstallWizardSession.Blank(a.OwnerEmail) ?? "default", "--owner-email");
        lines.Add(new(string.Empty));
        lines.Add(new("* given on the command line", HvoTone.Muted));
        return lines;
    }

    private int? Previous(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (_pages[i].Applies)
            {
                return i;
            }
        }

        return null;
    }

    // The answers so far beside the page from 110 columns, the page taking the width below that; and a notice that needs
    // two rows takes one from the page, so that it is never cut.
    private void Arrange()
    {
        // Compared with what was last asked for, not with the frames, which change only when the window is next laid out.
        var wide = Window.Frame.Width >= SummaryWidth;
        var rows = _notice.RowsFor(Window.Frame.Width);
        if (_summary.Visible != wide || _noticeRows != rows)
        {
            _summary.Visible = wide;
            _noticeRows = rows;
            _pageArea.Width = Dim.Fill(wide ? 42 : 1);
            _notice.Height = rows;
            _pageArea.Y = 2 + rows;
            _summary.Y = 2 + rows;
            Window.SetNeedsLayout();
        }
    }

    private void Stop()
    {
        _closing.Cancel();
        _app.RequestStop();
    }

    private void OnKeyDown(object? sender, Key key)
    {
        if (key == Key.Esc || key == Key.C.WithCtrl)
        {
            key.Handled = true;
            Cancel();
            return;
        }

        if (key == Key.B.WithAlt)
        {
            key.Handled = true;
            Back();
            return;
        }

        if (key == Key.N.WithAlt)
        {
            key.Handled = true;
            Next();
            return;
        }

        if (key == Key.Enter && (Window.MostFocused is not Button || Window.MostFocused == _next))
        {
            // Enter is Next, as the key bar says, wherever the focus is but on another button, which Enter presses.
            key.Handled = true;
            Next();
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _closing.Cancel();
        _app.Keyboard.KeyDown -= OnKeyDown;
        _pageArea.Remove(Page);
        foreach (var page in _pages)
        {
            page.Dispose();
        }

        Window.Dispose();
        _closing.Dispose();
    }
}
