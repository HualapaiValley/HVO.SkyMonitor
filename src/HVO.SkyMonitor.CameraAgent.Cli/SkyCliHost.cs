using System.Reflection;
using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>
/// What <c>hvo-sky</c> needs from the machine it runs on: its output, the environment, a way to ask a question or for a
/// secret, a clock, and the connection to CameraAgent. <see cref="System"/> is the real one; tests give their own,
/// pointing the connection at a fake API or an in-process CameraAgent.
/// </summary>
internal sealed class SkyCliHost
{
    /// <summary>Results, and JSON with <c>--json</c>.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Errors, progress and prompts, so <see cref="Out"/> stays clean for scripts.</summary>
    public required TextWriter Error { get; init; }

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The home directory, for the default credentials file when <c>XDG_CONFIG_HOME</c> is not set.</summary>
    public string UserProfile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Reads one line for <paramref name="prompt"/>, without echoing it when the second argument is true. Returns null
    /// at the end of the input. The prompt goes to <see cref="Error"/>; with input redirected, no prompt is shown and the
    /// line is read as it is.
    /// </summary>
    public required Func<string, bool, string?> ReadLine { get; init; }

    /// <summary>True when a person can answer a prompt (the input is a terminal).</summary>
    public bool IsInteractive { get; init; }

    /// <summary>Reads the whole of standard input, for a file given as <c>-</c>.</summary>
    public Func<string> ReadAllInput { get; init; } = static () => Console.In.ReadToEnd();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How often <c>status --watch</c> reads the status, and a restart's wait looks for the new process.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long one request may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>hvo-sky's own version: <c>&lt;version&gt;+&lt;commit&gt;</c>.</summary>
    public string ClientVersion { get; init; } = Version;

    /// <summary>For tests: the HTTP handler of every connection to CameraAgent.</summary>
    public Func<HttpMessageHandler>? CreateHandler { get; init; }

    /// <summary>This build's informational version, which <c>--version</c> prints.</summary>
    public static string Version { get; } =
        typeof(SkyCliHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// The console of this process. Output that cannot be written once the console is gone (the terminal closed) is
    /// dropped rather than failing the command.
    /// </summary>
    public static SkyCliHost System() => new()
    {
        Out = new SkyCliConsoleWriter(Console.Out),
        Error = new SkyCliConsoleWriter(Console.Error),
        IsInteractive = !Console.IsInputRedirected,
        ReadLine = ReadConsoleLine
    };

    private static string? ReadConsoleLine(string prompt, bool secret)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write(prompt);
        if (!secret)
        {
            return Console.ReadLine();
        }

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D)
            {
                Console.Error.WriteLine();
                return null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>
/// Writes to the console, and drops what cannot be written once the console is gone: after SIGHUP a write to the
/// closed terminal throws <see cref="IOException"/>, and a command must still end with its exit code.
/// </summary>
internal sealed class SkyCliConsoleWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override IFormatProvider FormatProvider => inner.FormatProvider;

    [global::System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine
    {
        get => inner.NewLine;
        set => inner.NewLine = value;
    }

    public override void Write(char value) => Try(() => inner.Write(value));

    public override void Write(string? value) => Try(() => inner.Write(value));

    public override void Write(char[] buffer, int index, int count) => Try(() => inner.Write(buffer, index, count));

    public override void WriteLine() => Try(inner.WriteLine);

    public override void WriteLine(string? value) => Try(() => inner.WriteLine(value));

    public override void Flush() => Try(inner.Flush);

    private static void Try(Action write)
    {
        try
        {
            write();
        }
        catch (IOException)
        {
            // The console is gone.
        }
    }
}
