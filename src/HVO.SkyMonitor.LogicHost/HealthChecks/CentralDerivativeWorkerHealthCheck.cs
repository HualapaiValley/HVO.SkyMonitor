using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class CentralDerivativeWorkerHealthCheck(
    ApplicationDbContext dbContext,
    IOptions<CentralDerivativeWorkerOptions> options,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return HealthCheckResult.Healthy(
                "Central derivative worker is disabled.",
                Data("disabled", 0, 0, 0, 0));
        }

        var now = timeProvider.GetUtcNow();
        var pendingStatuses = new[]
        {
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.RetryableFailure
        };
        var snapshot = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => pendingStatuses.Contains(job.Status)
                && job.AvailableAtUtc != null
                && job.AvailableAtUtc <= now)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Oldest = group.Min(job => job.AvailableAtUtc!.Value)
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var pendingCount = snapshot?.Count ?? 0;
        var oldestAge = snapshot is null ? 0 : Math.Max(0, (long)(now - snapshot.Oldest).TotalSeconds);
        var waiting = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Status == CentralDerivativeJobStatus.Waiting)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Oldest = group.Min(job => job.ResolutionStartedAtUtc ?? job.CreatedAtUtc),
                Overdue = group.LongCount(job => job.ResolutionDeadlineUtc <= now)
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var activeWindowStatuses = new[]
        {
            CentralDerivativeJobStatus.Waiting,
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.Leased,
            CentralDerivativeJobStatus.RetryableFailure,
            CentralDerivativeJobStatus.CancelRequested
        };
        var oldestPin = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => activeWindowStatuses.Contains(input.Job!.Status))
            .MinAsync(input => (DateTimeOffset?)input.SelectedAtUtc, cancellationToken).ConfigureAwait(false);
        var inconsistentRunnable = await dbContext.CentralDerivativeJobs.AsNoTracking().AnyAsync(job =>
            (job.Status == CentralDerivativeJobStatus.Pending || job.Status == CentralDerivativeJobStatus.RetryableFailure)
            && job.InputRequirements.Any()
            && (job.InputSetIdentitySha256 == null || !job.Inputs.Any()
                || job.InputRequirements.Any(requirement => requirement.IsRequired
                    && (requirement.ResolutionState == CentralDerivativeInputResolutionState.Resolved
                        ? requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact
                            ? !job.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id)
                            : !job.CanonicalInputs.Any(input =>
                                input.CentralDerivativeJobInputRequirementId == requirement.Id)
                        : requirement.ResolutionState != CentralDerivativeInputResolutionState.Missing
                            || job.MissingInputOutcome != CentralDerivativeWindowOutcome.Run))),
            cancellationToken).ConfigureAwait(false);
        var waitingCount = waiting?.Count ?? 0;
        var oldestWaitAge = waiting is null ? 0 : Math.Max(0, (long)(now - waiting.Oldest).TotalSeconds);
        var overdueWaiting = waiting?.Overdue ?? 0;
        var oldestPinAge = oldestPin.HasValue ? Math.Max(0, (long)(now - oldestPin.Value).TotalSeconds) : 0;
        var lastPollAge = AgeSeconds(now, telemetry.LastPollUtc);
        var lastSuccessAge = AgeSeconds(now, telemetry.LastSuccessUtc);
        var data = Data("healthy", telemetry.ActiveCount, pendingCount, oldestAge, lastSuccessAge);
        data["WaitingCount"] = waitingCount;
        data["OldestWaitAgeSeconds"] = oldestWaitAge;
        data["OverdueWaitingCount"] = overdueWaiting;
        data["OldestPinAgeSeconds"] = oldestPinAge;

        if (inconsistentRunnable)
        {
            data["Status"] = "inconsistent-window";
            return HealthCheckResult.Unhealthy("A runnable derivative window has no frozen inputs.", data: data);
        }

        if (telemetry.LastPollUtc is null || lastPollAge > settings.LeaseDuration.TotalSeconds)
        {
            data["Status"] = "stale";
            return HealthCheckResult.Unhealthy("Central derivative worker heartbeat is stale.", data: data);
        }
        var hasRenewalFailure = telemetry.HasRecentRenewalFailure(now, settings.LeaseDuration);
        var hasDependencyFailure = telemetry.HasRecentDependencyFailure(now, settings.LeaseDuration);
        if (oldestAge > settings.BacklogDegradedAfter.TotalSeconds
            || oldestPinAge > settings.BacklogDegradedAfter.TotalSeconds || overdueWaiting > 0
            || hasRenewalFailure || hasDependencyFailure)
        {
            data["Status"] = hasRenewalFailure
                ? "renewal-failure"
                : hasDependencyFailure ? "dependency-failure" : overdueWaiting > 0 ? "window-overdue" : "backlog";
            return HealthCheckResult.Degraded("Central derivative worker is degraded.", data: data);
        }
        return HealthCheckResult.Healthy("Central derivative worker is healthy.", data);
    }

    private static Dictionary<string, object> Data(
        string status,
        long activeSlots,
        long pendingCount,
        long oldestAgeSeconds,
        long lastSuccessAgeSeconds) => new()
        {
            ["Status"] = status,
            ["ActiveSlots"] = activeSlots,
            ["PendingCount"] = pendingCount,
            ["OldestAgeSeconds"] = oldestAgeSeconds,
            ["LastSuccessAgeSeconds"] = lastSuccessAgeSeconds
        };

    private static long AgeSeconds(DateTimeOffset now, DateTimeOffset? value)
        => value.HasValue ? Math.Max(0, (long)(now - value.Value).TotalSeconds) : 0;
}
