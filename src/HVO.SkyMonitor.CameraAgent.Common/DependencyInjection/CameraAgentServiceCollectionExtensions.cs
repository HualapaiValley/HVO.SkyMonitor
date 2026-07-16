using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;

public static class CameraAgentServiceCollectionExtensions
{
    public static IServiceCollection AddCameraAgentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<CameraAgentHostOptions>()
            .Bind(configuration.GetSection("CameraAgent"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ICameraAgentConfigurationAccessor, CameraAgentConfigurationAccessor>();
        services.AddSingleton<ICameraAgentConfigurationLoader, FileCameraAgentConfigurationLoader>();
        services.AddSingleton<IFrameStorageService, FileSystemFrameStorageService>();
        services.AddSingleton<IStorageCapacityProvider, FileSystemStorageCapacityProvider>();
        services.AddSingleton<StoragePressureState>();
        services.AddSingleton<RawIngressState>();
        services.AddSingleton<RawIngressTelemetry>();
        services.AddSingleton<CaptureLaneState>();
        services.AddSingleton<CaptureLaneTelemetry>();
        services.AddSingleton<IRawIngressFaultInjector, NullRawIngressFaultInjector>();
        services.AddSingleton<ICaptureLaneFaultInjector, NullCaptureLaneFaultInjector>();
        services.AddSingleton<CaptureLanePolicy>();
        services.AddSingleton<RawCaptureIngress>();
        services.AddSingleton<IRawCaptureIngress>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IRawIngressRetentionHolds>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<ICaptureLaneStore>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<ICameraModuleFactory, CameraModuleFactory>();
        services.AddSingleton<IProjectedSceneStore, ProjectedSceneStore>();
        services.AddSingleton<IConstellationTopology>(StandardConstellationTopology.CreateD3Celestial());
        services.AddSingleton<IAnnotationSceneProvider>(provider => new AnnotationSceneProvider(
            () => provider.GetService<ICelestialCatalog>(),
            provider.GetRequiredService<IConstellationTopology>()));
        services.AddSingleton<IPlanetEphemeris, AstronomyEnginePlanetEphemeris>();
        services.AddSingleton<ILatestFrameAccessor, LatestFrameAccessor>();
        services.AddSingleton<ICaptureCalibrationProcessor, NullCaptureCalibrationProcessor>();
        services.AddSingleton<CaptureTelemetryMetricsRecorder>();
        services.AddSingleton<FleetRuntimeState>();
        services.AddSingleton<FleetHeartbeatState>();
        services.AddSingleton<FleetHeartbeatTelemetry>();
        services.AddSingleton<FleetStatusCollector>();
        services.AddSingleton<IFleetStatusOutbox, SqliteFleetStatusOutbox>();
        services.AddSingleton<CaptureControlTelemetry>();
        services.AddSingleton<CaptureTelemetrySink>();
        services.AddSingleton<ICaptureTelemetrySink>(sp => sp.GetRequiredService<CaptureTelemetrySink>());
        services.AddSingleton<ICaptureTelemetryProvider>(sp => sp.GetRequiredService<CaptureTelemetrySink>());
        services.AddSingleton<ICaptureProcessingPipelineFactory, CaptureProcessingPipelineFactory>();
        services.AddSingleton<CaptureProcessingState>();
        services.AddSingleton<CaptureProcessingTelemetry>();
        services.AddSingleton<SqliteCaptureProcessingStore>();
        services.AddSingleton<CaptureProcessingPersistence>();
        services.AddHostedService<CaptureProcessingStateRefreshService>();
        services.AddSingleton<IProcessingRetentionHolds>(provider =>
            provider.GetRequiredService<CaptureProcessingPersistence>());
        services.AddSingleton<IProcessingRecipeExecutor, ProcessingRecipeExecutor>();
        services.AddSingleton<CameraAgentRecipeExecutionAdapter>();
        services.AddSingleton<ArtifactOutboxState>();
        services.AddSingleton<ArtifactOutboxTelemetry>();
        services.AddSingleton<IArtifactOutbox, SqliteArtifactOutbox>();
        services.AddSingleton<StandardCaptureLaneHandler>();
        services.AddSingleton<UploadCaptureLaneHandler>();
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<StandardCaptureLaneHandler>());
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<UploadCaptureLaneHandler>());
        services.AddSingleton<CaptureDistributionService>();
        services.AddSingleton<ICaptureDistributor>(provider => provider.GetRequiredService<CaptureDistributionService>());
        services.AddTransient<ArtifactUploadClient>();
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Preview", typeof(PreviewCaptureProcessingStep), typeof(PreviewProcessingStepOptions), 50));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "RollingCombination", typeof(RollingCombinationCaptureProcessingStep), typeof(RollingCombinationProcessingStepOptions), 25));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Annotation", typeof(AnnotationCaptureProcessingStep), typeof(AnnotationProcessingStepOptions), 75));
        services.AddHostedService<CameraAgentConfigurationInitializer>();
        services.AddHostedService(provider => provider.GetRequiredService<CaptureDistributionService>());
        services.AddHostedService<CameraCaptureService>();
        services.AddHostedService<RetentionBackgroundService>();
        services.AddHostedService<ArtifactOutboxDrainService>();
        services.AddHostedService<FleetHeartbeatService>();

        return services;
    }
}
