using System.Text;
using HVO.SkyMonitor.CameraAgent.Cli;
using HVO.SkyMonitor.CameraAgent.Cli.Ui;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli.Ui;

/// <summary>
/// A fixed clock for the interface's screens, so what they show is the same on every run: the time it reads stands still
/// until a test moves it, and its timers (the deadlines) are real ones.
/// </summary>
internal sealed class SkyUiClock(DateTimeOffset now) : TimeProvider
{
    /// <summary>Two seconds after the fake summary was observed: what it says is current.</summary>
    public static readonly DateTimeOffset Observed = new(2026, 10, 18, 19, 30, 5, TimeSpan.Zero);

    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override System.Threading.ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => System.CreateTimer(callback, state, dueTime, period);
}

/// <summary>
/// <c>hvo-sky ui</c> on a <see cref="CliHarness"/>, drawn by Terminal.Gui's ANSI driver in memory with a virtual clock:
/// keys go in, and the screen comes out as text, as the colours of its cells, and as ANSI (<see cref="Render"/>), which
/// CI draws as the docs' screenshots. Reads the interface sends off its thread come back only when the loop is pumped
/// (<see cref="Pump"/>, <see cref="WaitIdle"/>); the poll is <see cref="Tick"/>.
/// </summary>
internal sealed class SkyUiDriver : IDisposable
{
    /// <summary>An operator's terminal, as the screenshots show it: true colour and UTF-8.</summary>
    public static readonly IReadOnlyDictionary<string, string?> Operator = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["TERM"] = "xterm-256color",
        ["COLORTERM"] = "truecolor",
        ["LANG"] = "C.UTF-8"
    };

    private readonly StringWriter _output;
    private readonly StringWriter _error;
    private SessionToken? _token;

    private SkyUiDriver(CliHarness harness, IApplication app, SkyTerminalUi ui, StringWriter output, StringWriter error)
    {
        Harness = harness;
        App = app;
        Ui = ui;
        _output = output;
        _error = error;
        _token = App.Begin(Ui.Window);
    }

    public CliHarness Harness { get; }

    public IApplication App { get; }

    public SkyTerminalUi Ui { get; }

    /// <summary>What the command line would have written; the interface writes nothing there.</summary>
    public string Output => _output.ToString() + _error.ToString();

    /// <summary>A Terminal.Gui application on the in-memory ANSI driver, <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static IApplication CreateApplication(int width, int height)
    {
        var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        return app;
    }

    /// <summary>
    /// Starts the interface on <paramref name="harness"/>, at a size, in a terminal <paramref name="environment"/> (an
    /// operator's unless given), with <paramref name="timeout"/> as <c>--timeout</c> and <paramref name="pollInterval"/>
    /// as the poll (2 s unless given: a restart's and a rig's waits read again that often), and waits for its first reads.
    /// </summary>
    public static SkyUiDriver Start(
        CliHarness harness,
        int width = 80,
        int height = 24,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        harness.PollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        foreach (var (name, value) in environment ?? Operator)
        {
            harness.Environment[name] = value;
        }

        if (harness.Time == TimeProvider.System)
        {
            harness.Time = new SkyUiClock(SkyUiClock.Observed);
        }

        var output = new StringWriter();
        var error = new StringWriter();
        var host = harness.CreateHost(output, error);
        var context = new SkyCliContext(host, json: false, url: null, credentialsFile: null);
        var deadline = new SkyCliDeadline(harness.Time, timeout ?? TimeSpan.FromSeconds(SkyCli.DefaultTimeoutSeconds), CancellationToken.None);
        deadline.Stop();
        context.Deadline = deadline;
        var app = CreateApplication(width, height);
        var theme = HvoTheme.For(name => harness.Environment.TryGetValue(name, out var value) ? value : null);
        theme.Prepare(app);
        var driver = new SkyUiDriver(harness, app, new SkyTerminalUi(context, app, theme), output, error);
        driver.Ui.Start();
        driver.WaitIdle("the first reads");
        return driver;
    }

    /// <summary>The screen as text, after a full layout and draw.</summary>
    public string Screen
    {
        get
        {
            App.LayoutAndDraw(true);
            return App.Driver!.ToString() ?? string.Empty;
        }
    }

    /// <summary>The screen's rows.</summary>
    public string[] Rows => [.. Screen.TrimEnd('\n').Split('\n').Select(row => row.TrimEnd('\r'))];

    /// <summary>The screen as ANSI.</summary>
    public string Ansi
    {
        get
        {
            App.LayoutAndDraw(true);
            return App.Driver!.ToAnsi();
        }
    }

    /// <summary>The colours of the first cell showing <paramref name="text"/>, reading from the top.</summary>
    public TuiAttribute ColoursOf(string text)
    {
        App.LayoutAndDraw(true);
        var contents = App.Driver!.Contents!;
        for (var row = 0; row < contents.GetLength(0); row++)
        {
            var line = new StringBuilder();
            var columns = new List<int>();
            for (var column = 0; column < contents.GetLength(1); column++)
            {
                var grapheme = contents[row, column].Grapheme is { Length: > 0 } drawn ? drawn : " ";
                line.Append(grapheme);
                columns.AddRange(Enumerable.Repeat(column, grapheme.Length));
            }

            var index = line.ToString().IndexOf(text, StringComparison.Ordinal);
            if (index >= 0)
            {
                return contents[row, columns[index]].Attribute ?? throw new AssertFailedException($"'{text}' is drawn with no colours.");
            }
        }

        throw new AssertFailedException($"'{text}' is not on the screen:\n{Screen}");
    }

    public void Press(Key key)
    {
        App.InjectKey(key);
        Pump();
    }

    /// <summary>Presses <paramref name="key"/> and waits for what it started.</summary>
    public void PressAndWait(Key key, string what)
    {
        Press(key);
        WaitIdle(what);
    }

    /// <summary>Selects what the focused field holds and types <paramref name="text"/> in its place.</summary>
    public void Replace(string text)
    {
        App.InjectKey(Key.End);
        for (var i = 0; i < 80; i++)
        {
            App.InjectKey(Key.Backspace);
        }

        Type(text);
    }

    public void Type(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            App.InjectKey(new Key(c));
        }

        Pump();
    }

    public void Pump() => App.TimedEvents!.RunTimers();

    /// <summary>One poll of the status, as the interface's timer does it, and its reads.</summary>
    public void Tick()
    {
        Ui.Tick();
        WaitIdle("the poll");
    }

    /// <summary>Runs the loop until no work is on its way and <paramref name="until"/> holds, or fails after 15 s.</summary>
    public void WaitIdle(string what, Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            Pump();
            if (Ui.PendingOperations == 0 && (until?.Invoke() ?? true))
            {
                Pump();
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}. Pending: {Ui.PendingOperations}. Screen:\n{Screen}");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>Shows page <paramref name="index"/> (0 for F1) and waits for its reads.</summary>
    public void ShowPage(int index)
    {
        PressAndWait(index switch
        {
            0 => Key.F1,
            1 => Key.F2,
            2 => Key.F3,
            3 => Key.F4,
            _ => Key.F5
        }, $"page F{index + 1}");
    }

    /// <summary>Saves the screen as <paramref name="name"/>.ans for the docs' screenshots, and attaches it to the results.</summary>
    public string Render(TestContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = TerminalRenders.Save(App, name, Path.Combine(context.TestRunResultsDirectory ?? Path.GetTempPath(), "terminal-renders"));
        context.AddResultFile(path);
        return path;
    }

    public void Dispose()
    {
        if (_token is { } token)
        {
            _token = null;
            App.End(token);
        }

        Ui.Dispose();
        App.Dispose();
        Ui.Context.Deadline?.Dispose();
        _output.Dispose();
        _error.Dispose();
    }
}
