using System.CommandLine;
using System.Net;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>What a restart did: the process before it, CameraAgent's answer, and the new process when it was waited for.</summary>
internal sealed record SkyRestartOutcome(SkyInfo Before, SkyRestartResponse Response, SkyInfo? After, TimeSpan Elapsed);

internal static partial class SkyCli
{
    internal sealed partial class CommandBuilder
    {
        private static Option<bool> NoWaitOption() => new("--no-wait")
        {
            Description = "Return once CameraAgent has accepted the restart, without waiting for it to come back."
        };

        private Command CreateRestartCommand()
        {
            var force = ForceOption("Restart without asking. Needed without a terminal, and with --json.");
            var noWait = NoWaitOption();
            var command = new Command(
                "restart",
                "Restart CameraAgent, so settings and a staged rig that apply at its next start take effect, and wait (within --timeout) until it is back. Capture stops until it is back. It restarts itself only where something starts it again (its container's restart policy); otherwise CameraAgent refuses.")
            {
                force,
                noWait
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var connection = context.ResolveConnection();
                if (!parseResult.GetValue(force)
                    && !context.Confirm($"Restart CameraAgent at {connection.Url}? Capture stops until it is back.", "--force"))
                {
                    return (int)SkyExitCode.Refused;
                }

                using var client = context.CreateClient(connection);
                var outcome = await RestartAsync(context, client, !parseResult.GetValue(noWait), "the restart", cancellationToken).ConfigureAwait(false);
                return ReportRestart(context, outcome);
            });
            return command;
        }

        private static int ReportRestart(SkyCliContext context, SkyRestartOutcome outcome)
        {
            if (context.Json)
            {
                context.WriteJson(new
                {
                    restarted = outcome.After is not null,
                    status = outcome.Response.Status,
                    previousStartedUtc = outcome.Before.Host.ProcessStartedUtc,
                    startedUtc = outcome.After?.Host.ProcessStartedUtc,
                    version = outcome.After?.Application.InformationalVersion,
                    waitedSeconds = outcome.After is null ? (double?)null : Math.Round(outcome.Elapsed.TotalSeconds, 1)
                });
            }
            else if (outcome.After is { } after)
            {
                context.Out.WriteLine(
                    $"CameraAgent is back: started {SkyCliFormat.Time(after.Host.ProcessStartedUtc)}, {SkyCliFormat.Duration(outcome.Elapsed)} after the request, running {after.Application.InformationalVersion}.");
            }
            else
            {
                context.Out.WriteLine($"CameraAgent is restarting. '{CommandName} status' answers again once it is back.");
            }

            return (int)SkyExitCode.Success;
        }
    }

    /// <summary>
    /// Asks CameraAgent to restart, and when <paramref name="wait"/> is set, waits until a new process answers: one with
    /// another start time than the process that took the request. A CameraAgent that nothing would start again refuses
    /// before anything is sent (exit 3). <paramref name="change"/> names the restart in what the command reports once
    /// CameraAgent has accepted it. The wait ends at the command's deadline, <paramref name="deadline"/>.
    /// <paramref name="progress"/>, when given, is told what it waits for instead of standard error (the terminal
    /// interface's), and <paramref name="waitingFor"/> words the timeout for a screen with its own status in place of the
    /// command's pointer to <c>hvo-sky status</c>.
    /// </summary>
    internal static async Task<SkyRestartOutcome> RestartAsync(
        SkyCliContext context,
        SkyApiClient client,
        bool wait,
        string change,
        CancellationToken cancellationToken,
        SkyCliDeadline? deadline = null,
        Action<string>? progress = null,
        string? waitingFor = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);
        deadline ??= context.Deadline;
        var before = await client.GetAsync<SkyInfo>(SkyRoutes.Info, cancellationToken).ConfigureAwait(false);
        if (!before.Restart.Supervised)
        {
            throw new SkyCliOutcomeException(
                $"CameraAgent at {client.Options.BaseAddress} is not supervised here, so it cannot restart itself: nothing would start it again. Nothing was sent; restart it with its service manager.",
                SkyExitCode.Refused,
                "Refused");
        }

        var started = context.Host.Time.GetUtcNow();
        var response = SkyApiClient.Read<SkyRestartResponse>(
            await context.SendChangeAsync(client, HttpMethod.Post, SkyRoutes.Restart, new { }, change, cancellationToken).ConfigureAwait(false));
        if (!wait)
        {
            return new SkyRestartOutcome(before, response, null, TimeSpan.Zero);
        }

        if (deadline is not null)
        {
            var waiting = $"Restart requested; waiting up to {SkyCliDeadline.Seconds(deadline.Remaining)} s for CameraAgent to come back.";
            if (progress is not null)
            {
                // The terminal interface says it on its own screen; standard error belongs to Terminal.Gui there.
                progress(waiting);
            }
            else if (!context.Json)
            {
                await context.Error.WriteLineAsync(waiting);
            }
        }

        var after = await PollAsync(
            context,
            deadline,
            waitingFor ?? $"waiting for CameraAgent to come back. It may still be starting: check with '{CommandName} status'",
            async token =>
            {
                var now = await client.GetAsync<SkyInfo>(SkyRoutes.Info, token).ConfigureAwait(false);
                return now.Host.ProcessStartedUtc != before.Host.ProcessStartedUtc ? now : null;
            },
            cancellationToken).ConfigureAwait(false);
        return new SkyRestartOutcome(before, response, after, context.Host.Time.GetUtcNow() - started);
    }

    /// <summary>
    /// Reads CameraAgent every poll interval until <paramref name="read"/> returns a result, within the command's
    /// deadline. A read that fails while CameraAgent is not answering, or not answering properly yet (as while it
    /// restarts), is read again; a key it refuses (HTTP 401 or 403) ends the wait at once, since reading again cannot
    /// change that, and so does any outcome <paramref name="read"/> throws. When the deadline passes, the wait ends with
    /// exit 1, saying what it was <paramref name="waitingFor"/>.
    /// </summary>
    internal static async Task<T> PollAsync<T>(
        SkyCliContext context,
        SkyCliDeadline? deadline,
        string waitingFor,
        Func<CancellationToken, Task<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            while (true)
            {
                try
                {
                    if (await read(cancellationToken).ConfigureAwait(false) is { } result)
                    {
                        return result;
                    }
                }
                catch (SkyApiException refused) when (refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw;
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested
                    && error is HttpRequestException or SkyApiException or SkyProtocolException)
                {
                    // Expected while the old process stops and the new one starts.
                }

                await Task.Delay(context.Host.PollInterval, context.Host.Time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline is { Expired: true })
        {
            throw new SkyCliOutcomeException(deadline.Describe(waitingFor), SkyExitCode.Failed, "TimedOut");
        }
    }
}
