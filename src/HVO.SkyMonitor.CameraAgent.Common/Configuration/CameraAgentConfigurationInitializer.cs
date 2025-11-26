using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class CameraAgentConfigurationInitializer(
    ICameraAgentConfigurationLoader loader,
    ICameraAgentConfigurationAccessor accessor,
    ILogger<CameraAgentConfigurationInitializer> logger) : IHostedService
{
    private readonly ICameraAgentConfigurationLoader _loader = loader;
    private readonly ICameraAgentConfigurationAccessor _accessor = accessor;
    private readonly ILogger<CameraAgentConfigurationInitializer> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = await _loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        _accessor.SetConfiguration(config);
        _logger.ConfigurationInitialized(config.ModuleType);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
