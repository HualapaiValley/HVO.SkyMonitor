using System.CommandLine;
using HVO.SkyMonitor.CameraAgent.Cli.Ui;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.App;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    internal sealed partial class CommandBuilder
    {
        /// <summary>
        /// <c>hvo-sky ui</c>: the full-screen terminal interface (<see cref="SkyTerminalUi"/>). It ends with exit 0 on F10,
        /// 130 on a signal, 2 when there is no terminal to draw on (or with <c>--json</c>, which it cannot write), and 1
        /// when Terminal.Gui cannot start on the terminal there is.
        /// </summary>
        private Command CreateUiCommand()
        {
            var command = new Command(
                "ui",
                "Open the full-screen terminal interface: CameraAgent's status, settings, camera and rig, schedule and system, over the same API and with the same checks as the commands. F1 to F5 choose a page, F10 quits. It needs a terminal; --timeout bounds each read and change.");
            SetAction(command, (context, _, cancellationToken) =>
            {
                if (context.Json)
                {
                    throw new SkyCliUsageException($"'{CommandName} ui' is interactive and writes no JSON: scripts use the other commands with --json.");
                }

                if (!context.Host.HasTerminal)
                {
                    throw new SkyCliUsageException($"'{CommandName} ui' needs a terminal for its input and output: scripts use the other commands.");
                }

                // The interface runs until F10, so --timeout bounds each read and change rather than the whole.
                context.Deadline?.Stop();
                var theme = HvoTheme.For(context.Host.GetEnvironmentVariable);
                IApplication app;
                try
                {
                    app = context.Host.CreateApplication();
                    theme.Prepare(app);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw new SkyCliOutcomeException(
                        $"The terminal interface could not start on this terminal: {error.Message} The other commands work without it.",
                        SkyExitCode.Failed, "Failed", error);
                }

                try
                {
                    using var ui = new SkyTerminalUi(context, app, theme);
                    using (cancellationToken.Register(() => ui.Post(ui.Quit)))
                    {
                        ui.Start();
                        context.Host.RunApplication(app, ui.Window);
                    }
                }
                finally
                {
                    app.Dispose();
                }

                // A signal closed it: reported as any interruption is (130).
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult((int)SkyExitCode.Success);
            });
            return command;
        }
    }
}
