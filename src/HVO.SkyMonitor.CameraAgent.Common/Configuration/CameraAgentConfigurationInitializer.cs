using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Logging;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.AgentCore;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Configuration;

public sealed class CameraAgentConfigurationInitializer(
    ICameraAgentConfigurationLoader loader,
    ICameraAgentConfigurationAccessor accessor,
    ICaptureProcessingPipelineFactory pipelineFactory,
    ILogger<CameraAgentConfigurationInitializer> logger,
    SqliteCaptureScheduleStore? scheduleStore = null,
    SqliteCalibrationLibraryStore? calibrationLibrary = null,
    CalibrationLibraryReconciler? calibrationReconciler = null,
    SqliteNamedRigProfileStore? namedRigProfiles = null,
    DeploymentLocation.IDeploymentLocationStore? deploymentLocationStore = null,
    IHostApplicationLifetime? hostLifetime = null) : IHostedService
{
    private readonly ICameraAgentConfigurationLoader _loader = loader;
    private readonly ICameraAgentConfigurationAccessor _accessor = accessor;
    private readonly ICaptureProcessingPipelineFactory _pipelineFactory = pipelineFactory;
    private readonly ILogger<CameraAgentConfigurationInitializer> _logger = logger;
    private readonly SqliteCaptureScheduleStore? _scheduleStore = scheduleStore;
    private readonly SqliteCalibrationLibraryStore? _calibrationLibrary = calibrationLibrary;
    private readonly CalibrationLibraryReconciler? _calibrationReconciler = calibrationReconciler;
    private readonly SqliteNamedRigProfileStore? _namedRigProfiles = namedRigProfiles;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed pending physical SDK check must not block startup of the independently verified active rig; no exception detail is exposed.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = await _loader.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (_accessor.IsConfigured) return;
        if (deploymentLocationStore is { } locations &&
            (locations.Active is null || !locations.RecentRetainedEvidenceReady)) return;
        // Schedule/calibration initialization can reconcile retained raw evidence.
        // Keep that work after Kestrel startup; the host-owned service calls this
        // again after ApplicationStarted, before publishing capture configuration.
        if (hostLifetime is not null && !hostLifetime.ApplicationStarted.IsCancellationRequested) return;
        // A real edit can arrive between the initial background result and this
        // capture initialization. Join a fresh, bounded-snapshot reconciliation
        // instead of leaving capture unpublished until the daily timer fires.
        if (deploymentLocationStore?.RequiresDeploymentLocationReconciliation == true)
            config = await _loader.LoadForRetainedIntegrityScrubAsync(cancellationToken).ConfigureAwait(false);
        if (_scheduleStore is not null)
        {
            var schedule = await _scheduleStore.InitializeAsync(config, cancellationToken).ConfigureAwait(false);
            if (_namedRigProfiles is not null)
            {
                var startupSelection = (await _namedRigProfiles.GetAsync(cancellationToken).ConfigureAwait(false)).Selection;
                _namedRigProfiles.SnapshotStartupPendingSelection(startupSelection);
                if (startupSelection.PendingRevisionId is null)
                    _ = await _namedRigProfiles.ImportActiveAsync(schedule.ActiveRevision, cancellationToken).ConfigureAwait(false);
            }
            config = schedule.ActiveRevision.Profile.ApplyTo(config);
            FileCameraAgentConfigurationLoader.ValidateConfig(config);
            if (!string.Equals(LocalCaptureProfileContract.ComputeEffectiveSha256(config),
                schedule.ActiveRevision.ProfileSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The active capture profile hash does not match the runtime configuration.");
            if (_namedRigProfiles is not null)
            {
                var catalog = await _namedRigProfiles.GetAsync(cancellationToken).ConfigureAwait(false);
                var active = catalog.Revisions.SingleOrDefault(revision => revision.RevisionId == catalog.Selection.ActiveRevisionId);
                if (active is null || !SqliteCaptureScheduleStore.SameEquipment(schedule.ActiveRevision.Profile,
                    active.Module, active.Rig))
                    throw new InvalidDataException("The active named rig differs from the active capture profile.");
            }
        }
        if (_calibrationLibrary is not null)
        {
            await _calibrationLibrary.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        if (_calibrationReconciler is not null)
        {
            _ = await _calibrationReconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        var graph = _pipelineFactory.CreateGraph(config);
        graph.DisposeSteps();
        _accessor.SetConfiguration(config);
        _logger.ConfigurationInitialized(config.ModuleType);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
