using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    /// <summary>
    /// What an installer snapshot rollback (#1314) leaves for raw frames: the journal goes back to the snapshot while
    /// every frame written since stays on disk. The next start records each frame it can verify again, with its own
    /// capture sequence, and never hands that sequence out a second time.
    /// </summary>
    [TestMethod]
    public async Task InitializeAsync_AfterASnapshotRollback_RecordsFramesWrittenSinceTheSnapshotAgainWithTheirOwnSequences()
    {
        var root = CreateRoot();
        var snapshot = Path.Combine(Path.GetTempPath(), $"hvo-raw-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            var journal = Path.Combine(root, "journal", "raw-ingress.db");
            RawCaptureReceipt later;
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
            {
                _ = await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false);
                // The installer's snapshot, taken with the SQLite backup API.
                using (var source = new SqliteConnection($"Data Source={journal};Pooling=False"))
                using (var copy = new SqliteConnection($"Data Source={snapshot};Pooling=False"))
                {
                    await source.OpenAsync().ConfigureAwait(false);
                    await copy.OpenAsync().ConfigureAwait(false);
                    source.BackupDatabase(copy);
                }
                later = (await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(6), [6, 6, 6, 6]), CancellationToken.None).ConfigureAwait(false))!;
            }
            long laterSequence;
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                laterSequence = await ScalarLongAsync(connection,
                    $"SELECT capture_sequence FROM raw_captures WHERE raw_artifact_id = '{later.Manifest.Descriptor.Artifact.ArtifactId:N}';").ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();

            // The rollback: the journal is replaced by the snapshot and its sidecars removed, the files are left alone,
            // and the installer carries the capture-sequence counter forward to the highest value it ever reached.
            foreach (var suffix in DatabaseSuffixes) File.Delete(journal + suffix);
            File.Copy(snapshot, journal);
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                using var floor = connection.CreateCommand();
                floor.CommandText = "UPDATE raw_capture_sequences SET last_sequence = MAX(last_sequence, $floor);";
                floor.Parameters.AddWithValue("$floor", laterSequence);
                Assert.AreEqual(1, await floor.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            SqliteConnection.ClearAllPools();
            Assert.IsTrue(File.Exists(later.StoredFrame.AbsolutePath), "A rollback never removes a frame.");

            var state = new RawIngressState(TimeProvider.System);
            using (var restarted = CreateIngress(root, state))
            {
                await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
                Assert.AreEqual(0L, state.Snapshot.QuarantineCount);
                using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
                {
                    var recovered = $"raw_artifact_id = '{later.Manifest.Descriptor.Artifact.ArtifactId:N}'";
                    Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                    Assert.AreEqual(laterSequence, await ScalarLongAsync(connection,
                        $"SELECT capture_sequence FROM raw_captures WHERE {recovered} AND state = 'committed' AND retention_hold = 1;").ConfigureAwait(false));
                    // Recorded again, it is queued for its capture lanes again too.
                    Assert.IsGreaterThan(0L, await ScalarLongAsync(connection,
                        $"SELECT COUNT(*) FROM capture_lane_work WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE {recovered});").ConfigureAwait(false));
                }
                var index = await File.ReadAllTextAsync(Path.Combine(root, "index", "frames_2026-07-14.jsonl")).ConfigureAwait(false);
                Assert.AreEqual(1, index.Split(later.Manifest.Descriptor.Artifact.ArtifactId.ToString()).Length - 1,
                    "The compatibility index names the recovered frame exactly once.");

                var next = (await restarted.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(7), [7, 7, 7, 7]), CancellationToken.None).ConfigureAwait(false))!;
                using var verify = await OpenJournalAsync(root).ConfigureAwait(false);
                Assert.IsGreaterThan(laterSequence, await ScalarLongAsync(verify,
                    $"SELECT capture_sequence FROM raw_captures WHERE raw_artifact_id = '{next.Manifest.Descriptor.Artifact.ArtifactId:N}';").ConfigureAwait(false));
            }
        }
        finally
        {
            DeleteRoot(root);
            File.Delete(snapshot);
        }
    }
}
