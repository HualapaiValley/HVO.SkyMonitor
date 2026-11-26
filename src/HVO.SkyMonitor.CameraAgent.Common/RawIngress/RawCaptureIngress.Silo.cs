using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class RawCaptureIngress
{
    internal bool SiloAdmissionsReady => Volatile.Read(ref _initialized);

    private const string SiloIntentsDirectory = "raw-admission-intents";
    private long _refusedSiloStagingBytes;

    private sealed record SiloRawIntent(Guid ArtifactId, DateTimeOffset ExposureStartedUtc);

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
            intents.Add((intent.ArtifactId, _files.GetPaths(intent.ExposureStartedUtc, intent.ArtifactId)));
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
            Interlocked.Exchange(ref _refusedSiloStagingBytes, bytes);
            _state.UpdateBaseStatus(RawIngressAvailability.Degraded, "silo-staging-pressure");
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<RawIngressRetentionHold>> ReadRetentionHoldsAsync(CancellationToken cancellationToken)
    {
        var holds = await _journal.ReadRetentionHoldsAsync(cancellationToken).ConfigureAwait(false);
        return _silo is null ? holds : [.. holds, .. await _journal.ReadSiloAdmissionHoldsAsync(cancellationToken).ConfigureAwait(false)];
    }

    internal async ValueTask<string?> PublishSiloAdmissionsAsync(int limit, CancellationToken cancellationToken)
    {
        if (_silo is null || !SiloAdmissionsReady) return null;

        var entries = await _journal.ReadPendingSiloAdmissionsAsync(limit, cancellationToken).ConfigureAwait(false);
        var root = PhysicalRoot.Open(_options.RawIngressRoot);
        foreach (var entry in entries)
        {
            if (entry.State != "committed") return "silo-admission-evidence-refused";
            var parsed = CaptureContractJson.ParseManifest(entry.ManifestJson);
            if (!parsed.IsValid || parsed.Document?.Manifest is not { } manifest ||
                CaptureContractJson.ComputeManifestSha256(entry.ManifestJson) != entry.ManifestSha256)
                return "silo-admission-evidence-refused";

            var draft = CreateSiloDraft(entry, manifest);
            await _silo.Publisher.StageAsync(draft, (member, token) =>
            {
                token.ThrowIfCancellationRequested();
                var path = root.Resolve(member.Role == "raw" ? entry.PayloadRelativePath : entry.SidecarRelativePath);
                root.Verify(path, "silo-admission-read");
                return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
            }, cancellationToken).ConfigureAwait(false);
            var outcome = await _silo.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, cancellationToken).ConfigureAwait(false);
            if (outcome.State != SiloStagedState.Visible || outcome.CatalogState != "visible")
                return outcome.BlockedCode ?? "silo-admission-publication-pending";
            await _journal.SettleSiloAdmissionAsync(entry.CaptureId, entry.ManifestSha256, cancellationToken).ConfigureAwait(false);
            var held = await _journal.ReadHeldTotalsAsync(cancellationToken).ConfigureAwait(false);
            _state.UpdateBaseHeldData(held.Count, held.Bytes, held.Oldest);
            var refused = Interlocked.Read(ref _refusedSiloStagingBytes);
            if (refused > 0 && held.Count < _siloSettings!.MaximumStagedFiles / 2 && refused <= _siloSettings.MaximumStagedBytes - held.Bytes &&
                Interlocked.CompareExchange(ref _refusedSiloStagingBytes, 0, refused) == refused &&
                _state.GetBaseSnapshot().Reason == "silo-staging-pressure")
                ReportPressure(underPressure: false);
        }
        return null;
    }

    private static SiloGroupDraft CreateSiloDraft(RawIngressJournalEntry entry, ArtifactManifestV2 manifest)
    {
        var artifact = entry.ArtifactId.ToString("D");
        var payloadSha = SiloCenteredViews.LowerHex(entry.PayloadSha256);
        var sidecarSha = SiloHash.Sha256(entry.ManifestJson);
        var descriptor = manifest.Descriptor;
        var capture = JsonSerializer.SerializeToElement(new { entry.AgentId, entry.CaptureId, entry.CaptureSequence, entry.DescriptorSha256 });
        return new("raw-capture", entry.CaptureId.ToString("N"), "raw-1", entry.DurableIngressUtc.ToUniversalTime(),
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
