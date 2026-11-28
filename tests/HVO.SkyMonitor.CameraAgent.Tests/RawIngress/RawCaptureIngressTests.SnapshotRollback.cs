using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    /// <summary>A durable Silo admission intent can restore a staged capture absent from a journal checkpoint.</summary>
    [TestMethod]
    public async Task InitializeAsync_RecoversPendingSiloAdmissionMissingFromJournalCheckpoint()
    {
        var root = CreateRoot();
        var snapshot = Path.Combine(Path.GetTempPath(), $"hvo-raw-snapshot-{Guid.NewGuid():N}.db");
        try
        {
            var journal = Path.Combine(root, "journal", "raw-ingress.db");
            using var silo = SiloInfrastructureFixture.OpenStore(root);
            using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System), siloProvider: silo))
            {
                _ = await ingress.AcceptAsync(
                    CreateConfiguration(), CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false);
                await SiloInfrastructureFixture.SettleRawAdmissionsAsync(ingress, CancellationToken.None).ConfigureAwait(false);
                // Take a checkpoint after the first admitted capture has settled in Silo.
                using (var source = new SqliteConnection($"Data Source={journal};Pooling=False"))
                using (var copy = new SqliteConnection($"Data Source={snapshot};Pooling=False"))
                {
                    await source.OpenAsync().ConfigureAwait(false);
                    await copy.OpenAsync().ConfigureAwait(false);
                    source.BackupDatabase(copy);
                }
            }
            var configuration = CreateConfiguration();
            var submission = CreateSubmission(Timestamp(6), [6, 6, 6, 6]);
            var artifactId = RawCaptureDescriptorFactory.CreateStableIds(configuration, submission).ArtifactId;
            var paths = RawIngressFileStore.GetPaths(root, Timestamp(6), artifactId);
            using (var interrupted = CreateIngress(root, new RawIngressState(TimeProvider.System),
                new OneShotFaultInjector(RawIngressFaultPoint.AfterJournalCommit), siloProvider: silo))
            {
                await Assert.ThrowsExactlyAsync<InjectedRawIngressFaultException>(async () =>
                    await interrupted.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
            var later = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(paths.SidecarAbsolutePath)
                .ConfigureAwait(false)).Document!.Manifest!;
            long laterSequence;
            using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                laterSequence = await ScalarLongAsync(connection,
                    $"SELECT capture_sequence FROM raw_captures WHERE raw_artifact_id = '{later.Descriptor.Artifact.ArtifactId:N}';").ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();

            // Restore only the journal checkpoint while the second capture is still owned by its Silo staging intent.
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
            Assert.IsTrue(File.Exists(paths.PayloadAbsolutePath), "A rollback never removes a frame.");

            var state = new RawIngressState(TimeProvider.System);
            using (var restarted = CreateIngress(root, state, siloProvider: silo))
            {
                await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
                Assert.AreEqual(0L, state.Snapshot.QuarantineCount);
                using (var connection = await OpenJournalAsync(root).ConfigureAwait(false))
                {
                    var recovered = $"raw_artifact_id = '{later.Descriptor.Artifact.ArtifactId:N}'";
                    Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
                    Assert.AreEqual(laterSequence, await ScalarLongAsync(connection,
                        $"SELECT capture_sequence FROM raw_captures WHERE {recovered} AND state = 'committed' AND retention_hold = 1;").ConfigureAwait(false));
                    // Recorded again, it is queued for its capture lanes again too.
                    Assert.IsGreaterThan(0L, await ScalarLongAsync(connection,
                        $"SELECT COUNT(*) FROM capture_lane_work WHERE raw_capture_row_id = (SELECT raw_capture_row_id FROM raw_captures WHERE {recovered});").ConfigureAwait(false));
                }
                await SiloInfrastructureFixture.SettleRawAdmissionsAsync(restarted, CancellationToken.None).ConfigureAwait(false);
                var storage = silo.GetRequiredService<SiloArtifactStorage>();
                CollectionAssert.AreEqual(new byte[] { 6, 6, 6, 6 },
                    await storage.ReadRawAsync(later.Descriptor, CancellationToken.None).ConfigureAwait(false));
                Assert.IsFalse(File.Exists(paths.PayloadAbsolutePath), "Settled Silo staging is reclaimed.");

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
