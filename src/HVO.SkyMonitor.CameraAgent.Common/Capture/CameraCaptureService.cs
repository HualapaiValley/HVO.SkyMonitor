using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public sealed class CameraCaptureService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    ICameraModuleFactory moduleFactory,
    ICaptureProcessingPipelineFactory pipelineFactory,
    TimeProvider timeProvider,
    ILogger<CameraCaptureService> logger) : BackgroundService
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly ICameraModuleFactory _moduleFactory = moduleFactory;
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<CameraCaptureService> _logger = logger;
    private readonly CancellationTokenSource _drainAbort = new();
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Capture loop must continue after transient module failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await _configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var module = _moduleFactory.Create(config.ModuleType);
            FrameProcessingChannel? channel = null;
            Task? processingTask = null;

            try
            {
                await module.InitializeAsync(config, stoppingToken).ConfigureAwait(false);
                _logger.CameraModuleInitialized(module.DisplayName);

                channel = new FrameProcessingChannel(capacity: 4);
                var hostContext = new CaptureHostContext(config, channel);
                var processingSteps = _pipelineFactory.CreatePipeline(config);
                var processingWorker = new FrameProcessingWorker(channel, processingSteps, _logger);
                processingTask = processingWorker.RunAsync(_drainAbort.Token);

                var runner = new CameraModuleRunner(module, hostContext, _timeProvider, _logger);
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.CaptureLoopFailed(ex);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(RestartDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                try
                {
                    channel?.Complete();
                    if (processingTask is not null)
                    {
                        try
                        {
                            await processingTask.ConfigureAwait(false);
                            _logger.CaptureProcessingDrainCompleted();
                        }
                        catch (OperationCanceledException) when (_drainAbort.IsCancellationRequested)
                        {
                            _logger.CaptureProcessingDrainAborted();
                        }
                    }
                }
                finally
                {
                    await module.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(_drainAbort.Cancel);
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await _drainAbort.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    public override void Dispose()
    {
        _drainAbort.Dispose();
        base.Dispose();
    }
}
