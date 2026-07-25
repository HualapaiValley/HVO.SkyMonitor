using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class CameraAgentConfigurationInitializer(
    ICameraAgentConfigurationLoader loader,
    ICameraAgentConfigurationAccessor accessor,
    ICaptureProcessingPipelineFactory pipelineFactory,
    ILogger<CameraAgentConfigurationInitializer> logger,
    SqliteCaptureScheduleStore? scheduleStore = null) : IHostedService
{
    private readonly ICameraAgentConfigurationLoader _loader = loader;
    private readonly ICameraAgentConfigurationAccessor _accessor = accessor;
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly ILogger<CameraAgentConfigurationInitializer> _logger = logger;
    private readonly SqliteCaptureScheduleStore? _scheduleStore = scheduleStore;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = await _loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_scheduleStore is not null)
        {
            var schedule = await _scheduleStore.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
            config = schedule.ActiveRevision.Profile.ApplyTo(config);
            FileCameraAgentConfigurationLoader.ValidateConfig(config);
        }
        var graph = _pipelineFactory.CreateGraph(config);
        graph.DisposeSteps();
        _accessor.SetConfiguration(config);
        _logger.ConfigurationInitialized(config.ModuleType);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
