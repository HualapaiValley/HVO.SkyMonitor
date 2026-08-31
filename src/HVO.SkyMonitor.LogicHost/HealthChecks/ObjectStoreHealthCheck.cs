using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed partial class ObjectStoreHealthCheck(
    IObjectStore objectStore,
    IOptions<CentralObjectStorageOptions> options,
    ILogger<ObjectStoreHealthCheck> logger) : IHealthCheck
{
    private const int PersistentFailureThreshold = 3;
    private readonly CentralObjectStorageOptions _options = options.Value;
    private readonly object _stateLock = new();
    private int _consecutiveRetryableFailures;
    private HealthStatus? _lastStatus;

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
        string reason)
    {
        lock (_stateLock)
        {
            if (_lastStatus != status)
            {
                Log.StateChanged(logger, _lastStatus?.ToString() ?? "Unknown", status.ToString(), reason);
                _lastStatus = status;
            }
        }
        return new HealthCheckResult(
            status,
            description,
            data: new Dictionary<string, object>(StringComparer.Ordinal) { ["Reason"] = reason });
    }

    private static partial class Log
    {
        [LoggerMessage(2184, LogLevel.Information,
            "Object-storage readiness changed: Previous={Previous} Current={Current} Reason={Reason}")]
        public static partial void StateChanged(ILogger logger, string previous, string current, string reason);
    }
}
