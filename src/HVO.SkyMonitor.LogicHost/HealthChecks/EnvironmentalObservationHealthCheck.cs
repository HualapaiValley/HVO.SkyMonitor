using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class EnvironmentalObservationHealthCheck(
    ApplicationDbContext dbContext,
    EnvironmentalRetentionState retentionState,
    TimeProvider timeProvider,
    IOptions<EnvironmentalObservationOptions> options) : IHealthCheck
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Public health must report a bounded failure without disclosing provider exception details.")]
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        int sourceCount;
        int observationCount;
        int retentionEligibleCount;
        DateTimeOffset? newestReceivedUtc;
        DateTimeOffset? oldestEligibleUtc;
        try
        {
            sourceCount = await dbContext.EnvironmentalObservationSources
                .AsNoTracking()
                .Select(source => source.Id)
                .Take(10_001)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
            observationCount = await dbContext.EnvironmentalObservations
                .AsNoTracking()
                .Select(observation => observation.Id)
                .Take(10_001)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
            newestReceivedUtc = await dbContext.EnvironmentalObservations
                .AsNoTracking()
                .OrderByDescending(observation => observation.ReceivedAtUtc)
                .Select(observation => (DateTimeOffset?)observation.ReceivedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var receiptCutoff = now - TimeSpan.FromDays(options.Value.ReceiptRetentionDays);
            var eligible = dbContext.EnvironmentalObservations
                .AsNoTracking()
                .Where(observation => observation.ReceivedAtUtc < receiptCutoff &&
                    observation.ValidThroughUtc < now && !observation.ReferencedBy.Any());
            retentionEligibleCount = await eligible
                .Select(observation => observation.Id)
                .Take(10_001)
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
            oldestEligibleUtc = await eligible
                .OrderBy(observation => observation.ReceivedAtUtc)
                .ThenBy(observation => observation.ValidThroughUtc)
                .ThenBy(observation => observation.Id)
                .Select(observation => (DateTimeOffset?)observation.ReceivedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Environmental observation persistence is unavailable.");
        }
        var retention = retentionState.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["SourceCount"] = sourceCount,
            ["ObservationCount"] = observationCount,
            ["NewestReceivedAgeSeconds"] = newestReceivedUtc is { } newest
                ? Math.Max(0, (now - newest).TotalSeconds)
                : 0,
            ["RetentionEligibleCount"] = retentionEligibleCount,
            ["RetentionOldestAgeSeconds"] = oldestEligibleUtc is { } oldest
                ? Math.Max(0, (now - oldest).TotalSeconds)
                : 0,
            ["RetentionLastSucceededUtc"] = retention.LastSucceededUtc?.ToString("O") ?? "never"
        };
        if (retention.FailureReason is not null)
        {
            return retention.TerminalFailure
                ? HealthCheckResult.Unhealthy("Environmental observation retention has a terminal failure.", data: data)
                : HealthCheckResult.Degraded("Environmental observation retention will retry after a failed sweep.", data: data);
        }
        if (retention.LastSucceededUtc is null)
        {
            return HealthCheckResult.Degraded(
                "Environmental observation retention has not completed its first sweep.",
                data: data);
        }
        var maximumAge = TimeSpan.FromSeconds(options.Value.RetentionSweepSeconds * 2L);
        return now - retention.LastSucceededUtc > maximumAge
            ? HealthCheckResult.Unhealthy("Environmental observation retention has not completed a recent sweep.", data: data)
            : HealthCheckResult.Healthy("Environmental observation persistence and retention are operational.", data);
    }
}
