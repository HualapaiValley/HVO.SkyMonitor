using System.Text.Json;
using System.Diagnostics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record PublicLocationProjection(
    ObservatoryLocationDisclosureLevel DisclosureLevel,
    string? RegionCode,
    string? RegionLabel,
    double? LatitudeDegrees,
    double? LongitudeDegrees,
    double? PrecisionMeters);

internal sealed record PublicObservatorySummary(
    string Slug,
    string DisplayName,
    string Description,
    PublicLocationProjection Location,
    int ReleasedCameraCount,
    bool PublishesEnvironmentalSummary);

internal sealed record PublicLogicalCameraSummary(
    string Slug,
    string Name,
    string Description);

internal sealed record PublicLogicalCameraPage(
    IReadOnlyList<PublicLogicalCameraSummary> Items,
    string? NextCursor);

internal sealed record PublicObservatoryDetail(
    PublicObservatorySummary Observatory,
    IReadOnlyList<PublicLogicalCameraSummary> Cameras,
    string? CamerasNextCursor,
    IReadOnlyList<PublicImageSummary> Products,
    string? ProductsNextCursor);

internal sealed record PublicObservatoryPage(
    IReadOnlyList<PublicObservatorySummary> Items,
    string? NextCursor);

internal sealed record PublicEventSummary(
    Guid PublicId,
    DateTimeOffset EventCreatedUtc,
    DateTimeOffset FirstObservedUtc,
    DateTimeOffset LastObservedUtc,
    TransientClassification Classification,
    TransientMeteorSeverity? MeteorSeverity,
    int ConfidenceMillionths,
    IReadOnlyList<string> ReleasedContributors);

internal sealed record PublicEventPage(
    IReadOnlyList<PublicEventSummary> Items,
    string? NextCursor);

internal sealed record PublicHomeSummary(
    IReadOnlyList<PublicObservatorySummary> Observatories,
    IReadOnlyList<PublicEventSummary> Events,
    IReadOnlyList<PublicImageSummary> Images);

internal sealed record PublicImageSummary(
    Guid PublicId,
    DateTimeOffset CapturedAtUtc,
    string ObservatoryName,
    string OriginLabel,
    string MediaType,
    string ContentPath);

internal sealed record PublicImagePage(
    IReadOnlyList<PublicImageSummary> Items,
    string? NextCursor);

internal interface IPublicNetworkReadService
{
    Task<PublicObservatoryPage> ListObservatoriesAsync(
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<PublicObservatoryDetail?> GetObservatoryAsync(
        string slug,
        CancellationToken cancellationToken = default);

    Task<PublicLogicalCameraPage> ListObservatoryCamerasAsync(
        string slug,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<PublicImagePage> ListObservatoryProductsAsync(
        string slug,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<PublicEventPage> ListEventsAsync(
        int take,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<PublicHomeSummary> GetHomeAsync(
        int takePerSection,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicImageSummary>> ListImagesAsync(
        int take,
        CancellationToken cancellationToken = default);
}

internal sealed class PublicNetworkReadService(
    ApplicationDbContext dbContext,
    OperatorUiTelemetry? telemetry = null) : IPublicNetworkReadService
{
    public async Task<PublicHomeSummary> GetHomeAsync(
        int takePerSection,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("public-home");
        ValidateTake(takePerSection);
        var observatories = (await ListObservatoriesAsync(50, null, cancellationToken).ConfigureAwait(false)).Items.ToList();
        var events = (await ListEventsAsync(50, null, cancellationToken).ConfigureAwait(false)).Items.ToList();
        var featuredObservatoryPlacements = await CurrentPlacementDecisions(CuratedPublicSurface.HomeObservatory)
            .Where(decision => decision.State == CuratedPlacementState.Featured)
            .Join(CurrentPublicProfiles(), decision => decision.ObservatoryId, profile => profile.ObservatoryId,
                (decision, profile) => new { profile.PublicSlug, decision.State, decision.DisplayOrder, decision.OccurredAtUtc })
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.OccurredAtUtc)
            .Take(takePerSection)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var featuredEventPlacements = await CurrentPlacementDecisions(CuratedPublicSurface.HomeEvent)
            .Where(decision => decision.State == CuratedPlacementState.Featured)
            .Select(decision => new { decision.PublicRecordId, decision.State, decision.DisplayOrder, decision.OccurredAtUtc })
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.OccurredAtUtc)
            .Take(takePerSection)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var automaticEventIds = await CurrentReleasedDecisions()
            .Where(decision => decision.SubjectKind == PublicRecordSubjectKind.TransientEvent
                && dbContext.ObservatoryPublicationProfileVersions.Any(profile =>
                    profile.ObservatoryId == decision.AuthorityObservatoryId
                    && profile.SupersededAtUtc == null
                    && profile.ProfileVisibility == ObservatoryProfileVisibility.Public
                    && profile.Observatory!.IsActive
                    && profile.AllowAutomaticVerifiedEventInclusion))
            .Select(decision => decision.PublicId)
            .Distinct()
            .OrderBy(publicId => publicId)
            .Take(takePerSection)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var relevantObservatorySlugs = observatories.Select(item => item.Slug)
            .Concat(featuredObservatoryPlacements.Select(item => item.PublicSlug)).Distinct().ToArray();
        var observatoryPlacements = await CurrentPlacementDecisions(CuratedPublicSurface.HomeObservatory)
            .Join(CurrentPublicProfiles().Where(profile => relevantObservatorySlugs.Contains(profile.PublicSlug)),
                decision => decision.ObservatoryId,
                profile => profile.ObservatoryId,
                (decision, profile) => new
                {
                    profile.PublicSlug,
                    decision.State,
                    decision.DisplayOrder,
                    decision.OccurredAtUtc
                })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var relevantEventIds = events.Select(item => item.PublicId)
            .Concat(featuredEventPlacements.Where(item => item.PublicRecordId.HasValue)
                .Select(item => item.PublicRecordId!.Value))
            .Concat(automaticEventIds).Distinct().ToArray();
        var eventPlacements = await CurrentPlacementDecisions(CuratedPublicSurface.HomeEvent)
            .Where(decision => decision.PublicRecordId != null
                && relevantEventIds.Contains(decision.PublicRecordId.Value))
            .Select(decision => new
            {
                decision.PublicRecordId,
                decision.State,
                decision.DisplayOrder,
                decision.OccurredAtUtc
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var images = await ListImagesAsync(takePerSection, cancellationToken).ConfigureAwait(false);

        var currentObservatoryPlacements = observatoryPlacements.GroupBy(item => item.PublicSlug)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.OccurredAtUtc).First());
        var currentEventPlacements = eventPlacements.Where(item => item.PublicRecordId.HasValue)
            .GroupBy(item => item.PublicRecordId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.OccurredAtUtc).First());
        foreach (var slug in currentObservatoryPlacements.Where(item => item.Value.State == CuratedPlacementState.Featured)
            .OrderBy(item => item.Value.DisplayOrder).Select(item => item.Key).Take(takePerSection))
        {
            if (observatories.All(item => item.Slug != slug)
                && await GetObservatoryAsync(slug, cancellationToken).ConfigureAwait(false) is { } detail)
            {
                observatories.Add(detail.Observatory);
            }
        }
        foreach (var publicId in currentEventPlacements.Where(item => item.Value.State == CuratedPlacementState.Featured)
            .OrderBy(item => item.Value.DisplayOrder).Select(item => item.Key).Take(takePerSection))
        {
            if (events.All(item => item.PublicId != publicId)
                && await GetEventAsync(publicId, cancellationToken).ConfigureAwait(false) is { } publicEvent)
            {
                events.Add(publicEvent);
            }
        }
        var selectedObservatories = observatories
            .Where(item => currentObservatoryPlacements.GetValueOrDefault(item.Slug)?.State == CuratedPlacementState.Featured)
            .OrderBy(item => currentObservatoryPlacements[item.Slug].DisplayOrder)
            .ThenBy(item => item.DisplayName)
            .Concat(observatories.Where(item => !currentObservatoryPlacements.TryGetValue(item.Slug, out var placement)
                || placement.State == CuratedPlacementState.Cleared))
            .DistinctBy(item => item.Slug)
            .Take(takePerSection)
            .ToArray();
        var selectedEvents = events
            .Where(item => currentEventPlacements.GetValueOrDefault(item.PublicId)?.State == CuratedPlacementState.Featured)
            .OrderBy(item => currentEventPlacements[item.PublicId].DisplayOrder)
            .ThenByDescending(item => item.EventCreatedUtc)
            .Concat(events.Where(item => automaticEventIds.Contains(item.PublicId)
                && (!currentEventPlacements.TryGetValue(item.PublicId, out var placement)
                    || placement.State == CuratedPlacementState.Cleared)))
            .DistinctBy(item => item.PublicId)
            .Take(takePerSection)
            .ToArray();
        telemetry?.RecordRead("public-home", "visitor", "success",
            selectedObservatories.Length + selectedEvents.Length + images.Count, 0, Stopwatch.GetElapsedTime(started));
        return new(selectedObservatories, selectedEvents, images);
    }

    public async Task<IReadOnlyList<PublicImageSummary>> ListImagesAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("public-images");
        ValidateTake(take);
        var releasedCentralPreviewFrames = CurrentReleasedDecisions()
            .Where(decision => decision.SubjectKind == PublicRecordSubjectKind.Artifact
                && decision.CentralArtifactId != null)
            .Join(dbContext.CentralArtifacts.AsNoTracking().Where(artifact =>
                    artifact.Role == FrameArtifactRole.Preview
                    && dbContext.CentralDerivativeJobs.Any(job => job.ResultCentralArtifactId == artifact.Id)),
                decision => decision.CentralArtifactId,
                artifact => artifact.Id,
                (_, artifact) => artifact.CentralFrameId);
        var result = await CurrentReleasedDecisions()
            .Where(decision => decision.SubjectKind == PublicRecordSubjectKind.Artifact
                && decision.CentralArtifactId != null)
            .Join(dbContext.CentralArtifacts.AsNoTracking().Where(artifact =>
                    artifact.ObjectState == CentralArtifactObjectState.Available
                    && artifact.ReconstructionState == CentralReconstructionState.Complete
                    && (artifact.Role == FrameArtifactRole.Preview
                        || artifact.Role == FrameArtifactRole.AnnotatedPreview)
                    && (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png"
                        || artifact.MediaType == "image/webp")),
                decision => decision.CentralArtifactId,
                artifact => artifact.Id,
                (decision, artifact) => new { Decision = decision, Artifact = artifact })
            .Where(item => item.Artifact.Role != FrameArtifactRole.Preview
                || dbContext.CentralDerivativeJobs.Any(job => job.ResultCentralArtifactId == item.Artifact.Id)
                || !releasedCentralPreviewFrames.Contains(item.Artifact.CentralFrameId))
            .Join(CurrentPublicProfiles().Where(profile => profile.Observatory!.IsActive),
                item => item.Decision.AuthorityObservatoryId,
                profile => profile.ObservatoryId,
                (item, profile) => new { item.Decision, item.Artifact, Profile = profile })
            .OrderByDescending(item => item.Artifact.Frame!.CapturedAtUtc)
            .ThenByDescending(item => item.Decision.PublicId)
            .Take(take)
            .Select(item => new PublicImageSummary(
                item.Decision.PublicId,
                item.Artifact.Frame!.CapturedAtUtc,
                item.Profile.PublicDisplayName,
                dbContext.CentralDerivativeJobs.Any(job => job.ResultCentralArtifactId == item.Artifact.Id)
                    ? "Central derivative"
                    : item.Artifact.Role == FrameArtifactRole.AnnotatedPreview
                        ? "Edge annotation"
                        : "Edge preview",
                item.Artifact.MediaType,
                $"/api/v1.0/public/artifacts/{item.Decision.PublicId:D}/content"))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        telemetry?.RecordRead("public-images", "visitor", "success", result.Length, 0, Stopwatch.GetElapsedTime(started));
        return result;
    }

    private async Task<PublicEventSummary?> GetEventAsync(Guid publicId, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("public-event-detail");
        var releases = CurrentReleasedDecisions().Where(decision =>
            decision.PublicId == publicId
            && decision.SubjectKind == PublicRecordSubjectKind.TransientEvent
            && decision.CentralTransientEventId != null
            && dbContext.ObservatoryPublicationProfileVersions.Any(profile =>
                profile.ObservatoryId == decision.AuthorityObservatoryId
                && profile.SupersededAtUtc == null
                && profile.ProfileVisibility == ObservatoryProfileVisibility.Public
                && profile.Observatory!.IsActive));
        var row = await releases.GroupBy(decision => decision.PublicId)
            .Select(group => new
            {
                PublicId = group.Key,
                CentralTransientEventId = group.Select(item => item.CentralTransientEventId!.Value).First(),
                SourceEventVersionId = group.Select(item => item.SourceEventVersionId!.Value).First()
            })
            .Join(dbContext.CentralTransientEventCurrent.AsNoTracking(),
                release => new { release.CentralTransientEventId, release.SourceEventVersionId },
                current => new { current.CentralTransientEventId, SourceEventVersionId = current.LatestEventVersionId },
                (release, current) => new { release.PublicId, Current = current })
            .Where(item => (item.Current.ReviewState == CentralTransientReviewState.Reviewed
                    || item.Current.ReviewState == CentralTransientReviewState.Overridden)
                && item.Current.ActiveAssessment!.Authority == TransientAssessmentAuthority.Authoritative)
            .Select(item => new EventRow(
                item.PublicId,
                item.Current.CentralTransientEventId,
                item.Current.Event!.EventCreatedUtc,
                item.Current.LatestEventVersion!.FirstObservedUtc,
                item.Current.LatestEventVersion.LastObservedUtc,
                item.Current.EffectiveClassification,
                item.Current.EffectiveMeteorSeverity,
                item.Current.EffectiveConfidenceMillionths))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            telemetry?.RecordRead("public-event-detail", "visitor", "not-found", 0, 0, Stopwatch.GetElapsedTime(started));
            return null;
        }
        var contributors = await CurrentReleasedDecisions()
            .Where(decision => decision.PublicId == publicId
                && decision.SubjectKind == PublicRecordSubjectKind.TransientEvent)
            .Join(CurrentPublicProfiles().Where(profile => profile.Observatory!.IsActive),
                decision => decision.AuthorityObservatoryId,
                profile => profile.ObservatoryId,
                (_, profile) => profile.PublicDisplayName)
            .Distinct().OrderBy(item => item)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var result = new PublicEventSummary(row.PublicId, row.EventCreatedUtc, row.FirstObservedUtc, row.LastObservedUtc,
            row.Classification, row.MeteorSeverity, row.ConfidenceMillionths, contributors);
        telemetry?.RecordRead("public-event-detail", "visitor", "success", 1, 0, Stopwatch.GetElapsedTime(started));
        return result;
    }

    public async Task<PublicObservatoryPage> ListObservatoriesAsync(
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("public-observatories");
        ValidateTake(take);
        if (!TryDecodeObservatoryCursor(cursor, out var cursorValue))
        {
            throw new ArgumentException("The public observatory cursor is invalid.", nameof(cursor));
        }
        var query = CurrentPublicProfiles()
            .Join(dbContext.Observatories.AsNoTracking().Where(observatory => observatory.IsActive),
                profile => profile.ObservatoryId,
                observatory => observatory.Id,
                (profile, observatory) => new { Profile = profile, Observatory = observatory })
            .Join(dbContext.ObservatoryLocationDisclosureVersions.AsNoTracking().Where(disclosure =>
                    disclosure.SupersededAtUtc == null),
                item => item.Observatory.Id,
                disclosure => disclosure.ObservatoryId,
                (item, disclosure) => new { item.Profile, item.Observatory, Disclosure = disclosure });
        if (cursorValue is not null)
        {
#pragma warning disable CA1309 // SQL Server performs this comparison using the indexed database collation.
            query = query.Where(item => string.Compare(item.Profile.PublicDisplayName, cursorValue.DisplayName) > 0
                || item.Profile.PublicDisplayName == cursorValue.DisplayName
                && string.Compare(item.Profile.PublicSlug, cursorValue.Slug) > 0);
#pragma warning restore CA1309
        }
        var rows = await query
            .OrderBy(item => item.Profile.PublicDisplayName)
            .ThenBy(item => item.Profile.PublicSlug)
            .Take(take + 1)
            .Select(item => new ObservatoryRow(
                item.Observatory.Id,
                item.Profile.PublicSlug,
                item.Profile.PublicDisplayName,
                item.Profile.PublicDescription,
                item.Disclosure.DisclosureLevel,
                item.Disclosure.RegionCode,
                item.Disclosure.RegionLabel,
                item.Disclosure.SourceObservatoryLocationVersion!.SupersededAtUtc == null
                    ? item.Disclosure.PublicLatitudeDegrees
                    : null,
                item.Disclosure.SourceObservatoryLocationVersion!.SupersededAtUtc == null
                    ? item.Disclosure.PublicLongitudeDegrees
                    : null,
                item.Disclosure.SourceObservatoryLocationVersion!.SupersededAtUtc == null
                    ? item.Disclosure.PublicPrecisionMeters
                    : null,
                dbContext.LogicalCameras.Count(camera => camera.ObservatoryId == item.Observatory.Id
                    && camera.DeactivatedAtUtc == null
                    && dbContext.PublicRecordPublicationDecisions.Any(decision =>
                        decision.LogicalCameraId == camera.Id
                        && decision.State == PublicationDecisionState.Released
                        && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                            successor.SupersedesDecisionId == decision.Id))),
                item.Profile.PublishEnvironmentalSummary))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var items = rows.Select(ProjectObservatory).ToArray();
        var nextCursor = hasMore && rows.Count > 0
            ? Encode(new ObservatoryCursor(rows[^1].DisplayName, rows[^1].Slug))
            : null;
        telemetry?.RecordRead("public-observatories", "visitor", "success", items.Length, 0, Stopwatch.GetElapsedTime(started));
        return new(items, nextCursor);
    }

    public async Task<PublicObservatoryDetail?> GetObservatoryAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("public-observatory-detail");
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        slug = slug.Trim().ToLowerInvariant();
        var row = await CurrentPublicProfiles()
            .Join(dbContext.Observatories.AsNoTracking().Where(observatory => observatory.IsActive),
                profile => profile.ObservatoryId,
                observatory => observatory.Id,
                (profile, observatory) => new { Profile = profile, Observatory = observatory })
            .Join(dbContext.ObservatoryLocationDisclosureVersions.AsNoTracking().Where(disclosure =>
                    disclosure.SupersededAtUtc == null),
                item => item.Observatory.Id,
                disclosure => disclosure.ObservatoryId,
                (item, disclosure) => new { item.Profile, item.Observatory, Disclosure = disclosure })
            .Where(item => item.Profile.PublicSlug == slug)
            .Select(item => new ObservatoryRow(
                item.Observatory.Id,
                item.Profile.PublicSlug,
                item.Profile.PublicDisplayName,
                item.Profile.PublicDescription,
                item.Disclosure.DisclosureLevel,
                item.Disclosure.RegionCode,
                item.Disclosure.RegionLabel,
                item.Disclosure.SourceObservatoryLocationVersion!.SupersededAtUtc == null
                    ? item.Disclosure.PublicLatitudeDegrees
                    : null,
                item.Disclosure.SourceObservatoryLocationVersion!.SupersededAtUtc == null
                    ? item.Disclosure.PublicLongitudeDegrees
                    : null,
                item.Disclosure.SourceObservatoryLocationVersion!.SupersededAtUtc == null
                    ? item.Disclosure.PublicPrecisionMeters
                    : null,
                dbContext.LogicalCameras.Count(camera => camera.ObservatoryId == item.Observatory.Id
                    && camera.DeactivatedAtUtc == null
                    && dbContext.PublicRecordPublicationDecisions.Any(decision =>
                        decision.LogicalCameraId == camera.Id
                        && decision.State == PublicationDecisionState.Released
                        && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                            successor.SupersedesDecisionId == decision.Id))),
                item.Profile.PublishEnvironmentalSummary))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            telemetry?.RecordRead("public-observatory-detail", "visitor", "not-found", 0, 0, Stopwatch.GetElapsedTime(started));
            return null;
        }
        var cameraPage = await ListObservatoryCamerasAsync(slug, 50, null, cancellationToken).ConfigureAwait(false);
        var productPage = await ListObservatoryProductsAsync(slug, 12, null, cancellationToken).ConfigureAwait(false);
        telemetry?.RecordRead("public-observatory-detail", "visitor", "success",
            1 + cameraPage.Items.Count + productPage.Items.Count, 0, Stopwatch.GetElapsedTime(started));
        return new(
            ProjectObservatory(row),
            cameraPage.Items,
            cameraPage.NextCursor,
            productPage.Items,
            productPage.NextCursor);
    }

    public async Task<PublicLogicalCameraPage> ListObservatoryCamerasAsync(
        string slug,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ValidateTake(take);
        if (!TryDecode(cursor, out PublicCameraCursor? cursorValue))
        {
            throw new ArgumentException("The public camera cursor is invalid.", nameof(cursor));
        }
        slug = slug.Trim().ToLowerInvariant();
        var observatoryId = await CurrentPublicProfiles()
            .Where(profile => profile.PublicSlug == slug
                && dbContext.Observatories.Any(observatory => observatory.Id == profile.ObservatoryId
                    && observatory.IsActive))
            .Select(profile => (Guid?)profile.ObservatoryId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (observatoryId is null) return new([], null);
        IQueryable<LogicalCamera> query = dbContext.LogicalCameras.AsNoTracking()
            .Where(camera => camera.ObservatoryId == observatoryId.Value
                && camera.DeactivatedAtUtc == null
                && dbContext.PublicRecordPublicationDecisions.Any(decision =>
                    decision.LogicalCameraId == camera.Id
                    && decision.State == PublicationDecisionState.Released
                    && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                        successor.SupersedesDecisionId == decision.Id)));
        if (cursorValue is not null)
        {
#pragma warning disable CA1309 // SQL Server performs these comparisons using the database collation.
            query = query.Where(camera => string.Compare(camera.Name, cursorValue.Name) > 0
                || camera.Name == cursorValue.Name
                && string.Compare(camera.Slug, cursorValue.Slug) > 0);
#pragma warning restore CA1309
        }
        var rows = await query.OrderBy(camera => camera.Name).ThenBy(camera => camera.Slug)
            .Take(take + 1)
            .Select(camera => new PublicLogicalCameraSummary(camera.Slug, camera.Name, camera.Description))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new PublicCameraCursor(rows[^1].Name, rows[^1].Slug))
            : null);
    }

    public async Task<PublicImagePage> ListObservatoryProductsAsync(
        string slug,
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ValidateTake(take);
        if (!TryDecode(cursor, out PublicProductCursor? cursorValue))
        {
            throw new ArgumentException("The public product cursor is invalid.", nameof(cursor));
        }
        slug = slug.Trim().ToLowerInvariant();
        var authority = await CurrentPublicProfiles()
            .Where(profile => profile.PublicSlug == slug
                && dbContext.Observatories.Any(observatory => observatory.Id == profile.ObservatoryId
                    && observatory.IsActive))
            .Select(profile => new { profile.ObservatoryId, profile.PublicDisplayName })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (authority is null) return new([], null);
        var query = CurrentReleasedDecisions()
            .Where(decision => decision.AuthorityObservatoryId == authority.ObservatoryId
                && decision.SubjectKind == PublicRecordSubjectKind.Artifact
                && decision.CentralArtifactId != null)
            .Join(dbContext.CentralArtifacts.AsNoTracking().Where(artifact =>
                    artifact.ObjectState == CentralArtifactObjectState.Available
                    && artifact.ReconstructionState == CentralReconstructionState.Complete
                    && (artifact.Role == FrameArtifactRole.Preview
                        || artifact.Role == FrameArtifactRole.AnnotatedPreview)
                    && (artifact.MediaType == "image/jpeg" || artifact.MediaType == "image/png"
                        || artifact.MediaType == "image/webp")),
                decision => decision.CentralArtifactId,
                artifact => artifact.Id,
                (decision, artifact) => new { Decision = decision, Artifact = artifact });
        if (cursorValue is not null)
        {
            query = query.Where(item => item.Artifact.Frame!.CapturedAtUtc < cursorValue.CapturedAtUtc
                || item.Artifact.Frame.CapturedAtUtc == cursorValue.CapturedAtUtc
                && item.Decision.PublicId.CompareTo(cursorValue.PublicId) < 0);
        }
        var rows = await query.OrderByDescending(item => item.Artifact.Frame!.CapturedAtUtc)
            .ThenByDescending(item => item.Decision.PublicId)
            .Take(take + 1)
            .Select(item => new PublicImageSummary(
                item.Decision.PublicId,
                item.Artifact.Frame!.CapturedAtUtc,
                authority.PublicDisplayName,
                dbContext.CentralDerivativeJobs.Any(job => job.ResultCentralArtifactId == item.Artifact.Id)
                    ? "Central derivative"
                    : item.Artifact.Role == FrameArtifactRole.AnnotatedPreview
                        ? "Edge annotation"
                        : "Edge preview",
                item.Artifact.MediaType,
                $"/api/v1.0/public/artifacts/{item.Decision.PublicId:D}/content"))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new(rows, hasMore && rows.Count > 0
            ? Encode(new PublicProductCursor(rows[^1].CapturedAtUtc, rows[^1].PublicId))
            : null);
    }

    public async Task<PublicEventPage> ListEventsAsync(
        int take,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartRead("public-events");
        ValidateTake(take);
        if (!TryDecodeEventCursor(cursor, out var cursorValue))
        {
            throw new ArgumentException("The public event cursor is invalid.", nameof(cursor));
        }
        var releases = CurrentReleasedDecisions().Where(decision =>
            decision.SubjectKind == PublicRecordSubjectKind.TransientEvent
            && decision.CentralTransientEventId != null
            && dbContext.ObservatoryPublicationProfileVersions.Any(profile =>
                profile.ObservatoryId == decision.AuthorityObservatoryId
                && profile.SupersededAtUtc == null
                && profile.ProfileVisibility == ObservatoryProfileVisibility.Public
                && profile.Observatory!.IsActive));
        var query = releases.GroupBy(decision => decision.PublicId)
            .Select(group => new
            {
                PublicId = group.Key,
                CentralTransientEventId = group.Select(item => item.CentralTransientEventId!.Value).First(),
                SourceEventVersionId = group.Select(item => item.SourceEventVersionId!.Value).First()
            })
            .Join(dbContext.CentralTransientEventCurrent.AsNoTracking(),
                release => new { release.CentralTransientEventId, release.SourceEventVersionId },
                current => new
                {
                    current.CentralTransientEventId,
                    SourceEventVersionId = current.LatestEventVersionId
                },
                (release, current) => new { release.PublicId, Current = current })
            .Where(item => item.Current.ReviewState == CentralTransientReviewState.Reviewed
                || item.Current.ReviewState == CentralTransientReviewState.Overridden)
            .Where(item => item.Current.ActiveAssessment!.Authority == TransientAssessmentAuthority.Authoritative);
        if (cursorValue is not null)
        {
            query = query.Where(item => item.Current.Event!.EventCreatedUtc < cursorValue.EventCreatedUtc
                || item.Current.Event.EventCreatedUtc == cursorValue.EventCreatedUtc
                && item.PublicId.CompareTo(cursorValue.PublicId) < 0);
        }
        var rows = await query.OrderByDescending(item => item.Current.Event!.EventCreatedUtc)
            .ThenByDescending(item => item.PublicId)
            .Take(take + 1)
            .Select(item => new EventRow(
                item.PublicId,
                item.Current.CentralTransientEventId,
                item.Current.Event!.EventCreatedUtc,
                item.Current.LatestEventVersion!.FirstObservedUtc,
                item.Current.LatestEventVersion.LastObservedUtc,
                item.Current.EffectiveClassification,
                item.Current.EffectiveMeteorSeverity,
                item.Current.EffectiveConfidenceMillionths))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var publicIds = rows.Select(item => item.PublicId).ToArray();
        var contributors = await CurrentReleasedDecisions()
            .Where(decision => publicIds.Contains(decision.PublicId)
                && decision.SubjectKind == PublicRecordSubjectKind.TransientEvent)
            .Join(dbContext.ObservatoryPublicationProfileVersions.AsNoTracking().Where(profile =>
                    profile.SupersededAtUtc == null
                    && profile.ProfileVisibility == ObservatoryProfileVisibility.Public
                    && profile.Observatory!.IsActive),
                decision => decision.AuthorityObservatoryId,
                profile => profile.ObservatoryId,
                (decision, profile) => new { decision.PublicId, profile.PublicDisplayName })
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = rows.Select(row => new PublicEventSummary(
            row.PublicId,
            row.EventCreatedUtc,
            row.FirstObservedUtc,
            row.LastObservedUtc,
            row.Classification,
            row.MeteorSeverity,
            row.ConfidenceMillionths,
            contributors.Where(item => item.PublicId == row.PublicId)
                .Select(item => item.PublicDisplayName).Order().ToArray())).ToArray();
        var nextCursor = hasMore && rows.Count > 0
            ? Encode(new EventCursor(rows[^1].EventCreatedUtc, rows[^1].PublicId))
            : null;
        telemetry?.RecordRead("public-events", "visitor", "success", items.Length, 0, Stopwatch.GetElapsedTime(started));
        return new(items, nextCursor);
    }

    private IQueryable<ObservatoryPublicationProfileVersion> CurrentPublicProfiles()
        => dbContext.ObservatoryPublicationProfileVersions.AsNoTracking()
            .Where(profile => profile.SupersededAtUtc == null
                && profile.ProfileVisibility == ObservatoryProfileVisibility.Public);

    private IQueryable<PublicRecordPublicationDecision> CurrentReleasedDecisions()
        => dbContext.PublicRecordPublicationDecisions.AsNoTracking().Where(decision =>
            decision.State == PublicationDecisionState.Released
            && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                successor.SupersedesDecisionId == decision.Id));

    private IQueryable<CuratedPublicPlacementDecision> CurrentPlacementDecisions(CuratedPublicSurface surface)
        => dbContext.CuratedPublicPlacementDecisions.AsNoTracking().Where(decision =>
            decision.Surface == surface
            && !dbContext.CuratedPublicPlacementDecisions.Any(successor =>
                successor.SupersedesDecisionId == decision.Id));

    private static PublicObservatorySummary ProjectObservatory(ObservatoryRow row)
    {
        var coordinatesCurrent = row.DisclosureLevel == ObservatoryLocationDisclosureLevel.Approximate
            && row.LatitudeDegrees.HasValue
            && row.LongitudeDegrees.HasValue;
        var level = row.DisclosureLevel == ObservatoryLocationDisclosureLevel.Exact
            || row.DisclosureLevel == ObservatoryLocationDisclosureLevel.Approximate && !coordinatesCurrent
                ? ObservatoryLocationDisclosureLevel.Hidden
                : row.DisclosureLevel;
        return new(
            row.Slug,
            row.DisplayName,
            row.Description,
            new PublicLocationProjection(
                level,
                level == ObservatoryLocationDisclosureLevel.Hidden ? null : row.RegionCode,
                level == ObservatoryLocationDisclosureLevel.Hidden ? null : row.RegionLabel,
                coordinatesCurrent ? row.LatitudeDegrees : null,
                coordinatesCurrent ? row.LongitudeDegrees : null,
                coordinatesCurrent ? row.PrecisionMeters : null),
            row.ReleasedCameraCount,
            row.PublishesEnvironmentalSummary);
    }

    private static void ValidateTake(int take)
    {
        if (take is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }
    }

    private static string Encode<T>(T value)
        => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));

    private static bool TryDecodeObservatoryCursor(string? cursor, out ObservatoryCursor? value)
        => TryDecode(cursor, out value);

    private static bool TryDecodeEventCursor(string? cursor, out EventCursor? value)
        => TryDecode(cursor, out value);

    private static bool TryDecode<T>(string? cursor, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(WebEncoders.Base64UrlDecode(cursor));
            return value is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record ObservatoryRow(
        Guid ObservatoryId,
        string Slug,
        string DisplayName,
        string Description,
        ObservatoryLocationDisclosureLevel DisclosureLevel,
        string? RegionCode,
        string? RegionLabel,
        double? LatitudeDegrees,
        double? LongitudeDegrees,
        double? PrecisionMeters,
        int ReleasedCameraCount,
        bool PublishesEnvironmentalSummary);

    private sealed record EventRow(
        Guid PublicId,
        Guid CentralTransientEventId,
        DateTimeOffset EventCreatedUtc,
        DateTimeOffset FirstObservedUtc,
        DateTimeOffset LastObservedUtc,
        TransientClassification Classification,
        TransientMeteorSeverity? MeteorSeverity,
        int ConfidenceMillionths);

    private sealed record ObservatoryCursor(string DisplayName, string Slug);
    private sealed record EventCursor(DateTimeOffset EventCreatedUtc, Guid PublicId);
    private sealed record PublicCameraCursor(string Name, string Slug);
    private sealed record PublicProductCursor(DateTimeOffset CapturedAtUtc, Guid PublicId);
}
