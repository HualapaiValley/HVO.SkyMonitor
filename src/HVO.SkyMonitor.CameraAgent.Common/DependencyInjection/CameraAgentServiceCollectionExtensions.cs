using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Modules;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SiteProfile;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.Video.FFmpeg;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Replay;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Extensions.Hosting;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Common.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;

public static class CameraAgentServiceCollectionExtensions
{
    public static IServiceCollection AddCameraAgentInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var siloSelected = configuration.GetSection("CameraAgent:Silo").Exists();
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<CameraAgentHostOptions>()
            .Bind(configuration.GetSection("CameraAgent"))
            .Configure(options => options.SpaceWeather.BindProducts(configuration))
            .ValidateDataAnnotations()
            .Validate(options => !siloSelected || options.ProcessingGraphs.ReplayProfile != ReplayExecutionProfile.LocalRunner,
                "The Silo storage layout requires in-process replay.")
            .ValidateOnStart();
        if (siloSelected)
        {
            services.AddSingleton(_ => SiloInstalledSettings.Read(configuration.GetSection("CameraAgent:Silo")));
            services.AddSingleton<SiloInstalledStorage>();
            services.AddSingleton<SiloArtifactStorage>();
            services.AddSingleton<ICameraAgentSiloVerification>(provider => provider.GetRequiredService<SiloInstalledStorage>());
            services.AddSingleton<SiloStorageWorker>();
            services.AddHostedService(provider => provider.GetRequiredService<SiloStorageWorker>());
            services.AddSingleton<SiloRetentionWorker>();
            services.AddHostedService(provider => provider.GetRequiredService<SiloRetentionWorker>());
            services.AddSingleton<ICameraAgentSiloRetention>(provider => provider.GetRequiredService<SiloRetentionWorker>());
            services.AddSingleton<ISiloObjectLeases>(provider => provider.GetRequiredService<SiloInstalledStorage>().Retention);
            services.AddSingleton<ISiloDiskBudget>(provider => provider.GetRequiredService<SiloInstalledStorage>().DiskBudget);
            services.AddHealthChecks().AddCheck<SiloStorageWorker>("silo-storage", tags: ["dependency"]);
        }

        services.AddSingleton<SiloArchiveWorker>();
        services.AddSingleton<ICameraAgentSiloArchive>(provider => provider.GetRequiredService<SiloArchiveWorker>());
        services.AddHostedService(provider => provider.GetRequiredService<SiloArchiveWorker>());

        services.AddSingleton<ICameraAgentConfigurationAccessor, CameraAgentConfigurationAccessor>();
        services.AddSingleton<DeploymentLocationTelemetry>();
        services.AddSingleton<IDeploymentLocationStore, ProtectedDeploymentLocationStore>();
        services.AddSingleton<ISiteProfileStore>(provider => new SettingsFileSiteProfileStore(
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<ILogger<SettingsFileSiteProfileStore>>(),
            provider.GetService<OperatorSettingsFile>()));
        services.AddSingleton<ICameraAgentConfigurationLoader, FileCameraAgentConfigurationLoader>();
        services.AddSingleton<IFrameStorageService, FileSystemFrameStorageService>();
        var acceptanceFaultRoot = configuration["CameraAgent:AcceptanceFaultControlRoot"];
        if (!string.IsNullOrWhiteSpace(acceptanceFaultRoot))
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                    "StandaloneW6",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Acceptance fault control is permitted only in StandaloneW6.");
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
        services.AddSingleton<IRawEvidenceOperations>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IProcessingLaneQuarantineOperations>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IRawCaptureIngress>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IRawIngressRetentionHolds>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IProjectedSceneStageOwnerProvider>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<ICaptureLaneStore>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<IOperationsQueueSnapshotRefresher>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<SqliteCaptureScheduleStore>();
        services.AddSingleton<SqliteNamedRigProfileStore>();
        services.AddSingleton(provider => new SqliteCalibrationLibraryStore(
            provider.GetRequiredService<IRawCaptureIngress>(), provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<CalibrationTelemetry>())
        { Silo = provider.GetService<SiloArtifactStorage>() });
        services.AddSingleton<SqliteCalibrationFamilyStore>();
        services.AddSingleton<CalibrationFamilyActivationService>();
        services.AddSingleton(static provider => new CalibrationArtifactPublisher(
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(), provider.GetRequiredService<ICalibrationPublicationFaultInjector>())
        { Silo = provider.GetService<SiloArtifactStorage>() });
        services.AddSingleton<VirtualCalibrationAcquisitionCoordinator>();
        services.AddSingleton<CalibrationLibraryOperationsCoordinator>();
        services.AddSingleton<CalibrationLibraryReconciler>();
        services.AddSingleton<CameraModuleOwnership>();
        services.AddSingleton<IManualFocusPreviewSource, CameraModuleManualFocusPreviewSource>();
        services.AddSingleton<IManualFocusSessionStore, ManualFocusSessionStore>();
        services.AddSingleton(provider => new ManualFocusSessionCoordinator(
            provider.GetRequiredService<IManualFocusPreviewSource>(),
            provider.GetRequiredService<TimeProvider>(),
            store: provider.GetRequiredService<IManualFocusSessionStore>(),
            logger: provider.GetRequiredService<ILogger<ManualFocusSessionCoordinator>>()));
        services.AddSingleton<SqliteTransientCandidateJournal>();
        services.AddSingleton<ITransientCandidateJournal>(provider =>
            provider.GetRequiredService<SqliteTransientCandidateJournal>());
        services.AddSingleton<ICameraAgentTransientOperatorProjection, SqliteCameraAgentTransientOperatorProjection>();
        services.AddSingleton<SqliteTransientRuntimeStore>();
        services.AddSingleton<ITransientRuntimeManagement, TransientRuntimeManagement>();
        services.AddSingleton(provider => new TransientDetectorRuntime(
            provider.GetRequiredService<ICelestialCatalog>(),
            provider.GetService<IConstellationTopology>(),
            provider.GetService<IPlanetEphemeris>()));
        services.AddSingleton(provider => new TransientDetectorSourcePreparation(
            () => provider.GetService<IDeploymentLocationStore>()));
        services.AddSingleton<TransientWorkerWakeup>();
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
        services.AddSingleton(provider =>
        {
            var catalog = provider.GetService<ICelestialCatalog>();
            return new CenteredExposureSceneProvider(
                provider.GetRequiredService<RawCaptureIngress>(),
                catalog is null ? null : provider.GetRequiredService<CaptureProjectedSceneStager>(),
                catalog,
                () => provider.GetService<IDeploymentLocationStore>(),
                provider.GetService<IConstellationTopology>(),
                provider.GetService<IPlanetEphemeris>());
        });
        services.AddSingleton<IVirtualSkyGraphSelection>(provider => new VirtualSkyGraphSelectionProvider(
            provider.GetRequiredService<ProcessingGraphOperationsCoordinator>(),
            (provider.GetRequiredService<ICelestialCatalog>() as IDeepSkyCatalogSource)?.DeepSky?.Semantics.SnapshotIdentity is not null));
        services.AddSingleton<IConstellationTopology>(StandardConstellationTopology.CreateD3Celestial());
        services.AddSingleton<IAnnotationSceneProvider>(provider => new AnnotationSceneProvider(
            () => provider.GetService<ICelestialCatalog>(),
            provider.GetRequiredService<IConstellationTopology>(),
            () => provider.GetService<IDeploymentLocationStore>()));
        services.AddSingleton<IPlanetEphemeris, AstronomyEnginePlanetEphemeris>();
        services.AddSingleton(CreateSceneObjectBound(configuration));
        services.AddSingleton<ICameraAgentSkyMapProjection>(provider =>
        {
            var sceneObjectBound = provider.GetRequiredService<ISceneObjectBound>();
            return new CameraAgentSkyMapProjection(
                provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
                provider.GetRequiredService<ICelestialCatalog>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<IConstellationTopology>(),
                provider.GetService<IDeploymentLocationStore>(),
                provider.GetService<ILatestFrameAccessor>(),
                provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>(),
                () => sceneObjectBound.Current,
                provider.GetService<IPlanetEphemeris>());
        });
        services.AddSingleton<ILatestFrameAccessor, LatestFrameAccessor>();
        services.AddSingleton<ICaptureCalibrationProcessor, NullCaptureCalibrationProcessor>();
        services.AddSingleton<CaptureTelemetryMetricsRecorder>();
        services.AddSingleton<CaptureRuntimeState>();
        services.AddSingleton<EnvironmentalAcquisitionTelemetry>();
        services.AddSingleton<SqliteEnvironmentalObservationStore>(provider =>
        {
            var configured = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions>>().Value;
            return new SqliteEnvironmentalObservationStore(
                provider.GetRequiredService<TimeProvider>(),
                configured.RawIngressSqliteBusyTimeoutSeconds,
                maximumLocalRecords: configured.EnvironmentalAcquisition.MaximumHistoryCount,
                maximumLocalBytes: configured.EnvironmentalAcquisition.MaximumHistoryBytes,
                localRetentionDays: configured.EnvironmentalAcquisition.RetentionDays,
                localRetentionBatchSize: configured.EnvironmentalAcquisition.RetentionBatchSize,
                acquisitionTelemetry: provider.GetRequiredService<EnvironmentalAcquisitionTelemetry>());
        });
        services.AddSingleton<ILocalEnvironmentalObservationStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationStore>());
        services.AddSingleton<ILocalEnvironmentalAssociationStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationStore>());
        services.AddSingleton<IEnvironmentalAcquisitionStateStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationStore>());
        services.AddSingleton<IEnvironmentalOnDemandCommandStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationStore>());
        services.AddSingleton<ILocalEnvironmentalRetentionStore>(provider =>
            provider.GetRequiredService<SqliteEnvironmentalObservationStore>());
        services.AddSingleton<IEnvironmentalObservationPublisher, EnvironmentalObservationPublisher>();
        services.AddSingleton(new EnvironmentalSourceRegistration(
            "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions)));
        services.AddSingleton(new EnvironmentalSourceRegistration("LocalWeatherStation", typeof(LocalWeatherStationSource), typeof(EnvironmentalDeviceOptions)));
        services.AddSingleton(new EnvironmentalSourceRegistration("InfraredSky", typeof(InfraredSkySource), typeof(EnvironmentalDeviceOptions)));
        services.AddSingleton(new EnvironmentalSourceRegistration("SkyQualityMeter", typeof(SkyQualityMeterSource), typeof(EnvironmentalDeviceOptions)));
        services.AddSingleton(new EnvironmentalSourceRegistration("OnlineWeather", typeof(OnlineWeatherSource), typeof(OnlineWeatherSourceOptions)));
        services.AddSingleton(provider => new WeatherProviderClient(
            new HttpClient(SwpcSource.CreatePrimaryHandler()), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IEnvironmentalAcquisitionStateStore>(), provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot));
        services.AddSingleton<EnvironmentalSourceFactory>();
        services.AddSingleton<EnvironmentalAcquisitionCoordinator>();
        services.AddSingleton<EnvironmentalOnDemandAcquisitionService>();
        services.AddSingleton<EnvironmentalAssociationService>();
        services.AddSingleton<HVO.SkyMonitor.CameraAgent.Common.Deployment.CameraAgentControlStore>();
        services.AddSingleton<ILocalAutomationPeriodicTaskAdapter, HVO.SkyMonitor.CameraAgent.Common.Deployment.CameraAgentRestartAutomationAdapter>();
        services.AddSingleton<LocalAutomationTelemetry>();
        services.AddSingleton<ILocalAutomationCaptureSequenceSource, DeploymentContinuityCaptureSequenceSource>();
        services.AddOptions<CustomScriptOptions>().Bind(configuration.GetSection("CameraAgent:CustomScripts"));
        services.AddSingleton<ILocalAutomationPeriodicTaskAdapter, CustomScriptTaskAdapter>();
        services.AddSingleton<EnvironmentalLocalAutomationTaskRegistry>();
        services.AddSingleton<ILocalAutomationTaskRegistry, LocalAutomationTaskRegistry>();
        services.AddSingleton<LocalAutomationWindowPlanner>();
        services.AddSingleton<SqliteLocalAutomationStore>();
        services.AddSingleton<ILocalAutomationStore>(provider =>
            provider.GetRequiredService<SqliteLocalAutomationStore>());
        services.AddSingleton<CameraAgentCloudEnvironment>();
        services.AddSingleton<PresentationMetadataFactsBuilder>();
        services.AddSingleton<EnvironmentalAssociationCaptureLaneHandler>();
        services.AddSingleton<EnvironmentalAcquisitionService>();
        services.AddSingleton<EnvironmentalCaptureTriggerBridge>();
        services.AddSingleton<CaptureControlTelemetry>();
        services.AddSingleton<CapturePipelineTraceStore>();
        services.AddSingleton<CaptureMaintenanceGate>();
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
        services.AddSingleton<CenteredExposureProductChanges>();
        services.AddSingleton<CenteredExposureProductReader>();
        services.AddSingleton<IExposureProductReader>(provider =>
            provider.GetRequiredService<CenteredExposureProductReader>());
        services.AddSingleton<ICenteredExposureRuntimeReader>(provider => provider.GetRequiredService<RawCaptureIngress>());
        services.AddSingleton<CenteredExposureInputCustody>();
        services.AddSingleton<CenteredWindowWakeup>();
        services.AddSingleton<ICenteredWindowJournal, CenteredWindowRuntimeJournal>();
        services.AddSingleton<ICenteredWindowReader, CenteredWindowReader>();
        services.AddSingleton<ICenteredWindowRegistrationSource, CenteredWindowRetainedRegistrationSource>();
        services.AddSingleton<CenteredWindowCoordinator>();
        services.AddSingleton<CenteredWindowRegistrationWorker>();
        services.AddSingleton<ProcessingReplayWakeup>();
        services.AddSingleton(provider => new ProcessingGraphOperationsCoordinator(
            provider.GetRequiredService<ICaptureProcessingPipelineFactory>(),
            provider.GetRequiredService<SqliteCaptureProcessingStore>(),
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ProcessingReplayWakeup>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            // Defer lookup: schedule storage/admission depend on raw ingress, which depends on this coordinator.
            // Hostless graph tools have no initialized accessor; do not construct capture admission for them.
            () => provider.GetRequiredService<ICameraAgentConfigurationAccessor>().IsConfigured
                ? provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>().Snapshot?.Configuration
                : null,
            provider.GetServices<ICenteredExposureGraphExtension>(),
            () => provider.GetService<IAstrometricCatalogSource>()));
        services.AddSingleton<IProcessingGraphOperations>(provider =>
            provider.GetRequiredService<ProcessingGraphOperationsCoordinator>());
        services.AddSingleton<IObservingDayCalendarProvider, DeploymentObservingDayCalendarProvider>();
        if (!siloSelected)
        {
            services.AddSingleton<LocalArchiveReader>(static provider => new LocalArchiveReader(
                provider.GetRequiredService<IFrameStorageService>(),
                provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
                provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
                // Resolve the schedule at read time, after service construction.
                () => provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>().Snapshot?.Configuration));
        }
        services.AddSingleton<SqliteCameraAgentGallery>();
        services.AddSingleton<ICameraAgentGallery>(provider => provider.GetRequiredService<SqliteCameraAgentGallery>());
        services.AddSingleton<ICameraAgentArchive>(provider => provider.GetRequiredService<SqliteCameraAgentGallery>());
        services.AddSingleton<ICameraAgentCapturePresentationProjector, CameraAgentCapturePresentationProjector>();
        services.AddSingleton<ICameraAgentPresentationRuntime, CameraAgentPresentationRuntime>();
        services.AddSingleton<ICameraAgentStructuredLayerAvailability, CameraAgentStructuredLayerAvailability>();
        services.AddSingleton<ICameraAgentProcessingCompletion, CameraAgentProcessingCompletionReader>();
        services.AddSingleton<ICameraAgentCurrentImagePresentationService, CameraAgentCurrentImagePresentationService>();
        services.AddSingleton<ICameraAgentPreviewEncoder, CameraAgentPreviewEncoder>();
        services.AddSingleton<CameraAgentArtifactService>();
        services.AddSingleton<ICameraAgentArtifactService>(provider => provider.GetRequiredService<CameraAgentArtifactService>());
        services.AddSingleton<ICameraAgentScientificExports, CameraAgentScientificExports>();
        services.AddSingleton<ICameraAgentLayeredPresentationService, CameraAgentLayeredPresentationService>();
        services.TryAddSingleton<ICenteredMeasuredDeepSky>(provider => new CenteredMeasuredDeepSky(
            provider.GetRequiredService<ICenteredViewReadIndex>(), provider.GetRequiredService<ICenteredViewPublisher>(),
            provider.GetRequiredService<ICenteredWindowRegistrationSource>(), provider.GetRequiredService<CaptureProcessingPersistence>(),
            provider.GetRequiredService<ICelestialCatalog>(), provider.GetRequiredService<RawCaptureIngress>()));
        services.AddSingleton<SqliteCenteredViews>();
        services.AddSingleton<ICenteredViewReadIndex>(provider => provider.GetRequiredService<SqliteCenteredViews>());
        services.AddSingleton<ICenteredViewPublisher>(provider => provider.GetRequiredService<SqliteCenteredViews>());
        services.AddSingleton<ICenteredViewProducer, CenteredViewProducer>();
        services.AddSingleton<CenteredMeasuredDeepSkyScheduler>();
        services.AddHostedService(provider => provider.GetRequiredService<CenteredMeasuredDeepSkyScheduler>());
        services.AddSingleton<ICenteredViewLayerProducer, CenteredViewLayerProducer>();
        services.AddSingleton<ICenteredViewPresentationReader, CenteredViewPresentationReader>();
        services.AddSingleton<ICenteredViewLookup, CenteredViewLookup>();
        services.AddSingleton<ICenteredViewLayerProgressReader, CenteredViewLayerProgressReader>();
        services.AddSingleton<CenteredViewSourceLayers>();
        services.AddSingleton<CenteredViewPublicationWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<CenteredViewPublicationWorker>());
        services.AddSingleton<CenteredViewWindowPublicationWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<CenteredViewWindowPublicationWorker>());
        services.AddSingleton<CenteredViewTrailPublication>();
        services.AddHostedService(provider => provider.GetRequiredService<CenteredViewTrailPublication>());
        services.AddSingleton<ICameraAgentRetainedAstrometryService>(provider => new CameraAgentRetainedAstrometryService(
            provider.GetRequiredService<SqliteCaptureProcessingStore>(),
            provider.GetRequiredService<ICameraAgentArtifactService>(),
            provider.GetRequiredService<CameraAgentArtifactService>().BeginRetainedHistoryRead));
        services.AddSingleton<CaptureProcessingPersistence>();
        services.AddExposureMeasurement();
        services.AddSingleton<SqliteNightlyProductStore>();
        services.AddSingleton<INightlyProductCatalog>(static provider =>
            provider.GetRequiredService<SqliteNightlyProductStore>());
        services.AddSingleton<INightlyProductSourceReader, JournalNightlyProductSourceReader>();
        services.AddSingleton(static provider => new NightlyProductGenerator(
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            provider.GetRequiredService<INightlyProductSourceReader>(),
            provider.GetRequiredService<SqliteNightlyProductStore>(),
            provider.GetRequiredService<IPlanetEphemeris>(),
            () => provider.GetService<IDeploymentLocationStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<NightlyProductGenerator>>()));
        services.AddSingleton<ILocalAutomationWindowTaskAdapter, NightlyProductAutomationTaskRegistry>();
        services.AddSingleton<SqliteTimeLapseStore>();
        services.AddSingleton<ICameraAgentTimeLapseCatalog>(static provider => provider.GetRequiredService<SqliteTimeLapseStore>());
        services.AddSingleton<ICameraAgentTimeLapseCommands>(static provider => provider.GetRequiredService<SqliteTimeLapseStore>());
        services.AddSingleton(static provider => new FFmpegTimeLapseEncoder(
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.TimeLapses.Encoder with
            {
                ScratchDirectory = Path.Combine(Path.GetFullPath(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot),
                    ".time-lapses", "encoder")
            }));
        services.AddSingleton(static provider => new TimeLapseSourceReader(
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(), provider.GetRequiredService<RawCaptureIngress>(),
            provider.GetRequiredService<SqliteCaptureProcessingStore>(), provider.GetRequiredService<SqliteTimeLapseStore>(),
            () => provider.GetService<IDeploymentLocationStore>(), provider.GetRequiredService<IPlanetEphemeris>(),
            provider.GetService<SiloArtifactStorage>()));
        services.AddSingleton<TimeLapseGenerator>();
        services.AddSingleton<ILocalAutomationWindowTaskAdapter, TimeLapseAutomationAdapter>();
        services.AddHostedService<TimeLapseWorker>();
        services.AddSingleton<ICameraAgentPresentationMaterializer, CameraAgentPresentationMaterializer>();
        services.AddSingleton<CameraAgentClearReferenceLoader>();
        services.AddSingleton<SyntheticCalibrationReferenceStore>();
        services.AddSingleton<CalibrationLibraryProcessingInputLoader>();
        services.AddSingleton<CameraAgentConfigurationInitializer>();
        services.AddHostedService(provider => provider.GetRequiredService<CameraAgentConfigurationInitializer>());
        services.AddHostedService(provider => new RetainedIntegrityScrubService(
            provider.GetRequiredService<ICameraAgentConfigurationLoader>(), provider.GetRequiredService<ILogger<RetainedIntegrityScrubService>>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<CameraAgentConfigurationInitializer>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(), provider.GetRequiredService<IDeploymentLocationStore>(),
            provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetRequiredService<CaptureLaneState>(),
            provider.GetService<ICameraAgentSiloVerification>()));
        services.AddSingleton<CaptureAdmissionInitializationService>();
        services.AddHostedService<AfterApplicationStartedService<CaptureAdmissionInitializationService>>();
        services.AddHostedService<CaptureProcessingStateRefreshService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CenteredWindowCoordinator>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CenteredWindowRegistrationWorker>());
        if (!siloSelected)
        {
            services.AddHostedService<DerivedProductReconciliationService>();
        }
        services.AddHostedService<ProjectedSceneStageReconciliationService>();
        services.AddSingleton<IProcessingRetentionHolds>(provider =>
            new CompositeProcessingRetentionHolds(
                provider.GetRequiredService<CaptureProcessingPersistence>(),
                provider.GetRequiredService<CameraAgentClearReferenceLoader>(),
                provider.GetRequiredService<SqliteCalibrationLibraryStore>(),
                provider.GetService<IAcceptanceRetentionControl>(),
                provider.GetRequiredService<SqliteTimeLapseStore>(),
                provider.GetRequiredService<SqliteCalibrationFamilyStore>(),
                provider.GetRequiredService<CaptureSatelliteWorkStore>()));
        services.AddSingleton<IProcessingRecipeExecutor, ProcessingRecipeExecutor>();
        services.AddSingleton<ActiveCalibrationFamilySelector>();
        services.AddSingleton<NormalizationPlanFreezer>();
        services.AddSingleton<GainNormalizationPreparer>();
        var replayProfile = configuration.GetValue(
            "CameraAgent:ProcessingGraphs:ReplayProfile",
            ReplayExecutionProfile.InProcess);
        services.AddSingleton(provider =>
        {
            var graphOptions = provider.GetRequiredService<IOptions<CameraAgentHostOptions>>()
                .Value.ProcessingGraphs;
            return new LocalReplayRunnerClient(
                graphOptions.LocalRunner.ToTransportOptions(graphOptions.ReplayMaximumConcurrency));
        });
        services.AddSingleton(provider =>
        {
            var runner = replayProfile == ReplayExecutionProfile.LocalRunner
                ? provider.GetRequiredService<LocalReplayRunnerClient>()
                : null;
            return new CameraAgentRecipeExecutionAdapter(
                provider.GetRequiredService<IProcessingRecipeExecutor>(),
                replayProfile,
                runner,
                // Resolved on first use by a mixed-gain node: composing any graph must not require the durable stores.
                provider.GetRequiredService<GainNormalizationPreparer>);
        });
        services.AddSingleton<StandardCaptureLaneHandler>();
        services.AddSingleton<TransientCaptureLaneHandler>();
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<StandardCaptureLaneHandler>());
        services.AddSingleton<ICaptureLaneHandler>(provider => provider.GetRequiredService<TransientCaptureLaneHandler>());
        services.AddSingleton<ICaptureLaneHandler>(provider =>
            provider.GetRequiredService<EnvironmentalAssociationCaptureLaneHandler>());
        services.AddSingleton<CaptureDistributionService>();
        services.AddSingleton<ICaptureDistributor>(provider => provider.GetRequiredService<CaptureDistributionService>());
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
            AstrometryPredictionCaptureProcessingStep.Alias, typeof(AstrometryPredictionCaptureProcessingStep),
            typeof(AstrometryPredictionCaptureProcessingStepOptions), 15, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            MeasuredStellarAssociationCaptureProcessingStep.Alias, typeof(MeasuredStellarAssociationCaptureProcessingStep),
            typeof(MeasuredStellarAssociationProcessingStepOptions), 16, AutoInclude: false));
        services.AddSingleton<CaptureSatelliteWorkStore>();
        services.AddSingleton<CaptureAstrometryExecutionGate>();
        services.AddSingleton(new CaptureProcessingStepRegistration(
            MeasuredAstrometryCaptureProcessingStep.Alias, typeof(MeasuredAstrometryCaptureProcessingStep),
            typeof(MeasuredAstrometryCaptureOptions), 19, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            CaptureSatelliteTracksProcessingStep.Alias, typeof(CaptureSatelliteTracksProcessingStep),
            typeof(CaptureSatelliteTracksOptions), 17, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            CaptureSatelliteLayerProcessingStep.Alias, typeof(CaptureSatelliteLayerProcessingStep),
            typeof(CaptureSatelliteLayerOptions), 18, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "CloudAssessment", typeof(CloudAssessmentCaptureProcessingStep),
            typeof(CloudAssessmentProcessingStepOptions), 80, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ImageQuality", typeof(ImageQualityCaptureProcessingStep),
            typeof(ImageQualityProcessingStepOptions), 70, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Storage", typeof(FileStorageCaptureProcessingStep),
            typeof(FileStorageCaptureProcessingStepOptions), 100, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "Telemetry", typeof(TelemetryCaptureProcessingStep),
            typeof(TelemetryProcessingStepOptions), 200, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "WeatherCloudOverlay", typeof(WeatherCloudOverlayCaptureProcessingStep),
            typeof(WeatherCloudOverlayProcessingStepOptions), 90, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            SceneStylePresentation.StepAlias, typeof(SceneStylePresentationCaptureProcessingStep),
            typeof(SceneStylePresentationStepOptions), 70, AutoInclude: false));
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
            FrameFactsPresentation.StepAlias, typeof(FrameFactsPresentationCaptureProcessingStep),
            typeof(FrameFactsPresentationProcessingStepOptions), 72, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "OverlayManifest", typeof(OverlayManifestCaptureProcessingStep),
            typeof(OverlayManifestProcessingStepOptions), 80, AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "PresentationMaterializer", typeof(PresentationMaterializerCaptureProcessingStep),
            typeof(PresentationMaterializerProcessingStepOptions), 81, AutoInclude: false));
        // Registers the orbital store initializer, so it runs before the automation runner's first sweep.
        services.AddCameraAgentOrbitalElements();
        services.AddCameraAgentSpaceWeather();
        services.AddSingleton<LocalAutomationOffSweepWorker>();
        services.AddSingleton<LocalAutomationScriptCommands>();
        services.AddHostedService(static provider => provider.GetRequiredService<LocalAutomationOffSweepWorker>());
        services.AddHostedService<LocalAutomationRunnerService>();
        services.AddHostedService<EnvironmentalObservationSchemaInitializationService>();
        services.AddHostedService(provider => provider.GetRequiredService<EnvironmentalAcquisitionService>());
        services.AddHostedService<CalibrationLibraryValidationService>();
        services.AddSingleton<VirtualCalibrationAcquisitionRecoveryService>();
        services.AddHostedService<AfterApplicationStartedService<VirtualCalibrationAcquisitionRecoveryService>>();
        services.AddHostedService<AfterApplicationStartedService<CaptureDistributionService>>();
        services.AddSingleton<ProcessingReplayWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ProcessingReplayWorker>());
        services.AddHostedService<CameraCaptureService>();
        // Registered after the capture owner so host shutdown stops a focus session before the module is revoked.
        services.AddHostedService(provider => provider.GetRequiredService<ManualFocusSessionCoordinator>());
        services.AddHostedService<RetentionBackgroundService>();
        services.AddSingleton<TransientWorkerService>();
        services.AddHostedService(provider => provider.GetRequiredService<TransientWorkerService>());
        services.TryAddSingleton<ISntpClient, SntpClient>();
        services.TryAddSingleton<IKernelClockReader, LinuxKernelClockReader>();
        services.AddSingleton(provider => new ClockSyncMonitor(
            () => TimeSyncSettings.Read(configuration),
            provider.GetRequiredService<ISntpClient>(),
            provider.GetRequiredService<IKernelClockReader>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ClockSyncMonitor>>()));
        services.AddSingleton<IClockSyncMonitor>(provider => provider.GetRequiredService<ClockSyncMonitor>());
        services.AddHostedService(provider => provider.GetRequiredService<ClockSyncMonitor>());

        services.TryAddSingleton(new OperatorNotificationNetworkPolicy(isDevelopment: false));
        services.AddSingleton<OperatorNotificationTelemetry>();
        services.TryAddSingleton<IOperatorNotificationConfigProvider, ConfigurationOperatorNotificationConfigProvider>();
        services.TryAddSingleton<ISmtpNotificationTransport, SmtpNotificationTransport>();
        services.TryAddSingleton<IWebhookNotificationTransport, WebhookNotificationTransport>();
        services.AddSingleton<OperatorNotificationDispatcher>();
        services.AddSingleton<IOperatorNotificationPublisher>(provider => provider.GetRequiredService<OperatorNotificationDispatcher>());
        services.AddSingleton<IOperatorNotificationService>(provider => provider.GetRequiredService<OperatorNotificationDispatcher>());
        services.AddHostedService(provider => provider.GetRequiredService<OperatorNotificationDispatcher>());
        services.AddSingleton<OperatorNotificationMonitorService>();
        services.AddHostedService(provider => provider.GetRequiredService<OperatorNotificationMonitorService>());

        return services;
    }

    internal static ISceneObjectBound CreateSceneObjectBound(IConfiguration configuration)
        => new ConfigurationSceneObjectBound(
            () => ReadObjectBound(configuration, SkyMapOptions.MaximumObjectsKey, CameraAgentSkyMapProjection.MaximumConfigurableObjects),
            () => ReadObjectBound(configuration, VirtualSkyRendererOptions.CandidateCapKey, VirtualSkyRendererOptions.MaximumCandidateCap));

    /// <summary>
    /// Reads a live override. A malformed value introduced after startup falls back until it is corrected.
    /// </summary>
    private static int? ReadObjectBound(IConfiguration configuration, string key, int maximum)
        => int.TryParse(
                configuration[key],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) && value >= 1 && value <= maximum
            ? value
            : null;
}
