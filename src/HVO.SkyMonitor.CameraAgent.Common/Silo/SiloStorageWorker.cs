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
        string? previous = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = await CheckAndRecoverAsync(stoppingToken).ConfigureAwait(false);
            Volatile.Write(ref _status, status);
            if (status != previous)
                ReportStatus(logger, status);
            previous = status;
            await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<string> CheckAndRecoverAsync(CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await storage.VerifyReadinessAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SiloStoreUnavailableException ||
            exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
        {
            return "silo-store-unavailable";
        }
        catch (Exception exception) when (exception is SiloConflictException or SiloDataException)
        {
            return "silo-binding-refused";
        }

        // Readiness owns only the bounded probe. Resumable work can take longer and is recovered with host cancellation.
        Volatile.Write(ref _status, "silo-ready");
        try
        {
            var outcomes = await storage.Publisher.ResumeAsync(16, stoppingToken).ConfigureAwait(false);
            var raw = await ingress.PublishSiloAdmissionsAsync(16, stoppingToken).ConfigureAwait(false);
            if (ingress.SiloAdmissionsReady)
                await views.PublishSiloChangesAsync(8, stoppingToken).ConfigureAwait(false);
            return raw ?? outcomes.FirstOrDefault(static outcome => outcome.BlockedCode is not null)?.BlockedCode ?? "silo-ready";
        }
        catch (SiloStoreUnavailableException)
        {
            return "silo-store-unavailable";
        }
        catch (Exception exception) when (exception is SiloCatalogDivergedException or SiloConflictException or SiloDataException)
        {
            return "silo-reconciliation-refused";
        }
        catch (IOException)
        {
            return "silo-staged-evidence-unavailable";
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = Volatile.Read(ref _status);
        return Task.FromResult(status == "silo-ready" ? HealthCheckResult.Healthy(status) : HealthCheckResult.Unhealthy(status));
    }

    [LoggerMessage(7396, LogLevel.Information, "CameraAgent Silo storage state {Status}")]
    private static partial void ReportStatus(ILogger logger, string status);
}
