using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class RawCaptureIngress
{
    internal bool SiloAdmissionsReady => Volatile.Read(ref _journalValidated);

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
        if (_silo is null || !Volatile.Read(ref _journalValidated)) return null;

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
            if (_state.GetBaseSnapshot().Reason == "silo-staging-pressure")
                _state.UpdateBaseStatus(RawIngressAvailability.Accepting, "accepting");
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
