using System.CommandLine;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateRigCommand()
        {
            var command = new Command(
                "rig",
                "The camera rig: show the active and staged rig revisions, stage one, and apply it. A staged rig takes effect when CameraAgent next starts.");
            command.Add(CreateRigShowCommand());
            command.Add(CreateRigStageCommand());
            command.Add(CreateRigApplyCommand());
            return command;
        }

        private Command CreateRigShowCommand()
        {
            var command = new Command("show", "Show the active rig revision, the staged one, and the rig revisions CameraAgent has.");
            SetAction(command, async (context, _, cancellationToken) =>
            {
                using var client = context.Connect();
                var rigs = await client.GetAsync(SkyRoutes.RigProfiles, cancellationToken).ConfigureAwait(false);
                if (context.Json)
                {
                    context.WriteJson(rigs);
                    return (int)SkyExitCode.Success;
                }

                var active = SkyJson.Text(rigs, "selection", "activeRevisionId");
                var pending = SkyJson.Text(rigs, "selection", "pendingRevisionId");
                var revisions = SkyJson.Items(rigs, "revisions");
                var rows = new List<(string, string)>
                {
                    ("Active", DescribeRevision(revisions, active)),
                    ("Staged", pending is null
                        ? "none"
                        : $"{DescribeRevision(revisions, pending)}; it applies when CameraAgent next starts ('{CommandName} rig apply')")
                };
                if (SkyJson.Text(rigs, "activeRuntimeFailure") is { } activeFailure)
                {
                    rows.Add(("Active failure", activeFailure));
                }

                if (SkyJson.Text(rigs, "pendingRuntimeFailure") is { } pendingFailure)
                {
                    rows.Add(("Staged failure", pendingFailure));
                }

                SkyCliFormat.WriteRows(context.Out, rows);
                if (revisions.Count > 0)
                {
                    await context.Out.WriteLineAsync();
                    SkyCliFormat.WriteTable(
                        context.Out,
                        ["REVISION", "PROFILE", "NUMBER", "MODULE", "SENSOR", "STATE"],
                        revisions.Select(revision =>
                        {
                            var id = SkyJson.Text(revision, "revisionId") ?? "?";
                            return (IReadOnlyList<string>)
                            [
                                id,
                                SkyJson.Text(revision, "profileId") ?? string.Empty,
                                SkyCliFormat.Count(SkyJson.Integer(revision, "revisionNumber")),
                                SkyJson.Text(revision, "moduleType") ?? string.Empty,
                                SkyJson.Text(revision, "sensorName") ?? string.Empty,
                                id == active ? "active" : id == pending ? "staged" : string.Empty
                            ];
                        }));
                }

                return (int)SkyExitCode.Success;
            });
            return command;
        }

        private static string DescribeRevision(IReadOnlyList<JsonElement> revisions, string? id)
        {
            if (id is null)
            {
                return "none";
            }

            var revision = revisions.FirstOrDefault(candidate => SkyJson.Text(candidate, "revisionId") == id);
            if (revision.ValueKind != JsonValueKind.Object)
            {
                return id;
            }

            var details = new[]
            {
                SkyJson.Text(revision, "profileId") is { } profile ? $"profile {profile}" : null,
                SkyJson.Integer(revision, "revisionNumber") is { } number ? $"revision {number}" : null,
                SkyJson.Text(revision, "moduleType"),
                SkyJson.Text(revision, "sensorName")
            }.OfType<string>();
            return $"{id} ({string.Join(", ", details)})";
        }

        private Command CreateRigStageCommand()
        {
            var revision = new Argument<string>("revision") { Description = "The rig revision to stage, as 'rig show' lists it." };
            RejectOption(revision);
            var acknowledge = new Option<bool>("--acknowledge-unvalidated")
            {
                Description = "Acknowledge that the rig is unvalidated at runtime: it takes effect at the next start, and its camera may fail to initialize then. Needed without a terminal, and with --json."
            };
            var command = new Command(
                "stage",
                "Stage a rig revision for the next CameraAgent start, after CameraAgent's preview checks it against the active schedule. 'rig apply' restarts CameraAgent to apply it.")
            {
                revision,
                acknowledge
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var id = parseResult.GetRequiredValue(revision).Trim();
                using var client = context.Connect();
                var rigs = await client.GetAsync(SkyRoutes.RigProfiles, cancellationToken).ConfigureAwait(false);
                var version = SkyJson.Integer(rigs, "selection", "version")
                    ?? throw new SkyProtocolException("CameraAgent did not return the rig selection's version.");
                var preview = await client.GetAsync(SkyRoutes.RigPreview + Uri.EscapeDataString(id), cancellationToken).ConfigureAwait(false);
                if (SkyJson.Boolean(preview, "valid") != true)
                {
                    throw new SkyCliOutcomeException(
                        $"Rig revision {id} cannot be staged: {DescribePreviewFailure(SkyJson.Text(preview, "failure"))} Nothing was sent.",
                        SkyExitCode.Refused, "Refused");
                }

                if (!parseResult.GetValue(acknowledge)
                    && !context.Confirm(
                        $"Rig revision {id} is unvalidated at runtime: it takes effect when CameraAgent next starts, and its camera may fail to initialize then. Stage it?",
                        "--acknowledge-unvalidated"))
                {
                    return (int)SkyExitCode.Refused;
                }

                var receipt = await context.SendChangeAsync(client, HttpMethod.Post, SkyRoutes.RigStage, new
                {
                    revisionId = id,
                    expectedVersion = version,
                    acknowledgeUnvalidated = true,
                    expectedScheduleRevisionId = SkyJson.Text(preview, "scheduleRevisionId"),
                    expectedScheduleProfileSha256 = SkyJson.Text(preview, "scheduleProfileSha256")
                }, $"the staging of rig revision {id}", cancellationToken).ConfigureAwait(false);
                return Report(context, receipt,
                    $"Staged rig revision {id}. It takes effect when CameraAgent next starts: '{CommandName} rig apply' restarts it now.");
            });
            return command;
        }

        private static string DescribePreviewFailure(string? failure) => failure switch
        {
            "pending-named-rig" => "another rig revision is already staged and awaits a restart ('hvo-sky rig apply').",
            "pending-schedule-draft" => "a schedule revision is staged; apply it first ('hvo-sky schedule apply').",
            null or "" => "CameraAgent's preview refused it.",
            _ => $"CameraAgent's preview refused it ({failure})."
        };

        private Command CreateRigApplyCommand()
        {
            var force = ForceOption("Restart without asking. Needed without a terminal, and with --json.");
            var noWait = NoWaitOption();
            var command = new Command(
                "apply",
                "Apply the staged rig revision by restarting CameraAgent, then wait (within --timeout) until it is the active one, or its camera fails to initialize. Capture stops until CameraAgent is back.")
            {
                force,
                noWait
            };
            SetAction(command, async (context, parseResult, cancellationToken) =>
            {
                var connection = context.ResolveConnection();
                using var client = context.CreateClient(connection);
                var rigs = await client.GetAsync(SkyRoutes.RigProfiles, cancellationToken).ConfigureAwait(false);
                if (SkyJson.Text(rigs, "selection", "pendingRevisionId") is not { } pending)
                {
                    return Report(context, new { applied = false, revisionId = (string?)null },
                        "No rig revision is staged; nothing to apply.");
                }

                if (!parseResult.GetValue(force)
                    && !context.Confirm($"Restart CameraAgent at {connection.Url} to apply rig revision {pending}? Capture stops until it is back.", "--force"))
                {
                    return (int)SkyExitCode.Refused;
                }

                var outcome = await RestartAsync(context, client, !parseResult.GetValue(noWait), $"the restart that applies rig revision {pending}", cancellationToken)
                    .ConfigureAwait(false);
                if (outcome.After is null)
                {
                    return Report(context, new { applied = (bool?)null, revisionId = pending, restarting = true },
                        $"CameraAgent is restarting to apply rig revision {pending}. '{CommandName} rig show' says whether it became active.");
                }

                await WaitForRigAsync(context, client, pending, cancellationToken).ConfigureAwait(false);
                return Report(context, new { applied = true, revisionId = pending, startedUtc = outcome.After.Host.ProcessStartedUtc },
                    $"Rig revision {pending} is active. CameraAgent restarted at {SkyCliFormat.Time(outcome.After.Host.ProcessStartedUtc)}.");
            });
            return command;
        }
    }

    /// <summary>
    /// After the restart, the staged rig becomes active only once its camera has initialized, so the rig is read until
    /// <paramref name="pending"/> is the active revision; until CameraAgent reports that it failed to initialize, or that
    /// it is no longer staged; or until the deadline.
    /// </summary>
    internal static Task<string> WaitForRigAsync(SkyCliContext context, SkyApiClient client, string pending, CancellationToken cancellationToken)
        => PollAsync(
            context,
            context.Deadline,
            $"waiting for rig revision {pending} to become active. '{CommandName} rig show' shows the rig in use",
            async token =>
            {
                var rigs = await client.GetAsync(SkyRoutes.RigProfiles, token).ConfigureAwait(false);
                if (SkyJson.Text(rigs, "selection", "activeRevisionId") == pending)
                {
                    return pending;
                }

                if ((SkyJson.Text(rigs, "pendingRuntimeFailure") ?? SkyJson.Text(rigs, "activeRuntimeFailure")) is { } failure)
                {
                    throw new SkyCliOutcomeException(
                        $"CameraAgent restarted, but rig revision {pending} did not become active: {failure} '{CommandName} rig show' shows the rig in use.",
                        SkyExitCode.Failed, "Failed")
                    { SaysWhatWasAccepted = true };
                }

                if (SkyJson.Text(rigs, "selection", "pendingRevisionId") != pending)
                {
                    throw new SkyCliOutcomeException(
                        $"CameraAgent restarted, but rig revision {pending} is no longer staged and did not become active. '{CommandName} rig show' shows the rig in use.",
                        SkyExitCode.Failed, "Failed")
                    { SaysWhatWasAccepted = true };
                }

                return null;
            },
            cancellationToken);
}
