using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;

[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
    Justification = "Injected disposable services are owned by the dependency injection container.")]
public sealed class VirtualCalibrationAcquisitionCoordinator(
    SqliteCalibrationLibraryStore store,
    CalibrationArtifactPublisher publisher,
    CaptureAdmissionCoordinator admissionCoordinator,
    ICameraAgentConfigurationAccessor configurationAccessor,
    TimeProvider timeProvider,
    CaptureScheduleRuntimeCoordinator? scheduleRuntimeCoordinator = null) : IDisposable
{
    private static readonly JsonElement NoneOptions = JsonSerializer.SerializeToElement(new { mode = "none" });
    private readonly SqliteCalibrationLibraryStore _store = store;
    private readonly CalibrationArtifactPublisher _publisher = publisher;
    private readonly CaptureAdmissionCoordinator _admissionCoordinator = admissionCoordinator;
    private readonly ICameraAgentConfigurationAccessor _configurationAccessor = configurationAccessor;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly CaptureScheduleRuntimeCoordinator? _scheduleRuntimeCoordinator = scheduleRuntimeCoordinator;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _activeExecutionSync = new();
    private readonly Dictionary<string, int> _cancellationIntents = new(StringComparer.Ordinal);
    private string? _activeJobId;
    private CancellationTokenSource? _activeExecutionCancellation;

    public async Task<CalibrationAcquisitionJobSnapshot> AcquireAsync(
        VirtualCalibrationAcquisitionRequestV1 request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VirtualCalibrationAcquisitionContractJson.ValidateRequest(request);
        var requestIdentity = VirtualCalibrationAcquisitionContractJson.ComputeRequestIdentitySha256(request);
        var replay = await _store.ReadAcquireReplayAsync(
            request.IdempotencyKey, requestIdentity, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay.IsTerminal
                ? replay
                : await ExecuteSerializedAsync(replay.Plan.JobId, cancellationToken).ConfigureAwait(false);
        }

        var configuration = _scheduleRuntimeCoordinator?.Snapshot?.Configuration ??
            await _configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var plan = CreatePlan(configuration, request, requestIdentity);
        var job = await _store.PlanAcquireAsync(
            plan, request.IdempotencyKey, requestIdentity, request.Actor, request.Reason, cancellationToken)
            .ConfigureAwait(false);
        return await ExecuteSerializedAsync(job.Plan.JobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalibrationAcquisitionJobSnapshot?> ResumePendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _store.ReadPendingAcquisitionJobAsync(cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return null;
        }
        return await ExecuteSerializedAsync(pending.Plan.JobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalibrationAcquisitionJobSnapshot> CancelAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128)
        {
            throw new ArgumentException("A valid calibration acquisition job identifier is required.", nameof(jobId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_activeExecutionSync)
        {
            _cancellationIntents[jobId] = _cancellationIntents.GetValueOrDefault(jobId) + 1;
            if (string.Equals(_activeJobId, jobId, StringComparison.Ordinal))
            {
                _activeExecutionCancellation?.Cancel();
            }
        }

        await _executionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = await _store.GetAcquisitionJobAsync(jobId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The calibration acquisition job was not found.");
            return current.IsTerminal ? current : await ResolveCancellationAsync(current).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeExecutionSync)
            {
                if (_cancellationIntents.TryGetValue(jobId, out var count))
                {
                    if (count == 1)
                    {
                        _cancellationIntents.Remove(jobId);
                    }
                    else
                    {
                        _cancellationIntents[jobId]--;
                    }
                }
            }
            _executionGate.Release();
        }
    }

    private async Task<CalibrationAcquisitionJobSnapshot> ExecuteSerializedAsync(
        string jobId,
        CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bool cancellationPending;
        lock (_activeExecutionSync)
        {
            _activeJobId = jobId;
            _activeExecutionCancellation = executionCancellation;
            cancellationPending = _cancellationIntents.ContainsKey(jobId);
        }
        try
        {
            var current = await _store.GetAcquisitionJobAsync(
                jobId,
                cancellationPending ? CancellationToken.None : executionCancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The durable calibration acquisition job is missing.");
            if (current.IsTerminal)
            {
                return current;
            }
            return cancellationPending
                ? await ResolveCancellationAsync(current).ConfigureAwait(false)
                : await ExecuteAsync(current, executionCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeExecutionSync)
            {
                if (ReferenceEquals(_activeExecutionCancellation, executionCancellation))
                {
                    _activeJobId = null;
                    _activeExecutionCancellation = null;
                }
            }
            _executionGate.Release();
        }
    }

    private async Task<CalibrationAcquisitionJobSnapshot> ResolveCancellationAsync(
        CalibrationAcquisitionJobSnapshot current)
    {
        try
        {
            var markerCommitted = await _publisher.EnsureCommittedEvidenceIsCompleteAsync(
                ProfilePath(current.Plan),
                EvidencePaths(current.Plan),
                AcquisitionPhaseRank(current.Phase) >= AcquisitionPhaseRank("profile-published"),
                CancellationToken.None).ConfigureAwait(false);
            return markerCommitted
                ? await ExecuteAsync(current, CancellationToken.None).ConfigureAwait(false)
                : await _store.CancelAcquisitionAsync(current.Plan.JobId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (CalibrationLibraryAcquisitionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CalibrationPublicationConflictException or
                                          InvalidDataException or CalibrationLibraryStoreConflictException)
        {
            _ = await _store.FailAcquisitionAsync(
                current.Plan.JobId, CalibrationLibraryReasonCodes.PublicationConflict, CancellationToken.None)
                .ConfigureAwait(false);
            throw new CalibrationLibraryAcquisitionException(
                CalibrationLibraryReasonCodes.PublicationConflict,
                "Virtual calibration publication conflicts with committed evidence.", exception);
        }
    }

    private async Task<CalibrationAcquisitionJobSnapshot> ExecuteAsync(
        CalibrationAcquisitionJobSnapshot job,
        CancellationToken cancellationToken)
    {
        if (job.IsTerminal)
        {
            return job;
        }
        try
        {
            var markerCommitted = await _publisher.EnsureCommittedEvidenceIsCompleteAsync(
                ProfilePath(job.Plan),
                EvidencePaths(job.Plan),
                AcquisitionPhaseRank(job.Phase) >= AcquisitionPhaseRank("profile-published"),
                cancellationToken).ConfigureAwait(false);
            if (!markerCommitted && job.AttemptCount >= SqliteCalibrationLibraryStore.MaximumAcquisitionAttempts)
            {
                _ = await _store.FailAcquisitionAsync(
                    job.Plan.JobId, CalibrationLibraryReasonCodes.AcquisitionFailure, CancellationToken.None)
                    .ConfigureAwait(false);
                throw new CalibrationLibraryAcquisitionException(
                    CalibrationLibraryReasonCodes.AcquisitionFailure,
                    "Virtual calibration acquisition exhausted its retry limit.");
            }
            job = await _store.BeginAcquisitionAttemptAsync(job.Plan.JobId, cancellationToken).ConfigureAwait(false);
            var artifacts = new List<CalibrationLibraryArtifactV1>(16);
            var masters = new List<CalibrationLibraryArtifactV1>(4);
            ushort? flatNormalization = null;
            await _admissionCoordinator.ExecuteCaptureBoundaryAsync(
                async boundaryToken =>
                {
                    var current = job;
                    for (var kindIndex = 0; kindIndex < CalibrationReferenceKinds.All.Count; kindIndex++)
                    {
                        var kind = CalibrationReferenceKinds.All[kindIndex];
                        var frames = new List<CalibrationSourceFrame>(CalibrationMasterBuilder.RequiredSourceCount);
                        var sourceIds = new List<Guid>(CalibrationMasterBuilder.RequiredSourceCount);
                        for (var sourceIndex = 0; sourceIndex < CalibrationMasterBuilder.RequiredSourceCount; sourceIndex++)
                        {
                            var generated = VirtualCalibrationSourceGenerator.Generate(
                                ToSourceKind(kind), sourceIndex, job.Plan.InputLayout, job.Plan.ExposureFor(kind),
                                job.Plan.Gain, job.Plan.Offset, job.Plan.TemperatureC, job.Plan.SourceModel,
                                boundaryToken);
                            frames.Add(generated);
                            var artifact = CreateArtifact(
                                job.Plan, kind, sourceIndex, generated.PixelData,
                                kindIndex * CalibrationMasterBuilder.RequiredSourceCount + sourceIndex + 1);
                            await _publisher.PublishPairAsync(
                                PayloadPath(artifact.ManifestRelativePath), generated.PixelData,
                                artifact.ManifestRelativePath, artifact.ManifestJson, boundaryToken).ConfigureAwait(false);
                            artifacts.Add(artifact.LibraryArtifact);
                            sourceIds.Add(artifact.LibraryArtifact.ArtifactId);
                            var phase = $"source-{kind}-{sourceIndex}";
                            if (current.State == CalibrationAcquisitionStates.Acquiring &&
                                AcquisitionPhaseRank(current.Phase) < AcquisitionPhaseRank(phase))
                            {
                                current = await _store.RecordAcquisitionProgressAsync(
                                    job.Plan.JobId, CalibrationAcquisitionStates.Acquiring, phase, boundaryToken)
                                    .ConfigureAwait(false);
                            }
                        }

                        var result = kind == CalibrationReferenceKinds.Defect
                            ? CalibrationMasterBuilder.BuildDefectMask(frames, boundaryToken)
                            : CalibrationMasterBuilder.BuildMedian(frames, boundaryToken);
                        var master = CreateMasterArtifact(job.Plan, kind, result, sourceIds, 13 + kindIndex);
                        await _publisher.PublishPairAsync(
                            PayloadPath(master.ManifestRelativePath), master.Payload,
                            master.ManifestRelativePath, master.ManifestJson, boundaryToken).ConfigureAwait(false);
                        artifacts.Add(master.LibraryArtifact);
                        masters.Add(master.LibraryArtifact);
                        if (kind == CalibrationReferenceKinds.Flat)
                        {
                            flatNormalization = ResolveFlatNormalization(master.Payload);
                        }
                    }
                    if (current.State == CalibrationAcquisitionStates.Acquiring)
                    {
                        current = await _store.RecordAcquisitionProgressAsync(
                            job.Plan.JobId, CalibrationAcquisitionStates.Building, "masters-pending", boundaryToken)
                            .ConfigureAwait(false);
                    }
                    job = current;
                    return true;
                }, cancellationToken).ConfigureAwait(false);

            if (job.State == CalibrationAcquisitionStates.Building &&
                AcquisitionPhaseRank(job.Phase) < AcquisitionPhaseRank("masters-built"))
            {
                job = await _store.RecordAcquisitionProgressAsync(
                    job.Plan.JobId, CalibrationAcquisitionStates.Building, "masters-built", cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var master in masters)
            {
                var phase = $"master-{master.Kind}";
                if (job.State == CalibrationAcquisitionStates.Building ||
                    job.State == CalibrationAcquisitionStates.Publishing &&
                    AcquisitionPhaseRank(job.Phase) < AcquisitionPhaseRank(phase))
                {
                    job = await _store.RecordAcquisitionProgressAsync(
                        job.Plan.JobId, CalibrationAcquisitionStates.Publishing, phase, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var profile = CreateProfile(
                job.Plan,
                masters,
                flatNormalization ?? throw new InvalidDataException("The flat master normalization is missing."));
            var profileJson = ReferenceCalibrationProfileJson.Serialize(profile);
            var profilePath = ProfilePath(job.Plan);
            await _publisher.PublishProfileMarkerAsync(profilePath, profileJson, cancellationToken).ConfigureAwait(false);
            if (job.State == CalibrationAcquisitionStates.Publishing &&
                AcquisitionPhaseRank(job.Phase) < AcquisitionPhaseRank("profile-published"))
            {
                job = await _store.RecordAcquisitionProgressAsync(
                    job.Plan.JobId, CalibrationAcquisitionStates.Publishing, "profile-published", CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var bundle = CreateBundle(job.Plan, profilePath, profileJson, artifacts);
            _ = await _store.AdoptPublishedBundleAsync(bundle, CancellationToken.None).ConfigureAwait(false);
            return await _store.CompleteAcquisitionAsync(
                job.Plan.JobId, bundle.BundleId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CalibrationLibraryAcquisitionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CalibrationPublicationConflictException or
                                          InvalidDataException or CalibrationLibraryStoreConflictException)
        {
            _ = await _store.FailAcquisitionAsync(
                job.Plan.JobId, CalibrationLibraryReasonCodes.PublicationConflict, CancellationToken.None)
                .ConfigureAwait(false);
            throw new CalibrationLibraryAcquisitionException(
                CalibrationLibraryReasonCodes.PublicationConflict,
                "Virtual calibration publication conflicts with committed evidence.", exception);
        }
        catch (Exception exception)
        {
            var current = await _store.RecordAcquisitionAttemptFailureAsync(
                job.Plan.JobId, CancellationToken.None).ConfigureAwait(false);
            if (current.AttemptCount >= SqliteCalibrationLibraryStore.MaximumAcquisitionAttempts)
            {
                _ = await _store.FailAcquisitionAsync(
                    job.Plan.JobId, CalibrationLibraryReasonCodes.AcquisitionFailure, CancellationToken.None)
                    .ConfigureAwait(false);
                throw new CalibrationLibraryAcquisitionException(
                    CalibrationLibraryReasonCodes.AcquisitionFailure,
                    "Virtual calibration acquisition exhausted its retry limit.", exception);
            }
            throw;
        }
    }

    private VirtualCalibrationAcquisitionPlanV1 CreatePlan(
        CameraModuleConfig configuration,
        VirtualCalibrationAcquisitionRequestV1 request,
        string requestIdentity)
    {
        if (!string.Equals(configuration.ModuleType, "VirtualSky", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(configuration.AgentId) || configuration.Rig?.Readout is null)
        {
            throw new InvalidOperationException(
                "Virtual calibration acquisition requires a configured VirtualSky agent with an explicit native readout.");
        }
        var input = SensorReadoutResolver.Resolve(configuration.Rig.Sensor, configuration.Rig.Readout).Layout;
        var rigIdentity = CameraRigProfileIdentity.ComputeSha256(configuration.Rig);
        var sensorIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(configuration.Rig.Sensor);
        var rigId = $"rig-{rigIdentity[..16].ToUpperInvariant()}";
        var jobMaterial = string.Join('\n', requestIdentity, configuration.AgentId, rigId,
            CaptureContractJson.ComputeCanonicalJsonSha256(input));
        var jobIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobMaterial)));
        var plan = new VirtualCalibrationAcquisitionPlanV1(
            VirtualCalibrationAcquisitionPlanV1.CurrentSchemaVersion,
            $"virtual-{jobIdentity[..32]}",
            configuration.ModuleType,
            configuration.AgentId,
            rigId,
            new ProfileIdentityDescriptor("rig", configuration.Rig.ProfileVersion, rigIdentity),
            new ProfileIdentityDescriptor(
                configuration.Rig.Sensor.Name, configuration.Rig.Sensor.SensorRecipeVersion, sensorIdentity),
            input,
            CalibrationMasterBuilder.CreateNormalizedLayout(input),
            request.Gain,
            request.Offset,
            request.TemperatureC,
            request.BiasExposure,
            request.DarkExposure,
            request.FlatExposure,
            request.DefectExposure,
            request.ApplicableLightExposure,
            request.EffectiveFromUtc,
            request.EffectiveUntilUtc,
            ToMilliseconds(_timeProvider.GetUtcNow()),
            request.SourceModel,
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(request.SourceModel));
        VirtualCalibrationAcquisitionContractJson.ValidatePlan(plan);
        return VirtualCalibrationAcquisitionContractJson.ParsePlan(
            VirtualCalibrationAcquisitionContractJson.SerializePlan(plan));
    }

    private static PublishedArtifact CreateArtifact(
        VirtualCalibrationAcquisitionPlanV1 plan,
        string kind,
        int sourceIndex,
        ReadOnlyMemory<byte> payload,
        int sequence)
    {
        var artifactId = StableGuid("source-artifact", plan.JobId, kind, sourceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var captureId = StableGuid("source-capture", plan.JobId, kind, sourceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var manifestPath = $"{BasePath(plan)}/sources/{kind}-{sourceIndex}.json";
        var recipe = RecipeIdentityDescriptor.Create(
            "virtual-calibration-source", "1.0.0", VirtualCalibrationSourceGenerator.AlgorithmVersion,
            JsonSerializer.SerializeToElement(new
            {
                schemaVersion = plan.SourceModel.SchemaVersion,
                sourceModelIdentitySha256 = plan.SourceModelIdentitySha256,
                referenceKind = kind,
                sourceIndex
            }));
        return CreatePublishedArtifact(
            plan, kind, sourceIndex, artifactId, captureId, sequence, payload, plan.InputLayout,
            manifestPath, [], recipe, "VirtualCalibrationSource");
    }

    private static PublishedArtifact CreateMasterArtifact(
        VirtualCalibrationAcquisitionPlanV1 plan,
        string kind,
        CalibrationMasterResult result,
        IReadOnlyList<Guid> sourceIds,
        int sequence)
    {
        var artifactId = StableGuid("master-artifact", plan.JobId, kind);
        var captureId = StableGuid("master-capture", plan.JobId, kind);
        var recipe = RecipeIdentityDescriptor.Create(
            "calibration-master-build", "1.0.0", result.AlgorithmVersion,
            JsonSerializer.SerializeToElement(new { referenceKind = kind, sourceCount = result.SourceCount }));
        return CreatePublishedArtifact(
            plan, kind, null, artifactId, captureId, sequence, result.PixelData, result.Layout,
            $"{BasePath(plan)}/masters/{kind}.json", sourceIds, recipe, "CalibrationMasterBuilder");
    }

    private static PublishedArtifact CreatePublishedArtifact(
        VirtualCalibrationAcquisitionPlanV1 plan,
        string kind,
        int? sourceIndex,
        Guid artifactId,
        Guid captureId,
        int sequence,
        ReadOnlyMemory<byte> payload,
        FrameLayoutDescriptor layout,
        string manifestPath,
        IReadOnlyList<Guid> sourceIds,
        RecipeIdentityDescriptor recipe,
        string sourceId)
    {
        var exposure = plan.ExposureFor(kind);
        var started = plan.CreatedUtc.AddMilliseconds(sequence);
        var ended = started.Add(exposure);
        var captureSequence = CreateCaptureSequence(plan.JobId, sequence);
        var payloadSha256 = PayloadChecksum.ComputeSha256(payload.Span);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(plan.AgentId, plan.RigId, captureSequence, captureId),
            new CaptureTimingDescriptor(started, started, ended, ended, ended),
            new CaptureControlDescriptor(
                exposure, exposure, plan.Gain, plan.Gain, plan.Offset, plan.Offset,
                plan.TemperatureC, plan.TemperatureC),
            Profiles(plan),
            layout,
            new ArtifactDescriptor(
                artifactId,
                sourceIndex.HasValue ? FrameArtifactRole.Raw : FrameArtifactRole.Combined,
                sourceId,
                kind,
                ended,
                sourceIds,
                recipe,
                layout.PixelFormat == CameraPixelFormat.Mono16
                    ? "application/x-skymonitor-mono16"
                    : "application/x-skymonitor-bayer-rggb16",
                payloadSha256));
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion, descriptor, PayloadPath(manifestPath));
        var manifestJson = CaptureContractJson.Serialize(manifest);
        var libraryArtifact = new CalibrationLibraryArtifactV1(
            kind,
            sourceIndex.HasValue ? CalibrationLibraryArtifactRoles.Source : CalibrationLibraryArtifactRoles.Master,
            artifactId,
            manifestPath,
            payloadSha256,
            exposure,
            plan.Gain,
            plan.Offset,
            plan.TemperatureC,
            sourceIndex,
            sourceIds,
            sourceIndex.HasValue ? null : recipe);
        return new PublishedArtifact(libraryArtifact, payload, manifestPath, manifestJson);
    }

    private static ReferenceCalibrationProfileV1 CreateProfile(
        VirtualCalibrationAcquisitionPlanV1 plan,
        IReadOnlyList<CalibrationLibraryArtifactV1> masters,
        ushort flatNormalization)
        => new(
            ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            $"virtual-{plan.JobId}",
            VirtualCalibrationAcquisitionPlanV1.CurrentSchemaVersion,
            "VirtualSky durable calibration acquisition; software-only evidence",
            plan.EffectiveFromUtc,
            plan.EffectiveUntilUtc,
            plan.OutputLayout.Width,
            plan.OutputLayout.Height,
            plan.OutputLayout.PixelFormat,
            flatNormalization,
            plan.Gain,
            plan.Gain,
            plan.TemperatureC,
            plan.TemperatureC,
            masters.Select(master => new CalibrationReferenceDescriptorV1(
                master.Kind,
                master.ArtifactId,
                master.PayloadSha256,
                master.Exposure,
                master.Gain,
                master.TemperatureC)).ToArray());

    private static CalibrationLibraryBundleV1 CreateBundle(
        VirtualCalibrationAcquisitionPlanV1 plan,
        string profilePath,
        ReadOnlySpan<byte> profileJson,
        IReadOnlyList<CalibrationLibraryArtifactV1> artifacts)
        => new(
            CalibrationLibraryBundleV1.CurrentSchemaVersion,
            $"bundle-{plan.JobId}",
            CalibrationLibraryBundleSources.VirtualAcquisitionV1,
            plan.CreatedUtc,
            profilePath,
            PayloadChecksum.ComputeSha256(profileJson),
            plan.SourceModelIdentitySha256,
            new CalibrationApplicabilityV1(
                plan.AgentId,
                plan.RigId,
                plan.RigProfile.Sha256,
                plan.SensorProfile.Sha256,
                plan.InputLayout,
                plan.OutputLayout,
                plan.Gain,
                plan.Gain,
                plan.Offset,
                plan.Offset,
                plan.ApplicableLightExposure,
                plan.ApplicableLightExposure,
                plan.TemperatureC,
                plan.TemperatureC,
                plan.EffectiveFromUtc,
                plan.EffectiveUntilUtc),
            artifacts);

    private static CaptureProfileSet Profiles(VirtualCalibrationAcquisitionPlanV1 plan)
        => new(
            plan.RigProfile,
            new ProfileIdentityDescriptor(
                "virtual-calibration-source-model", plan.SourceModel.SchemaVersion, plan.SourceModelIdentitySha256),
            new ProfileIdentityDescriptor("mask", "none-v1", CaptureContractJson.ComputeCanonicalJsonSha256(NoneOptions)),
            plan.SensorProfile,
            new ProfileIdentityDescriptor(
                "calibration-acquisition", VirtualCalibrationAcquisitionPlanV1.CurrentSchemaVersion,
                CaptureContractJson.ComputeCanonicalJsonSha256(new { plan.JobId })));

    private static ushort ResolveFlatNormalization(ReadOnlyMemory<byte> payload)
    {
        ulong sum = 0;
        var span = payload.Span;
        for (var index = 0; index < span.Length; index += 2)
        {
            sum += (ushort)(span[index] | span[index + 1] << 8);
        }
        var value = checked((ushort)Math.Clamp(
            (long)((sum + (ulong)(span.Length / 4)) / (ulong)(span.Length / 2)), 1, ushort.MaxValue));
        return value;
    }

    private static VirtualCalibrationSourceKind ToSourceKind(string kind)
        => kind switch
        {
            CalibrationReferenceKinds.Bias => VirtualCalibrationSourceKind.Bias,
            CalibrationReferenceKinds.Dark => VirtualCalibrationSourceKind.Dark,
            CalibrationReferenceKinds.Flat => VirtualCalibrationSourceKind.Flat,
            CalibrationReferenceKinds.Defect => VirtualCalibrationSourceKind.Defect,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static Guid StableGuid(string domain, params string[] values)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', [domain, .. values])));
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static long CreateCaptureSequence(string jobId, int ordinal)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId}:capture-sequence"));
        const ulong reservedStart = (ulong)long.MaxValue / 2;
        const ulong blockSize = 16;
        var blockCount = reservedStart / blockSize;
        var block = BinaryPrimitives.ReadUInt64BigEndian(hash) % blockCount;
        return checked((long)(reservedStart + block * blockSize + (uint)(ordinal - 1)));
    }

    private static string BasePath(VirtualCalibrationAcquisitionPlanV1 plan)
        => $"calibration/virtual/{plan.JobId}";

    private static string ProfilePath(VirtualCalibrationAcquisitionPlanV1 plan)
        => $"{BasePath(plan)}/reference-calibration-profile.json";

    private static List<string> EvidencePaths(VirtualCalibrationAcquisitionPlanV1 plan)
    {
        var paths = new List<string>(32);
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            for (var sourceIndex = 0; sourceIndex < CalibrationMasterBuilder.RequiredSourceCount; sourceIndex++)
            {
                var manifest = $"{BasePath(plan)}/sources/{kind}-{sourceIndex}.json";
                paths.Add(PayloadPath(manifest));
                paths.Add(manifest);
            }
        }
        foreach (var kind in CalibrationReferenceKinds.All)
        {
            var manifest = $"{BasePath(plan)}/masters/{kind}.json";
            paths.Add(PayloadPath(manifest));
            paths.Add(manifest);
        }
        return paths;
    }

    private static string PayloadPath(string manifestPath)
        => string.Concat(manifestPath.AsSpan(0, manifestPath.Length - ".json".Length), ".bin");

    private static DateTimeOffset ToMilliseconds(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUniversalTime().ToUnixTimeMilliseconds());

    private static int AcquisitionPhaseRank(string phase)
    {
        if (phase == "planned") return 0;
        if (phase == "sources-pending") return 1;
        if (phase == "masters-pending") return 14;
        if (phase == "masters-built") return 15;
        if (phase == "profile-published") return 20;
        var kinds = CalibrationReferenceKinds.All;
        for (var kindIndex = 0; kindIndex < kinds.Count; kindIndex++)
        {
            for (var sourceIndex = 0; sourceIndex < 3; sourceIndex++)
            {
                if (phase == $"source-{kinds[kindIndex]}-{sourceIndex}") return 2 + kindIndex * 3 + sourceIndex;
            }
            if (phase == $"master-{kinds[kindIndex]}") return 16 + kindIndex;
        }
        return phase == "published" ? 21 : throw new InvalidDataException("The acquisition phase is invalid.");
    }

    public void Dispose()
    {
        lock (_activeExecutionSync)
        {
            _activeExecutionCancellation?.Dispose();
            _activeExecutionCancellation = null;
            _activeJobId = null;
            _cancellationIntents.Clear();
        }
        _executionGate.Dispose();
    }

    private sealed record PublishedArtifact(
        CalibrationLibraryArtifactV1 LibraryArtifact,
        ReadOnlyMemory<byte> Payload,
        string ManifestRelativePath,
        ReadOnlyMemory<byte> ManifestJson);
}

public sealed class VirtualCalibrationAcquisitionRecoveryService(
    VirtualCalibrationAcquisitionCoordinator coordinator) : IHostedService
{
    private readonly VirtualCalibrationAcquisitionCoordinator _coordinator = coordinator;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (await _coordinator.ResumePendingAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
