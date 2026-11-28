using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

public sealed partial class ProtectedDeploymentLocationStoreTests
{
    [TestMethod]
    [DataRow("staging")]
    [DataRow("journal")]
    public async Task StartupDetectedRecentTamperCannotBeClearedByIncrementalQualification(string target)
    {
        var seed = CreateSeed(35, -113, 0, "America/Phoenix");
        string file;
        string capture;
        byte[] originalBytes;
        using (var initial = CreateStore())
        {
            var active = await initial.InitializeAsync(seed, CancellationToken.None).ConfigureAwait(false);
            (file, originalBytes, capture) = await WriteRound7CaptureAsync(active).ConfigureAwait(false);
            _ = await initial.FullVerifyAsync(seed, CancellationToken.None).ConfigureAwait(false);
        }
        var changed = ChangeRound7Manifest(originalBytes);
        if (target == "staging")
        {
            var timestamp = File.GetLastWriteTimeUtc(file);
            await File.WriteAllBytesAsync(file, changed).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(file, timestamp);
        }
        else
        {
            await EditJournalBytesAsync(capture, changed).ConfigureAwait(false);
        }
        using var restarted = CreateStore();
        _ = await restarted.LoadCurrentAsync(seed, false, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(restarted.RecentRetainedEvidenceReady, "The startup check must first detect the actual tamper.");
        try { _ = await restarted.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidDataException) { /* A full byte pass may refuse immediately. */ }
        Assert.IsFalse(restarted.RecentRetainedEvidenceReady, "Matching metadata cannot erase an observed recent failure.");
        Assert.IsTrue(restarted.RequiresRetainedIntegrityScrub);
        await Assert.ThrowsExactlyAsync<InstallationVerificationPendingException>(async () =>
            _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        if (target == "staging") await File.WriteAllBytesAsync(file, originalBytes).ConfigureAwait(false);
        else await EditJournalBytesAsync(capture, originalBytes).ConfigureAwait(false);
        _ = await restarted.ScrubCurrentAsync(seed, () => true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(restarted.RecentRetainedEvidenceReady, "A real full pass after restoring authentic bytes may recover readiness.");
        Assert.IsFalse(restarted.RequiresRetainedIntegrityScrub);
        _ = await restarted.VerifyInstallationAsync(seed, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EditJournalBytesAsync(string capture, byte[] json)
    {
        var path = Path.Combine(_options.Value.RawIngressRoot, "journal", "raw-ingress.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE raw_captures SET manifest_json=$json WHERE capture_id=$capture;";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$capture", capture);
        Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
    }

    private async Task<(string Path, byte[] Json, string Capture)> WriteRound7CaptureAsync(DeploymentLocationSnapshot active)
    {
        var (path, bytes) = await WriteSummaryCaptureAsync(active).ConfigureAwait(false);
        var manifest = CaptureContractJson.ParseManifest(bytes).Document!.Manifest;
        var staged = RawIngressFileStore.GetPaths(_options.Value.RawIngressRoot, manifest.Descriptor.Timing.ExposureStartedUtc,
            manifest.Descriptor.Artifact.ArtifactId);
        var committed = await CommitLocationEvidenceAsync(manifest, staged).ConfigureAwait(false);
        await RetainedFileMutations.WriteAllBytesAsync(path, committed.Json).ConfigureAwait(false);
        return (path, committed.Json, committed.Capture);
    }

    private async Task<(byte[] Json, string Capture)> CommitLocationEvidenceAsync(
        ArtifactManifestV2 manifest, RawIngressPaths? staged = null)
    {
        var descriptor = manifest.Descriptor;
        var root = _options.Value.RawIngressRoot;
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
        await journal.InitializeAsync([], CancellationToken.None).ConfigureAwait(false);
        var identity = await journal.ReserveIdentityAsync(descriptor.Capture.AgentId, descriptor.Capture.CaptureId,
            descriptor.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        descriptor = descriptor with { Capture = descriptor.Capture with { CaptureSequence = identity.CaptureSequence } };
        var payloadRef = staged?.PayloadRelativePath ?? SiloArtifactStorage.RawReference(identity.ArtifactId, ".bin");
        var bytes = CaptureContractJson.Serialize(manifest with { Descriptor = descriptor, RelativeArtifactPath = payloadRef });
        _ = await journal.CommitAsync(new RawIngressJournalEntry(descriptor.Capture.AgentId, descriptor.Capture.CaptureSequence,
            descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor),
            CaptureContractJson.ComputeManifestSha256(bytes), descriptor.Artifact.ChecksumSha256, descriptor.Layout.ByteLength,
            payloadRef, staged?.SidecarRelativePath ?? SiloArtifactStorage.RawReference(identity.ArtifactId, ".json"), bytes,
            descriptor.Timing.ExposureStartedUtc, descriptor.Timing.DurableIngressUtc), null, null, [], CancellationToken.None).ConfigureAwait(false);
        return (bytes, descriptor.Capture.CaptureId.ToString("N"));
    }

    private static byte[] ChangeRound7Manifest(byte[] original)
    {
        var text = Encoding.UTF8.GetString(original);
        var changed = text.Replace("\"version\":1", "\"version\":9", StringComparison.Ordinal);
        Assert.AreNotEqual(text, changed);
        var bytes = Encoding.UTF8.GetBytes(changed);
        Assert.AreEqual(original.Length, bytes.Length);
        return bytes;
    }
}
