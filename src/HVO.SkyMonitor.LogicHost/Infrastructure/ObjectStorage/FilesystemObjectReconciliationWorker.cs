using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

/// <summary>
/// Runs reconciliation once at startup (interrupted publications are resumed before the
/// store serves traffic for long) and then on a fixed cadence. Registered only when the
/// filesystem provider is selected. The latest report is exposed for the health check, so
/// quarantine and reclamation backlog are operator-visible facts rather than log lines.
/// </summary>
internal sealed partial class FilesystemObjectReconciliationWorker(
    IObjectStore objectStore,
    IOptions<CentralObjectStorageOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly CentralObjectStorageOptions _options = options.Value;
    private readonly ILogger<FilesystemObjectReconciliationWorker> _logger = loggerFactory.CreateLogger<FilesystemObjectReconciliationWorker>();
    private readonly object _reportLock = new();
    private Dictionary<string, FilesystemReconciliationReport>? _latest;
    private DateTimeOffset? _latestUtc;

    internal static readonly TimeSpan Cadence = TimeSpan.FromMinutes(10);

    /// <summary>The most recent per-bucket reports, or null before the first pass completes.</summary>
    public (IReadOnlyDictionary<string, FilesystemReconciliationReport> Reports, DateTimeOffset CompletedUtc)? Latest
    {
        get
        {
            lock (_reportLock)
            {
                return _latest is null || _latestUtc is null ? null : (_latest, _latestUtc.Value);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (objectStore is not FilesystemObjectStore store)
        {
            return;
        }
        var reconciler = new FilesystemObjectReconciler(store, timeProvider, loggerFactory.CreateLogger<FilesystemObjectReconciler>());
        using var timer = new PeriodicTimer(Cadence, timeProvider);
        do
        {
            RunOnce(reconciler, stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal void RunOnce(
        FilesystemObjectReconciler reconciler,
        CancellationToken cancellationToken,
        FilesystemReconciliationOptions? reconciliationOptions = null)
    {
        var reports = new Dictionary<string, FilesystemReconciliationReport>(StringComparer.Ordinal);
        foreach (var bucket in new[] { _options.ArtifactBucket, _options.DiagnosticsBucket })
        {
            try
            {
                reports[bucket] = reconciler.Reconcile(bucket, reconciliationOptions ?? new FilesystemReconciliationOptions(), cancellationToken);
            }
            catch (ObjectStoreException exception)
            {
                var outcome = ObjectStoreException.GetOutcome(exception.Kind);
                reports[bucket] = new FilesystemReconciliationReport { FailureOutcome = outcome };
                LogBucketSkipped(_logger, bucket, outcome);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HVO.SkyMonitor.Storage.FileSystem.FileSystemFaultException)
            {
                reports[bucket] = new FilesystemReconciliationReport { FailureOutcome = "filesystem-access" };
                LogBucketSkipped(_logger, bucket, "filesystem-access");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reports[bucket] = new FilesystemReconciliationReport { FailureOutcome = "reconciliation-failed" };
                LogBucketFailed(_logger, bucket, exception);
            }
        }
        lock (_reportLock)
        {
            _latest = reports;
            _latestUtc = timeProvider.GetUtcNow();
        }
    }

    [LoggerMessage(2187, LogLevel.Warning, "Filesystem object store reconciliation skipped bucket {Bucket}: {Outcome}.")]
    private static partial void LogBucketSkipped(ILogger logger, string bucket, string outcome);

    [LoggerMessage(2188, LogLevel.Error, "Filesystem object store reconciliation failed unexpectedly for bucket {Bucket}.")]
    private static partial void LogBucketFailed(ILogger logger, string bucket, Exception exception);
}

internal sealed class FilesystemObjectReconciliationHostedService(
    FilesystemObjectReconciliationWorker worker) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => worker.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => worker.StopAsync(cancellationToken);
}
