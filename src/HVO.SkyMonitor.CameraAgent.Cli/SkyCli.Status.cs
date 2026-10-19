using System.CommandLine;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateStatusCommand()
        {
            var watch = new Option<bool>("--watch", "-w")
            {
                Description = "Read the status again every --interval seconds and write one line per change, and a line when a read fails, until Ctrl+C or a refused key (exit 3). --timeout bounds each read, its request and its answer, not the watch. Ctrl+C ends it at once: exit 0 when its last read succeeded, 1 when it did not."
            };
            var interval = new Option<int?>("--interval")
            {
                Description = "With --watch: seconds between reads, 1 to 3600 (default 2)."
            };
            var command = new Command("status", "Show the running CameraAgent's status: capture, queues, storage and LogicHost delivery. With --watch, follow it until Ctrl+C.")
            {
                watch,
                interval
            };
            SetAction(command, (context, parseResult, cancellationToken) =>
            {
                var seconds = parseResult.GetValue(interval);
                if (seconds is not null && !parseResult.GetValue(watch))
                {
                    throw new SkyCliUsageException("--interval is used with --watch.");
                }

                if (seconds is < 1 or > 3600)
                {
                    throw new SkyCliUsageException("--interval takes 1 to 3600 seconds.");
                }

                return parseResult.GetValue(watch)
                    ? WatchStatusAsync(context, seconds is { } s ? TimeSpan.FromSeconds(s) : context.Host.PollInterval, cancellationToken)
                    : ShowStatusAsync(context, cancellationToken);
            });
            return command;
        }

        private static async Task<int> ShowStatusAsync(SkyCliContext context, CancellationToken cancellationToken)
        {
            var connection = context.ResolveConnection();
            using var client = context.CreateClient(connection);
            var summary = await client.GetAsync(SkyRoutes.Summary, cancellationToken).ConfigureAwait(false);
            if (context.Json)
            {
                context.WriteJson(summary);
            }
            else
            {
                SkyCliFormat.WriteRows(context.Out, SkyStatusText.DescribeRows(summary, connection.Url, context.Host.Time.GetUtcNow()));
            }

            return (int)SkyExitCode.Success;
        }

        /// <summary>
        /// Follows the status by reading it every <paramref name="interval"/>. A line is written when the state changes,
        /// and when CameraAgent stops or starts answering. Ctrl+C ends the watch: it exits 0 when the last read
        /// succeeded and 1 when it did not (or none did), as a read that fails says nothing about CameraAgent's state.
        /// A key CameraAgent refuses ends it at once (exit 3), since reading again cannot change that. The watch runs until
        /// it is interrupted, so <c>--timeout</c> bounds each read rather than the whole; a read that runs out of time is a
        /// failed read. With <c>--json</c>, every line is one compact JSON document, the final error's too.
        /// </summary>
        private static async Task<int> WatchStatusAsync(SkyCliContext context, TimeSpan interval, CancellationToken cancellationToken)
        {
            context.JsonLines = true;
            var limit = context.Deadline?.Limit ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
            context.Deadline?.Stop();
            var connection = context.ResolveConnection();
            using var client = context.CreateClient(connection);
            string? lastState = null;
            string? lastFailure = null;
            var lastReadSucceeded = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var summary = await ReadSummaryAsync(context, client, limit, cancellationToken).ConfigureAwait(false);
                    var state = SkyStatusText.DescribeState(summary);
                    if (state != lastState || lastFailure is not null)
                    {
                        if (context.Json)
                        {
                            context.WriteJsonLine(summary);
                        }
                        else
                        {
                            await context.Out.WriteLineAsync($"{SkyCliFormat.Time(context.Host.Time.GetUtcNow())}  {state}");
                        }
                    }

                    lastState = state;
                    lastFailure = null;
                    lastReadSucceeded = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SkyApiException refused) when (refused.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    throw;
                }
                catch (Exception error) when (error is HttpRequestException or SkyApiException or SkyProtocolException
                    or SkyCliOutcomeException { Kind: "TimedOut" })
                {
                    var (_, kind, message) = SkyCliContext.Classify(error);
                    if (message != lastFailure)
                    {
                        var now = context.Host.Time.GetUtcNow();
                        if (context.Json)
                        {
                            context.WriteJsonLine(new { observedUtc = now, error = new { kind, message } });
                        }
                        else
                        {
                            await context.Out.WriteLineAsync($"{SkyCliFormat.Time(now)}  NOT READ: {message}");
                        }
                    }

                    lastFailure = message;
                    lastReadSucceeded = false;
                }

                try
                {
                    await Task.Delay(interval, context.Host.Time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            return (int)(lastReadSucceeded ? SkyExitCode.Success : SkyExitCode.Failed);
        }

        /// <summary>One read of the watch, within a deadline of its own: one that runs out of time is a failed read.</summary>
        private static async Task<JsonElement> ReadSummaryAsync(SkyCliContext context, SkyApiClient client, TimeSpan limit, CancellationToken cancellationToken)
        {
            using var read = new SkyCliDeadline(context.Host.Time, limit, cancellationToken);
            try
            {
                return await client.GetAsync(SkyRoutes.Summary, read.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (read.Expired && !cancellationToken.IsCancellationRequested)
            {
                throw new SkyCliOutcomeException(read.Describe(), SkyExitCode.Failed, "TimedOut");
            }
        }

        private Command CreateInfoCommand()
        {
            var command = new Command("info", "Show hvo-sky's own version, then CameraAgent's version, runtime, catalog, restart support and the key in use.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                var connection = context.ResolveConnection();
                using var client = context.CreateClient(connection);
                var document = await client.GetAsync(SkyRoutes.Info, cancellationToken).ConfigureAwait(false);
                var info = SkyApiClient.Read<SkyInfo>(document);
                if (context.Json)
                {
                    context.WriteJson(new
                    {
                        client = new { name = CommandName, version = context.Host.ClientVersion },
                        url = connection.Url,
                        credential = new { keyId = connection.KeyId, source = connection.KeySource },
                        info = document
                    });
                    return (int)SkyExitCode.Success;
                }

                SkyCliFormat.WriteRows(context.Out, SkyStatusText.DescribeInfo(info, connection, context.Host.ClientVersion));
                return (int)SkyExitCode.Success;
            });
            return command;
        }
    }
}

/// <summary>How <c>status</c> and <c>info</c> describe CameraAgent.</summary>
internal static class SkyStatusText
{
    public static IReadOnlyList<(string Label, string Value)> DescribeRows(JsonElement summary, Uri url, DateTimeOffset now)
    {
        var rows = new List<(string, string)>();
        var configuration = Section(summary, "configuration");
        var agent = SkyJson.Text(summary, "configuration", "value", "agentId");
        var module = SkyJson.Text(summary, "configuration", "value", "moduleType");
        rows.Add(("CameraAgent", agent is null ? url.ToString() : $"{url} (agent {agent}{(module is null ? string.Empty : ", " + module)})"));

        var control = SkyJson.Text(summary, "captureControl", "value", "state") ?? "unknown";
        var initialized = SkyJson.Boolean(summary, "captureControl", "value", "isInitialized");
        rows.Add(("Capture", control + (initialized == false ? ", not initialized" : string.Empty) + Freshness(summary, "captureControl")));

        var camera = SkyJson.Text(summary, "captureRuntime", "value", "availability") ?? "unknown";
        var last = SkyJson.Time(summary, "captureTelemetry", "value", "latestCaptureStartedUtc")
            ?? SkyJson.Time(summary, "captureRuntime", "value", "lastSucceededUtc");
        var perMinute = SkyJson.Number(summary, "captureTelemetry", "value", "capturesPerMinute");
        var cameraText = camera
            + (last is { } at ? $"; last frame {SkyCliFormat.Time(at)} ({SkyCliFormat.Duration(now - at)} ago)" : "; no frame yet")
            + (perMinute is { } rate ? $"; {SkyCliFormat.Number(rate, "0.0")} captures/min" : string.Empty);
        rows.Add(("Camera", cameraText + Freshness(summary, "captureRuntime")));

        if (configuration is not null)
        {
            var validation = SkyJson.Text(summary, "configuration", "value", "validationStatus") ?? "unknown";
            var current = SkyJson.Boolean(summary, "configuration", "value", "isCurrent");
            rows.Add(("Configuration", validation + (current == false ? ", not current" : string.Empty)));
        }

        rows.Add(("Raw ingress", Queue(summary, "rawIngress", bytes: true)));
        rows.Add(("Capture lanes", Queue(summary, "captureLanes", bytes: true)));
        rows.Add(("Processing", Queue(summary, "captureProcessing", bytes: false)));
        rows.Add(("Artifact outbox", Queue(summary, "artifactOutbox", bytes: true)));
        foreach (var store in SkyJson.Items(summary, "storage", "value"))
        {
            var alias = SkyJson.Text(store, "alias") ?? "storage";
            var notes = new List<string>();
            if (SkyJson.Boolean(store, "isUnderPressure") == true)
            {
                notes.Add("UNDER PRESSURE");
            }

            if (SkyJson.Boolean(store, "probeSucceeded") == false)
            {
                notes.Add("probe failed");
            }

            rows.Add(($"Storage {alias}",
                $"{SkyCliFormat.Bytes(SkyJson.Integer(store, "availableBytes"))} free of {SkyCliFormat.Bytes(SkyJson.Integer(store, "totalBytes"))}"
                + (notes.Count > 0 ? ", " + string.Join(", ", notes) : string.Empty)));
        }

        var central = SkyJson.Text(summary, "configuration", "value", "centralIntegration");
        var heartbeat = SkyJson.Text(summary, "heartbeat", "value", "availability");
        if (central is not null || heartbeat is not null)
        {
            var acknowledged = SkyJson.Time(summary, "heartbeat", "value", "lastAcknowledgedUtc");
            rows.Add(("LogicHost", $"{central ?? "unknown"}; heartbeat {heartbeat ?? "unknown"}"
                + (acknowledged is { } ack ? $", last acknowledged {SkyCliFormat.Time(ack)}" : string.Empty)
                + Freshness(summary, "heartbeat")));
        }

        rows.Add(("As of", SkyCliFormat.Time(SkyJson.Time(summary, "capturedUtc"))));
        return rows;
    }

    /// <summary>
    /// The state in one line, without times: capture control, the camera, the queues' pending counts, storage pressure
    /// and the configuration. <c>status --watch</c> writes a line when it changes.
    /// </summary>
    public static string DescribeState(JsonElement summary)
    {
        var pressure = SkyJson.Items(summary, "storage", "value").Any(static store => SkyJson.Boolean(store, "isUnderPressure") == true);
        var parts = new List<string>
        {
            $"capture {SkyJson.Text(summary, "captureControl", "value", "state") ?? "unknown"}",
            $"camera {SkyJson.Text(summary, "captureRuntime", "value", "availability") ?? "unknown"}",
            $"raw {SkyCliFormat.Count(SkyJson.Integer(summary, "rawIngress", "value", "pendingCount"))}",
            $"lanes {SkyCliFormat.Count(SkyJson.Integer(summary, "captureLanes", "value", "pendingCount"))}",
            $"processing {SkyCliFormat.Count(SkyJson.Integer(summary, "captureProcessing", "value", "pendingCount"))}",
            $"outbox {SkyCliFormat.Count(SkyJson.Integer(summary, "artifactOutbox", "value", "pendingCount"))}",
            pressure ? "storage UNDER PRESSURE" : "storage ok"
        };
        if (SkyJson.Text(summary, "configuration", "value", "validationStatus") is { } validation)
        {
            parts.Add($"configuration {validation}");
        }

        return string.Join("; ", parts);
    }

    public static IReadOnlyList<(string Label, string Value)> DescribeInfo(SkyInfo info, SkyCliConnection connection, string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(connection);
        var rows = new List<(string, string)>
        {
            (SkyCli.CommandName, clientVersion),
            (info.Application.Name, info.Application.InformationalVersion),
            ("Runtime", $"{info.Host.Runtime} ({info.Host.RuntimeIdentifier})"),
            ("Started", $"{SkyCliFormat.Time(info.Host.ProcessStartedUtc)} (up {SkyCliFormat.Duration(TimeSpan.FromSeconds(info.Host.UptimeSeconds))})")
        };
        if (info.Catalog is { ValueKind: JsonValueKind.Object } catalog)
        {
            rows.Add(("Catalog", $"{SkyJson.Text(catalog, "catalogId") ?? "unknown"} {SkyJson.Text(catalog, "catalogVersion") ?? string.Empty}".TrimEnd()
                + (SkyJson.Integer(catalog, "rowCount") is { } count ? $" ({SkyCliFormat.Count(count)} rows)" : string.Empty)));
        }

        rows.Add(("Restart", info.Restart.Requested
            ? "requested; CameraAgent is restarting"
            : info.Restart.Supervised
                ? $"supervised: '{SkyCli.CommandName} restart' restarts it"
                : "not supervised: nothing would start it again, so restart it with its service manager"));
        rows.Add(("Address", connection.Url.ToString()));
        rows.Add(("Credential", DescribeCaller(info.Caller) + $", from {connection.KeySource}"));
        return rows;
    }

    public static string DescribeCaller(SkyCallerInfo caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.Credential == "ApiKey"
            ? $"API key {caller.ApiKeyId ?? "unknown"} ({DescribeAccess(caller.Access)})"
            : "the owner's session";
    }

    public static string DescribeAccess(string? access) => access switch
    {
        "ReadWrite" => "read-write",
        "Read" => "read-only",
        null => "unknown access",
        _ => access
    };

    private static JsonElement? Section(JsonElement summary, string name) => SkyJson.Member(summary, name);

    private static string Queue(JsonElement summary, string name, bool bytes)
    {
        if (SkyJson.Member(summary, name, "value") is not { } value)
        {
            return "unknown";
        }

        var parts = new List<string>
        {
            $"{SkyCliFormat.Count(SkyJson.Integer(value, "pendingCount"))} pending"
                + (bytes && SkyJson.Integer(value, "pendingBytes") is { } size ? $" ({SkyCliFormat.Bytes(size)})" : string.Empty)
        };
        if (SkyJson.Integer(value, "leasedCount") is > 0 and var leased)
        {
            parts.Add($"{SkyCliFormat.Count(leased)} in progress");
        }

        if (SkyJson.Integer(value, "retryCount") is > 0 and var retry)
        {
            parts.Add($"{SkyCliFormat.Count(retry)} retrying");
        }

        parts.Add($"{SkyCliFormat.Count(SkyJson.Integer(value, "quarantineCount"))} quarantined");
        if (SkyJson.Text(value, "availability") is { } availability && availability != "Available")
        {
            parts.Add(availability);
        }

        return string.Join(", ", parts) + Freshness(summary, name);
    }

    private static string Freshness(JsonElement summary, string section)
        => SkyJson.Text(summary, section, "freshness") switch
        {
            "stale" => " (stale)",
            "unknown" => " (not observed yet)",
            "disabled" => " (disabled)",
            _ => string.Empty
        };
}
