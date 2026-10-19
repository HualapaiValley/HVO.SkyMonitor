using System.Collections.Concurrent;
using System.Text;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Wizard;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.SkyMonitor.Deployment.Cli.Tests.Wizard;

/// <summary>
/// A machine for the wizard: its own config folder (where the answers are saved), its own host policy, a terminal
/// environment (true colour and UTF-8 unless a test says otherwise), and an installer that records each request and
/// answers a plan with a fixed one. Nothing outside the folder is read or written, and Docker is never contacted.
/// </summary>
internal sealed class WizardHost : IDisposable
{
    public const string Version = "0.9.0-dev+0123456789abcdef0123456789abcdef01234567";

    public static readonly Guid InstanceId = Guid.Parse("6f9b3c1e-2d4a-4e8b-9c7d-1a2b3c4d5e6f");

    private const UnixFileMode SharedDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly Dictionary<string, string?> _environment;

    public WizardHost(IReadOnlyDictionary<string, string?>? environment = null)
    {
        Root = Path.Combine(Path.GetTempPath().TrimEnd('/'), $"hvo-wizard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        File.SetUnixFileMode(Root, SharedDirectory);
        Config = Path.Combine(Root, "config");
        Location = new HostPolicyLocation(
            Path.Combine(Root, "etc/hvo-sky/host.json"),
            Root,
            NativeLinux.getuid(),
            NativeLinux.getgid(),
            InstallRequest.DefaultProductRoot,
            Path.Combine(Root, "run/hvo-sky-host-policy.lock"));
        _environment = new(StringComparer.Ordinal)
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["LANG"] = "C.UTF-8",
            ["HOME"] = Root,
            ["XDG_CONFIG_HOME"] = Config
        };
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            _environment[name] = value;
        }
    }

    public string Root { get; }

    public string Config { get; }

    public HostPolicyLocation Location { get; }

    /// <summary>Where the wizard saves the answers unless told otherwise.</summary>
    public string AnswersPath => Path.Combine(Config, "hvo-sky", "install-answers.json");

    /// <summary>Every install request the installer was given: the plan checks, then the install.</summary>
    public ConcurrentQueue<InstallRequest> Requests { get; } = new();

    /// <summary>What a plan check does instead of returning the plan, when set.</summary>
    public Func<InstallRequest, Exception>? PlanFailure { get; set; }

    /// <summary>Holds the plan check until released, to look at the wizard while it is on its way.</summary>
    public ManualResetEventSlim PlanReleased { get; } = new(initialState: true);

    public Func<string, string?> Environment => name => _environment.TryGetValue(name, out var value) ? value : null;

    public InstallTerminal Terminal => new(true, Environment);

    public ProgramServices Services
    {
        get
        {
            var services = new ProgramServices(
                arguments => CommandLine.ParseCommand(arguments, new CommandLineContext(false, Location)),
                InstallAsync,
                (_, _) => throw new AssertFailedException("The wizard never runs a state preflight."),
                (_, _) => throw new AssertFailedException("The wizard never resets state."),
                (_, _) => throw new AssertFailedException("The wizard never sets the host policy."),
                (_, _) => throw new AssertFailedException("The wizard never recovers the owner."),
                (_, _) => throw new AssertFailedException("The wizard never runs a lifecycle command."));
            return services with { Terminal = Terminal };
        }
    }

    /// <summary>The install requests that were not plans: what an install would have changed.</summary>
    public IReadOnlyList<InstallRequest> Installs => [.. Requests.Where(request => !request.DryRun)];

    /// <summary>A catalog bundle folder, made so its path is real.</summary>
    public string Catalog
    {
        get
        {
            var path = Path.Combine(Root, "catalog", "hyg-v4.2-p3-s2-r1.bundle");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>A release folder.</summary>
    public string Release
    {
        get
        {
            var path = Path.Combine(Root, "release");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static InstallationResult Result(InstallRequest request, bool planned)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalog = new CatalogInstallationIdentity(
            HygV42.CatalogId, "hyg-v4.2-p3-s2-r1", "1", "1", new string('a', 64), 1, 1, "/var/lib/hvo-sky/catalogs/hyg-v4.2",
            new string('a', 64), "local-offline");
        const string image = "sha256:9c3f6e2b1a7d4c8e5f0a3b6d9e2c5f8a1b4d7e0c3f6a9b2d5e8f1a4c7b0d3e6f";
        var root = $"/var/lib/hvo-sky/cameraagents/{request.InstanceId:D}";
        return new InstallationResult(
            1, planned ? InstallationOutcome.Planned : InstallationOutcome.Installed, Guid.NewGuid(), request.InstanceId ?? InstanceId, Guid.NewGuid(),
            request.FriendlyName, new Uri($"http://{request.BindAddress}:{request.Port}/"), request.OwnerEmail, string.Empty, request.ProductRoot, root,
            $"{root}/config", $"{root}/state", 1000, 1000, "v1", request.LatitudeDegrees, request.LongitudeDegrees, request.ElevationMeters,
            request.TimeZoneId, "4e1f0c9b7a2d5e8f3b6c9a1d4e7f0b3c6d9e2a5f8b1c4d7e0a3f6b9c2e5d8a1f", "7c2e5a8d1f4b7e0a3c6f9b2d5e8a1c4f7b0d3e6a9c2f5b8e1d4a7c0f3b6e9d2a",
            "2a5d8f1c4e7b0a3d6f9c2e5b8a1d4f7c0e3b6a9d2f5c8e1b4a7d0c3f6e9b2a5d", "default", "1", "1", "active",
            "9e2b5d8a1f4c7e0b3d6a9f2c5e8b1d4a7f0c3e6b9d2a5f8c1e4b7d0a3c6f9e2b", catalog, new ImageInstallationIdentity("registry", image, image, "amd64", null),
            new DockerDaemonIdentity("d", "h", "amd64", "29"), true, true, "owner-password-change-required", DateTimeOffset.UnixEpoch, OwnerPasswordIsDefault: true)
        {
            Plan = DeploymentPlan.Of(
            [
                new(DeploymentPlanAction.Create, "folder", root),
                new(DeploymentPlanAction.Create, "file", $"{root}/config/cameraagent.json"),
                new(DeploymentPlanAction.Create, "image", image, "imported from the release's archive"),
                new(DeploymentPlanAction.Create, "catalog", "/var/lib/hvo-sky/catalogs/hyg-v4.2"),
                new(DeploymentPlanAction.Create, "container", $"hvo-sky-cameraagent-{request.InstanceId:N}", "2 CPUs, 4 GiB, 256 PIDs"),
                new(DeploymentPlanAction.Change, "record", "/etc/hvo-sky/install.json", "adds this instance")
            ])
        };
    }

    public void Dispose()
    {
        PlanReleased.Set();
        PlanReleased.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private async Task<InstallationResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        if (request.DryRun)
        {
            await Task.Run(() => PlanReleased.Wait(TimeSpan.FromSeconds(30), cancellationToken), cancellationToken);
            if (PlanFailure?.Invoke(request) is { } failure)
            {
                throw failure;
            }
        }

        return Result(request, planned: request.DryRun);
    }
}

/// <summary>
/// The wizard on <see cref="WizardHost"/>, drawn by Terminal.Gui's ANSI driver in memory with a virtual clock: keys go
/// in, and the screen comes out as text, as the colours of its cells, and as ANSI (<see cref="Render"/>), which CI draws
/// as the docs' screenshots. Work the wizard sends off its thread (the plan check) comes back only when the loop is
/// pumped (<see cref="Pump"/>, <see cref="WaitIdle"/>).
/// </summary>
internal sealed class WizardDriver : IDisposable
{
    private readonly bool _owns;
    private SessionToken? _token;

    private WizardDriver(IApplication app, InstallWizard wizard, bool owns = true)
    {
        App = app;
        Wizard = wizard;
        _owns = owns;
        _token = App.Begin(Wizard.Window);
    }

    /// <summary>Drives a wizard someone else made and disposes (the runner's), for as long as the returned driver lives.</summary>
    public static WizardDriver Attach(IApplication app, InstallWizard wizard) => new(app, wizard, owns: false);

    public IApplication App { get; }

    public InstallWizard Wizard { get; }

    public InstallWizardSession Session => Wizard.Session;

    public WizardPage Page => Wizard.Page;

    public bool Closed => ((IRunnable)Wizard.Window).StopRequested;

    public T PageOf<T>()
        where T : WizardPage
        => Wizard.Pages.OfType<T>().Single();

    /// <summary>A Terminal.Gui application on the in-memory ANSI driver, <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static IApplication CreateApplication(int width, int height)
    {
        var app = Application.Create(new VirtualTimeProvider());
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        return app;
    }

    /// <summary>Starts the wizard for <paramref name="args"/> on <paramref name="host"/>, as the installer does.</summary>
    public static WizardDriver Start(WizardHost host, string[] args, int width = 80, int height = 24)
    {
        ArgumentNullException.ThrowIfNull(host);
        var services = host.Services;
        var session = new InstallWizardSession(args, services, services.Terminal, WizardHost.Version, WizardHost.InstanceId);
        var app = CreateApplication(width, height);
        var theme = HvoTheme.For(host.Environment);
        theme.Prepare(app);
        return new WizardDriver(app, new InstallWizard(app, session, theme));
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

    /// <summary>The colours of every cell drawn.</summary>
    public IEnumerable<TuiAttribute> DrawnAttributes()
    {
        App.LayoutAndDraw(true);
        var contents = App.Driver!.Contents!;
        for (var row = 0; row < contents.GetLength(0); row++)
        {
            for (var column = 0; column < contents.GetLength(1); column++)
            {
                if (contents[row, column].Attribute is { } attribute)
                {
                    yield return attribute;
                }
            }
        }
    }

    public void Press(Key key)
    {
        App.InjectKey(key);
        Pump();
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

    /// <summary>Runs the loop until no work is on its way and <paramref name="until"/> holds, or fails after 10 s.</summary>
    public void WaitIdle(string what, Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            Pump();
            if (Wizard.PendingOperations == 0 && (until?.Invoke() ?? true))
            {
                Pump();
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}. Pending: {Wizard.PendingOperations}. Screen:\n{Screen}");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>Presses Enter (Next) until the page is a <typeparamref name="T"/>.</summary>
    public T NextTo<T>()
        where T : WizardPage
    {
        for (var step = 0; step < 10 && Page is not T; step++)
        {
            var from = Page;
            Press(Key.Enter);
            WaitIdle($"the page after {from.StepName}");
            if (Page == from)
            {
                throw new AssertFailedException($"Next stayed on {from.StepName}: '{Wizard.Notice.Message}'. Screen:\n{Screen}");
            }
        }

        return (T)Page;
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

        if (_owns)
        {
            Wizard.Dispose();
            App.Dispose();
        }
    }
}
