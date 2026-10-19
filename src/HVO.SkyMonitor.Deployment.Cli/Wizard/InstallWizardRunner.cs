using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;

namespace HVO.SkyMonitor.Deployment.Wizard;

/// <summary>
/// Runs the installer's wizard and then the install it produced (install contract section 5, #1317):
/// <list type="number">
/// <item>The command line is read as <c>cameraagent install</c> reads it; a mistake in it is a usage error, with the
/// install's usage, before anything opens.</item>
/// <item>The wizard asks what the command line left unanswered and reviews the plan. Esc or Ctrl+C closes it: exit 130,
/// nothing changed.</item>
/// <item>On Install it has saved the answers; the terminal is the installer's again, and the install runs as
/// <c>cameraagent install --answers FILE</c> through <see cref="Program.RunAsync"/>, the same path, output and exit
/// codes as that command typed. <c>sudo</c> can ask for a password there, which it could not over the wizard.</item>
/// <item>Before and after it, a panel in the wizard's style says what is happening, and how to carry on.</item>
/// </list>
/// </summary>
internal static class InstallWizardRunner
{
    /// <summary>The production wizard: Terminal.Gui on this process's terminal.</summary>
    public static Task<int> RunAsync(string[] args, ProgramServices services, CancellationToken cancellationToken)
        => RunAsync(args, services, Interactive.System, cancellationToken);

    /// <summary>Runs the wizard with <paramref name="interactive"/>'s terminal, which a test replaces to drive it.</summary>
    public static async Task<int> RunAsync(string[] args, ProgramServices services, Interactive interactive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(interactive);
        InstallWizardSession session;
        try
        {
            session = new InstallWizardSession(args, services, services.Terminal, InstallerBuildIdentity.Current);
        }
        catch (InstallUsageException exception)
        {
            // The command line's own mistake, before anything opens: reported as the command line reports one, with the
            // install's usage (exit 2).
            exception.Usage ??= CommandLine.CommandUsage(CommandLine.Commands.Single(command => command.Name == "cameraagent install"));
            throw;
        }

        var theme = HvoTheme.For(services.Terminal.Environment);
        var result = InstallWizardResult.Cancelled;
        IApplication app;
        try
        {
            app = interactive.CreateApplication();
            theme.Prepare(app);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A terminal Terminal.Gui cannot draw on is as good as none: nothing is asked, and the command line says what
            // it lacks.
            await interactive.Error.WriteLineAsync($"The wizard could not start on this terminal: {Redaction.SafeDiagnostic(exception.Message)}").ConfigureAwait(false);
            return await Program.RunAsync(InstallWizardGate.InstallArguments(args), services with { Wizard = null }, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var wizard = new InstallWizard(app, session, theme);
            using (cancellationToken.Register(() => wizard.Post(wizard.Cancel)))
            {
                interactive.Run(app, wizard);
            }

            result = wizard.Result;
        }
        finally
        {
            app.Dispose();
        }

        var text = new HvoConsoleText(theme);
        var width = interactive.Width();
        if (result != InstallWizardResult.Install || cancellationToken.IsCancellationRequested)
        {
            await interactive.Error.WriteAsync(text.Notice(HvoNotice.Info, "Cancelled: nothing was changed.", width, "[CANCELLED]")).ConfigureAwait(false);
            return (int)InstallerExitCode.Interrupted;
        }

        var arguments = session.Arguments(session.AnswersPath);
        var command = $"{InstallerProgram.Current} {Quote(arguments)}";
        await interactive.Out.WriteAsync(text.Panel("Installing", Installing(session, command), width) + command + "\n\n").ConfigureAwait(false);
        var exitCode = await Program.RunAsync(arguments, services with { Wizard = null }, cancellationToken).ConfigureAwait(false);
        await interactive.Out.WriteAsync(Outcome(text, exitCode, session, command, width)).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>What the installing panel says: what runs, that <c>sudo</c> may ask, and that Ctrl+C stops it.</summary>
    internal static IReadOnlyList<HvoLine> Installing(InstallWizardSession session, string command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        return
        [
            new($"Carrying out the plan you reviewed for {session.Answers.FriendlyName}, instance {session.Answers.InstanceId:D}."),
            new("The installer asks sudo for the steps that need root: it may ask for your password below.", HvoTone.Muted),
            new("It takes a few minutes. Ctrl+C stops it; running the same command again carries on.", HvoTone.Muted),
            new(HvoConsoleText.Field("Answers", session.AnswersPath, 10)),
            new("The command, under this panel, is the one to run again.", HvoTone.Muted)
        ];
    }

    /// <summary>
    /// The panel after the install: done, or why it stopped and how to carry on, by its exit code, with the command to run
    /// again under it on a line of its own, so that it can be copied whole.
    /// </summary>
    internal static string Outcome(HvoConsoleText text, int exitCode, InstallWizardSession session, string command, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        var saved = new HvoLine(HvoConsoleText.Field("Answers", session.AnswersPath, 10));
        var panel = exitCode switch
        {
            (int)InstallerExitCode.Done => text.Notice(HvoNotice.Success, "The install finished.", width)
                + text.Panel("Done", [saved, new("Running the command below again changes nothing.", HvoTone.Muted)], width),
            (int)InstallerExitCode.Refused => text.Notice(HvoNotice.Danger, "The installer refused: nothing was changed.", width, "[REFUSED]")
                + text.Panel("What next", [saved, new("Correct what it says above, then run the command below again.", HvoTone.Muted)], width),
            (int)InstallerExitCode.Usage => text.Notice(HvoNotice.Danger, "The answers are not valid: see above. Nothing was changed.", width)
                + text.Panel("What next", [saved, new($"Correct the answers file, or run {InstallerProgram.Current} without --answers to use the wizard again.", HvoTone.Muted)], width),
            (int)InstallerExitCode.Interrupted => text.Notice(HvoNotice.Warning, "Interrupted.", width, "[STOPPED]")
                + text.Panel("What next", [saved, new("What it finished is kept. Run the command below again to carry on.", HvoTone.Muted)], width),
            _ => text.Notice(HvoNotice.Danger, "A step failed: see above.", width, "[STOPPED]")
                + text.Panel("What next", [saved, new("What it finished is kept. Run the command below again to carry on, with --resume where the message says so.", HvoTone.Muted)], width)
        };
        return panel + command + "\n";
    }

    /// <summary><paramref name="arguments"/> as a shell would take them back: each in single quotes when it needs quoting.</summary>
    public static string Quote(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(argument =>
            argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=@+,".Contains(c, StringComparison.Ordinal))
                ? argument
                : $"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'"));
    }

    /// <summary>
    /// The terminal the wizard draws on: how its Terminal.Gui application is made and run, where the panels around it are
    /// written, and how wide they are. <see cref="System"/> is this process's; a test drives the wizard itself.
    /// </summary>
    /// <param name="CreateApplication">Makes the Terminal.Gui application, initialised.</param>
    /// <param name="Run">Runs the wizard until it closes.</param>
    /// <param name="Out">Where the panels go.</param>
    /// <param name="Error">Where the cancellation goes.</param>
    /// <param name="Width">How wide the panels are.</param>
    internal sealed record Interactive(
        Func<IApplication> CreateApplication,
        Action<IApplication, InstallWizard> Run,
        TextWriter Out,
        TextWriter Error,
        Func<int> Width)
    {
        /// <summary>This process's terminal.</summary>
        public static Interactive System { get; } = new(
            static () =>
            {
                var app = Application.Create();
                app.Init();
                return app;
            },
            static (app, wizard) => app.Run(wizard.Window),
            Console.Out,
            Console.Error,
            static () => Math.Clamp(SafeWidth(), 60, 100));

        private static int SafeWidth()
        {
            try
            {
                return Console.WindowWidth;
            }
            catch (IOException)
            {
                return 80;
            }
        }
    }
}
