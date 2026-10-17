using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    /// <summary>
    /// Raw-ingress reconciliation quarantines committed evidence that no longer matches its record, which a snapshot
    /// restore would bring back. Under the installer's durable pause it finds the mismatch but moves nothing, at the
    /// first initialization and at a re-initialization alike, and quarantines it once capture admission opens (#1314 R10).
    /// </summary>
    [TestMethod]
    public async Task InitializeAsync_WhileCaptureIsPaused_MovesNoEvidenceAndQuarantinesItOnceCaptureRuns()
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        RawCaptureReceipt accepted;
        using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            accepted = (await original.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
        }
        SqliteConnection.ClearAllPools();
        var payload = accepted.StoredFrame.AbsolutePath;
        await File.WriteAllBytesAsync(payload, [6, 6, 6, 6]).ConfigureAwait(false);
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        var state = new RawIngressState(TimeProvider.System);
        using var restarted = CreateIngress(root, state, maintenanceGate: hold.Gate);

        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(File.Exists(payload), "A paused first initialization moved committed evidence.");
        Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
        restarted.InvalidateEvidence();
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(payload), "A paused re-initialization moved committed evidence.");

        // Once capture opens, the deferred quarantine runs, and raw ingress reports the mismatched evidence exactly as an
        // unpaused start would.
        await admission.ResumeAsync("raw-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsFalse(File.Exists(payload), "The deferred quarantine never ran.");
        Assert.HasCount(1, Directory.EnumerateFiles(Path.Combine(root, "quarantine"), Path.GetFileName(payload), SearchOption.AllDirectories).ToArray());
        using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            $"SELECT COUNT(*) FROM raw_captures WHERE raw_artifact_id = '{accepted.Manifest.Descriptor.Artifact.ArtifactId:N}' AND state = 'quarantined';").ConfigureAwait(false));
    }
}
