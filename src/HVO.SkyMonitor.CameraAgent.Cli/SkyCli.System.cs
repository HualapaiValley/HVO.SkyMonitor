using System.CommandLine;
using System.Net;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

/// <summary>What a restart did: the process before it, CameraAgent's answer, and the new process when it was waited for.</summary>
internal sealed record SkyRestartOutcome(SkyInfo Before, SkyRestartResponse Response, SkyInfo? After, TimeSpan Elapsed);

internal static partial class SkyCli
{
    /// <summary>How long a restart waits for CameraAgent to answer again by default.</summary>
    internal static readonly TimeSpan DefaultRestartTimeout = TimeSpan.FromSeconds(180);

    internal sealed partial class CommandBuilder
    {
        private static Option<bool> NoWaitOption() => new("--no-wait")
        {
            Description = "Return once CameraAgent has accepted the restart, without waiting for it to come back."
        };

        private static Option<int?> TimeoutOption() => new("--timeout")
        {
            Description = $"Seconds to wait for CameraAgent to come back, 1 to 3600 (default {(int)DefaultRestartTimeout.TotalSeconds})."
        };

        private static TimeSpan ReadTimeout(System.CommandLine.ParseResult parseResult, Option<int?> option)
            => parseResult.GetValue(option) switch
            {
                null => DefaultRestartTimeout,
                < 1 or > 3600 => throw new SkyCliUsageException("--timeout takes 1 to 3600 seconds."),
                { } seconds => TimeSpan.FromSeconds(seconds)
            };

        private Command CreateRestartCommand()
        {
            var force = ForceOption("Restart without asking. Needed without a terminal, and with --json.");
            var noWait = NoWaitOption();
            var timeout = TimeoutOption();
            var command = new Command(
                "restart",
                "Restart CameraAgent, so settings and a staged rig that apply at its next start take effect. Capture stops until it is back. It restarts itself only where something starts it again (its container's restart policy); otherwise CameraAgent refuses.")
            {
                force,
                noWait,
                timeout
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var wait = ReadTimeout(parseResult, timeout);
                var connection = context.ResolveConnection();
                if (!parseResult.GetValue(force)
                    && !context.Confirm($"Restart CameraAgent at {connection.Url}? Capture stops until it is back.", "--force"))
                {
                    return (int)SkyExitCode.Refused;
                }

                using var client = context.CreateClient(connection);
                var outcome = await RestartAsync(context, client, !parseResult.GetValue(noWait), wait, cancellationToken).ConfigureAwait(false);
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
    /// before anything is sent (exit 3). <paramref name="progress"/>, when given, is told what it waits for instead of
    /// standard error (the terminal interface's).
    /// </summary>
    internal static async Task<SkyRestartOutcome> RestartAsync(
        SkyCliContext context,
        SkyApiClient client,
        bool wait,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(client);
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
            await client.SendAsync(HttpMethod.Post, SkyRoutes.Restart, new { }, cancellationToken).ConfigureAwait(false));
        if (!wait)
        {
            return new SkyRestartOutcome(before, response, null, TimeSpan.Zero);
        }

        var waiting = $"Restart requested; waiting up to {(int)timeout.TotalSeconds} s for CameraAgent to come back.";
        if (progress is not null)
        {
            // The terminal interface says it on its own screen; standard error belongs to Terminal.Gui there.
            progress(waiting);
        }
        else if (!context.Json)
        {
            await context.Error.WriteLineAsync(waiting);
        }

        var deadline = started + timeout;
        while (true)
        {
            try
            {
                var now = await client.GetAsync<SkyInfo>(SkyRoutes.Info, cancellationToken).ConfigureAwait(false);
                if (now.Host.ProcessStartedUtc != before.Host.ProcessStartedUtc)
                {
                    return new SkyRestartOutcome(before, response, now, context.Host.Time.GetUtcNow() - started);
                }
            }
            catch (SkyApiException refused) when (refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is HttpRequestException
                or SkyApiException or SkyProtocolException or TimeoutException or TaskCanceledException)
            {
                // Expected while the old process stops and the new one starts.
            }

            if (context.Host.Time.GetUtcNow() >= deadline)
            {
                throw new SkyCliOutcomeException(
                    $"CameraAgent did not come back within {(int)timeout.TotalSeconds} s of the restart. It may still be starting: check with '{CommandName} status'.",
                    SkyExitCode.Failed,
                    "Failed");
            }

            await Task.Delay(context.Host.PollInterval, context.Host.Time, cancellationToken).ConfigureAwait(false);
        }
    }
}
