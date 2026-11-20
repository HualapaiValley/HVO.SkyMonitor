using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed record CenteredExposureReservation(
    CenteredPendingAttempt Pending, ProcessingGraphRevisionSnapshot Revision);

internal sealed partial class RawCaptureIngress : ICenteredExposureRuntimeReader
{
    internal CenteredRecentExposureIndex RecentCenteredExposures { get; } = new();

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
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var profile = configuration.CenteredExposure ??
            throw new InvalidOperationException("The centered exposure profile must be selected.");
        var graph = _graphOperations ??
            throw new InvalidOperationException("Centered exposure requires durable graph execution.");
        var revision = await graph.SelectLiveRevisionAsync(configuration, false, cancellationToken).ConfigureAwait(false);
        var camera = new CameraKey(configuration.AgentId!, profile.CameraId);
        var location = configuration.DeploymentLocation!.ToProvenance();
        var linear = revision.Pipeline.Steps.SingleOrDefault(static step =>
            step.Enabled != false && step.Type.Equals("Calibration", StringComparison.OrdinalIgnoreCase));
        var calibrationOptions = linear?.Options is { } options
            ? options.Deserialize<CalibrationProcessingStepOptions>(JsonSerializerOptions.Web)
            : null;
        var linearNodeId = linear is null ? null : string.IsNullOrWhiteSpace(linear.Id) ? linear.Type : linear.Id.Trim();
        if (linearNodeId is not null &&
            !revision.Nodes.Any(node => node.NodeId.Equals(linearNodeId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Centered exposure requires an executable pinned calibration slot.");
        var pinnedCalibration = await FreezeCenteredCalibrationAsync(calibrationOptions, calibration, cancellationToken)
            .ConfigureAwait(false);
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
            pinnedCalibration);
        var hash = PayloadChecksum.ComputeSha256(snapshot.Serialize());
        if (pending is not null && pending.RunHash == hash)
        {
            RecentCenteredExposures.Activate(camera, pending.Key.RunId);
            return new(pending, revision);
        }

        await _journal.ExpireCenteredRunsAsync(camera.AgentId, camera.CameraId, "generation-replaced",
            uncertainOnly: false, cancellationToken).ConfigureAwait(false);
        snapshot = snapshot with { RunId = Guid.NewGuid() };
        hash = PayloadChecksum.ComputeSha256(snapshot.Serialize());
        var attempt = new ExposureAttemptKey(camera, snapshot.RunId, 1, Guid.NewGuid());
        await _journal.StartCenteredRunAsync(snapshot, attempt, cancellationToken).ConfigureAwait(false);
        RecentCenteredExposures.Activate(camera, snapshot.RunId);
        return new(new(attempt, snapshot, hash, null), revision);
    }

    private static async Task<CenteredCalibrationBinding?> FreezeCenteredCalibrationAsync(
        CalibrationProcessingStepOptions? options, SqliteCalibrationLibraryStore? calibration, CancellationToken cancellationToken)
    {
        if (calibration is null || options?.UsesCalibrationLibrary != true)
            return null;

        var state = await calibration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return state.ActiveBundle is { } bundle
            ? new(bundle.Bundle.BundleId, bundle.BundleIdentitySha256, state.Version)
            : null;
    }

    internal Task DispatchCenteredAsync(
        ExposureAttemptKey attempt,
        CaptureSetpoint settings,
        CaptureMeteringEvidence? evidence,
        string reason,
        CancellationToken cancellationToken)
        => _journal.DispatchCenteredAttemptAsync(attempt, settings, evidence, reason, cancellationToken);

    internal Task ExpireCenteredPendingAsync(ExposureAttemptKey attempt, CancellationToken cancellationToken)
        => _journal.ExpireCenteredPendingAsync(attempt, cancellationToken);

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
            submission = submission with { Result = submission.Result with
            {
                Frame = frame with { Metadata = frame.Metadata with { Scene = retainedClock } }
            } };
        }
        var receipt = await AcceptAsync(
                   configuration, submission, new RawCaptureGraphBinding(reservation.Revision, false),
                   cancellationToken, binding).ConfigureAwait(false)
               ?? throw new InvalidOperationException("A centered exposure requires a raw frame.");
        if (receipt.Outcome == RawIngressOutcome.Committed)
            RecentCenteredExposures.Observe(new(source.Camera, source.RunId, receipt.Manifest.Descriptor.Capture.CaptureId));
        return receipt;
    }

    internal Task<CenteredRunSnapshot> ReadCenteredRunAsync(
        CenteredExposureBindingV1 binding, CancellationToken cancellationToken)
        => _journal.ReadCenteredRunAsync(binding, cancellationToken);

    internal Task<CenteredRawEvidence?> ReadCenteredRawAsync(ExposureKey key, CancellationToken cancellationToken)
        => _journal.ReadCenteredRawAsync(key, cancellationToken);

    internal bool CenteredFrameFilesExist(ReconstructionDescriptor descriptor)
    {
        var paths = _files.GetPaths(descriptor.Timing.ExposureStartedUtc, descriptor.Artifact.ArtifactId);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.SidecarAbsolutePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.PayloadAbsolutePath);
        return File.Exists(paths.SidecarAbsolutePath) && File.Exists(paths.PayloadAbsolutePath);
    }

    internal async Task<CameraFrame> ReadCenteredFrameAsync(CenteredRawEvidence evidence, CancellationToken cancellationToken)
    {
        if (!evidence.IsCommitted)
            throw new InvalidDataException("The centered raw source is not available as committed evidence.");

        var descriptor = evidence.Manifest.Descriptor;
        var paths = _files.GetPaths(descriptor.Timing.ExposureStartedUtc, descriptor.Artifact.ArtifactId);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.SidecarAbsolutePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_options.RawIngressRoot, paths.PayloadAbsolutePath);
        var sidecar = await File.ReadAllBytesAsync(paths.SidecarAbsolutePath, cancellationToken).ConfigureAwait(false);
        if (PayloadChecksum.ComputeSha256(sidecar) != evidence.ManifestSha256 ||
            new FileInfo(paths.PayloadAbsolutePath).Length != descriptor.Layout.ByteLength)
            throw new InvalidDataException("Centered raw frame evidence differs from its committed identity.");

        var bytes = await File.ReadAllBytesAsync(paths.PayloadAbsolutePath, cancellationToken).ConfigureAwait(false);
        var validation = FrameReconstructor.TryReconstruct(descriptor, bytes, out var frame);
        return validation.IsValid && frame is not null
            ? frame
            : throw new InvalidDataException($"Centered raw frame could not be verified ({validation.ReasonCode}).");
    }
}
