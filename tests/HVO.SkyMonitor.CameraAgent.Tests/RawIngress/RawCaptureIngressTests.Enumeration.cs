using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task EnumerateAsync_ClosesEachReaderAndExcludesNewCaptures()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            for (var index = 0; index < 3; index++)
                await ingress.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(index), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false);
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            var entries = journal.EnumerateAsync(CancellationToken.None).GetAsyncEnumerator();
            await using var enumeratorScope = entries.ConfigureAwait(false);
            Assert.IsTrue(await entries.MoveNextAsync().ConfigureAwait(false));
            var seen = new HashSet<Guid> { entries.Current.CaptureId };
            var added = await ingress.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(4), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(added);
            using (var writer = await OpenJournalAsync(root).ConfigureAwait(false))
            {
                using var command = writer.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(0L, reader.GetInt64(0), "The iterator must not hold a read transaction while the caller validates files.");
            }
            while (await entries.MoveNextAsync().ConfigureAwait(false))
                Assert.IsTrue(seen.Add(entries.Current.CaptureId));
            Assert.HasCount(3, seen);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task EnumerateAsync_CancelledBetweenRowsStopsWithoutChangingEvidence()
    {
        var root = CreateRoot();
        try
        {
            using var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System));
            await ingress.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(1), [1, 2, 3, 4]), CancellationToken.None).ConfigureAwait(false);
            await ingress.AcceptAsync(CreateConfiguration(), CreateSubmission(Timestamp(2), [4, 3, 2, 1]), CancellationToken.None).ConfigureAwait(false);
            var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
            using var cancellation = new CancellationTokenSource();
            var entries = journal.EnumerateAsync(cancellation.Token).GetAsyncEnumerator();
            await using var enumeratorScope = entries.ConfigureAwait(false);
            Assert.IsTrue(await entries.MoveNextAsync().ConfigureAwait(false));
            await cancellation.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await entries.MoveNextAsync().ConfigureAwait(false)).ConfigureAwait(false);
            var count = 0;
            await foreach (var entry in journal.EnumerateAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Assert.AreEqual("committed", entry.State);
                count++;
            }
            Assert.AreEqual(2, count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
