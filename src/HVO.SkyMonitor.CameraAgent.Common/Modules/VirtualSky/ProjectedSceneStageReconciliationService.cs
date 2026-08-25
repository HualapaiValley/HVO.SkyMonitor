using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

internal sealed class ProjectedSceneStageReconciliationService(
    IRawCaptureIngress rawIngress,
    IProjectedSceneStageOwnerProvider ownerProvider,
    IProjectedSceneStagingReconciler reconciler,
    RawIngressState state,
    RawIngressTelemetry telemetry,
    TimeProvider timeProvider,
    ProjectedSceneStageLifecycleCoordinator lifecycle,
    ILogger<ProjectedSceneStageReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan BacklogDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeriodicDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await rawIngress.InitializeAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            ProjectedSceneStageReconciliationResult result;
            try
            {
                result = await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.ProjectedSceneStageReconciliationFailed(exception);
                result = new(0, 0, 1);
                state.SetProjectedSceneBacklog(1);
            }
            await Task.Delay(result.BacklogCount > 0 ? BacklogDelay : PeriodicDelay, timeProvider, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask<ProjectedSceneStageReconciliationResult> ReconcileOnceAsync(
        CancellationToken cancellationToken)
    {
        using var lease = await lifecycle.AcquireReconciliationLeaseAsync(cancellationToken).ConfigureAwait(false);
        var owned = await ownerProvider.GetOwnedStageKeysAsync(cancellationToken).ConfigureAwait(false);
        var protectedKeys = new HashSet<string>(owned, StringComparer.Ordinal);
        protectedKeys.UnionWith(lease.PendingStageKeys);
        var result = await reconciler.ReconcileAsync(protectedKeys, cancellationToken).ConfigureAwait(false);
        telemetry.RecordProjectedSceneReconciliation(result);
        state.SetProjectedSceneBacklog(result.BacklogCount);
        return result;
    }
}
