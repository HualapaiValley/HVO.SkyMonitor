using System.CommandLine;
using HVO.SkyMonitor.CameraAgent.Cli.Client;

namespace HVO.SkyMonitor.CameraAgent.Cli;

internal static partial class SkyCli
{
    internal sealed partial class CommandBuilder
    {
        private Command CreateAstrometryCommand()
        {
            const string graphs = SkyApiClient.OperationsRoute + "processing-graphs";
            var magnitude = new Option<double?>("--reference-magnitude")
            {
                Description = "Limit for the installed reference stars. Required on real cameras. VirtualSky keeps an existing coherent limit or defaults to 3.25 for a new branch."
            };
            var reason = ReasonOption();
            var configure = new Command("configure", "Create, validate, and activate an optional measured astrometry branch on the active graph.")
            {
                magnitude,
                reason
            };
            SetAction(configure, async (context, parseResult, cancellationToken) =>
            {
                var referenceMagnitude = parseResult.GetValue(magnitude);
                if (referenceMagnitude is { } value && (!double.IsFinite(value) || value is < -30 or > 30))
                    throw new SkyCliUsageException("Reference magnitude must be finite and between -30 and 30.");
                var why = ReadReason(parseResult, reason);
                using var client = context.Connect();
                var registry = await client.GetAsync(graphs + "/", cancellationToken).ConfigureAwait(false);
                var basisRevisionId = SkyJson.Text(registry, "activeRevisionId")
                    ?? throw new SkyProtocolException("CameraAgent did not return the active graph revision.");
                var expectedVersion = SkyJson.Integer(registry, "stateVersion")
                    ?? throw new SkyProtocolException("CameraAgent did not return the graph registry version.");
                var draft = await context.SendChangeAsync(client, HttpMethod.Post, graphs + "/revisions/reference-astrometry",
                    new { basisRevisionId, revision = $"reference-astrometry-{Guid.NewGuid():N}", referenceMagnitude, reason = why },
                    "the astrometry draft", cancellationToken).ConfigureAwait(false);
                var revisionId = SkyJson.Text(draft, "revisionId")
                    ?? throw new SkyProtocolException("CameraAgent did not return the astrometry draft revision.");
                var selection = SkyJson.Member(draft, "catalogSelection")
                    ?? throw new SkyProtocolException("CameraAgent did not return the installed reference selection.");
                var revisionPath = graphs + "/revisions/" + Uri.EscapeDataString(revisionId);
                await context.SendChangeAsync(client, HttpMethod.Post, revisionPath + "/validate", new { reason = why },
                    "the astrometry draft validation", cancellationToken).ConfigureAwait(false);
                var active = await context.SendChangeAsync(client, HttpMethod.Post, revisionPath + "/activate", new { expectedVersion, reason = why },
                    "the astrometry activation", cancellationToken).ConfigureAwait(false);
                if (SkyJson.Text(active, "activeRevisionId") != revisionId)
                    throw new SkyProtocolException("CameraAgent did not confirm the new active astrometry revision.");
                return Report(context, new { revisionId, previousRevisionId = basisRevisionId, catalogSelection = selection },
                    $"Activated measured astrometry revision {revisionId}. Previous graph revision {basisRevisionId}.");
            });
            return new Command("astrometry", "Configure measured astrometry on a compatible camera profile.") { configure };
        }
    }
}
