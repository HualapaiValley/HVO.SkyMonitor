using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record CenteredPipelinePublication(CenteredViewBase Base, string RevisionId, string Role, CenteredView? View);
internal sealed record CenteredPipelineRunPublications(IReadOnlyList<CenteredPipelinePublication> Views, string? ExecutionVersion);

/// <summary>Reads publication identities scoped to the admitted stream and the recorded reference execution.</summary>
internal static partial class CenteredPipelinePublications
{
    public static async ValueTask<CenteredViewCurrent?> ReadLastLiveAsync(
        ICenteredViewReadIndex index, CameraKey camera, CancellationToken cancellationToken)
    {
        var current = await index.ReadLastLiveAsync(camera, cancellationToken).ConfigureAwait(false);
        if (current is not null && (!CenteredViewLinks.IsIdentity(current.View.Base.BaseId) ||
            !CenteredViewLinks.IsIdentity(current.View.Revision.RevisionId) ||
            current.View.Base.Definition.Stream.Camera != camera ||
            current.View.Revision.BaseId != current.View.Base.BaseId ||
            current.Interval.BaseId != current.View.Base.BaseId ||
            current.Interval.RevisionId != current.View.Revision.RevisionId ||
            current.Interval.EndedOrder is not null || current.Interval.EndedUtc is not null))
            throw new InvalidDataException("The retained live selection does not match its stream, base, and revision.");
        return current;
    }

    public static async ValueTask<IReadOnlyList<CenteredPipelinePublication>> ReadRunAsync(
        ICenteredViewReadIndex index, ICenteredViewLookup lookup, ProcessingGraphExecutionState execution,
        ILogger logger, CancellationToken cancellationToken)
    {
        var publications = new List<CenteredPipelinePublication>();
        foreach (var archive in await lookup.ReadArchiveAsync(execution.CaptureId, cancellationToken).ConfigureAwait(false))
        {
            if (!MatchesExecution(archive.Base.Definition))
                continue;

            var firstRevision = archive.LastLiveRevisionId ?? archive.InitialRevisionId;
            var role = archive.LastLiveRevisionId is null ? "Initial archived revision" : "Last issued live";
            await AddRevisionAsync(archive.Base, firstRevision, role).ConfigureAwait(false);
            if (archive.LatestRevisionId != firstRevision)
            {
                await AddRevisionAsync(archive.Base, archive.LatestRevisionId, "Later archived revision")
                    .ConfigureAwait(false);
            }
        }
        return publications;

        async ValueTask AddRevisionAsync(CenteredViewBase archivedBase, string revisionId, string role)
        {
            var baseId = archivedBase.BaseId;
            var view = await TryReadRevisionAsync(index, baseId, revisionId, logger, cancellationToken).ConfigureAwait(false);
            var matches = view is not null && view.Base.BaseId == baseId && view.Revision.BaseId == baseId &&
                view.Revision.RevisionId == revisionId && view.Base.Definition.Stream == archivedBase.Definition.Stream &&
                MatchesExecution(view.Base.Definition);
            publications.Add(new(archivedBase, revisionId, role, matches ? view : null));
        }

        bool MatchesExecution(CenteredViewBaseDefinition definition) =>
            definition.Reference.Capture.CaptureId == execution.CaptureId &&
            definition.Reference.ExecutionId == execution.ExecutionId &&
            definition.Reference.Input.Raw.Artifact.ArtifactId == execution.PrimaryArtifactId &&
            definition.Stream.GenerationId == execution.GraphRevisionId;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed read of one retained revision must not conceal other independently verified revisions.")]
    private static async ValueTask<CenteredView?> TryReadRevisionAsync(
        ICenteredViewReadIndex index, string baseId, string revisionId, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CenteredViewLinks.IsIdentity(baseId) || !CenteredViewLinks.IsIdentity(revisionId))
            return null;
        try
        {
            return await index.ReadRevisionAsync(baseId, revisionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogRevisionReadFailure(logger, exception);
            return null;
        }
    }

    [LoggerMessage(2535, LogLevel.Warning, "Operations could not read a recorded centered publication revision.")]
    private static partial void LogRevisionReadFailure(ILogger logger, Exception exception);

    public static TimeSpan? ExposureEndToPublication(CenteredView view)
    {
        var end = view.Base.Definition.Reference.ExposureEndedUtc;
        var published = view.Revision.PublishedUtc;
        return end != default && published >= end ? published - end : null;
    }
}
