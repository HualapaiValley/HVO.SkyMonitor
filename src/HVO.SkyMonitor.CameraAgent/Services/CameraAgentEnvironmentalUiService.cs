using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentEnvironmentalUiService
{
    ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken);
    ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>> GetHistoryAsync(
        EnvironmentalObservationKind? kind, int pageSize, string? cursor, CancellationToken cancellationToken);
    ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>> AcquireAsync(
        string sourceId, string idempotencyKey, string reason, CancellationToken cancellationToken);
}

internal sealed record EnvironmentalUiStatus(
    bool Enabled,
    DateTimeOffset EvaluatedUtc,
    long StoredCount,
    long StoredBytes,
    long OverflowCount,
    IReadOnlyList<EnvironmentalUiSource> Sources,
    IReadOnlyList<EnvironmentalAcquisitionAttemptRecord> Attempts);

internal sealed record EnvironmentalUiSource(
    string Id,
    EnvironmentalObservationKind Kind,
    bool Required,
    bool SupportsOnDemand,
    string Freshness,
    EnvironmentalAcquisitionDisposition? LastDisposition,
    string? LastReason,
    DateTimeOffset? LastObservedUtc,
    double? LastObservationAgeSeconds,
    DateTimeOffset? NextPollUtc,
    int ConsecutiveFailures);

internal sealed record EnvironmentalUiHistoryPage(
    IReadOnlyList<EnvironmentalUiObservation> Items,
    string? NextCursor);

internal sealed record EnvironmentalUiObservation(
    Guid ObservationId,
    string SourceId,
    EnvironmentalObservationKind Kind,
    EnvironmentalObservationUnit Unit,
    double? NumericValue,
    bool? BooleanValue,
    EnvironmentalObservationQuality Quality,
    string? RigId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset StaleAfterUtc);

internal sealed class CameraAgentEnvironmentalUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IEnvironmentalAcquisitionStateStore stateStore,
    ILocalEnvironmentalObservationStore observationStore,
    EnvironmentalOnDemandAcquisitionService commandService,
    IOptions<CameraAgentHostOptions> options,
    OutboxOperationsTokenService tokens,
    TimeProvider timeProvider,
    ILogger<CameraAgentEnvironmentalUiService> logger) : ICameraAgentEnvironmentalUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<EnvironmentalUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var configured = options.Value.EnvironmentalAcquisition;
            var root = options.Value.RawIngressRoot;
            var now = timeProvider.GetUtcNow();
            var runtime = await stateStore.ReadSourceStatesAsync(root, cancellationToken).ConfigureAwait(false);
            var byId = runtime.ToDictionary(static state => state.SourceId, StringComparer.Ordinal);
            var snapshot = await observationStore.GetLocalSnapshotAsync(root, cancellationToken).ConfigureAwait(false);
            var attempts = await stateStore.ReadAttemptsAsync(root, 50, cancellationToken).ConfigureAwait(false);
            var sources = configured.Sources.OrderBy(static source => source.Id, StringComparer.Ordinal).Select(source =>
            {
                _ = byId.TryGetValue(source.Id, out var state);
                return new EnvironmentalUiSource(
                    source.Id,
                    source.Kind,
                    source.Required,
                    source.Triggers.Contains(EnvironmentalAcquisitionTrigger.OnDemand),
                    state?.LastStaleAfterUtc is { } staleAfter ? staleAfter > now ? "Fresh" : "Stale" : "Never observed",
                    state?.LastDisposition,
                    state?.LastReason,
                    state?.LastObservedUtc,
                    state?.LastObservedUtc is { } observed ? Math.Max(0, (now - observed).TotalSeconds) : null,
                    state?.NextPollUtc,
                    state?.ConsecutiveFailures ?? 0);
            }).ToArray();
            return OperatorUiResult<EnvironmentalUiStatus>.Success(new(
                configured.Enabled,
                now,
                snapshot.StoredCount,
                snapshot.StoredBytes,
                snapshot.OverflowCount,
                sources,
                attempts));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent environmental UI status read failed.");
            return OperatorUiResult<EnvironmentalUiStatus>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental status is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>> AcquireAsync(
        string sourceId,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        var actor = await GetAuthorizedActorAsync().ConfigureAwait(false);
        if (actor is null)
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unauthorized, "You are not authorized for this operation.");
        }
        var configured = options.Value.EnvironmentalAcquisition;
        if (!configured.Enabled)
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Invalid, "Environmental acquisition is disabled.");
        }
        var source = configured.Sources.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
        if (source is null)
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.NotFound, "The environmental source is not configured.");
        }
        if (!source.Triggers.Contains(EnvironmentalAcquisitionTrigger.OnDemand))
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Invalid, "This source does not support on-demand acquisition.");
        }
        if (!ValidText(idempotencyKey, 128) || !ValidText(reason, 128))
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Invalid, "The on-demand request is invalid.");
        }
        try
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Success(
                await commandService.AcquireAsync(
                    sourceId, idempotencyKey, actor, reason, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EnvironmentalOnDemandCommandConflictException)
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Conflict, "The idempotency key is already bound to another request.");
        }
        catch (EnvironmentalOnDemandCommandBusyException)
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The environmental source is already acquiring an observation.");
        }
        catch (EnvironmentalOnDemandCommandCapacityException)
        {
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental command capacity is unavailable.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent environmental on-demand command failed.");
            return OperatorUiResult<EnvironmentalOnDemandAcquisitionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The environmental request could not be completed.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<EnvironmentalUiHistoryPage>> GetHistoryAsync(
        EnvironmentalObservationKind? kind,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<EnvironmentalUiHistoryPage>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            LocalEnvironmentalObservationCursor? decoded = null;
            if (cursor is not null && !tokens.TryReadEnvironmentalHistoryCursor(cursor, kind, out decoded))
            {
                return OperatorUiResult<EnvironmentalUiHistoryPage>.Failure(
                    OperatorUiResultKind.Invalid, "The history page expired. Return to newest observations.");
            }
            var page = await observationStore.ReadLocalPageAsync(
                options.Value.RawIngressRoot, kind, pageSize, decoded, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<EnvironmentalUiHistoryPage>.Success(new(
                page.Items.Select(static item => new EnvironmentalUiObservation(
                    item.Fact.ObservationId,
                    item.Fact.Source.SourceId,
                    item.Fact.Value.Kind,
                    item.Fact.Value.Unit,
                    item.Fact.Value.NumericValue,
                    item.Fact.Value.BooleanValue,
                    item.Fact.Value.Quality,
                    item.Fact.RigId,
                    item.Fact.ObservedAtUtc,
                    item.Fact.StaleAfterUtc)).ToArray(),
                page.NextCursor is null ? null : tokens.ProtectEnvironmentalHistoryCursor(page.NextCursor, kind)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent environmental UI history read failed.");
            return OperatorUiResult<EnvironmentalUiHistoryPage>.Failure(
                OperatorUiResultKind.Unavailable, "Environmental history is unavailable.");
        }
    }

    private async Task<bool> IsAuthorizedAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return (await authorizationService.AuthorizeAsync(
            state.User, resource: null, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded;
    }

    private async ValueTask<string?> GetAuthorizedActorAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        if (!(await authorizationService.AuthorizeAsync(
                state.User, CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false)).Succeeded)
        {
            return null;
        }
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 and <= 128 } actor
            ? actor
            : null;
    }

    private static bool ValidText(string value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
            value == value.Trim() && !value.Any(char.IsControl);
}
