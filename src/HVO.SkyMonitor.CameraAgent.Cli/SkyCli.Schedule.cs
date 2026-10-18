using System.CommandLine;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    /// <summary>The upcoming capture windows <c>schedule show</c> lists.</summary>
    private const int UpcomingWindows = 6;

    /// <summary>The largest capture profile file <c>schedule stage</c> reads.</summary>
    private const int MaximumProfileBytes = 1024 * 1024;

    internal sealed partial class CommandBuilder
    {
        private Command CreateScheduleCommand()
        {
            var command = new Command(
                "schedule",
                "The capture schedule: show the active and staged revisions, stage a revision from a capture profile, and apply it. An applied revision takes effect at the next capture boundary.");
            command.Add(CreateScheduleShowCommand());
            command.Add(CreateScheduleStageCommand());
            command.Add(CreateScheduleApplyCommand());
            return command;
        }

        private Command CreateScheduleShowCommand()
        {
            var command = new Command("show", "Show the active and staged schedule revisions, whether capture is admitted now, overrides, and the next capture windows.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var schedule = await client.GetAsync(SkyRoutes.Schedule, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(schedule);
                    return (int)SkyExitCode.Success;
                }

                var now = context.Host.Time.GetUtcNow();
                var rows = new List<(string, string)>
                {
                    ("Active", DescribeScheduleRevision(SkyJson.Member(schedule, "activeRevision"))),
                    ("Staged", SkyJson.Member(schedule, "pendingRevision") is { } pending
                        ? $"{DescribeScheduleRevision(pending)}; '{CommandName} schedule apply' activates it"
                        : "none"),
                    ("Capture now", (SkyJson.Boolean(schedule, "decision", "admitted") switch
                        {
                            true => "admitted",
                            false => "not admitted",
                            null => "unknown"
                        })
                        + (SkyJson.Time(schedule, "decision", "nextTransitionUtc") is { } next ? $"; next change {SkyCliFormat.Time(next)}" : string.Empty))
                };
                var overrides = SkyJson.Items(schedule, "overrides");
                rows.Add(("Overrides", overrides.Count == 0
                    ? "none"
                    : string.Join("; ", overrides.Select(static item =>
                        $"{SkyJson.Text(item, "id")} {SkyCliFormat.Time(SkyJson.Time(item, "startUtc"))} to {SkyCliFormat.Time(SkyJson.Time(item, "endUtc"))}"))));
                SkyCliFormat.WriteRows(context.Out, rows);

                var upcoming = SkyJson.Items(schedule, "preview", "intervals")
                    .Where(interval => SkyJson.Time(interval, "endUtc") is { } end && end > now)
                    .Take(UpcomingWindows)
                    .ToArray();
                if (upcoming.Length > 0)
                {
                    await context.Out.WriteLineAsync();
                    SkyCliFormat.WriteTable(
                        context.Out,
                        ["FROM", "TO", "SETPOINT"],
                        upcoming.Select(static interval => (IReadOnlyList<string>)
                        [
                            SkyCliFormat.Time(SkyJson.Time(interval, "startUtc")),
                            SkyCliFormat.Time(SkyJson.Time(interval, "endUtc")),
                            SkyJson.Text(interval, "setpointProfileId") ?? string.Empty
                        ]));
                }

                return (int)SkyExitCode.Success;
            });
            return command;
        }

        private static string DescribeScheduleRevision(JsonElement? revision)
        {
            if (revision is not { } value)
            {
                return "none";
            }

            var details = new[]
            {
                SkyJson.Integer(value, "revisionNumber") is { } number ? $"#{number}" : null,
                SkyJson.Text(value, "source") is { } source ? $"from {source}" : null,
                SkyJson.Time(value, "createdUtc") is { } created ? SkyCliFormat.Time(created) : null
            }.OfType<string>();
            return $"{SkyJson.Text(value, "revisionId") ?? "unknown"} ({string.Join(", ", details)})";
        }

        private Command CreateScheduleStageCommand()
        {
            var file = new Argument<string>("file")
            {
                Description = "A capture profile as JSON, or - for standard input: the 'profile' of a revision, as 'schedule show --json' gives it, edited."
            };
            RejectOption(file, allowDash: true);
            var basis = new Option<string?>("--basis")
            {
                Description = "The revision the profile is based on, whose module and step options it keeps (default: the active revision)."
            };
            var reason = ReasonOption();
            var command = new Command(
                "stage",
                "Stage a schedule revision from a capture profile. CameraAgent validates it; 'schedule apply' activates it at the next capture boundary.")
            {
                file,
                basis,
                reason
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var why = ReadReason(parseResult, reason);
                var profile = ReadProfile(context, parseResult.GetRequiredValue(file));
                using var client = context.Connect();
                var schedule = await client.GetAsync(SkyRoutes.Schedule, cancellationToken).ConfigureAwait(false);
                var version = SkyJson.Integer(schedule, "stateVersion")
                    ?? throw new SkyProtocolException("CameraAgent did not return the schedule's version.");
                var basisId = parseResult.GetValue(basis)?.Trim() is { Length: > 0 } given
                    ? given
                    : SkyJson.Text(schedule, "activeRevision", "revisionId")
                        ?? throw new SkyProtocolException("CameraAgent did not return the active schedule revision.");
                var staged = await context.SendChangeAsync(client, HttpMethod.Post, SkyRoutes.ScheduleStage, new
                {
                    profile,
                    basisRevisionId = basisId,
                    expectedVersion = version,
                    reason = why
                }, "the staged schedule revision", cancellationToken).ConfigureAwait(false);
                var id = SkyJson.Text(staged, "pendingRevision", "revisionId") ?? "unknown";
                return Report(context, staged,
                    $"Staged schedule revision {id}, based on {basisId}. '{CommandName} schedule apply' activates it at the next capture boundary.");
            });
            return command;
        }

        private static JsonElement ReadProfile(SkyCliContext context, string path)
        {
            string text;
            try
            {
                text = path == "-"
                    ? context.Host.IsInteractive
                        ? throw new SkyCliUsageException("'-' reads the capture profile from standard input, which is a terminal here.")
                        : context.ReadAllInput()
                    : new FileInfo(path).Length > MaximumProfileBytes
                        ? throw new SkyCliUsageException($"The capture profile is larger than {MaximumProfileBytes / 1024} KiB.")
                        : File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new SkyCliUsageException($"The capture profile cannot be read: {error.Message}");
            }

            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.Clone()
                    : throw new SkyCliUsageException("The capture profile must be a JSON object.");
            }
            catch (JsonException)
            {
                throw new SkyCliUsageException("The capture profile is not valid JSON.");
            }
        }

        private Command CreateScheduleApplyCommand()
        {
            var revision = new Argument<string?>("revision")
            {
                Description = "The revision to activate (default: the staged one). An older revision goes back to it by rollback; one CameraAgent does not list among its revisions is refused, as whether it is older cannot be known.",
                Arity = ArgumentArity.ZeroOrOne
            };
            RejectOption(revision);
            var reason = ReasonOption();
            var command = new Command("apply", "Activate the staged schedule revision, or the one named, at the next capture boundary.")
            {
                revision,
                reason
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var why = ReadReason(parseResult, reason);
                using var client = context.Connect();
                var schedule = await client.GetAsync(SkyRoutes.Schedule, cancellationToken).ConfigureAwait(false);
                var version = SkyJson.Integer(schedule, "stateVersion")
                    ?? throw new SkyProtocolException("CameraAgent did not return the schedule's version.");
                var id = parseResult.GetValue(revision)?.Trim() is { Length: > 0 } given
                    ? given
                    : SkyJson.Text(schedule, "pendingRevision", "revisionId");
                if (id is null)
                {
                    return Report(context, new { applied = false, revisionId = (string?)null },
                        "No schedule revision is staged; nothing to apply.");
                }

                if (id == SkyJson.Text(schedule, "activeRevision", "revisionId"))
                {
                    return Report(context, new { applied = false, revisionId = id },
                        $"Schedule revision {id} is already active; nothing to apply.");
                }

                var rollback = SkyScheduleDirection.IsRollback(schedule, id);
                var applied = await context.SendChangeAsync(client, HttpMethod.Post, rollback ? SkyRoutes.ScheduleRollback : SkyRoutes.ScheduleActivate, new
                {
                    revisionId = id,
                    expectedVersion = version,
                    reason = why
                }, SkyScheduleDirection.Describe(id, rollback), cancellationToken).ConfigureAwait(false);
                return Report(context, applied, $"Schedule revision {id} is active from the next capture boundary.");
            });
            return command;
        }
    }
}

/// <summary>
/// Whether activating a schedule revision goes forward (activate) or back (rollback), which CameraAgent's schedule API
/// takes as separate commands and refuses in the wrong direction. It is decided from the revision numbers CameraAgent
/// reports, never guessed: a revision it does not list (the schedule lists the active, the staged and the most recent
/// revisions), or one listed without a number, is refused before anything is sent.
/// </summary>
internal static class SkyScheduleDirection
{
    /// <summary>True when <paramref name="id"/> is older than the active revision, false when it is newer.</summary>
    /// <exception cref="SkyCliOutcomeException">The order cannot be established from what CameraAgent reported (exit 3).</exception>
    public static bool IsRollback(JsonElement schedule, string id)
    {
        var target = SkyJson.Items(schedule, "history").Append(SkyJson.Member(schedule, "pendingRevision") ?? default)
            .FirstOrDefault(candidate => candidate.ValueKind == JsonValueKind.Object && SkyJson.Text(candidate, "revisionId") == id);
        if (target.ValueKind != JsonValueKind.Object)
        {
            throw Unknown(id, "CameraAgent does not list it among its revisions (the active, the staged and the most recent ones). Check the name with 'hvo-sky schedule show --json'.");
        }

        var number = SkyJson.Integer(target, "revisionNumber");
        var active = SkyJson.Integer(schedule, "activeRevision", "revisionNumber");
        if (number is null || active is null)
        {
            throw Unknown(id, $"CameraAgent did not give the revision number of {(number is null ? "that revision" : "the active revision")}.");
        }

        return number < active;
    }

    /// <summary>What the command reports CameraAgent accepted: "the rollback to schedule revision X", or its activation.</summary>
    public static string Describe(string id, bool rollback)
        => rollback ? $"the rollback to schedule revision {id}" : $"the activation of schedule revision {id}";

    private static SkyCliOutcomeException Unknown(string id, string why) => new(
        $"Schedule revision {id} was not applied: whether schedule revision {id} is older or newer than the active revision cannot be established, because {why} Nothing was sent.",
        SkyExitCode.Refused,
        "Refused");
}
