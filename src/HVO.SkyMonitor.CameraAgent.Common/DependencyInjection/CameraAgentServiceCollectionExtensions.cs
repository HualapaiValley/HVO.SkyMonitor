using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;

public static class CameraAgentServiceCollectionExtensions
{
    public static IServiceCollection AddCameraAgentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CameraAgentHostOptions>()
            .Bind(configuration.GetSection("CameraAgent"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ICameraAgentConfigurationAccessor, CameraAgentConfigurationAccessor>();
        services.AddSingleton<ICameraAgentConfigurationLoader, FileCameraAgentConfigurationLoader>();
        services.AddSingleton<IFrameStorageService, FileSystemFrameStorageService>();
        services.AddSingleton<ICameraModuleFactory, CameraModuleFactory>();
        services.AddSingleton<ILatestFrameAccessor, LatestFrameAccessor>();
        services.AddSingleton<ICaptureCalibrationProcessor, NullCaptureCalibrationProcessor>();
        services.AddSingleton<CaptureTelemetryMetricsRecorder>();
        services.AddSingleton<CaptureTelemetrySink>();
        services.AddSingleton<ICaptureTelemetrySink>(sp => sp.GetRequiredService<CaptureTelemetrySink>());
        services.AddSingleton<ICaptureTelemetryProvider>(sp => sp.GetRequiredService<CaptureTelemetrySink>());
        services.AddSingleton<ICaptureProcessingPipelineFactory, CaptureProcessingPipelineFactory>();
        services.AddHostedService<CameraAgentConfigurationInitializer>();
        services.AddHostedService<CameraCaptureService>();
        services.AddHostedService<RetentionBackgroundService>();

        return services;
    }
}
