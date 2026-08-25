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
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
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
    CaptureAdmissionCoordinator captureAdmissionCoordinator,
    FleetRuntimeState fleetRuntimeState,
    IHostApplicationLifetime applicationLifetime,
    ILogger<CameraCaptureService> logger,
    CaptureScheduleRuntimeCoordinator? scheduleRuntimeCoordinator = null,
    EnvironmentalCaptureTriggerBridge? environmentalTriggers = null,
    IProjectedSceneStagingStore? projectedSceneStaging = null,
    ProjectedSceneStageLifecycleCoordinator? projectedSceneLifecycle = null,
    CaptureProjectedSceneStager? projectedSceneStager = null) : BackgroundService
{
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly ICameraModuleFactory _moduleFactory = moduleFactory;
    private readonly IRawCaptureIngress _rawCaptureIngress = rawCaptureIngress;
    private readonly ICaptureDistributor _captureDistributor = captureDistributor;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IPlanetEphemeris _planetEphemeris = planetEphemeris;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The dependency injection container owns this singleton telemetry service.")]
    private readonly CaptureControlTelemetry _captureControlTelemetry = captureControlTelemetry;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The dependency injection container owns this singleton coordinator.")]
    private readonly CaptureAdmissionCoordinator _captureAdmissionCoordinator = captureAdmissionCoordinator;
    private readonly FleetRuntimeState _fleetRuntimeState = fleetRuntimeState;
    private readonly IHostApplicationLifetime _applicationLifetime = applicationLifetime;
    private readonly ILogger<CameraCaptureService> _logger = logger;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The dependency injection container owns this singleton coordinator.")]
    private readonly CaptureScheduleRuntimeCoordinator? _scheduleRuntimeCoordinator = scheduleRuntimeCoordinator;
    private readonly EnvironmentalCaptureTriggerBridge? _environmentalTriggers = environmentalTriggers;
    private readonly IProjectedSceneStagingStore? _projectedSceneStaging = projectedSceneStaging;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The dependency injection container owns this singleton lifecycle coordinator.")]
    private readonly ProjectedSceneStageLifecycleCoordinator? _projectedSceneLifecycle = projectedSceneLifecycle;
    private readonly CaptureProjectedSceneStager? _projectedSceneStager = projectedSceneStager;
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Capture loop must continue after transient module failures.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStartedAsync(stoppingToken).ConfigureAwait(false);
        var fileConfiguration = await _configurationAccessor.WaitForConfigurationAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            CaptureScheduleCaptureContext? captureContext = null;
            CameraModuleConfig config;
            if (_scheduleRuntimeCoordinator is null)
            {
                config = fileConfiguration;
            }
            else
            {
                _ = await _scheduleRuntimeCoordinator.InitializeAsync(fileConfiguration, stoppingToken).ConfigureAwait(false);
                captureContext = _scheduleRuntimeCoordinator.CaptureContext;
                config = captureContext.Value.Snapshot.Configuration;
            }
            using var revisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                captureContext?.RevisionChanged ?? CancellationToken.None);
            var captureToken = revisionCancellation.Token;
            ICameraModule? module = null;
            try
            {
                module = _moduleFactory.Create(config.ModuleType);
                await _rawCaptureIngress.InitializeAsync(captureToken).ConfigureAwait(false);
                await _captureAdmissionCoordinator.InitializeAsync(captureToken).ConfigureAwait(false);
                await module.InitializeAsync(config, captureToken).ConfigureAwait(false);
                _fleetRuntimeState.ModuleAvailable();
                _logger.CameraModuleInitialized(module.DisplayName);

                var hostContext = new CaptureHostContext(
                     config, _rawCaptureIngress, _captureDistributor, _environmentalTriggers,
                     _projectedSceneStaging, _projectedSceneLifecycle, _projectedSceneStager, _logger);

                var runner = new CameraModuleRunner(
                    module,
                    hostContext,
                    _timeProvider,
                    _logger,
                    _captureAdmissionCoordinator,
                    _planetEphemeris,
                    _captureControlTelemetry,
                    _fleetRuntimeState,
                    _scheduleRuntimeCoordinator,
                    _environmentalTriggers);
                await runner.RunAsync(captureToken).ConfigureAwait(false);
                if (_scheduleRuntimeCoordinator is not null &&
                    revisionCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    continue;
                }
                break;
            }
            catch (OperationCanceledException) when (
                _scheduleRuntimeCoordinator is not null &&
                revisionCancellation.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _fleetRuntimeState.CaptureFailed(ex.GetType().Name);
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
                if (module is not null)
                {
                    try
                    {
                        await module.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _fleetRuntimeState.CaptureFailed(ex.GetType().Name);
                        _logger.CaptureLoopFailed(ex);
                        if (!stoppingToken.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                // A failed native close cannot be retried safely in the same host process.
                            }
                        }
                    }
                }
            }
        }
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stoppingRegistration = stoppingToken.Register(
            () => started.TrySetCanceled(stoppingToken));
        using var startedRegistration = _applicationLifetime.ApplicationStarted.Register(
            () => started.TrySetResult());
        await started.Task.ConfigureAwait(false);
    }
}
