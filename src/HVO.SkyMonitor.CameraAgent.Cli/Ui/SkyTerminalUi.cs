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

    /// <summary>The keys of the key bar, and what each does.</summary>
    internal static readonly (string Key, string Name)[] Keys = [("Tab", "move"), ("Enter", "select"), ("Esc", "close"), ("Ctrl+R", "refresh")];

    /// <summary>The key of each page.</summary>
    private static readonly Key[] PageKeys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5];

    /// <summary>The width from which pages lay out for a wide terminal.</summary>
    internal const int WideWidth = 110;

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
        _tabs = new HvoTabBar(theme) { X = 0, Y = Pos.AnchorEnd(2), Tabs = [.. PageNames.Select((name, index) => $"F{index + 1} {name}")], Right = "F10 Quit" };
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
                return summary.FailureKind == "Unreachable" ? SkyUiFreshness.Offline : SkyUiFreshness.Stale;
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
        prompt.Panel.Dispose();
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
    /// Runs <paramref name="work"/> with the client away from the interface's thread, then <paramref name="done"/> on it with
    /// the result or the failure. Nothing runs without a connection.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every failure of background work is reported on the interface, never thrown on a thread pool thread.")]
    public void Run<T>(Func<CancellationToken, Task<T>> work, Action<T?, Exception?> done)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(done);
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
        }, CancellationToken.None);
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
    /// Restarts CameraAgent and waits for the new process, saying how it goes in the notice band; then
    /// <paramref name="done"/> with the outcome or the failure.
    /// </summary>
    public void Restart(Action<SkyRestartOutcome?, Exception?> done)
    {
        if (_client is not { } client)
        {
            return;
        }

        Say(HvoNotice.Info, "Restart requested: waiting for CameraAgent to come back.", "[RESTARTING]");
        Run(
            token => SkyCli.RestartAsync(Context, client, wait: true, "the restart", token, progress: _ => { }),
            (outcome, error) =>
            {
                // A new process: everything is read again from it, then the caller checks what it asked for.
                Connect();
                done(outcome, error);
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
        var freshness = Freshness;
        (_header.Badge, _header.BadgeTone) = freshness switch
        {
            SkyUiFreshness.Live => ("[LIVE]", HvoTone.Success),
            SkyUiFreshness.Stale => ("[STALE]", HvoTone.Warning),
            SkyUiFreshness.Offline => ("[OFFLINE]", HvoTone.Danger),
            SkyUiFreshness.SignedOut => ("[SIGNED OUT]", HvoTone.Danger),
            SkyUiFreshness.NotSignedIn => ("[NOT SIGNED IN]", HvoTone.Warning),
            _ => ("[CONNECTING]", HvoTone.Info)
        };
        var summary = Data.Summary.Value;
        var agent = Data.Summary.HasValue ? SkyJson.Text(summary, "configuration", "value", "agentId") : null;
        var module = Data.Summary.HasValue ? SkyJson.Text(summary, "configuration", "value", "moduleType") : null;
        _header.Context = Connection is null
            ? "No CameraAgent address or API key"
            : $"CameraAgent {Connection.Url}" + (agent is null ? string.Empty : $"  {agent}") + (module is null ? string.Empty : $" · {module}");
        _header.Note = Connection is null
            ? string.Empty
            : $"key {Connection.KeyId}" + (Data.Info.Value?.Caller.Access is { } access ? $" {SkyStatusText.DescribeAccess(access)}" : string.Empty);
        _keys.Right = Data.Summary.ReadAt is { } read ? $"read {SkyCliFormat.Time(read)[11..]}" : string.Empty;

        var problem = DataNotice(freshness);
        if (problem is { } shown)
        {
            _notice.Show(shown.Kind, shown.Text, shown.Word);
        }
        else if (_actionNotice is { } action)
        {
            _notice.Show(action.Kind, action.Text, action.Word);
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
            SkyUiFreshness.Offline => (HvoNotice.Danger, $"{summary.Failure} {asOf}", "[OFFLINE]"),
            SkyUiFreshness.Stale => (HvoNotice.Warning,
                $"Status not current{(summary.Failure is null ? string.Empty : $": {summary.Failure}")}. {asOf}", "[STALE]"),
            _ => null
        };
    }

    // A notice that needs two rows takes one from the page, so it is never cut; the pages lay out for the width.
    private void Arrange()
    {
        var width = Window.Frame.Width;
        var rows = _notice.RowsFor(width);
        var wide = width >= WideWidth;
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
