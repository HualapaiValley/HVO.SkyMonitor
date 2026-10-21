using System.CommandLine;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateTimeDataCommand()
        {
            var command = new Command("time-data",
                "Show the Earth-orientation and leap-second data satellite planning uses: the active source (override, managed or bundled), its coverage and expiry, the last time-data refresh attempt and the last adoption. Read-only; turn the refresh on with 'hvo-sky config set timeDataRefresh.enabled=true'.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                var connection = context.ResolveConnection();
                using var client = context.CreateClient(connection);
                var status = await client.GetAsync(SkyRoutes.TimeData, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(status);
                }
                else
                {
                    SkyCliFormat.WriteRows(context.Out, SkyTimeDataText.DescribeRows(status));
                }

                return (int)SkyExitCode.Success;
            });
            return command;
        }
    }
}

/// <summary>The text form of <c>GET /api/v1/operations/time-data</c>.</summary>
internal static class SkyTimeDataText
{
    public static IEnumerable<(string Label, string Value)> DescribeRows(JsonElement status)
    {
        var problem = SkyJson.Text(status, "problem") ?? "unknown";
        var source = SkyJson.Text(status, "activeSource") ?? "unknown";
        yield return ("Planning", SkyJson.Boolean(status, "planningEnabled") == true ? "on" : "off");
        yield return ("Active source", problem == "none" ? source : $"{source} (not active: {problem})");
        var end = SkyJson.Time(status, "coverageEndUtc");
        yield return ("Coverage", end is null
            ? "none"
            : SkyJson.Boolean(status, "expired") == true
                ? $"EXPIRED: planning was covered until {SkyCliFormat.Time(end)}"
                : $"planning covered until {SkyCliFormat.Time(end)}");
        yield return ("Earth orientation to", SkyCliFormat.Time(SkyJson.Time(status, "earthOrientationEndUtc")));
        yield return ("Leap seconds expire", SkyCliFormat.Time(SkyJson.Time(status, "leapSecondsExpireUtc")));
        yield return ("Refresh", (SkyJson.Boolean(status, "refreshEnabled") == true ? "on" : "off") +
            (SkyJson.Boolean(status, "refreshScheduled") == true ? ", daily automation set up" : ", daily automation not set up"));
        var outcome = SkyJson.Text(status, "refresh", "outcome") ?? "unknown";
        var attempt = SkyJson.Time(status, "refresh", "lastAttemptUtc");
        var reason = SkyJson.Text(status, "refresh", "reasonCode");
        yield return ("Last attempt", attempt is null
            ? "never"
            : $"{outcome} ({SkyCliFormat.Time(attempt)}{(reason is null ? string.Empty : ", " + reason)})");
        if (SkyJson.Text(status, "refresh", "detail") is { Length: > 0 } detail)
        {
            yield return ("Attempt detail", detail);
        }

        if (SkyJson.Time(status, "refresh", "nextPermittedAttemptUtc") is { } next)
        {
            yield return ("Next permitted attempt", SkyCliFormat.Time(next));
        }

        var adopted = SkyJson.Time(status, "managedPair", "adoptedUtc");
        yield return ("Last adoption", adopted is null
            ? "none"
            : $"{SkyCliFormat.Time(adopted)} ({SkyJson.Text(status, "managedPair", "state") ?? "unknown"})");
    }
}
