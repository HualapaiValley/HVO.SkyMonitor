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
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
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
        var acceptanceFaultRoot = configuration["CameraAgent:AcceptanceFaultControlRoot"];
        if (!string.IsNullOrWhiteSpace(acceptanceFaultRoot))
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                    "StandaloneW6",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    configuration["CameraAgent:CentralIntegration:Mode"],
                    "Disabled",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Acceptance fault control is permitted only in StandaloneW6 with central integration disabled.");
            }
            services.AddSingleton(new CameraAgentAcceptanceFaultController(acceptanceFaultRoot));
            services.AddSingleton<IStorageCapacityProvider>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
            services.AddSingleton<IRawIngressFaultInjector>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
            services.AddSingleton<ICalibrationPublicationFaultInjector>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
            services.AddSingleton<ITransientCandidateFaultInjector>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
            services.AddSingleton<ITransientRuntimeFaultInjector>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
            services.AddSingleton<ICaptureProcessingFaultInjector>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
            services.AddSingleton<IAcceptanceRetentionControl>(provider =>
                provider.GetRequiredService<CameraAgentAcceptanceFaultController>());
        }
        else
        {
            services.AddSingleton<IStorageCapacityProvider, FileSystemStorageCapacityProvider>();
            services.AddSingleton<IRawIngressFaultInjector, NullRawIngressFaultInjector>();
            services.AddSingleton<ICalibrationPublicationFaultInjector>(
                NullCalibrationPublicationFaultInjector.Instance);
            services.AddSingleton<ITransientCandidateFaultInjector>(NullTransientCandidateFaultInjector.Instance);
            services.AddSingleton<ITransientRuntimeFaultInjector>(NullTransientRuntimeFaultInjector.Instance);
            services.AddSingleton<ICaptureProcessingFaultInjector>(NullCaptureProcessingFaultInjector.Instance);
        }
        services.AddSingleton<StoragePressureState>();
        services.AddSingleton<DeploymentContinuityReader>();
        services.AddSingleton<RawIngressState>();
        services.AddSingleton<RawIngressTelemetry>();
        services.AddSingleton<CaptureLaneState>();
        services.AddSingleton<CaptureLaneTelemetry>();
        services.AddSingleton<ICaptureLaneFaultInjector, NullCaptureLaneFaultInjector>();
        services.AddSingleton<CalibrationTelemetry>();
        services.AddSingleton<CaptureLanePolicy>();
        services.AddSingleton<RawCaptureIngress>();
        services.AddSingleton<IRawCaptureIngress>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IRawIngressRetentionHolds>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IProjectedSceneStageOwnerProvider>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<ICaptureLaneStore>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IOperationsQueueSnapshotRefresher>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<SqliteCaptureScheduleStore>();
        services.AddSingleton<SqliteCalibrationLibraryStore>();
        services.AddSingleton<CalibrationArtifactPublisher>();
        services.AddSingleton<VirtualCalibrationAcquisitionCoordinator>();
        services.AddSingleton<CalibrationLibraryOperationsCoordinator>();
        services.AddSingleton<CalibrationLibraryReconciler>();
        services.AddSingleton<SqliteTransientCandidateJournal>();
        services.AddSingleton<ITransientCandidateJournal>(provider =>
            provider.GetRequiredService<SqliteTransientCandidateJournal>());
        services.AddSingleton<ICameraAgentTransientOperatorProjection, SqliteCameraAgentTransientOperatorProjection>();
        services.AddSingleton<SqliteTransientRuntimeStore>();
        services.AddSingleton<ITransientRuntimeManagement>(provider =>
            provider.GetRequiredService<SqliteTransientRuntimeStore>());
        services.AddSingleton(provider => new TransientDetectorRuntime(
            provider.GetRequiredService<ICelestialCatalog>(),
            provider.GetService<IConstellationTopology>(),
            provider.GetService<IPlanetEphemeris>(),
            () => provider.GetService<IDeploymentLocationStore>()));
        services.AddSingleton<TransientWorkerWakeup>();
        services.AddSingleton<TransientCandidateDeliveryWakeup>();
        services.AddSingleton<TransientCandidateDeliveryState>();
        services.TryAddSingleton<ITransientCandidateTransport>(NullTransientCandidateTransport.Instance);
        services.AddSingleton<TransientWorkerState>();
        services.AddSingleton<TransientWorkerTelemetry>();
        services.AddSingleton<CameraModuleFactory>();
        services.AddSingleton<ICameraModuleFactory>(provider => provider.GetRequiredService<CameraModuleFactory>());
        services.AddSingleton<ICameraModuleConfigurationValidator>(provider =>
            provider.GetRequiredService<CameraModuleFactory>());
        services.AddSingleton<IProjectedSceneStore, ProjectedSceneStore>();
        services.AddSingleton<ProjectedSceneStageLifecycleCoordinator>();
        services.AddSingleton<ProjectedSceneStagingStore>();
        services.AddSingleton<IProjectedSceneStagingStore>(provider => provider.GetRequiredService<ProjectedSceneStagingStore>());
        services.AddSingleton<IProjectedSceneStagingReader>(provider => provider.GetRequiredService<ProjectedSceneStagingStore>());
        services.AddSingleton<IProjectedSceneStagingReconciler>(provider => provider.GetRequiredService<ProjectedSceneStagingStore>());
        services.AddSingleton<CaptureProjectedSceneStager>();
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
        services.TryAddSingleton<IEnvironmentalObservationTargetResolver, NullEnvironmentalObservationTargetResolver>();
        services.AddSingleton<EnvironmentalObservationDeliveryState>();
        services.AddSingleton<EnvironmentalObservationDeliveryTelemetry>();
        services.AddSingleton<EnvironmentalAcquisitionTelemetry>();
        services.AddSingleton<SqliteEnvironmentalObservationOutbox>(provider =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions>>().Value;
            return new SqliteEnvironmentalObservationOutbox(
                provider.GetRequiredService<TimeProvider>(),
                configured.RawIngressSqliteBusyTimeoutSeconds,
                configured.EnvironmentalDelivery.MaximumPendingCount,
                configured.EnvironmentalDelivery.MaximumPendingBytes,
                maximumLocalRecords: configured.EnvironmentalAcquisition.MaximumHistoryCount,
                maximumLocalBytes: configured.EnvironmentalAcquisition.MaximumHistoryBytes,
                localRetentionDays: configured.EnvironmentalAcquisition.RetentionDays,
                localRetentionBatchSize: configured.EnvironmentalAcquisition.RetentionBatchSize,
                acquisitionTelemetry: provider.GetRequiredService<EnvironmentalAcquisitionTelemetry>());
        });
        services.AddSingleton<IEnvironmentalObservationOutbox>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>());
        services.AddSingleton<ILocalEnvironmentalObservationStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>());
        services.AddSingleton<ILocalEnvironmentalAssociationStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>());
        services.AddSingleton<IEnvironmentalAcquisitionStateStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>());
        services.AddSingleton<IEnvironmentalOnDemandCommandStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>());
        services.AddSingleton<ILocalEnvironmentalRetentionStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationOutbox>());
        services.AddSingleton<IEnvironmentalObservationPublisher, EnvironmentalObservationPublisher>();
        services.AddSingleton(new EnvironmentalSourceRegistration(
            "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions)));
        services.AddSingleton<EnvironmentalSourceFactory>();
        services.AddSingleton<EnvironmentalAcquisitionCoordinator>();
        services.AddSingleton<EnvironmentalOnDemandAcquisitionService>();
        services.AddSingleton<EnvironmentalAssociationService>();
        services.AddSingleton<CameraAgentCloudEnvironment>();
        services.AddSingleton<PresentationMetadataFactsBuilder>();
        services.AddSingleton<EnvironmentalAssociationCaptureLaneHandler>();
        services.AddSingleton<EnvironmentalAcquisitionService>();
        services.AddSingleton<EnvironmentalCaptureTriggerBridge>();
        services.AddSingleton<CaptureControlTelemetry>();
        services.AddSingleton<CapturePipelineTraceStore>();
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
        services.AddHostedService<DerivedProductReconciliationService>();
        services.AddHostedService<ProjectedSceneStageReconciliationService>();
        services.AddSingleton<IProcessingRetentionHolds>(provider =>
            new CompositeProcessingRetentionHolds(
                provider.GetRequiredService<CaptureProcessingPersistence>(),
                provider.GetRequiredService<CameraAgentClearReferenceLoader>(),
                provider.GetRequiredService<SqliteCalibrationLibraryStore>(),
                provider.GetService<IAcceptanceRetentionControl>()));
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
        services.AddSingleton<ICaptureLaneHandler>(provider =>
            provider.GetRequiredService<EnvironmentalAssociationCaptureLaneHandler>());
        services.AddSingleton<CaptureDistributionService>();
        services.AddSingleton<ICaptureDistributor>(provider => provider.GetRequiredService<CaptureDistributionService>());
        services.AddTransient<ArtifactUploadClient>();
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Preview", typeof(PreviewCaptureProcessingStep), typeof(PreviewProcessingStepOptions), 50));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "CalibratedPreview", typeof(CalibratedPreviewCaptureProcessingStep),
            typeof(CalibratedPreviewProcessingStepOptions), 50, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "CombinedPreview", typeof(CombinedPreviewCaptureProcessingStep),
            typeof(CombinedPreviewProcessingStepOptions), 50, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "RollingCombination", typeof(RollingCombinationCaptureProcessingStep), typeof(RollingCombinationProcessingStepOptions), 25));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Annotation", typeof(AnnotationCaptureProcessingStep), typeof(AnnotationProcessingStepOptions), 75));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "JpegEncoding", typeof(JpegEncodingCaptureProcessingStep),
            typeof(JpegEncodingProcessingStepOptions), 80, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Calibration", typeof(CalibrationCaptureProcessingStep), typeof(CalibrationProcessingStepOptions), 20, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "VirtualSkyCloudObservation", typeof(VirtualSkyCloudObservationProcessingStep),
            typeof(VirtualSkyCloudObservationProcessingStepOptions), 10, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ProjectedScene", typeof(ProjectedSceneCaptureProcessingStep),
            typeof(ProjectedSceneCaptureProcessingStepOptions), 15, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "CloudAssessment", typeof(CloudAssessmentCaptureProcessingStep),
            typeof(CloudAssessmentProcessingStepOptions), 80, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ImageQuality", typeof(ImageQualityCaptureProcessingStep),
            typeof(ImageQualityProcessingStepOptions), 70, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Storage", typeof(NoOpFileStorageProcessingStep),
            typeof(NoOpFileStorageProcessingStepOptions), 100, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Telemetry", typeof(TelemetryCaptureProcessingStep),
            typeof(TelemetryProcessingStepOptions), 200, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "WeatherCloudOverlay", typeof(WeatherCloudOverlayCaptureProcessingStep),
            typeof(WeatherCloudOverlayProcessingStepOptions), 90, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ScenePresentationLayer", typeof(ScenePresentationLayerCaptureProcessingStep),
            typeof(ScenePresentationLayerProcessingStepOptions), 70, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "CloudPresentationLayer", typeof(CloudPresentationLayerCaptureProcessingStep),
            typeof(CloudPresentationLayerProcessingStepOptions), 71, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "EnvironmentPresentationLayer", typeof(EnvironmentPresentationLayerCaptureProcessingStep),
            typeof(EnvironmentPresentationLayerProcessingStepOptions), 72, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "OverlayManifest", typeof(OverlayManifestCaptureProcessingStep),
            typeof(OverlayManifestProcessingStepOptions), 80, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "PresentationMaterializer", typeof(PresentationMaterializerCaptureProcessingStep),
            typeof(PresentationMaterializerProcessingStepOptions), 81, AutoInclude: false));
        services.AddHostedService<CameraAgentConfigurationInitializer>();
        services.AddHostedService(provider => provider.GetRequiredService<EnvironmentalAcquisitionService>());
        services.AddHostedService<CalibrationLibraryValidationService>();
        services.AddHostedService<VirtualCalibrationAcquisitionRecoveryService>();
        services.AddHostedService(provider => provider.GetRequiredService<CaptureDistributionService>());
        services.AddHostedService<CameraCaptureService>();
        services.AddHostedService<RetentionBackgroundService>();
        services.AddHostedService<ArtifactOutboxDrainService>();
        services.AddHostedService<FleetHeartbeatService>();
        services.AddHostedService<EnvironmentalObservationDeliveryService>();
        services.AddSingleton<TransientWorkerService>();
        services.AddHostedService(provider => provider.GetRequiredService<TransientWorkerService>());
        services.AddSingleton<TransientCandidateDeliveryService>();
        services.AddHostedService(provider => provider.GetRequiredService<TransientCandidateDeliveryService>());

        return services;
    }
}
