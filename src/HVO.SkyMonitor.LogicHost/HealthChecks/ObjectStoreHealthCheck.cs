using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed partial class ObjectStoreHealthCheck(
    IObjectStore objectStore,
    IOptions<CentralObjectStorageOptions> options,
    ILogger<ObjectStoreHealthCheck> logger,
    Infrastructure.ObjectStorage.FilesystemObjectReconciliationWorker? reconciliation = null,
    TimeProvider? timeProvider = null) : IHealthCheck
{
    private const int PersistentFailureThreshold = 3;
    private readonly CentralObjectStorageOptions _options = options.Value;
    private readonly object _stateLock = new();
    private int _consecutiveRetryableFailures;
    private HealthStatus? _lastStatus;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await objectStore.BucketExistsAsync(_options.ArtifactBucket, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    HealthStatus.Unhealthy,
                    "The required artifact bucket is unavailable.",
                    "missing-bucket");
            }
            if (!await objectStore.BucketExistsAsync(_options.DiagnosticsBucket, cancellationToken).ConfigureAwait(false))
            {
                return Result(
                    HealthStatus.Unhealthy,
                    "The required diagnostics bucket is unavailable.",
                    "missing-bucket");
            }
            Interlocked.Exchange(ref _consecutiveRetryableFailures, 0);
            // For the filesystem provider, reconciliation facts are part of readiness: a
            // bucket with quarantined objects is serving (the rest of its objects are fine)
            // but an operator must look, which is Degraded; a bucket the reconciler could not
            // clean is the same. Bucket reachability alone is not the whole story locally.
            var latest = reconciliation?.Latest;
            if (objectStore is Infrastructure.ObjectStorage.FilesystemObjectStore && reconciliation is not null && latest is null)
            {
                return Result(
                    HealthStatus.Degraded,
                    "Object storage is available but its first reconciliation pass has not completed.",
                    "reconciliation-pending");
            }
            if (latest is { } facts)
            {
                var quarantined = facts.Reports.Values.Sum(report => report.Quarantined);
                var reclaimFailed = facts.Reports.Values.Sum(report => report.ReclaimFailed);
                var retiredBytes = facts.Reports.Values.Sum(report => report.RetiredBytes);
                var oldestRetired = facts.Reports.Values.Select(report => report.OldestRetiredAge).DefaultIfEmpty(TimeSpan.Zero).Max();
                var truncated = facts.Reports.Values.Count(report => report.Truncated);
                var failedBuckets = facts.Reports.Values.Count(report => report.FailureOutcome is not null);
                var age = _timeProvider.GetUtcNow() - facts.CompletedUtc;
                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["QuarantinedBuckets"] = quarantined,
                    ["ReclaimFailedCount"] = reclaimFailed,
                    ["RetiredBytes"] = retiredBytes,
                    ["OldestRetiredAgeSeconds"] = (long)oldestRetired.TotalSeconds,
                    ["ReconciledUtc"] = facts.CompletedUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["ReconciliationTruncatedBuckets"] = truncated,
                    ["ReconciliationFailedBuckets"] = failedBuckets,
                    ["ReconciliationAgeSeconds"] = Math.Max(0, (long)age.TotalSeconds)
                };
                if (age > Infrastructure.ObjectStorage.FilesystemObjectReconciliationWorker.Cadence * 2)
                {
                    return Result(
                        HealthStatus.Degraded,
                        "Object storage is serving but reconciliation evidence is stale.",
                        "reconciliation-stale",
                        data);
                }
                if (quarantined > 0 || reclaimFailed > 0 || truncated > 0 || failedBuckets > 0)
                {
                    return Result(
                        HealthStatus.Degraded,
                        "Object storage is serving but reconciliation quarantined or could not reclaim objects; operator attention is required.",
                        "reconciliation-attention",
                        data);
                }
                return Result(
                    HealthStatus.Healthy,
                    "Both required object-storage buckets are available and reconciled.",
                    "both-required-buckets-reconciled",
                    data);
            }
            return Result(
                HealthStatus.Healthy,
                "Authenticated access to both required object-storage buckets succeeded.",
                "both-required-buckets-authenticated");
        }
        catch (ObjectStoreException exception) when (exception.IsRetryable)
        {
            var failures = Interlocked.Increment(ref _consecutiveRetryableFailures);
            return failures >= PersistentFailureThreshold
                ? Result(
                    HealthStatus.Unhealthy,
                    "Object storage remains unavailable after repeated readiness checks.",
                    "persistent-outage")
                : Result(
                    HealthStatus.Degraded,
                    "Object storage is temporarily unavailable.",
                    ObjectStoreException.GetOutcome(exception.Kind));
        }
        catch (Exception exception) when (exception is ObjectStoreException or OperationCanceledException)
        {
            Interlocked.Exchange(ref _consecutiveRetryableFailures, 0);
            var reason = exception is ObjectStoreException objectStoreException
                ? ObjectStoreException.GetOutcome(objectStoreException.Kind)
                : "canceled";
            return Result(
                HealthStatus.Unhealthy,
                "Authenticated object-storage readiness failed.",
                reason);
        }
    }

    private HealthCheckResult Result(
        HealthStatus status,
        string description,
        string reason,
        Dictionary<string, object>? data = null)
    {
        lock (_stateLock)
        {
            if (_lastStatus != status)
            {
                Log.StateChanged(logger, _lastStatus?.ToString() ?? "Unknown", status.ToString(), reason);
                _lastStatus = status;
            }
        }
        data ??= new Dictionary<string, object>(StringComparer.Ordinal);
        data["Reason"] = reason;
        return new HealthCheckResult(status, description, data: data);
    }

    private static partial class Log
    {
        [LoggerMessage(2184, LogLevel.Information,
            "Object-storage readiness changed: Previous={Previous} Current={Current} Reason={Reason}")]
        public static partial void StateChanged(ILogger logger, string previous, string current, string reason);
    }
}
