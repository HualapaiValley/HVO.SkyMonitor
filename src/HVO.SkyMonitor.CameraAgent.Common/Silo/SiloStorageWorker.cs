using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed partial class SiloStorageWorker(SiloInstalledStorage storage, RawCaptureIngress ingress, SqliteCenteredViews views,
    TimeProvider clock, ILogger<SiloStorageWorker> logger)
    : BackgroundService, IHealthCheck
{
    private string _status = "silo-initializing";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            string? previous = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var (status, more) = await CheckAndRecoverAsync(stoppingToken).ConfigureAwait(false);
                Volatile.Write(ref _status, status);
                if (status != previous)
                    ReportStatus(logger, status);
                previous = status;
                if (!more)
                    await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A deliberate worker stop completes cleanly even if the host is still running.
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A storage failure is reported through dependency health without terminating capture host supervision.")]
    private async ValueTask<(string Status, bool More)> CheckAndRecoverAsync(CancellationToken stoppingToken)
    {
        const int rawBatchSize = 16;
        const int viewBatchSize = 8;
        var verifyingBinding = true;
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await storage.VerifyReadinessAsync(deadline.Token).ConfigureAwait(false);
            }
            verifyingBinding = false;

            // The raw ledger may already contain a quarantine or write-off. Only its owner advances raw groups.
            var resumed = await storage.Publisher.ResumeAsync(rawBatchSize, skipRawAdmissions: true, stoppingToken).ConfigureAwait(false);
            var raw = await ingress.PublishSiloAdmissionsAsync(rawBatchSize, stoppingToken).ConfigureAwait(false);
            if (!ingress.SiloAdmissionsReady)
                return ("silo-initializing", false);

            var centered = await views.PublishSiloChangesAsync(viewBatchSize, stoppingToken).ConfigureAwait(false);
            var recovery = resumed.FirstOrDefault(static outcome => outcome.BlockedCode is not null ||
                outcome.CatalogState == "closed");
            var status = raw.Status ?? centered.Status ?? recovery?.BlockedCode ?? recovery?.CatalogOutcome;
            return (status ?? "silo-ready", raw.More || centered.More ||
                status is null && resumed.Count == rawBatchSize && resumed.All(static outcome => outcome.State == SiloStagedState.Visible));
        }
        catch (Exception exception) when (exception is SiloStoreUnavailableException ||
            exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
        {
            return ("silo-store-unavailable", false);
        }
        catch (Exception exception) when (exception is SiloCatalogDivergedException or SiloConflictException or SiloDataException)
        {
            ReportFailure(logger, exception);
            return (verifyingBinding ? "silo-binding-refused" : "silo-reconciliation-refused", false);
        }
        catch (SiloSpoolFullException)
        {
            return ("silo-publication-spool-full", false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ReportFailure(logger, exception);
            return ("silo-staged-evidence-missing", false);
        }
        catch (Exception exception) when (exception is IOException)
        {
            ReportFailure(logger, exception);
            return ("silo-local-io-unavailable", false);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            ReportFailure(logger, exception);
            return ("silo-worker-failed", false);
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = Volatile.Read(ref _status);
        return Task.FromResult(status == "silo-ready" ? HealthCheckResult.Healthy(status) : HealthCheckResult.Unhealthy(status));
    }

    [LoggerMessage(7397, LogLevel.Error, "CameraAgent Silo storage recovery failed")]
    private static partial void ReportFailure(ILogger logger, Exception exception);

    [LoggerMessage(7396, LogLevel.Information, "CameraAgent Silo storage state {Status}")]
    private static partial void ReportStatus(ILogger logger, string status);
}
