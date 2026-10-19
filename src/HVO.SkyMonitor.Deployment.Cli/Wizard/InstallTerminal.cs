namespace HVO.SkyMonitor.Deployment.Wizard;

/// <summary>
/// The terminal the installer runs on, if any: whether a person can be asked (its input and output are both a terminal,
/// and the terminal is not <c>dumb</c>), and the environment the wizard's colours and frames are chosen from. Decided
/// once, where the process starts, and carried on <see cref="ProgramServices"/>, so a test states its own.
/// </summary>
/// <param name="IsInteractive">Whether a person is at a terminal.</param>
/// <param name="Environment">The process environment.</param>
internal sealed record InstallTerminal(bool IsInteractive, Func<string, string?> Environment)
{
    /// <summary>No terminal: nothing is asked.</summary>
    public static InstallTerminal None { get; } = new(false, static _ => null);

    /// <summary>This process's terminal.</summary>
    public static InstallTerminal Current()
        => new(
            !Console.IsInputRedirected && !Console.IsOutputRedirected &&
            !string.Equals(System.Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal),
            System.Environment.GetEnvironmentVariable);
}

/// <summary>
/// When the installer runs its wizard (install contract section 5, #1317): for <c>cameraagent install</c>, or no command
/// at all, on a terminal, when the command line leaves a question the install cannot go without (the camera's name,
/// its software, its catalog), and never with <c>--answers</c>, <c>--config</c>, <c>--plan</c>, <c>--dry-run</c>,
/// <c>--json</c>, <c>--resume</c> or a request for help. Everywhere else it asks nothing: without a terminal a missing
/// value is a usage error (exit 2) that names it, and a command line that answers everything installs as before.
/// </summary>
internal static class InstallWizardGate
{
    private static readonly string[] Excluding = [CommandLine.AnswersOption, "--config", CommandLine.PlanOption, "--dry-run", "--json", "--resume"];

    /// <summary>Whether <paramref name="args"/> on <paramref name="terminal"/> run the wizard.</summary>
    public static bool Wants(string[] args, InstallTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!terminal.IsInteractive || !IsInstall(args) || CommandLine.HelpFor(args) is not null)
        {
            return false;
        }

        Dictionary<string, string?> values;
        HashSet<string> flags;
        try
        {
            (values, flags) = CommandLine.ReadInstallOptions(InstallArguments(args));
        }
        catch (InstallUsageException)
        {
            // A command line that cannot be read is reported by the command line, as it always is.
            return false;
        }

        if (Excluding.Any(option => values.ContainsKey(option) || flags.Contains(option)))
        {
            return false;
        }

        return MissingRequired(values).Any();
    }

    /// <summary>The required questions <paramref name="values"/> leaves unanswered, as the options that answer them.</summary>
    public static IEnumerable<string> MissingRequired(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.ContainsKey("--friendly-name"))
        {
            yield return "--friendly-name";
        }

        if (!values.ContainsKey("--release") && !values.ContainsKey("--image-ref"))
        {
            yield return "--release";
        }

        if (!values.ContainsKey("--catalog-bundle"))
        {
            yield return "--catalog-bundle";
        }
    }

    /// <summary><paramref name="args"/> as <c>cameraagent install</c> arguments: no command at all is an install.</summary>
    public static string[] InstallArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length == 0 ? ["cameraagent", "install"] : args;
    }

    private static bool IsInstall(string[] args) => args is [] or ["cameraagent", "install", ..];
}
