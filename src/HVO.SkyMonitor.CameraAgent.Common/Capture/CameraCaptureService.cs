using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

public sealed class CameraCaptureService(
    ICameraAgentConfigurationAccessor configurationAccessor,
    ICameraModuleFactory moduleFactory,
    IRawCaptureIngress rawCaptureIngress,
    ICaptureDistributor captureDistributor,
    TimeProvider timeProvider,
    IPlanetEphemeris planetEphemeris,
    CaptureControlTelemetry captureControlTelemetry,
    ILogger<CameraCaptureService> logger) : BackgroundService
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly ICameraModuleFactory _moduleFactory = moduleFactory;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly ICaptureDistributor _captureDistributor = captureDistributor;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IPlanetEphemeris _planetEphemeris = planetEphemeris;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The dependency injection container owns this singleton telemetry service.")]
    private readonly CaptureControlTelemetry _captureControlTelemetry = captureControlTelemetry;
    private readonly ILogger<CameraCaptureService> _logger = logger;
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Capture loop must continue after transient module failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await _configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var module = _moduleFactory.Create(config.ModuleType);
            try
            {
                await _rawCaptureIngress.InitializeAsync(stoppingToken).ConfigureAwait(false);
                await module.InitializeAsync(config, stoppingToken).ConfigureAwait(false);
                _logger.CameraModuleInitialized(module.DisplayName);

                var hostContext = new CaptureHostContext(config, _rawCaptureIngress, _captureDistributor);

                var runner = new CameraModuleRunner(
                    module,
                    hostContext,
                    _timeProvider,
                    _logger,
                    _planetEphemeris,
                    _captureControlTelemetry);
                await runner.RunAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ex is RawIngressConflictException or IOException or InvalidDataException or
                    UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    _logger.RawIngressRefused("capture-loop", "storage-unavailable");
                }
                else
                {
                    _logger.CaptureLoopFailed(ex);
                }
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
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
