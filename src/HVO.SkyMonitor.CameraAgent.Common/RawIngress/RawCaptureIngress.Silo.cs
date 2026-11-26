using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class RawCaptureIngress
{
    internal bool SiloAdmissionsReady => Volatile.Read(ref _journalValidated);

    private const string SiloIntentsDirectory = "raw-admission-intents";
    private TaskCompletionSource? _siloPressureRelease;
    private long _nextSiloAdmissionBytes;
    private long _siloAdmissionCursor;
    private string? _siloAdmissionScanStatus;

    internal Task? SiloPressureRelease => Volatile.Read(ref _siloPressureRelease)?.Task;

    private sealed record SiloRawIntent(Guid ArtifactId, DateTimeOffset ExposureStartedUtc, bool Reclaim = false);

    private async ValueTask RecordSiloRawIntentAsync(Guid artifactId, DateTimeOffset exposureStartedUtc)
    {
        if (_silo is null) return;
        var intent = new SiloRawIntent(artifactId, exposureStartedUtc);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
        await AtomicPublisher.PublishAsync(_silo.PhysicalState, SiloIntentPath(artifactId), PublishMode.CreateNew,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<(Guid ArtifactId, RawIngressPaths Paths)>?> ReadSiloRawIntentsAsync(CancellationToken cancellationToken)
    {
        if (_silo is null) return null;
        var root = _silo.PhysicalState;
        var directory = root.Resolve(SiloIntentsDirectory);
        if (!Directory.Exists(directory)) return [];
        if (AtomicPublisher.CleanupTemporaries(root, directory, _siloSettings!.MaximumStagedFiles) > 0)
            DurableSync.Directory(directory);
        var intents = new List<(Guid, RawIngressPaths)>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            root.Verify(path, "silo-raw-intent");
            if (intents.Count >= _siloSettings.MaximumStagedFiles || new FileInfo(path).Length is < 1 or > 512)
                throw new InvalidDataException("The Silo raw admission intent inventory exceeds its bounds.");
            var intent = JsonSerializer.Deserialize<SiloRawIntent>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            if (intent is null || intent.ArtifactId == Guid.Empty || path != root.Resolve(SiloIntentPath(intent.ArtifactId)))
                throw new InvalidDataException("A Silo raw admission intent has an invalid identity.");
            var paths = _files.GetPaths(intent.ExposureStartedUtc, intent.ArtifactId);
            if (intent.Reclaim)
            {
                var entry = await _journal.ReadSiloAdmissionByArtifactAsync(intent.ArtifactId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The Silo raw reclamation intent has no journal identity.");
                await ReclaimSiloStagingAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                intents.Add((intent.ArtifactId, paths));
            }
        }
        return intents;
    }

    private void ReleaseSiloRawIntent(Guid artifactId)
    {
        if (_silo is null) return;
        var path = _silo.PhysicalState.Resolve(SiloIntentPath(artifactId));
        if (!File.Exists(path)) return;
        File.Delete(path);
        DurableSync.Directory(Path.GetDirectoryName(path)!);
    }

    private static string SiloIntentPath(Guid artifactId) => Path.Combine(SiloIntentsDirectory, artifactId.ToString("N") + ".json");

    private void EnsureSiloBinding()
    {
        if (_silo is { BindingVerified: false })
            throw new CaptureLaneBackpressureException("The installed Silo writer binding is awaiting verification.");
    }

    private async ValueTask EnsureSiloCapacityAsync(long length, ArtifactManifestV2 preview, CancellationToken cancellationToken)
    {
        if (_siloSettings is null) return;
        // The real sidecar is emitted after the payload is durable. Reserve room for its updated UTC timestamp.
        var bytes = checked(length + CaptureContractJson.Serialize(preview).LongLength + 64);
        try
        {
            await _journal.EnsureSiloStagingCapacityAsync(bytes, _siloSettings.MaximumStagedBytes, _siloSettings.MaximumStagedFiles,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CaptureLaneBackpressureException)
        {
            Interlocked.Exchange(ref _nextSiloAdmissionBytes, bytes);
            HoldSiloPressure();
            throw;
        }
    }

    private void CheckSiloPressure(long heldCount, long heldBytes, long observedAdmissionBytes = 0)
    {
        if (_siloSettings is null) return;
        if (observedAdmissionBytes > 0) Interlocked.Exchange(ref _nextSiloAdmissionBytes, observedAdmissionBytes);
        var nextBytes = Math.Max(1, Interlocked.Read(ref _nextSiloAdmissionBytes));
        if (heldCount >= _siloSettings.MaximumStagedFiles / 2 || nextBytes > _siloSettings.MaximumStagedBytes - heldBytes)
        {
            HoldSiloPressure();
        }
        else if (Interlocked.Exchange(ref _siloPressureRelease, null) is { } release)
        {
            if (_state.GetBaseSnapshot().Reason == "silo-staging-pressure")
                ReportPressure(underPressure: false);
            release.TrySetResult();
        }
    }

    private void HoldSiloPressure()
    {
        if (SiloPressureRelease is null)
            Interlocked.CompareExchange(ref _siloPressureRelease, new(TaskCreationOptions.RunContinuationsAsynchronously), null);
        if (_state.GetBaseSnapshot().Reason == "accepting")
            _state.UpdateBaseStatus(RawIngressAvailability.Degraded, "silo-staging-pressure");
    }

    private async ValueTask<IReadOnlyList<RawIngressRetentionHold>> ReadRetentionHoldsAsync(CancellationToken cancellationToken)
    {
        var holds = await _journal.ReadRetentionHoldsAsync(cancellationToken).ConfigureAwait(false);
        return _silo is null ? holds : [.. holds, .. await _journal.ReadSiloAdmissionHoldsAsync(cancellationToken).ConfigureAwait(false)];
    }

    internal async ValueTask<(bool More, string? Status)> PublishSiloAdmissionsAsync(int limit, CancellationToken cancellationToken)
    {
        if (_silo is null || !SiloAdmissionsReady) return (false, null);
        var entries = await _journal.ReadPendingSiloAdmissionsAsync(limit, cancellationToken, _siloAdmissionCursor).ConfigureAwait(false);
        var lifecycle = RawIngressLifecycleLock.ForRoot(_options.RawIngressRoot);
        foreach (var listed in entries)
        {
            string? status;
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                status = await PublishSiloAdmissionAsync(listed, lifecycle, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lifecycle.Release();
            }
            _siloAdmissionCursor = listed.AdmissionRowId;
            _siloAdmissionScanStatus ??= status;
        }
        if (SiloPressureRelease is not null || entries.Count > 0)
            await RefreshSiloPressureAsync(cancellationToken).ConfigureAwait(false);
        var more = entries.Count == limit;
        var result = _siloAdmissionScanStatus;
        if (!more)
        {
            _siloAdmissionCursor = 0;
            _siloAdmissionScanStatus = null;
        }
        return (more, result);
    }

    private async ValueTask<string?> PublishSiloAdmissionAsync(RawIngressJournalEntry listed, SemaphoreSlim lifecycle,
        CancellationToken cancellationToken)
    {
        var silo = _silo!;
        // An operator may have changed the disposition after the batch was read.
        var entry = await _journal.ReadSiloAdmissionByArtifactAsync(listed.ArtifactId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("A pending Silo admission has no journal identity.");
        var groupId = entry.CaptureId.ToString("N");
        var revision = "raw-1";
        var staged = await silo.State.ReadGroupAsync(groupId, revision, cancellationToken).ConfigureAwait(false);
        if (staged?.State == SiloStagedState.Abandoned && entry.AdmissionRevalidationCount > 0)
        {
            revision = "raw-revalidated-" + entry.AdmissionRevalidationCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            staged = await silo.State.ReadGroupAsync(groupId, revision, cancellationToken).ConfigureAwait(false);
        }

        if (staged?.State != SiloStagedState.Visible && (entry.EvidenceWrittenOff || entry.State == "quarantined"))
        {
            await silo.Publisher.AbandonIfUncommittedAsync(groupId, revision, "raw-evidence-refused", cancellationToken).ConfigureAwait(false);
            await _journal.SettleSiloAdmissionAsync(entry.CaptureId, entry.ManifestSha256, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (staged?.State != SiloStagedState.Visible && entry.State != "committed")
            return "silo-staged-evidence-missing";

        if (staged?.State != SiloStagedState.Visible)
        {
            var parsed = CaptureContractJson.ParseManifest(entry.ManifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
                CaptureContractJson.ComputeManifestSha256(entry.ManifestJson) != entry.ManifestSha256)
                return "silo-admission-evidence-refused";

            var draft = CreateSiloDraft(entry, manifest, revision);
            try
            {
                var root = PhysicalRoot.Open(_options.RawIngressRoot);
                await silo.Publisher.StageAsync(draft, (member, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var path = root.Resolve(member.Role == "raw" ? entry.PayloadRelativePath : entry.SidecarRelativePath);
                    return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (FileSystemFaultException exception) when (exception.Kind == FileSystemFaultKind.NotFound &&
                exception.Operation == "publish-write")
            {
                await MarkSiloEvidenceMissingAsync(entry, cancellationToken).ConfigureAwait(false);
                return "silo-staged-evidence-missing";
            }
            catch (SiloConflictException exception)
            {
                if (exception.Code is not ("silo.staged-payload-length" or "silo.staged-payload-identity"))
                    return "silo-admission-evidence-refused";

                await MarkSiloEvidenceMissingAsync(entry, cancellationToken).ConfigureAwait(false);
                return "silo-staged-evidence-missing";
            }
            catch (SiloSpoolFullException)
            {
                return "silo-publication-spool-full";
            }

            SiloPublicationOutcome outcome;
            lifecycle.Release();
            try
            {
                outcome = await silo.Publisher.PublishAsync(groupId, revision, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (outcome.State != SiloStagedState.Visible)
                return outcome.BlockedCode ?? "silo-admission-publication-pending";
        }

        var intent = new SiloRawIntent(entry.ArtifactId, entry.ExposureStartedUtc, Reclaim: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
        await AtomicPublisher.PublishAsync(silo.PhysicalState, SiloIntentPath(entry.ArtifactId), PublishMode.Replace,
            (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
        await ReclaimSiloStagingAsync(entry, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private Task MarkSiloEvidenceMissingAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken) =>
        _journal.MarkEvidenceFailureAsync(entry.CaptureId, "missing_evidence", "silo-staged-evidence-missing", cancellationToken);

    private async Task ReclaimSiloStagingAsync(RawIngressJournalEntry entry, CancellationToken cancellationToken)
    {
        var paths = _files.GetPaths(entry.ExposureStartedUtc, entry.ArtifactId);
        if (paths.PayloadRelativePath != entry.PayloadRelativePath || paths.SidecarRelativePath != entry.SidecarRelativePath)
            throw new InvalidDataException("The settled Silo admission has unexpected staging paths.");

        var root = PhysicalRoot.Open(_options.RawIngressRoot);
        root.Verify(paths.PayloadAbsolutePath, "silo-raw-reclaim");
        root.Verify(paths.SidecarAbsolutePath, "silo-raw-reclaim");
        var directory = Path.GetDirectoryName(paths.PayloadAbsolutePath)!;
        if (Directory.Exists(directory))
        {
            File.Delete(paths.PayloadAbsolutePath);
            File.Delete(paths.SidecarAbsolutePath);
            DurableSync.Directory(directory);
        }
        await _journal.SettleSiloAdmissionAsync(entry.CaptureId, entry.ManifestSha256, cancellationToken).ConfigureAwait(false);
        ReleaseSiloRawIntent(entry.ArtifactId);
    }

    private async ValueTask<ArtifactManifestV2> ReadSiloRetryManifestAsync(Guid artifactId, string payloadSha256, long length,
        bool admissionPending, CancellationToken cancellationToken)
    {
        var entry = await _journal.ReadSiloAdmissionByArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.EvidenceWrittenOff || entry.PayloadLength != length || entry.PayloadSha256 != payloadSha256 ||
            CaptureContractJson.ComputeManifestSha256(entry.ManifestJson) != entry.ManifestSha256 ||
            CaptureContractJson.ParseManifest(entry.ManifestJson).Document?.Manifest is not { } manifest)
            throw new RawIngressConflictException("The retry differs from the committed Silo capture evidence.");
        if (admissionPending &&
            (_siloArtifacts is null ||
             !await _siloArtifacts.HasMemberAsync(artifactId, entry.DescriptorSha256, cancellationToken).ConfigureAwait(false)))
            throw new RawIngressConflictException("The staged raw capture is missing its local pair and has not settled in Silo.");
        return manifest;
    }

    private async ValueTask RefreshSiloPressureAsync(CancellationToken cancellationToken)
    {
        await _acceptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var held = await _journal.ReadHeldTotalsAsync(cancellationToken).ConfigureAwait(false);
            _state.UpdateBaseHeldData(held.Count, held.Bytes, held.Oldest);
            CheckSiloPressure(held.Count, held.Bytes);
        }
        finally
        {
            _acceptGate.Release();
        }
    }

    private static SiloGroupDraft CreateSiloDraft(RawIngressJournalEntry entry, ArtifactManifestV2 manifest, string revision)
    {
        var artifact = entry.ArtifactId.ToString("D");
        var payloadSha = SiloCenteredViews.LowerHex(entry.PayloadSha256);
        var sidecarSha = SiloHash.Sha256(entry.ManifestJson);
        var descriptor = manifest.Descriptor;
        var capture = JsonSerializer.SerializeToElement(new { entry.AgentId, entry.CaptureId, entry.CaptureSequence, entry.DescriptorSha256 });
        return new("raw-capture", entry.CaptureId.ToString("N"), revision, entry.DurableIngressUtc.ToUniversalTime(),
            entry.ExposureStartedUtc.ToUniversalTime(), descriptor.Timing.ExposureEndedUtc.ToUniversalTime(),
            [
                new(artifact, entry.DescriptorSha256, "raw", SiloRetentionClass.Full, descriptor.Artifact.MediaType, entry.PayloadLength,
                    payloadSha, true, SiloFilePayloadSpool.TokenOf(payloadSha)),
                new(artifact, SiloArtifactStorage.RawEvidenceRevision, "raw-evidence", SiloRetentionClass.Metadata, "application/json", entry.ManifestJson.LongLength,
                    sidecarSha, true, SiloFilePayloadSpool.TokenOf(sidecarSha))
            ], [], new([], Capture: capture), CameraId: descriptor.Capture.RigId,
            Capture: new(entry.AgentId, entry.CaptureSequence));
    }
}
