using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed class CaptureAdmissionInitializationService(
    CaptureAdmissionCoordinator coordinator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
        => await coordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
