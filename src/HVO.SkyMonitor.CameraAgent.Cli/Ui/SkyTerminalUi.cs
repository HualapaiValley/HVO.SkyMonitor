using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.SkyMonitor.CameraAgent.Cli.Ui;

/// <summary>
/// <c>hvo-sky ui</c> (#1317): a full-screen terminal interface over the same operations API as the commands, in HVO Dark
/// as the RoofController #158 terminal prototype draws it. A header with CameraAgent's address and a badge saying whether
/// what shows is current (<c>[LIVE]</c>, <c>[STALE]</c>, <c>[OFFLINE]</c>), a notice band, the page, the pages on a tab
/// bar (F1 to F5; F10 quits) and the keys at the foot. It fits 80 x 24 and uses the room 120 x 32 gives.
/// <para>
/// The status is read every poll interval; each page reads its own data when shown, on Ctrl+R or its Refresh, and after
/// a change. A read that fails keeps the last values on screen, marked <c>LAST KNOWN</c> with when they were read: nothing
/// is shown as current that CameraAgent has not just said. Changes go through the same routes, checks and version
/// guards as the commands; nothing is sent until a prompt's action is chosen.
/// </para>
/// </summary>
internal sealed class SkyTerminalUi : IDisposable
{
    /// <summary>The pages, in F-key order.</summary>
    internal static readonly string[] PageNames = ["Status", "Settings", "Camera & rig", "Schedule", "System"];

    /// <summary>
    /// The pages' names on the tab bar below <see cref="WideWidth"/> columns, where "Camera & rig" is two cells more than
    /// the bar has room for: every page keeps its name at 80 columns, as the terminal prototype's do.
    /// </summary>
    internal static readonly string[] NarrowPageNames = ["Status", "Settings", "Camera", "Schedule", "System"];

    /// <summary>The keys of the key bar, and what each does.</summary>
    internal static readonly (string Key, string Name)[] Keys = [("Tab", "move"), ("Enter", "select"), ("Esc", "close prompt"), ("Ctrl+R", "refresh")];

    /// <summary>The key of each page.</summary>
    private static readonly Key[] PageKeys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5];

    /// <summary>The width from which pages lay out for a wide terminal.</summary>
    internal const int WideWidth = 110;

    /// <summary>The most rows the notice band takes, so the longest outcome is never cut at 80 columns.</summary>
    internal const int NoticeRows = 3;

    /// <summary>How many status reads pass between rereads of the shown page's own data.</summary>
    internal const int PageRefreshTicks = 15;

    private readonly IApplication _app;
    private readonly HvoHeaderBar _header;
    private readonly HvoNoticeBar _notice;
    private readonly View _content;
    private readonly Label _message;
    private readonly HvoTabBar _tabs;
    private readonly HvoKeyBar _keys;
    private readonly SkyUiPage[] _pages;
    private readonly CancellationTokenSource _closing = new();
    private SkyApiClient? _client;
    private SkyUiPrompt? _prompt;
    private (HvoNotice Kind, string Text, string? Word)? _actionNotice;
    private object? _timer;
    private int _index = -1;
    private int _ticks;
    private int _pending;
    private int _noticeRows = 1;
    private bool _wide;
    private volatile bool _closed;

    public SkyTerminalUi(SkyCliContext context, IApplication app, HvoTheme theme)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);
        Context = context;
        _app = app;
        Theme = theme;

        Window = new Window { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), BorderStyle = LineStyle.None };
        Window.SetScheme(theme.Page);
        _header = new HvoHeaderBar(theme) { Heading = "HVO SkyMonitor" };
        _notice = new HvoNoticeBar(theme) { X = 0, Y = 2 };
        _content = new View { X = 0, Y = 3, Width = Dim.Fill(), Height = Dim.Fill(3), CanFocus = true };
        _message = new Label { X = 1, Y = Pos.AnchorEnd(3), Width = Dim.Fill(1), Height = 1 };
        _message.SetScheme(theme.Label(HvoTone.Muted));
        _tabs = new HvoTabBar(theme) { X = 0, Y = Pos.AnchorEnd(2), Tabs = TabLabels(wide: false), Right = "F10 Quit" };
        _keys = new HvoKeyBar(theme) { X = 0, Y = Pos.AnchorEnd(1), Keys = Keys };
        Window.Add(_header, _notice, _content, _message, _tabs, _keys);
        Window.FrameChanged += (_, _) => Arrange();
        _notice.Changed += (_, _) => Arrange();

        _pages =
        [
            new SkyUiStatusPage(this),
            new SkyUiSettingsPage(this),
            new SkyUiRigPage(this),
            new SkyUiSchedulePage(this),
            new SkyUiSystemPage(this)
        ];
        _app.Keyboard.KeyDown += OnKeyDown;
    }

    public SkyCliContext Context { get; }

    public HvoTheme Theme { get; }

    public Window Window { get; }

    public SkyUiData Data { get; } = new();

    /// <summary>The address and key in use; null when there is none (the System page offers Sign in).</summary>
    public SkyCliConnection? Connection { get; private set; }

    /// <summary>Why there is no connection, when there is none.</summary>
    public string? ConnectionProblem { get; private set; }

    /// <summary>The client of the connection in use.</summary>
    public SkyApiClient? Client => _client;

    public IReadOnlyList<SkyUiPage> Pages => _pages;

    public SkyUiPage Page => _pages[_index];

    public int PageIndex => _index;

    public SkyUiPrompt? Prompt => _prompt;

    public HvoHeaderBar Header => _header;

    public HvoNoticeBar Notice => _notice;

    public HvoTabBar Tabs => _tabs;

    public string Message => _message.Text;

    /// <summary>Whether the pages are laid out for a wide terminal.</summary>
    public bool Wide => _wide;

    /// <summary>Reads still on their way (tests wait for none).</summary>
    public int PendingOperations => Volatile.Read(ref _pending);

    /// <summary>Ended when the interface closes: reads it started stop.</summary>
    public CancellationToken Closing => _closing.Token;

    /// <summary>
    /// How long each read and each change may wait for CameraAgent: <c>--timeout</c>. The interface runs until F10, so the
    /// deadline is each operation's rather than the whole command's.
    /// </summary>
    public TimeSpan OperationTimeout => Context.Deadline?.Limit ?? TimeSpan.FromSeconds(SkyCli.DefaultTimeoutSeconds);

    /// <summary>How long a status stays current without a newer read.</summary>
    public TimeSpan StaleAfter => TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(6).Ticks, Context.Host.PollInterval.Ticks * 3));

    /// <summary>Whether what shows is current, as the header badge says it.</summary>
    public SkyUiFreshness Freshness
    {
        get
        {
            if (Connection is null)
            {
                return SkyUiFreshness.NotSignedIn;
            }

            var summary = Data.Summary;
            if (summary.FailureKind == "SignedOut")
            {
                return SkyUiFreshness.SignedOut;
            }

            if (summary.Failure is not null)
            {
                return summary.FailureKind is "Unreachable" or "TimedOut" ? SkyUiFreshness.Offline : SkyUiFreshness.Stale;
            }

            if (!summary.HasValue)
            {
                return SkyUiFreshness.Connecting;
            }

            return Now - summary.ReadAt!.Value > StaleAfter ? SkyUiFreshness.Stale : SkyUiFreshness.Live;
        }
    }

    public DateTimeOffset Now => Context.Host.Time.GetUtcNow();

    /// <summary>Connects and shows the first page: Status, or System when there is no address or key; then reads every poll interval.</summary>
    public void Start()
    {
        Connect();
        ShowPage(Connection is null ? _pages.Length - 1 : 0);
        _timer = _app.AddTimeout(Context.Host.PollInterval, () =>
        {
            Tick();
            return !_closed;
        });
    }

    /// <summary>(Re)reads the address and key from <c>--url</c>, the environment and the credentials file, and reads again.</summary>
    public void Connect()
    {
        _client?.Dispose();
        _client = null;
        Data.Clear();
        Connection = null;
        ConnectionProblem = null;
        try
        {
            Connection = Context.ResolveConnection();
            _client = Context.CreateClient(Connection.Url, Connection.ApiKey);
        }
        catch (Exception error) when (error is SkyCliNotConfiguredException or SkyCredentialFileException)
        {
            ConnectionProblem = error.Message;
        }

        UpdateChrome();
        if (_index >= 0)
        {
            Page.DataChanged();
        }

        // Who CameraAgent takes the key to be, and whether it can restart itself: the header and every page's changes use it.
        Read(Data.Info, SkyRoutes.Info, SkyApiClient.Read<SkyInfo>);
        RefreshNow();
    }

    /// <summary>Reads the status and the shown page's data now.</summary>
    public void RefreshNow()
    {
        ReadSummary();
        if (_index >= 0)
        {
            Page.Refresh();
        }
    }

    /// <summary>One poll: the status, and every <see cref="PageRefreshTicks"/>th time the shown page's data.</summary>
    public void Tick()
    {
        if (_closed)
        {
            return;
        }

        ReadSummary();
        if (++_ticks % PageRefreshTicks == 0 && _index >= 0)
        {
            Page.Refresh();
        }

        UpdateChrome();
    }

    public void ShowPage(int index)
    {
        ClosePrompt();
        if (_index >= 0)
        {
            _content.Remove(Page);
        }

        _index = Math.Clamp(index, 0, _pages.Length - 1);
        _actionNotice = null;
        _content.Add(Page);
        _tabs.Current = _index;
        Page.Arrange(Math.Max(0, Window.Frame.Width), Math.Max(0, Window.Frame.Height - 3 - _noticeRows), _wide);
        Page.DataChanged();
        Page.Refresh();
        UpdateChrome();
        Page.FocusFirst();
    }

    /// <summary>Opens <paramref name="spec"/> over the page.</summary>
    public SkyUiPrompt OpenPrompt(SkyUiPromptSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ClosePrompt();
        _prompt = new SkyUiPrompt(Theme, spec, Window.Frame.Width > 0 ? Window.Frame.Width : 80, prompt => ClosePrompt(prompt));
        Window.Add(_prompt.Panel);
        _prompt.FocusFirst();
        return _prompt;
    }

    public void ClosePrompt(SkyUiPrompt? which = null)
    {
        if (_prompt is not { } prompt || (which is not null && which != prompt))
        {
            return;
        }

        _prompt = null;
        Window.Remove(prompt.Panel);
        prompt.Dispose();
        if (_index >= 0)
        {
            Page.FocusFirst();
        }
    }

    /// <summary>Says <paramref name="text"/> in the notice band until the next action or page.</summary>
    public void Say(HvoNotice kind, string text, string? word = null)
    {
        _actionNotice = (kind, text, word);
        UpdateChrome();
    }

    /// <summary>Says <paramref name="text"/> on the line above the tabs: the shown page's hint.</summary>
    public void Hint(string text) => _message.Text = text ?? string.Empty;

    /// <summary>Closes the interface (F10, or a termination signal).</summary>
    public void Quit()
    {
        _closing.Cancel();
        _app.RequestStop();
    }

    /// <summary>
    /// Reads <paramref name="route"/> into <paramref name="reading"/> away from the interface's thread, then on it updates
    /// the chrome and the page. A failure keeps the last value, marked last known, with why.
    /// </summary>
    public void Read<T>(SkyUiReading<T> reading, string route, Func<JsonElement, T> convert, Action? after = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(convert);
        if (_client is not { } client)
        {
            return;
        }

        Run(
            async token => convert(await client.GetAsync(route, token).ConfigureAwait(false)),
            (value, error) =>
            {
                if (!ReferenceEquals(client, _client))
                {
                    return;
                }

                if (error is null)
                {
                    reading.Succeeded(value!, Now);
                }
                else
                {
                    var (_, kind, message) = SkyCliContext.Classify(error);
                    reading.Failed(kind, message);
                }

                after?.Invoke();
                UpdateChrome();
                Page.DataChanged();
            });
    }

    /// <summary>Reads the settings catalogue and values together.</summary>
    public void ReadSettings(Action? after = null)
    {
        if (_client is not { } client)
        {
            return;
        }

        Run(
            async token =>
            {
                var catalogue = await client.GetAsync<SkySettingsCatalogue>(SkySettingsContract.CatalogueRoute, token).ConfigureAwait(false);
                var values = await client.GetAsync<SkySettingsValues>(SkySettingsContract.Route, token).ConfigureAwait(false);
                return SkySettingsForm.Create(catalogue, values);
            },
            (form, error) =>
            {
                if (!ReferenceEquals(client, _client))
                {
                    return;
                }

                if (error is null)
                {
                    Data.Settings.Succeeded(form!, Now);
                }
                else
                {
                    var (_, kind, message) = SkyCliContext.Classify(error);
                    Data.Settings.Failed(kind, message);
                }

                after?.Invoke();
                UpdateChrome();
                Page.DataChanged();
            });
    }

    /// <summary>
    /// Runs <paramref name="work"/> away from the interface's thread, within a deadline of its own (<see cref="OperationTimeout"/>),
    /// then <paramref name="done"/> on it with the result or the failure.
    /// </summary>
    public void Run<T>(Func<CancellationToken, Task<T>> work, Action<T?, Exception?> done)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(done);
        Run((_, token) => work(token), (result, failure, _) => done(result, failure));
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one operation: away from the interface's thread, with a context of its own (its
    /// deadline, and what CameraAgent accepted of it), then <paramref name="done"/> on the interface's thread with the
    /// result or the failure and that context, which words the failure as the commands do. A deadline that passes is the
    /// failure <c>TimedOut</c>, as a command's is.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every failure of background work is reported on the interface, never thrown on a thread pool thread.")]
    public void Run<T>(Func<SkyCliContext, CancellationToken, Task<T>> work, Action<T?, Exception?, SkyCliContext> done)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(done);
        Interlocked.Increment(ref _pending);
        var closing = _closing.Token;
        var limit = OperationTimeout;
        _ = Task.Run(async () =>
        {
            T? result = default;
            Exception? failure = null;
            using var deadline = new SkyCliDeadline(Context.Host.Time, limit, closing);
            var operation = Context.ForOperation(deadline);
            try
            {
                result = await work(operation, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.Expired && !closing.IsCancellationRequested)
            {
                failure = new SkyCliOutcomeException(deadline.Describe(), SkyExitCode.Failed, "TimedOut");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = error;
            }

            if (!Post(() =>
                {
                    try
                    {
                        done(result, failure, operation);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }))
            {
                Interlocked.Decrement(ref _pending);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Says how a change ended, in the notice band: <paramref name="success"/>; or the failure, as the commands word it.
    /// "<paramref name="notDone"/>" heads it only when nothing changed; once CameraAgent may have taken the change, the
    /// failure says what it accepted, or that whether it received it is not known.
    /// </summary>
    public void SayOutcome(SkyCliContext operation, Exception? error, string success, string notDone, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (error is null)
        {
            Say(HvoNotice.Success, success);
            return;
        }

        var failure = operation.Describe(error);
        var text = failure.ChangeMayHaveHappened ? failure.Message : $"{notDone}: {failure.Message}";
        Say(HvoNotice.Danger, detail is { Length: > 0 } ? $"{text} {detail}" : text,
            failure.ChangeMayHaveHappened ? "[UNVERIFIED]" : failure.Code == SkyExitCode.Refused ? "[REFUSED]" : "[FAILED]");
    }

    /// <summary>Runs <paramref name="action"/> on the interface's thread; false once the interface has closed.</summary>
    public bool Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
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

    /// <summary>
    /// Restarts CameraAgent as <paramref name="change"/> and waits for the new process, then, in the same operation and
    /// within its deadline, for <paramref name="after"/> (the rig page's wait for the staged rig); then everything is read
    /// again from the new process and <paramref name="done"/> runs with the outcome or the failure.
    /// </summary>
    public void Restart(string change, Func<SkyCliContext, SkyApiClient, CancellationToken, Task>? after, Action<SkyRestartOutcome?, Exception?, SkyCliContext> done)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(done);
        if (_client is not { } client)
        {
            return;
        }

        Say(HvoNotice.Info, "Restart requested: waiting for CameraAgent to come back.", "[RESTARTING]");
        Run(
            async (operation, token) =>
            {
                // The interface shows the status itself, so its timeout says no more than what it waited for: it fits the band.
                var outcome = await SkyCli.RestartAsync(operation, client, wait: true, change, token, operation.Deadline, progress: _ => { },
                    waitingFor: "waiting for CameraAgent to come back; it may still be starting").ConfigureAwait(false);
                if (after is not null)
                {
                    await after(operation, client, token).ConfigureAwait(false);
                }

                return outcome;
            },
            (outcome, error, operation) =>
            {
                // A new process: everything is read again from it.
                Connect();
                done(outcome, error, operation);
            });
    }

    /// <summary>The status read: kept, recorded for the recent changes, and shown.</summary>
    private void ReadSummary()
        => Read(Data.Summary, SkyRoutes.Summary, static element => element, () =>
        {
            if (Data.Summary is { Failure: null, Value: var summary } && Data.Summary.HasValue)
            {
                Data.Record(Data.Summary.ReadAt!.Value, SkyStatusText.DescribeState(summary));
            }
        });

    /// <summary>Brings the header, the notice band and the foot up to date with the connection and the reads.</summary>
    public void UpdateChrome()
    {
        // As the terminal prototype's header: the badge and who the key is on the first row, in the badge's tone; where
        // the data comes from, and when it was read, on the second.
        var freshness = Freshness;
        var (badge, tone) = freshness switch
        {
            SkyUiFreshness.Live => ("[LIVE]", HvoTone.Success),
            SkyUiFreshness.Stale => ("[STALE]", HvoTone.Warning),
            SkyUiFreshness.Offline => ("[OFFLINE]", HvoTone.Warning),
            SkyUiFreshness.SignedOut => ("[SIGNED OUT]", HvoTone.Danger),
            SkyUiFreshness.NotSignedIn => ("[NOT SIGNED IN]", HvoTone.Warning),
            _ => ("[CONNECTING]", HvoTone.Info)
        };
        var identity = Connection is null
            ? string.Empty
            : $"{(Data.Info.Value?.Caller.Access is { } access ? SkyStatusText.DescribeAccess(access) + " " : string.Empty)}key {Connection.KeyId}";
        (_header.Badge, _header.BadgeTone) = (identity.Length == 0 ? badge : $"{badge}  {identity}", tone);
        var summary = Data.Summary.Value;
        var agent = Data.Summary.HasValue ? SkyJson.Text(summary, "configuration", "value", "agentId") : null;
        var module = Data.Summary.HasValue ? SkyJson.Text(summary, "configuration", "value", "moduleType") : null;
        var names = string.Join(", ", new[] { agent, module }.OfType<string>());
        _header.Context = Connection is null
            ? "No CameraAgent address or API key"
            : $"CameraAgent: {Connection.Url}" + (names.Length == 0 ? string.Empty : $" ({names})");
        _header.Note = Connection is null ? string.Empty
            : Data.Summary.ReadAt is { } read ? $"read {SkyCliFormat.Time(read)[11..]}" : "not read yet";

        // What the person just did is answered first; the header's badge and the panels' LAST KNOWN keep marking data
        // that is not current meanwhile, and the band says so again on the next page or Ctrl+R.
        if (_actionNotice is { } action)
        {
            _notice.Show(action.Kind, action.Text, action.Word);
        }
        else if (DataNotice(freshness) is { } problem)
        {
            _notice.Show(problem.Kind, problem.Text, problem.Word);
        }
        else
        {
            _notice.Clear();
        }

        Hint(_index >= 0 ? Page.Help : string.Empty);
    }

    private (HvoNotice Kind, string Text, string? Word)? DataNotice(SkyUiFreshness freshness)
    {
        var summary = Data.Summary;
        var asOf = summary.ReadAt is { } at ? $"Values below are LAST KNOWN, as of {SkyCliFormat.Time(at)}." : "Nothing has been read yet.";
        return freshness switch
        {
            SkyUiFreshness.NotSignedIn => (HvoNotice.Warning,
                $"{ConnectionProblem ?? "No CameraAgent address or API key."} F5 System, Sign in; or run 'hvo-sky login'.", "[NOT SIGNED IN]"),
            SkyUiFreshness.SignedOut => (HvoNotice.Danger,
                "CameraAgent did not accept the API key (HTTP 401). F5 System, Sign in with a current key.", "[SIGNED OUT]"),
            SkyUiFreshness.Offline => (HvoNotice.Warning, $"{summary.Failure} {asOf}", "[OFFLINE]"),
            SkyUiFreshness.Stale => (HvoNotice.Warning,
                $"Status not current{(summary.Failure is null ? string.Empty : $": {summary.Failure}")}. {asOf}", "[STALE]"),
            _ => null
        };
    }

    // A notice that needs more rows takes them from the page, up to three, so it is never cut; the pages lay out for the
    // width and the rows left.
    private void Arrange()
    {
        var width = Window.Frame.Width;
        var rows = _notice.RowsFor(width, NoticeRows);
        var wide = width >= WideWidth;
        _keys.Right = $"{width}x{Window.Frame.Height}";
        if (wide != _wide || _tabs.Tabs.Count == 0)
        {
            _tabs.Tabs = TabLabels(wide);
        }

        if (rows != _noticeRows)
        {
            _noticeRows = rows;
            _notice.Height = rows;
            _content.Y = 2 + rows;
            Window.SetNeedsLayout();
        }

        _wide = wide;
        if (_index >= 0)
        {
            Page.Arrange(width, Math.Max(0, Window.Frame.Height - 2 - rows - 3), wide);
        }
    }

    private void OnKeyDown(object? sender, Key key)
    {
        // While a prompt is open the page keys and F10 wait, as the terminal prototype's do: Esc or Cancel closes it first,
        // so a review is never left by a stray key. A signal still ends the interface.
        if (_prompt is not null && (key == Key.F10 || PageKeys.Any(page => key == page)))
        {
            key.Handled = true;
            Hint("Close the prompt first: Esc, or Cancel.");
            return;
        }

        for (var index = 0; index < _pages.Length; index++)
        {
            if (key == PageKeys[index])
            {
                key.Handled = true;
                ShowPage(index);
                return;
            }
        }

        if (key == Key.F10)
        {
            key.Handled = true;
            Quit();
        }
        else if (key == Key.Esc)
        {
            // Esc closes a prompt, and never closes the interface: Terminal.Gui would otherwise quit on it.
            key.Handled = true;
            ClosePrompt();
        }
        else if (key == Key.R.WithCtrl)
        {
            key.Handled = true;
            _actionNotice = null;
            RefreshNow();
        }
    }

    private static string[] TabLabels(bool wide)
        => [.. (wide ? PageNames : NarrowPageNames).Select((name, index) => $"F{index + 1} {name}")];

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _closing.Cancel();
        if (_timer is not null)
        {
            _app.RemoveTimeout(_timer);
        }

        _app.Keyboard.KeyDown -= OnKeyDown;
        ClosePrompt();
        if (_index >= 0)
        {
            _content.Remove(Page);
        }

        foreach (var page in _pages)
        {
            page.Dispose();
        }

        Window.Dispose();
        _client?.Dispose();
        _closing.Dispose();
    }
}
