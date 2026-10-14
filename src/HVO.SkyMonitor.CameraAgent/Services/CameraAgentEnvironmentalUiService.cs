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
    ValueTask<OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>> GetLatestReadingsAsync(
        CancellationToken cancellationToken);
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
    IReadOnlyList<EnvironmentalAcquisitionAttemptRecord> Attempts,
    int RetentionDays,
    EnvironmentalUiDelivery Delivery);

/// <summary>
/// The independent environmental outbox as the Environment page shows it. Counters and times only; no payload,
/// endpoint or credential is exposed. <see cref="ExportEnabled"/> is false when central integration or the
/// environmental delivery lane is disabled, and every counter is then zero.
/// </summary>
internal sealed record EnvironmentalUiDelivery(
    bool ExportEnabled,
    string Availability,
    DateTimeOffset? LastAcknowledgedUtc,
    long? PendingCount,
    long? RetryCount,
    long? QuarantineCount,
    DateTimeOffset? OldestPendingUtc);

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
    int ConsecutiveFailures,
    IReadOnlyList<EnvironmentalAcquisitionTrigger> Triggers,
    int PeriodSeconds,
    int EveryNthCapture);

internal sealed record EnvironmentalUiHistoryPage(
    IReadOnlyList<EnvironmentalUiObservation> Items,
    string? NextCursor);

internal sealed record EnvironmentalUiObservation(
    Guid ObservationId,
    string SourceId,
    EnvironmentalObservationSourceKind SourceKind,
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
    EnvironmentalObservationDeliveryState deliveryState,
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
                    state?.ConsecutiveFailures ?? 0,
                    source.Triggers,
                    source.PeriodSeconds,
                    source.EveryNthCapture);
            }).ToArray();
            return OperatorUiResult<EnvironmentalUiStatus>.Success(new(
                configured.Enabled,
                now,
                snapshot.StoredCount,
                snapshot.StoredBytes,
                snapshot.OverflowCount,
                sources,
                attempts,
                configured.RetentionDays,
                Delivery()));
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
                page.Items.Select(static item => Project(item)).ToArray(),
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

    /// <summary>
    /// Reads the newest retained observation of every kind, one indexed single-row read per kind, so the page can
    /// show a reading slot without scanning history. A kind with no retained observation is simply absent.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>> GetLatestReadingsAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var root = options.Value.RawIngressRoot;
            var latest = new List<EnvironmentalUiObservation>();
            foreach (var kind in Enum.GetValues<EnvironmentalObservationKind>())
            {
                var page = await observationStore.ReadLocalPageAsync(root, kind, 1, null, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Items.Count > 0)
                {
                    latest.Add(Project(page.Items[0]));
                }
            }
            return OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>.Success(latest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent environmental latest-reading read failed.");
            return OperatorUiResult<IReadOnlyList<EnvironmentalUiObservation>>.Failure(
                OperatorUiResultKind.Unavailable, "Current environmental readings are unavailable.");
        }
    }

    private EnvironmentalUiDelivery Delivery()
    {
        var host = options.Value;
        if (host.CentralIntegration.Mode == CentralIntegrationMode.Disabled || !host.EnvironmentalDelivery.Enabled)
        {
            return new(false, "Disabled", null, 0, 0, 0, null);
        }
        // The outbox has no snapshot until the delivery worker first reads it; its counts are then unknown, not zero.
        var snapshot = deliveryState.Snapshot;
        var outbox = snapshot.Outbox;
        return new(
            true,
            snapshot.Availability.ToString(),
            snapshot.LastAcknowledgedUtc,
            outbox?.PendingCount,
            outbox?.RetryCount,
            outbox?.QuarantineCount,
            outbox?.OldestPendingUtc);
    }

    private static EnvironmentalUiObservation Project(LocalEnvironmentalObservationRecord item) => new(
        item.Fact.ObservationId,
        item.Fact.Source.SourceId,
        item.Fact.Source.Kind,
        item.Fact.Value.Kind,
        item.Fact.Value.Unit,
        item.Fact.Value.NumericValue,
        item.Fact.Value.BooleanValue,
        item.Fact.Value.Quality,
        item.Fact.RigId,
        item.Fact.ObservedAtUtc,
        item.Fact.StaleAfterUtc);

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
        return CameraAgentCredentialAccess.GetOwnerId(state.User) is { Length: > 0 and <= 128 } actor
            ? actor
            : null;
    }

    private static bool ValidText(string value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
            value == value.Trim() && !value.Any(char.IsControl);
}
