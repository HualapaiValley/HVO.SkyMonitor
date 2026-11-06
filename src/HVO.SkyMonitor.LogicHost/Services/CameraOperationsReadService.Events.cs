using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>
/// One association between a capture of the camera and a central transient event: the capture supplies an
/// observation source, a background reference, or both. An event spanning several captures appears once per capture.
/// </summary>
internal sealed record CameraTransientEventAssociation(
    Guid CaptureId,
    DateTimeOffset CapturedAtUtc,
    Guid LogicalCameraInstallationId,
    Guid CentralTransientEventId,
    Guid EventId,
    DateTimeOffset EventCreatedUtc,
    int Version,
    TransientEventState EventState,
    CentralTransientReviewState ReviewState,
    TransientClassification EffectiveClassification,
    int EffectiveConfidenceMillionths,
    DateTimeOffset UpdatedUtc,
    bool CaptureIsObservationSource,
    bool CaptureIsBackground);

internal sealed partial class CameraOperationsReadService
{
    /// <summary>
    /// Event associations of the camera's captures, newest capture first. Camera membership opens the camera; each
    /// event still requires the existing all-contributor rule of
    /// <see cref="CentralTransientEventReadService.ApplyAccess"/> (at least one observation source, and every source
    /// and background observatory within the caller's memberships and credential scope), so an event with a
    /// contributor outside them is omitted rather than partially shown. The rule is evaluated over the contributor
    /// observatories of the window's candidate events only, so no read grows with the network's event history.
    /// </summary>
    public Task<CameraCaptureAnchoredPage<CameraTransientEventAssociation>> ListEventAssociationsAsync(
        ClaimsPrincipal principal, Guid observatoryId, Guid cameraId, int take, string? cursor,
        CancellationToken cancellationToken = default)
    {
        var credentialScope = CentralArtifactCredentialAccess.GetObservatoryScope(principal);
        return CaptureAnchoredAsync(
            principal, observatoryId, cameraId, "events", take, cursor,
            (owner, installationIds, position, limit) => ReadEventAssociationsAsync(owner, credentialScope,
                observatoryId, installationIds, [position.Id], position.ChildId, limit, cancellationToken),
            (owner, installationIds, captureIds, limit) => ReadEventAssociationsAsync(owner, credentialScope,
                observatoryId, installationIds, captureIds, null, limit, cancellationToken),
            item => (item.CaptureId, item.CapturedAtUtc, null, item.CentralTransientEventId),
            // Contributor authority can change after the association read; recheck every returned event with the same
            // rule and credential scope before anything is returned.
            async (owner, items) =>
            {
                var allowed = await AllowedEventsAsync(owner, credentialScope,
                    items.Select(item => item.CentralTransientEventId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
                return items.Where(item => allowed.Contains(item.CentralTransientEventId)).ToList();
            },
            cancellationToken);
    }

    /// <summary>
    /// Every association of the given captures, in (capture time, capture, event) descending order, filtered by the
    /// all-contributor rule and then limited. Rows read are bounded by the events referencing these captures and
    /// their own contributors.
    /// </summary>
    private async Task<List<CameraTransientEventAssociation>> ReadEventAssociationsAsync(
        string owner, Guid? credentialScope, Guid observatoryId, IReadOnlyList<Guid> installationIds,
        List<Guid> captureIds, Guid? beforeEventId, int limit, CancellationToken cancellationToken)
    {
        var pairs = await EventPairsAsync(observatoryId, installationIds, captureIds, beforeEventId, cancellationToken)
            .ConfigureAwait(false);
        if (pairs.Count == 0) return [];
        var candidates = pairs.Select(pair => pair.EventId).Distinct().ToList();
        var allowed = await AllowedEventsAsync(owner, credentialScope, candidates, cancellationToken).ConfigureAwait(false);
        var page = pairs.Where(pair => allowed.Contains(pair.EventId)).Take(limit).ToList();
        if (page.Count == 0) return [];
        var details = (await EventDetails(page.Select(pair => pair.EventId).Distinct().ToList())
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(detail => detail.CentralTransientEventId);
        return page.Select(pair =>
        {
            var detail = details[pair.EventId];
            return new CameraTransientEventAssociation(
                pair.CaptureId, pair.CapturedAtUtc, pair.InstallationId, pair.EventId, detail.EventId,
                detail.EventCreatedUtc, detail.Version, Enum.Parse<TransientEventState>(detail.State),
                Enum.Parse<CentralTransientReviewState>(detail.ReviewState),
                Enum.Parse<TransientClassification>(detail.EffectiveClassification),
                detail.EffectiveConfidenceMillionths, detail.UpdatedUtc, pair.IsSource, pair.IsBackground);
        }).ToList();
    }

    /// <summary>
    /// The candidate events the caller may read: each must have a current projection and satisfy the all-contributor
    /// rule against the caller's current memberships, intersected with any credential observatory scope.
    /// </summary>
    private async Task<HashSet<Guid>> AllowedEventsAsync(
        string owner, Guid? credentialScope, List<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0) return [];
        var memberships = ObservatoryMembershipAccess.ForUser(db, owner);
        if (credentialScope is { } scope) memberships = memberships.Where(membership => membership.ObservatoryId == scope);
        var accessible = (await memberships.Select(membership => membership.ObservatoryId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var current = (await EventDetails(eventIds).Select(detail => detail.CentralTransientEventId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var contributors = await EventContributorsAsync(eventIds, cancellationToken).ConfigureAwait(false);
        return contributors.GroupBy(contributor => contributor.EventId)
            .Where(group => current.Contains(group.Key) &&
                group.Any(contributor => contributor.IsSource) &&
                group.All(contributor => accessible.Contains(contributor.ObservatoryId)))
            .Select(group => group.Key)
            .ToHashSet();
    }

    /// <summary>
    /// Capture-to-event pairs driven from the capture identities through forced seeks on SQL Server:
    /// capture, its artifacts, observation sources and backgrounds by artifact, then their observations.
    /// </summary>
    internal async Task<List<EventPairRow>> EventPairsAsync(
        Guid observatoryId, IReadOnlyList<Guid> installationIds, List<Guid> captureIds, Guid? beforeEventId,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            return await EventPairsSql(captureIds, installationIds, observatoryId, beforeEventId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        var frames = await db.CentralFrames.AsNoTracking()
            .Where(frame => captureIds.Contains(frame.Id) && frame.ObservatoryId == observatoryId &&
                installationIds.Contains(frame.LogicalCameraInstallationId!.Value))
            .Select(frame => new { frame.Id, frame.CapturedAtUtc, InstallationId = frame.LogicalCameraInstallationId!.Value })
            .ToDictionaryAsync(frame => frame.Id, cancellationToken).ConfigureAwait(false);
        var sources = await db.CentralTransientObservationSources.AsNoTracking()
            .Where(source => captureIds.Contains(source.Artifact!.CentralFrameId))
            .Select(source => new { CaptureId = source.Artifact!.CentralFrameId, source.Observation!.CentralTransientEventId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var backgrounds = await db.CentralTransientObservationBackgrounds.AsNoTracking()
            .Where(background => captureIds.Contains(background.Artifact!.CentralFrameId))
            .Select(background => new { CaptureId = background.Artifact!.CentralFrameId, background.Observation!.CentralTransientEventId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return sources.Select(item => (item.CaptureId, item.CentralTransientEventId, Source: true))
            .Concat(backgrounds.Select(item => (item.CaptureId, item.CentralTransientEventId, Source: false)))
            .Where(item => frames.ContainsKey(item.CaptureId) &&
                (beforeEventId is not { } before || item.CentralTransientEventId.CompareTo(before) < 0))
            .GroupBy(item => (item.CaptureId, item.CentralTransientEventId))
            .Select(group => new EventPairRow
            {
                CaptureId = group.Key.CaptureId,
                CapturedAtUtc = frames[group.Key.CaptureId].CapturedAtUtc,
                InstallationId = frames[group.Key.CaptureId].InstallationId,
                EventId = group.Key.CentralTransientEventId,
                IsSource = group.Any(item => item.Source),
                IsBackground = group.Any(item => !item.Source)
            })
            .OrderByDescending(pair => pair.CapturedAtUtc).ThenByDescending(pair => pair.CaptureId)
            .ThenByDescending(pair => pair.EventId)
            .ToList();
    }

    internal IQueryable<EventPairRow> EventPairsSql(
        List<Guid> captureIds, IReadOnlyList<Guid> installationIds, Guid observatoryId, Guid? beforeEventId)
        => db.Database.SqlQuery<EventPairRow>($"""
            SELECT pair.[CaptureId], frame.[CapturedAtUtc], frame.[LogicalCameraInstallationId] AS [InstallationId],
                pair.[EventId], CAST(MAX(pair.[IsSource]) AS bit) AS [IsSource],
                CAST(MAX(pair.[IsBackground]) AS bit) AS [IsBackground]
            FROM (
                SELECT capture.[Id] AS [CaptureId], observation.[CentralTransientEventId] AS [EventId],
                    1 AS [IsSource], 0 AS [IsBackground]
                FROM OPENJSON({JsonSerializer.Serialize(captureIds)}) WITH ([Id] uniqueidentifier '$') AS capture
                INNER JOIN [CentralArtifacts] AS artifact
                    WITH (FORCESEEK([IX_CentralArtifacts_CentralFrameId_ArtifactId]([CentralFrameId])))
                    ON artifact.[CentralFrameId] = capture.[Id]
                INNER JOIN [CentralTransientObservationSources] AS source
                    WITH (FORCESEEK([IX_CentralTransientObservationSources_CentralArtifactId]([CentralArtifactId])))
                    ON source.[CentralArtifactId] = artifact.[Id]
                INNER JOIN [CentralTransientObservations] AS observation
                    WITH (FORCESEEK([IX_CentralTransientObservations_SourceReferenceId]([SourceReferenceId])))
                    ON observation.[SourceReferenceId] = source.[ObservationId]
                UNION ALL
                SELECT capture.[Id], observation.[CentralTransientEventId], 0, 1
                FROM OPENJSON({JsonSerializer.Serialize(captureIds)}) WITH ([Id] uniqueidentifier '$') AS capture
                INNER JOIN [CentralArtifacts] AS artifact
                    WITH (FORCESEEK([IX_CentralArtifacts_CentralFrameId_ArtifactId]([CentralFrameId])))
                    ON artifact.[CentralFrameId] = capture.[Id]
                INNER JOIN [CentralTransientObservationBackgrounds] AS background
                    WITH (FORCESEEK([IX_CentralTransientObservationBackgrounds_CentralArtifactId]([CentralArtifactId])))
                    ON background.[CentralArtifactId] = artifact.[Id]
                INNER JOIN [CentralTransientObservations] AS observation
                    WITH (FORCESEEK([PK_CentralTransientObservations]([ObservationId])))
                    ON observation.[ObservationId] = background.[ObservationId]
            ) AS pair
            INNER JOIN [CentralFrames] AS frame WITH (FORCESEEK([PK_CentralFrames]([Id]))) ON frame.[Id] = pair.[CaptureId]
            WHERE frame.[ObservatoryId] = {observatoryId}
                AND frame.[LogicalCameraInstallationId] IN (
                    SELECT installation.[Id]
                    FROM OPENJSON({JsonSerializer.Serialize(installationIds)}) WITH ([Id] uniqueidentifier '$') AS installation)
                AND ({beforeEventId.HasValue} = 0 OR pair.[EventId] < {beforeEventId ?? Guid.Empty})
            GROUP BY pair.[CaptureId], frame.[CapturedAtUtc], frame.[LogicalCameraInstallationId], pair.[EventId]
            ORDER BY frame.[CapturedAtUtc] DESC, pair.[CaptureId] DESC, pair.[EventId] DESC
            """);

    /// <summary>The observatory of every source and background contributor of the given events.</summary>
    private async Task<List<EventContributorRow>> EventContributorsAsync(List<Guid> eventIds, CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlServer())
        {
            return await EventContributorsSql(eventIds).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        var sources = await db.CentralTransientObservationSources.AsNoTracking()
            .Where(source => eventIds.Contains(source.Observation!.CentralTransientEventId))
            .Select(source => new EventContributorRow
            {
                EventId = source.Observation!.CentralTransientEventId,
                ObservatoryId = source.Artifact!.Frame!.ObservatoryId,
                IsSource = true
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var backgrounds = await db.CentralTransientObservationBackgrounds.AsNoTracking()
            .Where(background => eventIds.Contains(background.Observation!.CentralTransientEventId))
            .Select(background => new EventContributorRow
            {
                EventId = background.Observation!.CentralTransientEventId,
                ObservatoryId = background.Artifact!.Frame!.ObservatoryId,
                IsSource = false
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. sources, .. backgrounds];
    }

    internal IQueryable<EventContributorRow> EventContributorsSql(List<Guid> eventIds)
        => db.Database.SqlQuery<EventContributorRow>($"""
            SELECT DISTINCT candidate.[Id] AS [EventId], frame.[ObservatoryId], CAST(1 AS bit) AS [IsSource]
            FROM OPENJSON({JsonSerializer.Serialize(eventIds)}) WITH ([Id] uniqueidentifier '$') AS candidate
            INNER JOIN [CentralTransientObservations] AS observation
                WITH (FORCESEEK([AK_CentralTransientObservations_CentralTransientEventId_ObservationId]([CentralTransientEventId])))
                ON observation.[CentralTransientEventId] = candidate.[Id]
            INNER JOIN [CentralTransientObservationSources] AS source
                WITH (FORCESEEK([PK_CentralTransientObservationSources]([ObservationId])))
                ON source.[ObservationId] = observation.[SourceReferenceId]
            INNER JOIN [CentralArtifacts] AS artifact WITH (FORCESEEK([PK_CentralArtifacts]([Id])))
                ON artifact.[Id] = source.[CentralArtifactId]
            INNER JOIN [CentralFrames] AS frame WITH (FORCESEEK([PK_CentralFrames]([Id])))
                ON frame.[Id] = artifact.[CentralFrameId]
            UNION
            SELECT candidate.[Id], frame.[ObservatoryId], CAST(0 AS bit)
            FROM OPENJSON({JsonSerializer.Serialize(eventIds)}) WITH ([Id] uniqueidentifier '$') AS candidate
            INNER JOIN [CentralTransientObservations] AS observation
                WITH (FORCESEEK([AK_CentralTransientObservations_CentralTransientEventId_ObservationId]([CentralTransientEventId])))
                ON observation.[CentralTransientEventId] = candidate.[Id]
            INNER JOIN [CentralTransientObservationBackgrounds] AS background
                WITH (FORCESEEK([IX_CentralTransientObservationBackgrounds_ObservationId_CentralArtifactId]([ObservationId])))
                ON background.[ObservationId] = observation.[ObservationId]
            INNER JOIN [CentralArtifacts] AS artifact WITH (FORCESEEK([PK_CentralArtifacts]([Id])))
                ON artifact.[Id] = background.[CentralArtifactId]
            INNER JOIN [CentralFrames] AS frame WITH (FORCESEEK([PK_CentralFrames]([Id])))
                ON frame.[Id] = artifact.[CentralFrameId]
            """);

    /// <summary>The current projection of each given event, by primary key.</summary>
    internal IQueryable<EventDetailRow> EventDetails(List<Guid> eventIds)
        => db.Database.IsSqlServer()
            ? db.Database.SqlQuery<EventDetailRow>($"""
                SELECT projection.[CentralTransientEventId], record.[EventId], record.[EventCreatedUtc],
                    projection.[LatestVersion] AS [Version], latest.[State], projection.[ReviewState],
                    projection.[EffectiveClassification], projection.[EffectiveConfidenceMillionths], projection.[UpdatedUtc]
                FROM OPENJSON({JsonSerializer.Serialize(eventIds)}) WITH ([Id] uniqueidentifier '$') AS candidate
                INNER JOIN [CentralTransientEventCurrent] AS projection
                    WITH (FORCESEEK([PK_CentralTransientEventCurrent]([CentralTransientEventId])))
                    ON projection.[CentralTransientEventId] = candidate.[Id]
                INNER JOIN [CentralTransientEvents] AS record WITH (FORCESEEK([PK_CentralTransientEvents]([Id])))
                    ON record.[Id] = projection.[CentralTransientEventId]
                INNER JOIN [CentralTransientEventVersions] AS latest
                    WITH (FORCESEEK([PK_CentralTransientEventVersions]([EventVersionId])))
                    ON latest.[EventVersionId] = projection.[LatestEventVersionId]
                    AND latest.[CentralTransientEventId] = projection.[CentralTransientEventId]
                """)
            : db.CentralTransientEventCurrent.AsNoTracking()
                .Where(current => eventIds.Contains(current.CentralTransientEventId))
                .Select(current => new EventDetailRow
                {
                    CentralTransientEventId = current.CentralTransientEventId,
                    EventId = current.Event!.EventId,
                    EventCreatedUtc = current.Event.EventCreatedUtc,
                    Version = current.LatestVersion,
                    State = current.LatestEventVersion!.State.ToString(),
                    ReviewState = current.ReviewState.ToString(),
                    EffectiveClassification = current.EffectiveClassification.ToString(),
                    EffectiveConfidenceMillionths = current.EffectiveConfidenceMillionths,
                    UpdatedUtc = current.UpdatedUtc
                });

    internal sealed class EventPairRow
    {
        public Guid CaptureId { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public Guid InstallationId { get; set; }
        public Guid EventId { get; set; }
        public bool IsSource { get; set; }
        public bool IsBackground { get; set; }
    }

    internal sealed class EventContributorRow
    {
        public Guid EventId { get; set; }
        public Guid ObservatoryId { get; set; }
        public bool IsSource { get; set; }
    }

    /// <summary>Enum columns are stored as text; they are parsed after the raw read.</summary>
    internal sealed class EventDetailRow
    {
        public Guid CentralTransientEventId { get; set; }
        public Guid EventId { get; set; }
        public DateTimeOffset EventCreatedUtc { get; set; }
        public int Version { get; set; }
        public string State { get; set; } = string.Empty;
        public string ReviewState { get; set; } = string.Empty;
        public string EffectiveClassification { get; set; } = string.Empty;
        public int EffectiveConfidenceMillionths { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
