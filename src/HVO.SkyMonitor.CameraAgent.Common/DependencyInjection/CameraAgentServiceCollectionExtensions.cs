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
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
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
        services.AddSingleton<DeploymentLocationTelemetry>();
        services.AddSingleton<IDeploymentLocationStore, ProtectedDeploymentLocationStore>();
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
        services.AddSingleton<ITransientCandidateFaultInjector>(NullTransientCandidateFaultInjector.Instance);
        services.AddSingleton<ITransientRuntimeFaultInjector>(NullTransientRuntimeFaultInjector.Instance);
        services.AddSingleton<CaptureLanePolicy>();
        services.AddSingleton<RawCaptureIngress>();
        services.AddSingleton<IRawCaptureIngress>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IRawIngressRetentionHolds>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<ICaptureLaneStore>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<SqliteCaptureScheduleStore>();
        services.AddSingleton<SqliteCalibrationLibraryStore>();
        services.AddSingleton<ICalibrationPublicationFaultInjector>(
            NullCalibrationPublicationFaultInjector.Instance);
        services.AddSingleton<CalibrationArtifactPublisher>();
        services.AddSingleton<VirtualCalibrationAcquisitionCoordinator>();
        services.AddSingleton<CalibrationLibraryOperationsCoordinator>();
        services.AddSingleton<CalibrationLibraryReconciler>();
        services.AddSingleton<SqliteTransientCandidateJournal>();
        services.AddSingleton<ITransientCandidateJournal>(provider =>
            provider.GetRequiredService<SqliteTransientCandidateJournal>());
        services.AddSingleton<SqliteTransientRuntimeStore>();
        services.AddSingleton<ITransientRuntimeManagement>(provider =>
            provider.GetRequiredService<SqliteTransientRuntimeStore>());
        services.AddSingleton(provider => new TransientDetectorRuntime(
            provider.GetRequiredService<ICelestialCatalog>(),
            provider.GetService<IConstellationTopology>(),
            provider.GetService<IPlanetEphemeris>(),
            () => provider.GetService<IDeploymentLocationStore>()));
        services.AddSingleton<TransientWorkerWakeup>();
        services.AddSingleton<TransientWorkerState>();
        services.AddSingleton<TransientWorkerTelemetry>();
        services.AddSingleton<CameraModuleFactory>();
        services.AddSingleton<ICameraModuleFactory>(provider => provider.GetRequiredService<CameraModuleFactory>());
        services.AddSingleton<ICameraModuleConfigurationValidator>(provider =>
            provider.GetRequiredService<CameraModuleFactory>());
        services.AddSingleton<IProjectedSceneStore, ProjectedSceneStore>();
        services.AddSingleton<IConstellationTopology>(StandardConstellationTopology.CreateD3Celestial());
        services.AddSingleton<IAnnotationSceneProvider>(provider => new AnnotationSceneProvider(
            () => provider.GetService<ICelestialCatalog>(),
            provider.GetRequiredService<IConstellationTopology>(),
            () => provider.GetService<IDeploymentLocationStore>()));
        services.AddSingleton<IPlanetEphemeris, AstronomyEnginePlanetEphemeris>();
        services.AddSingleton<ILatestFrameAccessor, LatestFrameAccessor>();
        services.AddSingleton<ICaptureCalibrationProcessor, NullCaptureCalibrationProcessor>();
        services.AddSingleton<CaptureTelemetryMetricsRecorder>();
        services.AddSingleton<FleetRuntimeState>();
        services.AddSingleton<FleetHeartbeatState>();
        services.AddSingleton<FleetHeartbeatTelemetry>();
        services.AddSingleton<FleetStatusCollector>();
        services.AddSingleton<IFleetStatusOutbox, SqliteFleetStatusOutbox>();
        services.AddSingleton<EnvironmentalObservationDeliveryWakeup>();
        services.AddSingleton<EnvironmentalObservationDeliveryState>();
        services.AddSingleton<EnvironmentalObservationDeliveryTelemetry>();
        services.AddSingleton<IEnvironmentalObservationOutbox>(provider =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions>>().Value;
            return new SqliteEnvironmentalObservationOutbox(
                provider.GetRequiredService<TimeProvider>(),
                configured.RawIngressSqliteBusyTimeoutSeconds,
                configured.EnvironmentalDelivery.MaximumPendingCount,
                configured.EnvironmentalDelivery.MaximumPendingBytes);
        });
        services.AddSingleton<IEnvironmentalObservationPublisher, EnvironmentalObservationPublisher>();
        services.AddSingleton<CaptureControlTelemetry>();
        services.AddSingleton<CaptureAdmissionCoordinator>();
        services.AddSingleton<CaptureScheduleRuntimeCoordinator>();
        services.AddSingleton<CameraAgentStorageResolver>();
        services.AddSingleton<ICameraAgentStorageResolver>(static provider =>
            provider.GetRequiredService<CameraAgentStorageResolver>());
        services.AddSingleton<CameraAgentOperationsSummaryProvider>();
        services.AddSingleton<CaptureTelemetrySink>();
        services.AddSingleton<ICaptureTelemetrySink>(sp => sp.GetRequiredService<CaptureTelemetrySink>());
        services.AddSingleton<ICaptureTelemetryProvider>(sp => sp.GetRequiredService<CaptureTelemetrySink>());
        services.AddSingleton<ICaptureProcessingPipelineFactory, CaptureProcessingPipelineFactory>();
        services.AddSingleton<CaptureProcessingState>();
        services.AddSingleton<CaptureProcessingTelemetry>();
        services.AddSingleton<SqliteCaptureProcessingStore>();
        services.AddSingleton<ICameraAgentGallery, SqliteCameraAgentGallery>();
        services.AddSingleton<ICameraAgentPreviewEncoder, CameraAgentPreviewEncoder>();
        services.AddSingleton<ICameraAgentArtifactService, CameraAgentArtifactService>();
        services.AddSingleton<CaptureProcessingPersistence>();
        services.AddSingleton<CameraAgentClearReferenceLoader>();
        services.AddSingleton<SyntheticCalibrationReferenceStore>();
        services.AddSingleton<CalibrationLibraryProcessingInputLoader>();
        services.AddHostedService<CaptureProcessingStateRefreshService>();
        services.AddSingleton<IProcessingRetentionHolds>(provider =>
            new CompositeProcessingRetentionHolds(
                provider.GetRequiredService<CaptureProcessingPersistence>(),
                provider.GetRequiredService<CameraAgentClearReferenceLoader>(),
                provider.GetRequiredService<SqliteCalibrationLibraryStore>()));
        services.AddSingleton<IProcessingRecipeExecutor, ProcessingRecipeExecutor>();
        services.AddSingleton<CameraAgentRecipeExecutionAdapter>();
        services.AddSingleton<ArtifactOutboxState>();
        services.AddSingleton<ArtifactOutboxTelemetry>();
        services.AddSingleton<IArtifactOutbox, SqliteArtifactOutbox>();
        services.AddSingleton<StandardCaptureLaneHandler>();
        services.AddSingleton<UploadCaptureLaneHandler>();
        services.AddSingleton<TransientCaptureLaneHandler>();
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<StandardCaptureLaneHandler>());
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<UploadCaptureLaneHandler>());
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<TransientCaptureLaneHandler>());
        services.AddSingleton<CaptureDistributionService>();
        services.AddSingleton<ICaptureDistributor>(provider => provider.GetRequiredService<CaptureDistributionService>());
        services.AddTransient<ArtifactUploadClient>();
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Preview", typeof(PreviewCaptureProcessingStep), typeof(PreviewProcessingStepOptions), 50));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "RollingCombination", typeof(RollingCombinationCaptureProcessingStep), typeof(RollingCombinationProcessingStepOptions), 25));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Annotation", typeof(AnnotationCaptureProcessingStep), typeof(AnnotationProcessingStepOptions), 75));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Calibration", typeof(CalibrationCaptureProcessingStep), typeof(CalibrationProcessingStepOptions), 20, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "VirtualSkyCloudObservation", typeof(VirtualSkyCloudObservationProcessingStep),
            typeof(VirtualSkyCloudObservationProcessingStepOptions), 10, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "CloudAssessment", typeof(CloudAssessmentCaptureProcessingStep),
            typeof(CloudAssessmentProcessingStepOptions), 80, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "WeatherCloudOverlay", typeof(WeatherCloudOverlayCaptureProcessingStep),
            typeof(WeatherCloudOverlayProcessingStepOptions), 90, AutoInclude: false));
        services.AddHostedService<CameraAgentConfigurationInitializer>();
        services.AddHostedService<VirtualCalibrationAcquisitionRecoveryService>();
        services.AddHostedService(provider => provider.GetRequiredService<CaptureDistributionService>());
        services.AddHostedService<CameraCaptureService>();
        services.AddHostedService<RetentionBackgroundService>();
        services.AddHostedService<ArtifactOutboxDrainService>();
        services.AddHostedService<FleetHeartbeatService>();
        services.AddHostedService<EnvironmentalObservationDeliveryService>();
        services.AddSingleton<TransientWorkerService>();
        services.AddHostedService(provider => provider.GetRequiredService<TransientWorkerService>());

        return services;
    }
}
