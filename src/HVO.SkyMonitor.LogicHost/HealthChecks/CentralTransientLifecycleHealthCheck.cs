using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class CentralTransientLifecycleHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<CentralTransientNotificationOptions> notificationOptions,
    IOptions<CentralTransientPayloadReleaseOptions> releaseOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var pendingNotifications = await dbContext.CentralTransientNotificationDispatches.AsNoTracking()
            .Where(item => item.State == CentralTransientNotificationDispatchState.Pending)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Oldest = group.Min(item => item.CreatedUtc) })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var fencedNotifications = await dbContext.CentralTransientNotificationDispatches.AsNoTracking()
            .Where(item => item.State == CentralTransientNotificationDispatchState.Fenced)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Oldest = group.Min(item => item.FencedUtc) })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var pendingReleases = await dbContext.CentralTransientPayloadReleases.AsNoTracking()
            .Where(item => item.State == CentralTransientPayloadReleaseState.Pending)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Oldest = group.Min(item => item.CreatedUtc) })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var reservationStaleBefore = now - releaseOptions.Value.ReservationLeaseTimeout;
        var pendingReleaseItems = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                RetryDue = group.Count(item => item.ReservationToken == null && item.RetryAtUtc <= now),
                Reserved = group.Count(item => item.ReservationToken != null),
                ReservedStale = group.Count(item => item.ReservationToken != null &&
                    item.RequestedAtUtc <= reservationStaleBefore),
                Oldest = group.Min(item => item.RequestedAtUtc ?? item.Release!.CreatedUtc)
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var reservedReleaseItems = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.ReservationToken != null)
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Oldest = group.Min(item => item.RequestedAtUtc) })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var pendingSourceBytes = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
            .Join(dbContext.CentralArtifacts.AsNoTracking(), item => item.RecordId, artifact => artifact.Id,
                (_, artifact) => (long?)artifact.ByteLength)
            .SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var pendingDerivativeBytes = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.Kind == CentralTransientPayloadReleaseItemKind.Derivative)
            .Join(dbContext.CentralTransientDerivativeOutputIntents.AsNoTracking(), item => item.RecordId,
                intent => intent.Id, (_, intent) => (long?)intent.ByteLength)
            .SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var reservedSourceBytes = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.ReservationToken != null && item.Kind == CentralTransientPayloadReleaseItemKind.SourceArtifact)
            .Join(dbContext.CentralArtifacts.AsNoTracking(), item => item.RecordId, artifact => artifact.Id,
                (_, artifact) => (long?)artifact.ByteLength)
            .SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var reservedDerivativeBytes = await dbContext.CentralTransientPayloadReleaseItems.AsNoTracking()
            .Where(item => item.Outcome == CentralTransientPayloadReleaseItemOutcome.Pending &&
                item.ReservationToken != null && item.Kind == CentralTransientPayloadReleaseItemKind.Derivative)
            .Join(dbContext.CentralTransientDerivativeOutputIntents.AsNoTracking(), item => item.RecordId,
                intent => intent.Id, (_, intent) => (long?)intent.ByteLength)
            .SumAsync(cancellationToken).ConfigureAwait(false) ?? 0;
        var pendingNotificationAge = AgeSeconds(now, pendingNotifications?.Oldest);
        var fencedAge = AgeSeconds(now, fencedNotifications?.Oldest);
        var pendingReleaseAge = AgeSeconds(now, pendingReleases?.Oldest);
        var pendingReleaseItemAge = AgeSeconds(now, pendingReleaseItems?.Oldest);
        var reservedReleaseItemAge = AgeSeconds(now, reservedReleaseItems?.Oldest);
        var data = new Dictionary<string, object>
        {
            ["PendingNotificationCount"] = pendingNotifications?.Count ?? 0,
            ["OldestPendingNotificationAgeSeconds"] = pendingNotificationAge,
            ["FencedNotificationCount"] = fencedNotifications?.Count ?? 0,
            ["OldestFencedNotificationAgeSeconds"] = fencedAge,
            ["PendingPayloadReleaseCount"] = pendingReleases?.Count ?? 0,
            ["OldestPendingPayloadReleaseAgeSeconds"] = pendingReleaseAge,
            ["PendingPayloadReleaseItemCount"] = pendingReleaseItems?.Count ?? 0,
            ["RetryDuePayloadReleaseItemCount"] = pendingReleaseItems?.RetryDue ?? 0,
            ["ReservedPayloadReleaseItemCount"] = pendingReleaseItems?.Reserved ?? 0,
            ["StaleReservedPayloadReleaseItemCount"] = pendingReleaseItems?.ReservedStale ?? 0,
            ["ReservedPayloadReleaseLogicalBytes"] = checked(reservedSourceBytes + reservedDerivativeBytes),
            ["OldestReservedPayloadReleaseItemAgeSeconds"] = reservedReleaseItemAge,
            ["PendingPayloadReleaseLogicalBytes"] = checked(pendingSourceBytes + pendingDerivativeBytes),
            ["OldestPendingPayloadReleaseItemAgeSeconds"] = pendingReleaseItemAge
        };
        var staleNotification = pendingNotificationAge > notificationOptions.Value.FenceTimeout.TotalSeconds * 2;
        var expiredFence = fencedAge > notificationOptions.Value.FenceTimeout.TotalSeconds;
        var releaseDegradedAfter = Math.Max(30, releaseOptions.Value.PollInterval.TotalSeconds * 3);
        var staleRelease = pendingReleaseAge > releaseDegradedAfter;
        var staleReleaseItem = pendingReleaseItemAge > releaseDegradedAfter ||
            (pendingReleaseItems?.ReservedStale ?? 0) > 0;
        return staleNotification || expiredFence || staleRelease || staleReleaseItem
            ? HealthCheckResult.Degraded("Central transient lifecycle backlog requires attention.", data: data)
            : HealthCheckResult.Healthy("Central transient lifecycle queues are within bounds.", data);
    }

    private static double AgeSeconds(DateTimeOffset now, DateTimeOffset? timestamp)
        => timestamp.HasValue ? Math.Max(0, (now - timestamp.Value).TotalSeconds) : 0;
}
