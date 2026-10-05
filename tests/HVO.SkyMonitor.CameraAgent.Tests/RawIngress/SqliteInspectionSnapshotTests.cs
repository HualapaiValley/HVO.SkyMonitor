using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class SqliteInspectionSnapshotTests
{
    [TestMethod]
    public async Task InspectAsync_ConcurrentWalCommitKeepsOneReadOnlyImage()
    {
        var root = Directory.CreateTempSubdirectory("hvo-inspection-test-");
        try
        {
            var path = Path.Combine(root.FullName, "test.db");
            using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
            await writer.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(writer, "PRAGMA journal_mode=WAL; CREATE TABLE evidence(value INTEGER); INSERT INTO evidence VALUES(1);").ConfigureAwait(false);
            var checks = 0;
            var result = await SqliteInspectionSnapshot.InspectAsync(
                path, 1, () => checks++,
                async (reader, token) =>
                {
                    Assert.AreEqual(1L, await ScalarAsync(reader, "SELECT value FROM evidence;", token).ConfigureAwait(false));
                    await ExecuteAsync(writer, "UPDATE evidence SET value=2;").ConfigureAwait(false);
                    Assert.AreEqual(1L, await ScalarAsync(reader, "SELECT value FROM evidence;", token).ConfigureAwait(false));
                    await Assert.ThrowsExactlyAsync<SqliteException>(() => ExecuteAsync(reader, "UPDATE evidence SET value=3;")).ConfigureAwait(false);
                    Assert.AreEqual(0L, await ScalarAsync(reader, "PRAGMA mmap_size;", token).ConfigureAwait(false));
                    Assert.AreEqual(-2048L, await ScalarAsync(reader, "PRAGMA cache_size;", token).ConfigureAwait(false));
                    return 1;
                }, null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(1, result);
            Assert.AreEqual(3, checks);
            Assert.AreEqual(2L, await ScalarAsync(writer, "SELECT value FROM evidence;", CancellationToken.None).ConfigureAwait(false));
            await ExecuteAsync(writer, "PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task InspectAsync_CancellationInterruptsNativeQueryAndReleasesReader()
    {
        var root = Directory.CreateTempSubdirectory("hvo-inspection-test-");
        try
        {
            var path = Path.Combine(root.FullName, "test.db");
            using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
            await writer.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(writer, "PRAGMA journal_mode=WAL; CREATE TABLE evidence(value INTEGER); INSERT INTO evidence VALUES(1);").ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await SqliteInspectionSnapshot.InspectAsync(
                    path, 1, () => { },
                    async (reader, token) =>
                    {
                        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                        return await ScalarAsync(reader, "WITH RECURSIVE n(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM n WHERE value<1000000000) SELECT sum(value) FROM n;", token).ConfigureAwait(false);
                    }, null, cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            using var checkpoint = writer.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var result = await checkpoint.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await result.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(0L, result.GetInt64(0), "The cancelled inspection must release its WAL read lock.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is fixed test input.")]
    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is fixed test input.")]
    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }
}
