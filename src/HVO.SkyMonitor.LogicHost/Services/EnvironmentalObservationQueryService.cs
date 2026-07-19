using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record EnvironmentalObservationQuery(
    Guid SiteId,
    EnvironmentalObservationKind Kind,
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    int Take = 100,
    Guid? AgentId = null,
    string? RigId = null);

internal sealed record EnvironmentalObservationSelector(
    EnvironmentalObservationKind Kind,
    IReadOnlyList<EnvironmentalObservationSourceKind> SourcePriority,
    IReadOnlyList<EnvironmentalObservationQuality> AllowedQualities,
    TimeSpan MaximumStaleness);

internal sealed record EnvironmentalObservationCorrelationRequest(
    EnvironmentalObservationTarget Target,
    DateTimeOffset FromUtc,
    DateTimeOffset ThroughUtc,
    EnvironmentalObservationSelector Selector);

internal sealed record EnvironmentalObservationSelection(
    EnvironmentalObservationMatch Match,
    Guid? RecordId,
    string? ContentSha256);

internal interface IEnvironmentalObservationQueryService
{
    Task<IReadOnlyList<ReceivedEnvironmentalObservationV1>> QueryAsync(
        EnvironmentalObservationQuery query,
        CancellationToken cancellationToken = default);

    Task<EnvironmentalObservationMatch> CorrelateAsync(
        EnvironmentalObservationCorrelationRequest request,
        CancellationToken cancellationToken = default);

    Task<EnvironmentalObservationMatch> CorrelateFrameAsync(
        Guid frameId,
        EnvironmentalObservationSelector selector,
        CancellationToken cancellationToken = default);

    Task<EnvironmentalObservationSelection> SelectFrameAsync(
        Guid frameId,
        EnvironmentalObservationSelector selector,
        CancellationToken cancellationToken = default);
}

internal sealed class EnvironmentalObservationQueryService(
    ApplicationDbContext dbContext,
    IOptions<EnvironmentalObservationOptions> options,
    TimeProvider timeProvider,
    EnvironmentalObservationTelemetry telemetry) : IEnvironmentalObservationQueryService
{
    public async Task<IReadOnlyList<ReceivedEnvironmentalObservationV1>> QueryAsync(
        EnvironmentalObservationQuery query,
        CancellationToken cancellationToken = default)
    {
        var ids = await BuildHistorySelectionIdQuery(query)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ids.Length == 0)
        {
            return [];
        }
        var records = await dbContext.EnvironmentalObservations
            .AsNoTracking()
            .Include(observation => observation.Source)
            .Include(observation => observation.Lineage)
                .ThenInclude(lineage => lineage.SourceObservation)
                    .ThenInclude(observation => observation!.Source)
            .Where(observation => ids.Contains(observation.Id))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = records.ToDictionary(observation => observation.Id);
        return ids.Where(byId.ContainsKey).Select(id => ToReceivedObservation(byId[id])).ToArray();
    }

    public async Task<EnvironmentalObservationMatch> CorrelateFrameAsync(
        Guid frameId,
        EnvironmentalObservationSelector selector,
        CancellationToken cancellationToken = default)
        => (await SelectFrameAsync(frameId, selector, cancellationToken).ConfigureAwait(false)).Match;

    public async Task<EnvironmentalObservationSelection> SelectFrameAsync(
        Guid frameId,
        EnvironmentalObservationSelector selector,
        CancellationToken cancellationToken = default)
    {
        if (frameId == Guid.Empty)
        {
            throw new ArgumentException("Frame identity is required.", nameof(frameId));
        }
        var frame = await dbContext.CentralFrames
            .AsNoTracking()
            .Include(item => item.Timing)
            .SingleOrDefaultAsync(item => item.Id == frameId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The central frame does not exist.");
        var from = frame.Timing?.ExposureStartedUtc ?? frame.CapturedAtUtc;
        var through = frame.Timing?.ExposureEndedUtc ?? from;
        return await CorrelateCoreAsync(
            new EnvironmentalObservationCorrelationRequest(
                new EnvironmentalObservationTarget(frame.ObservatoryId, frame.DevicePublicId, frame.RigId),
                from,
                through,
                selector),
            allowReselection: true,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<EnvironmentalObservationMatch> CorrelateAsync(
        EnvironmentalObservationCorrelationRequest request,
        CancellationToken cancellationToken = default)
        => CorrelateMatchAsync(request, cancellationToken);

    private async Task<EnvironmentalObservationMatch> CorrelateMatchAsync(
        EnvironmentalObservationCorrelationRequest request,
        CancellationToken cancellationToken)
        => (await CorrelateCoreAsync(request, allowReselection: true, cancellationToken).ConfigureAwait(false)).Match;

    private async Task<EnvironmentalObservationSelection> CorrelateCoreAsync(
        EnvironmentalObservationCorrelationRequest request,
        bool allowReselection,
        CancellationToken cancellationToken)
    {
        ValidateCorrelation(request);
        var started = timeProvider.GetTimestamp();
        using var activity = EnvironmentalObservationTelemetry.ActivitySource.StartActivity("environment.correlate");
        activity?.SetTag("environment.observation_kind", request.Selector.Kind.ToString());
        var candidates = BuildCandidates(request);
        var covering = BuildCoveringCandidates(candidates, request);
        var fresh = BuildFreshCandidates(covering, request);
        Guid? selectedId = null;
        var selectedFresh = false;
        var specificities = Specificities(request.Target);
        foreach (var sourceKind in request.Selector.SourcePriority)
        {
            foreach (var specificity in specificities)
            {
                selectedId = await SelectFirstIdAsync(
                    ApplySpecificity(
                        fresh.Where(observation => observation.SourceKind == sourceKind),
                        request.Target,
                        specificity),
                    cancellationToken).ConfigureAwait(false);
                if (selectedId is not null)
                {
                    selectedFresh = true;
                    break;
                }
            }
            if (selectedId is not null)
            {
                break;
            }
        }
        if (selectedId is null)
        {
            foreach (var sourceKind in request.Selector.SourcePriority)
            {
                foreach (var specificity in specificities)
                {
                    selectedId = await SelectFirstIdAsync(
                        ApplySpecificity(
                            candidates.Where(observation => observation.SourceKind == sourceKind),
                            request.Target,
                            specificity),
                        cancellationToken).ConfigureAwait(false);
                    if (selectedId is not null)
                    {
                        break;
                    }
                }
                if (selectedId is not null)
                {
                    break;
                }
            }
        }
        var overlapCount = 0;
        if (selectedId is not null)
        {
            foreach (var sourceKind in request.Selector.SourcePriority)
            {
                foreach (var specificity in specificities)
                {
                    overlapCount += await ApplySpecificity(
                            covering.Where(observation => observation.SourceKind == sourceKind),
                            request.Target,
                            specificity)
                        .Select(observation => observation.Id)
                        .Take(2 - overlapCount)
                        .CountAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (overlapCount > 1)
                    {
                        break;
                    }
                }
                if (overlapCount > 1)
                {
                    break;
                }
            }
        }
        var selected = selectedId is null
            ? null
            : await LoadObservationAsync(selectedId.Value, cancellationToken).ConfigureAwait(false);
        if (selectedId is not null && selected is null && allowReselection)
        {
            return await CorrelateCoreAsync(request, allowReselection: false, cancellationToken).ConfigureAwait(false);
        }
        EnvironmentalObservationMatch match;
        if (selected is null)
        {
            match = new(EnvironmentalObservationMatchStatus.Missing, null, null, false);
        }
        else
        {
            var age = request.ThroughUtc - selected.ObservedAtUtc;
            match = new(
                selectedFresh ? EnvironmentalObservationMatchStatus.Fresh : EnvironmentalObservationMatchStatus.Stale,
                ToObservation(selected),
                age < TimeSpan.Zero ? TimeSpan.Zero : age,
                overlapCount > 1);
        }
        telemetry.RecordCorrelation(
            request.Selector.Kind,
            match.Status,
            match.HadOverlap,
            timeProvider.GetElapsedTime(started));
        activity?.SetTag("environment.outcome", match.Status.ToString());
        activity?.SetTag("environment.overlap", match.HadOverlap);
        return new EnvironmentalObservationSelection(match, selected?.Id, selected?.PayloadSha256);
    }

    internal IQueryable<Guid> BuildFreshSelectionIdQuery(
        EnvironmentalObservationCorrelationRequest request,
        EnvironmentalObservationSourceKind sourceKind,
        int specificity)
    {
        ValidateCorrelation(request);
        return OrderCandidates(ApplySpecificity(
            BuildFreshCandidates(BuildCoveringCandidates(BuildCandidates(request), request), request)
                .Where(observation => observation.SourceKind == sourceKind),
            request.Target,
            specificity)).Select(observation => observation.Id);
    }

    internal IQueryable<Guid> BuildStaleSelectionIdQuery(
        EnvironmentalObservationCorrelationRequest request,
        EnvironmentalObservationSourceKind sourceKind,
        int specificity)
    {
        ValidateCorrelation(request);
        return OrderCandidates(ApplySpecificity(
            BuildCandidates(request).Where(observation => observation.SourceKind == sourceKind),
            request.Target,
            specificity)).Select(observation => observation.Id);
    }

    internal IQueryable<Guid> BuildHistorySelectionIdQuery(EnvironmentalObservationQuery query)
    {
        ValidateQuery(query);
        return dbContext.EnvironmentalObservations
            .AsNoTracking()
            .Where(observation => observation.SiteId == query.SiteId &&
                (observation.AgentId == null || query.AgentId != null && observation.AgentId == query.AgentId) &&
                (observation.RigId == null || query.RigId != null && observation.RigId == query.RigId) &&
                observation.Kind == query.Kind &&
                observation.ObservedAtUtc >= query.FromUtc &&
                observation.ObservedAtUtc < query.ThroughUtc)
            .OrderByDescending(observation => observation.ObservedAtUtc)
            .ThenBy(observation => observation.SourceIdentitySha256)
            .ThenBy(observation => observation.ObservationId)
            .ThenBy(observation => observation.Id)
            .Select(observation => observation.Id)
            .Take(query.Take);
    }

    private void ValidateQuery(EnvironmentalObservationQuery query)
    {
        if (query.SiteId == Guid.Empty || !Enum.IsDefined(query.Kind) ||
            query.FromUtc == default || query.ThroughUtc == default ||
            query.FromUtc.Offset != TimeSpan.Zero || query.ThroughUtc.Offset != TimeSpan.Zero ||
            query.FromUtc >= query.ThroughUtc ||
            query.ThroughUtc - query.FromUtc > TimeSpan.FromDays(options.Value.MaximumQueryRangeDays) ||
            query.Take is < 1 || query.Take > options.Value.MaximumQueryResults ||
            query.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(query.RigId) && query.RigId is not null ||
            query.RigId is { Length: > 128 } || query.RigId is not null && query.AgentId is null)
        {
            throw new ArgumentException("The environmental observation query is invalid.", nameof(query));
        }
    }

    private void ValidateCorrelation(EnvironmentalObservationCorrelationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Selector);
        var clockTolerance = TimeSpan.FromSeconds(options.Value.ClockToleranceSeconds);
        if (request.Target.SiteId == Guid.Empty || request.Target.AgentId == Guid.Empty ||
            request.Target.RigId is not null && request.Target.AgentId is null ||
            string.IsNullOrWhiteSpace(request.Target.RigId) && request.Target.RigId is not null ||
            request.Target.RigId is { Length: > 128 } ||
            request.FromUtc == default || request.ThroughUtc == default ||
            request.FromUtc.Offset != TimeSpan.Zero || request.ThroughUtc.Offset != TimeSpan.Zero ||
            request.FromUtc > request.ThroughUtc || !Enum.IsDefined(request.Selector.Kind) ||
            request.Selector.SourcePriority is null or { Count: 0 } ||
            request.Selector.SourcePriority.Any(kind => !Enum.IsDefined(kind)) ||
            request.Selector.SourcePriority.Distinct().Count() != request.Selector.SourcePriority.Count ||
            request.Selector.AllowedQualities is null or { Count: 0 } ||
            request.Selector.AllowedQualities.Any(quality => !Enum.IsDefined(quality)) ||
            request.Selector.AllowedQualities.Distinct().Count() != request.Selector.AllowedQualities.Count ||
            request.Selector.MaximumStaleness <= TimeSpan.Zero ||
            request.Selector.MaximumStaleness > TimeSpan.FromDays(options.Value.MaximumCorrelationStalenessDays) ||
            request.ThroughUtc < DateTimeOffset.MinValue + request.Selector.MaximumStaleness ||
            request.ThroughUtc > DateTimeOffset.MaxValue - clockTolerance)
        {
            throw new ArgumentException("The environmental observation correlation request is invalid.", nameof(request));
        }
    }

    private IQueryable<EnvironmentalObservationRecord> BuildCandidates(
        EnvironmentalObservationCorrelationRequest request)
    {
        var qualities = request.Selector.AllowedQualities.ToArray();
        var oldest = request.ThroughUtc - request.Selector.MaximumStaleness;
        var latestAllowedObservation = request.ThroughUtc.AddSeconds(options.Value.ClockToleranceSeconds);
        return dbContext.EnvironmentalObservations
            .AsNoTracking()
            .Where(observation => observation.SiteId == request.Target.SiteId &&
                observation.Kind == request.Selector.Kind &&
                qualities.Contains(observation.Quality) &&
                observation.ObservedAtUtc <= latestAllowedObservation &&
                observation.ObservedAtUtc >= oldest &&
                observation.ValidFromUtc <= request.ThroughUtc);
    }

    private static IQueryable<EnvironmentalObservationRecord> BuildCoveringCandidates(
        IQueryable<EnvironmentalObservationRecord> candidates,
        EnvironmentalObservationCorrelationRequest request)
    {
        var isPoint = request.FromUtc == request.ThroughUtc;
        return candidates.Where(observation => observation.ValidFromUtc <= request.FromUtc &&
            (isPoint
                ? observation.ValidThroughUtc > request.ThroughUtc
                : observation.ValidThroughUtc >= request.ThroughUtc));
    }

    private static IQueryable<EnvironmentalObservationRecord> BuildFreshCandidates(
        IQueryable<EnvironmentalObservationRecord> covering,
        EnvironmentalObservationCorrelationRequest request)
    {
        var isPoint = request.FromUtc == request.ThroughUtc;
        return covering.Where(observation => isPoint
            ? observation.StaleAfterUtc > request.ThroughUtc
            : observation.StaleAfterUtc >= request.ThroughUtc);
    }

    private static IOrderedQueryable<EnvironmentalObservationRecord> OrderCandidates(
        IQueryable<EnvironmentalObservationRecord> candidates)
        => candidates
            .OrderByDescending(observation => observation.ObservedAtUtc)
            .ThenBy(observation => observation.SourceIdentitySha256)
            .ThenBy(observation => observation.ObservationId);

    private static IReadOnlyList<int> Specificities(EnvironmentalObservationTarget target)
        => target.RigId is not null ? [2, 1, 0] : target.AgentId is not null ? [1, 0] : [0];

    private static IQueryable<EnvironmentalObservationRecord> ApplySpecificity(
        IQueryable<EnvironmentalObservationRecord> candidates,
        EnvironmentalObservationTarget target,
        int specificity)
        => specificity switch
        {
            2 => candidates.Where(observation => observation.AgentId == target.AgentId && observation.RigId == target.RigId),
            1 => candidates.Where(observation => observation.AgentId == target.AgentId && observation.RigId == null),
            0 => candidates.Where(observation => observation.AgentId == null && observation.RigId == null),
            _ => throw new ArgumentOutOfRangeException(nameof(specificity))
        };

    private static Task<Guid?> SelectFirstIdAsync(
        IQueryable<EnvironmentalObservationRecord> candidates,
        CancellationToken cancellationToken)
        => OrderCandidates(candidates)
            .Select(observation => (Guid?)observation.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private Task<EnvironmentalObservationRecord?> LoadObservationAsync(
        Guid recordId,
        CancellationToken cancellationToken)
        => dbContext.EnvironmentalObservations
            .AsNoTracking()
            .Include(observation => observation.Source)
            .Include(observation => observation.Lineage)
                .ThenInclude(lineage => lineage.SourceObservation)
                    .ThenInclude(observation => observation!.Source)
            .SingleOrDefaultAsync(observation => observation.Id == recordId, cancellationToken);

    private static ReceivedEnvironmentalObservationV1 ToReceivedObservation(EnvironmentalObservationRecord record)
        => new(ToObservation(record), record.ReceivedAtUtc, record.PayloadSha256);

    private static EnvironmentalObservationV1 ToObservation(EnvironmentalObservationRecord record)
    {
        var source = record.Source ?? throw new InvalidOperationException("Environmental source was not loaded.");
        using var parameters = JsonDocument.Parse(source.ParametersJson);
        var lineage = record.Lineage.OrderBy(item => item.Ordinal)
            .Select(item => new EnvironmentalObservationReference(
                item.SourceObservation?.Source?.IdentitySha256 ??
                    throw new InvalidOperationException("Environmental lineage source was not loaded."),
                item.SourceObservation.ObservationId)).ToArray();
        return new EnvironmentalObservationV1(
            record.SchemaVersion,
            record.ObservationId,
            new EnvironmentalObservationTarget(source.SiteId, source.AgentId, source.RigId),
            new EnvironmentalObservationSource(
                source.Provider,
                source.SourceId,
                source.Version,
                source.Kind,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity(source.MethodName, source.MethodVersion),
                    parameters.RootElement.Clone(),
                    source.ParametersSha256)),
            record.ObservedAtUtc,
            record.ObservedFromUtc,
            record.ObservedThroughUtc,
            record.ValidFromUtc,
            record.ValidThroughUtc,
            record.StaleAfterUtc,
            new EnvironmentalObservationValue(
                record.Kind,
                record.Unit,
                record.NumericValue,
                record.BooleanValue,
                record.Quality,
                record.Uncertainty,
                record.SubmittedNumericValue,
                record.SubmittedUnit),
            lineage);
    }
}
