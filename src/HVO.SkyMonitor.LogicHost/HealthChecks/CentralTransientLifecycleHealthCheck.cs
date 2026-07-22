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

        var pendingNotificationAge = AgeSeconds(now, pendingNotifications?.Oldest);
        var fencedAge = AgeSeconds(now, fencedNotifications?.Oldest);
        var pendingReleaseAge = AgeSeconds(now, pendingReleases?.Oldest);
        var data = new Dictionary<string, object>
        {
            ["PendingNotificationCount"] = pendingNotifications?.Count ?? 0,
            ["OldestPendingNotificationAgeSeconds"] = pendingNotificationAge,
            ["FencedNotificationCount"] = fencedNotifications?.Count ?? 0,
            ["OldestFencedNotificationAgeSeconds"] = fencedAge,
            ["PendingPayloadReleaseCount"] = pendingReleases?.Count ?? 0,
            ["OldestPendingPayloadReleaseAgeSeconds"] = pendingReleaseAge
        };
        var staleNotification = pendingNotificationAge > notificationOptions.Value.FenceTimeout.TotalSeconds * 2;
        var expiredFence = fencedAge > notificationOptions.Value.FenceTimeout.TotalSeconds;
        var staleRelease = pendingReleaseAge > Math.Max(30, releaseOptions.Value.PollInterval.TotalSeconds * 3);
        return staleNotification || expiredFence || staleRelease
            ? HealthCheckResult.Degraded("Central transient lifecycle backlog requires attention.", data: data)
            : HealthCheckResult.Healthy("Central transient lifecycle queues are within bounds.", data);
    }

    private static double AgeSeconds(DateTimeOffset now, DateTimeOffset? timestamp)
        => timestamp.HasValue ? Math.Max(0, (now - timestamp.Value).TotalSeconds) : 0;
}
