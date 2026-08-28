using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

internal sealed class EnvironmentalObservationSchemaInitializationService(
    IOptions<CameraAgentHostOptions> options,
    SqliteEnvironmentalObservationOutbox store) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
        => await store.InitializeAsync(options.Value.RawIngressRoot, cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
