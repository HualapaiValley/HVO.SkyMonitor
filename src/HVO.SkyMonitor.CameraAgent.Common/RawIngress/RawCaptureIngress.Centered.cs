using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed record CenteredExposureReservation(
    CenteredPendingAttempt Pending, ProcessingGraphRevisionSnapshot Revision);

internal sealed record RecoveredCenteredAdmission(
    ProcessingLiveExecutionSeed Execution, byte[] ContextJson, string ContextSha256, CaptureSetpoint RequestedSetpoint);

internal sealed partial class RawCaptureIngress : ICenteredExposureRuntimeReader
{
    internal CenteredRecentExposureIndex RecentCenteredExposures { get; } = new();
    internal bool CenteredPublicationReady => Volatile.Read(ref _initialized);

    internal Task<CenteredOwnerTransition?> FenceCenteredOwnerAsync(
        string agentId, string? cameraId, string? profileIdentity, CancellationToken cancellationToken)
        => _journal.FenceCenteredOwnerAsync(agentId, cameraId, profileIdentity, cancellationToken);

    internal Task FenceCenteredDeviceAsync(
        CenteredOwnerTransition transition, string deviceIdentity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentity) || deviceIdentity.Length > 256)
            throw new ArgumentException("The acquisition attachment identity is invalid.", nameof(deviceIdentity));
        return _journal.FenceCenteredDeviceAsync(transition, deviceIdentity, cancellationToken);
    }

    internal Task ActivateCenteredOwnerAsync(CenteredOwnerTransition transition, CancellationToken cancellationToken)
        => _journal.ActivateCenteredOwnerAsync(transition, cancellationToken);

    public ValueTask<CenteredExposureRuntimeSnapshot> ReadCurrentAsync(
        CameraKey camera, CancellationToken cancellationToken) => ReadCenteredRuntimeAsync(camera, null, cancellationToken);

    public ValueTask<CenteredExposureRuntimeSnapshot> ReadRunAsync(
        CameraKey camera, Guid runId, CancellationToken cancellationToken) => ReadCenteredRuntimeAsync(camera, runId, cancellationToken);

    private async ValueTask<CenteredExposureRuntimeSnapshot> ReadCenteredRuntimeAsync(
        CameraKey camera, Guid? runId, CancellationToken cancellationToken)
    {
        var before = RecentCenteredExposures.ReadActive(camera);
        var observedUtc = _timeProvider.GetUtcNow();
        if (!Volatile.Read(ref _initialized))
            return new(camera, runId, null, null, null, null, CenteredRecentExposureIndex.CapacityPerCamera,
                null, observedUtc, CenteredRuntimeEvidenceBasis.Unavailable, "runtime.journal-not-ready");

        var journal = await _journal.ReadCenteredRuntimeAsync(camera, runId, observedUtc, cancellationToken).ConfigureAwait(false);
        if (journal is null)
            return new(camera, runId, null, null, null, null, CenteredRecentExposureIndex.CapacityPerCamera,
                null, observedUtc, CenteredRuntimeEvidenceBasis.Unavailable, "runtime.no-centered-run");

        var after = RecentCenteredExposures.ReadActive(camera);
        var current = journal.IsOpen && before == after && after.RunId == journal.Run.RunId &&
            after.ObservedHead == journal.Head?.Exposure;
        return new(camera, journal.Run.RunId, journal.Run.GraphRevisionId,
            journal.Run.Configuration.CenteredExposure?.ExecutionVersion, journal.Head,
            current ? after.Occupancy : null, CenteredRecentExposureIndex.CapacityPerCamera, journal.Holds,
            observedUtc, current ? CenteredRuntimeEvidenceBasis.ActiveAcquisition : CenteredRuntimeEvidenceBasis.RetainedJournal,
            current ? null : "runtime.live-buffer-unobserved");
    }

    internal async Task WaitForCenteredWindowEvidenceAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await InitializeCoreAsync(allowEvidenceUnavailable: true, cancellationToken).ConfigureAwait(false))
                    return;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Ingress already reports the failure. Keep the host available
                // for Operations and wait for the ordinary recovery path.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal async Task ReconcileCenteredControlAsync(
        CameraModuleConfig configuration, CancellationToken cancellationToken, CameraKey? previousCamera = null)
    {
        var centered = configuration.CenteredExposure;
        if (centered is not null && (!centered.IsValid() || string.IsNullOrWhiteSpace(configuration.AgentId) ||
            configuration.DeploymentLocation is null))
            throw new InvalidOperationException("Centered exposure requires a valid camera identity and authorized site.");
        var currentCamera = centered is null ? null : new CameraKey(configuration.AgentId!, centered.CameraId);
        if (previousCamera is not null && previousCamera != currentCamera)
            await _journal.ExpireCenteredRunsAsync(previousCamera.AgentId, previousCamera.CameraId,
                "profile-replaced", uncertainOnly: false, cancellationToken).ConfigureAwait(false);
        if (currentCamera is not null)
            await _journal.ExpireCenteredRunsAsync(currentCamera.AgentId, currentCamera.CameraId,
                "target-progress-unknown", uncertainOnly: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CenteredExposureReservation> ReserveCenteredAsync(
        CameraModuleConfig configuration,
        CenteredSceneResources resources,
        SqliteCalibrationLibraryStore? calibration,
        CancellationToken cancellationToken,
        string? deviceIdentity = null,
        CenteredScheduleBinding? schedule = null,
        bool admissionInterrupted = false,
        long? admissionStateVersion = null)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var profile = configuration.CenteredExposure ??
            throw new InvalidOperationException("The centered exposure profile must be selected.");
        if (!profile.IsValid() || string.IsNullOrWhiteSpace(configuration.AgentId) || configuration.DeploymentLocation is null)
            throw new InvalidOperationException("Centered exposure requires a valid camera identity and authorized site.");
        var graph = _graphOperations ??
            throw new InvalidOperationException("Centered exposure requires durable graph execution.");
        var revision = await graph.SelectLiveRevisionAsync(configuration, false, cancellationToken).ConfigureAwait(false);
        var camera = new CameraKey(configuration.AgentId!, profile.CameraId);
        var location = configuration.DeploymentLocation!.ToProvenance();
        var linear = revision.Pipeline.Steps.SingleOrDefault(static step =>
            step.Enabled != false && step.Type.Equals("Calibration", StringComparison.OrdinalIgnoreCase));
        if (linear?.Publication?.Persistence == CaptureProcessingPersistenceMode.MemoryOnly)
            throw new InvalidOperationException("Centered calibration requires durable step-3 publication.");
        var calibrationOptions = linear?.Options is { } options
            ? options.Deserialize<CalibrationProcessingStepOptions>(JsonSerializerOptions.Web)
            : null;
        var configuredLinearId = linear is null ? null : string.IsNullOrWhiteSpace(linear.Id) ? linear.Type : linear.Id.Trim();
        var linearNodeId = configuredLinearId is null ? null
            : revision.Nodes.SingleOrDefault(node => node.NodeId.Equals(configuredLinearId, StringComparison.OrdinalIgnoreCase))?.NodeId
                ?? throw new InvalidOperationException("Centered exposure requires an executable pinned calibration slot.");
        if (calibrationOptions?.UsesCalibrationLibrary != true || calibration is null)
            return await ReserveCenteredRunAsync(configuration, resources, revision, camera, location, linearNodeId,
                calibrationOptions, null, deviceIdentity, schedule, admissionInterrupted, admissionStateVersion, cancellationToken).ConfigureAwait(false);

        var root = _options.RawIngressRoot;
        if (!calibration.OwnsStorageRoot(root))
            throw new InvalidOperationException("Centered calibration and raw admission must share a storage root.");
        var rawGate = RawIngressLifecycleLock.ForRoot(root);
        await rawGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var storageGate = StorageLifecycleLock.ForRoot(root);
            await StorageLifecycleLock.WaitAsync(storageGate, "centered-calibration", _logger, cancellationToken).ConfigureAwait(false);
            try
            {
                var state = await calibration.ReadStateAfterRawInitializationAsync(cancellationToken).ConfigureAwait(false);
                CenteredCalibrationBinding? binding = state.ActiveBundle is { } bundle
                    ? new(bundle.Bundle.BundleId, bundle.BundleIdentitySha256, state.Version)
                    : null;
                return await ReserveCenteredRunAsync(configuration, resources, revision, camera, location, linearNodeId,
                    calibrationOptions, binding, deviceIdentity, schedule, admissionInterrupted, admissionStateVersion, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                storageGate.Release();
            }
        }
        finally
        {
            rawGate.Release();
        }
    }

    private async Task<CenteredExposureReservation> ReserveCenteredRunAsync(
        CameraModuleConfig configuration,
        CenteredSceneResources resources,
        ProcessingGraphRevisionSnapshot revision,
        CameraKey camera,
        CaptureLocationProvenance location,
        string? linearNodeId,
        CalibrationProcessingStepOptions? calibrationOptions,
        CenteredCalibrationBinding? pinnedCalibration,
        string? deviceIdentity,
        CenteredScheduleBinding? schedule,
        bool admissionInterrupted,
        long? admissionStateVersion,
        CancellationToken cancellationToken)
    {
        var pending = await _journal.ReadCenteredPendingAsync(camera, cancellationToken).ConfigureAwait(false);
        var snapshot = new CenteredRunSnapshot(camera, pending?.Key.RunId ?? Guid.NewGuid(), revision.State.RevisionId,
            configuration with
            {
                Pipeline = revision.Pipeline,
                Observatory = new ObservatoryLocation(0, 0, 0, "UTC"),
                DeploymentLocation = null,
                DeploymentLocationRedacted = true
            },
            location, resources, linearNodeId,
            linearNodeId is null ? null : (calibrationOptions ?? new CalibrationProcessingStepOptions()).OutputVariant,
            pinnedCalibration, deviceIdentity, schedule)
        {
            AdmissionStateVersion = admissionStateVersion
        };
        var hash = PayloadChecksum.ComputeSha256(snapshot.Serialize());
        if (pending is not null && pending.RunHash == hash && !admissionInterrupted)
        {
            RecentCenteredExposures.Activate(camera, pending.Key.RunId);
            return new(pending, revision);
        }

        var reason = admissionInterrupted || pending is not null && pending.Run.AdmissionStateVersion != admissionStateVersion
            ? "schedule-admission-interrupted" :
            pending is not null && pending.Run.Schedule != schedule ? "schedule-grant-changed" : "generation-replaced";
        await _journal.ExpireCenteredRunsAsync(camera.AgentId, camera.CameraId, reason,
            uncertainOnly: false, cancellationToken).ConfigureAwait(false);
        snapshot = snapshot with { RunId = Guid.NewGuid() };
        hash = PayloadChecksum.ComputeSha256(snapshot.Serialize());
        var attempt = new ExposureAttemptKey(camera, snapshot.RunId, 1, Guid.NewGuid());
        await _journal.StartCenteredRunAsync(snapshot, attempt, cancellationToken).ConfigureAwait(false);
        RecentCenteredExposures.Activate(camera, snapshot.RunId);
        return new(new(attempt, snapshot, hash, null), revision);
    }

    internal Task DispatchCenteredAsync(
        ExposureAttemptKey attempt,
        CaptureSetpoint settings,
        CaptureMeteringEvidence? evidence,
        string reason,
        CancellationToken cancellationToken,
        DateTimeOffset? requestedStart = null,
        TimeSpan requestedInterval = default,
        CenteredOwnerTransition? owner = null,
        CenteredDispatchSchedule? schedule = null)
        => _journal.DispatchCenteredAttemptAsync(attempt, settings, evidence, reason, owner, cancellationToken,
            requestedStart, requestedInterval, schedule);

    internal Task ExpireCenteredPendingAsync(ExposureAttemptKey attempt, CancellationToken cancellationToken)
        => _journal.ExpireCenteredPendingAsync(attempt, cancellationToken);

    internal Task RefuseCenteredStartAsync(ExposureAttemptKey attempt, CancellationToken cancellationToken)
        => _journal.RefuseCenteredStartAsync(attempt, cancellationToken);

    internal async Task<RawCaptureReceipt> AcceptCenteredAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CenteredExposureReservation reservation,
        CancellationToken cancellationToken)
    {
        var source = reservation.Pending.Key;
        var targetNonce = SHA256.HashData(source.AttemptId.ToByteArray());
        var target = source with { Ordinal = checked(source.Ordinal + 1), AttemptId = new Guid(targetNonce.AsSpan(0, 16)) };
        var binding = new CenteredExposureBindingV1(source, target, reservation.Pending.RunHash);
        if (submission.Result.Frame is { Metadata.Scene: { RequiresProjectedScene: false } scene } frame)
        {
            var retainedClock = scene with { Objects = null, Segments = null };
            submission = submission with
            {
                Result = submission.Result with
                {
                    Frame = frame with { Metadata = frame.Metadata with { Scene = retainedClock } }
                }
            };
        }
        var receipt = await AcceptAsync(
                   configuration, submission,
                   new RawCaptureGraphBinding(reservation.Revision, false),
                   cancellationToken, binding).ConfigureAwait(false)
               ?? throw new InvalidOperationException("A centered exposure requires a raw frame.");
        if (receipt.Outcome == RawIngressOutcome.Committed)
            RecentCenteredExposures.Observe(new(source.Camera, source.RunId, receipt.Manifest.Descriptor.Capture.CaptureId));
        return receipt;
    }

    internal Task<CenteredRunSnapshot> ReadCenteredRunAsync(
        CenteredExposureBindingV1 binding, CancellationToken cancellationToken)
        => _journal.ReadCenteredRunAsync(binding, cancellationToken);

    private async Task<RecoveredCenteredAdmission> PrepareRecoveredCenteredAdmissionAsync(
        ArtifactManifestV2 manifest, CancellationToken cancellationToken)
    {
        if (manifest.CenteredExposure is not { } centered || manifest.CaptureGraphRevisionId is not { } revision ||
            _graphOperations is null)
            throw new InvalidDataException("The frozen centered graph is unavailable for raw recovery.");
        var run = await _journal.ReadCenteredRunAsync(centered, cancellationToken).ConfigureAwait(false);
        if (run.GraphRevisionId != revision)
            throw new InvalidDataException("The centered orphan differs from its frozen graph.");
        var descriptor = manifest.Descriptor;
        var (seed, configuration) = await _graphOperations.PrepareRecoveredLiveExecutionAsync(
            run.Configuration, descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
            descriptor.Timing.DurableIngressUtc, cancellationToken, revision).ConfigureAwait(false);
        var requested = await _journal.ReadCenteredRequestAsync(centered.Source,
            descriptor.Capture.CaptureId, cancellationToken).ConfigureAwait(false);
        var context = SerializeCenteredLaneEnvelope(configuration, descriptor, requested);
        return new(seed, context.Json, context.Sha256, requested);
    }

    internal Task<CenteredRawEvidence?> ReadCenteredRawAsync(ExposureKey key, CancellationToken cancellationToken)
        => _journal.ReadCenteredRawAsync(key, cancellationToken);

    internal async ValueTask<bool> CenteredFrameEvidenceExistsAsync(ReconstructionDescriptor descriptor, CancellationToken cancellationToken)
    {
        var paths = _files.GetPaths(descriptor.Timing.ExposureStartedUtc, descriptor.Artifact.ArtifactId);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.SidecarAbsolutePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.PayloadAbsolutePath);
        return File.Exists(paths.SidecarAbsolutePath) && File.Exists(paths.PayloadAbsolutePath) ||
            _siloArtifacts is not null &&
            await _siloArtifacts.HasMemberAsync(descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
                cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CameraFrame> ReadCenteredFrameAsync(CenteredRawEvidence evidence, CancellationToken cancellationToken)
    {
        if (!evidence.IsCommitted)
            throw new InvalidDataException("The centered raw source is not available as committed evidence.");

        var descriptor = evidence.Manifest.Descriptor;
        var paths = _files.GetPaths(descriptor.Timing.ExposureStartedUtc, descriptor.Artifact.ArtifactId);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.SidecarAbsolutePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.PayloadAbsolutePath);
        var sidecar = _siloArtifacts is null
            ? await File.ReadAllBytesAsync(paths.SidecarAbsolutePath, cancellationToken).ConfigureAwait(false)
            : await _siloArtifacts.ReadRawEvidenceAsync(descriptor.Artifact.ArtifactId, evidence.ManifestSha256, paths.SidecarAbsolutePath,
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The centered raw source has no retained evidence.");
        var bytes = _siloArtifacts is null
            ? await File.ReadAllBytesAsync(paths.PayloadAbsolutePath, cancellationToken).ConfigureAwait(false)
            : await _siloArtifacts.ReadLiveRawAsync(descriptor, paths.PayloadAbsolutePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The centered raw source has no retained payload.");
        if (PayloadChecksum.ComputeSha256(sidecar) != evidence.ManifestSha256 || bytes.LongLength != descriptor.Layout.ByteLength)
            throw new InvalidDataException("Centered raw frame evidence differs from its committed identity.");

        return ReconstructCenteredFrame(descriptor, bytes);
    }

    private static CameraFrame ReconstructCenteredFrame(ReconstructionDescriptor descriptor, byte[] bytes)
    {
        var validation = FrameReconstructor.TryReconstruct(descriptor, bytes, out var frame);
        return validation.IsValid && frame is not null
            ? frame
            : throw new InvalidDataException($"Centered raw frame could not be verified ({validation.ReasonCode}).");
    }
}
