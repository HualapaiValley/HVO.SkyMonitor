using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// The catalog file is replaced only after the real target, including committed pages still in its WAL, is proven to
/// be this instance's own supported catalog, and the replacement is staged so any interruption is finished or
/// discarded deterministically (#1965 4.6, 4.7; #1967 acceptance 5).
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloCatalogFileTests
{
    private const string ForeignSql = "CREATE TABLE someone_elses(x);";
    private const string WalSql = "PRAGMA journal_mode = WAL;";
    private const string NoCheckpointSql = "PRAGMA wal_autocheckpoint = 0;";
    private const string NewerInWalSql =
        "CREATE TABLE future(x); PRAGMA application_id = 1213416265; PRAGMA user_version = 2; INSERT INTO future VALUES(1);";

    private static string Target(SiloHarness silo) => Path.Combine(silo.RootPath, SiloStateRoot.IndexDatabaseFileName);

    private static IEnumerable<string> Residue(SiloHarness silo) => Directory.EnumerateFiles(silo.RootPath)
        .Select(Path.GetFileName).OfType<string>()
        .Where(static name => name.StartsWith(SiloStateRoot.IndexDatabaseFileName + ".", StringComparison.Ordinal));

    private static string Fingerprint(string path) => string.Join('|', new[] { string.Empty, "-wal" }
        .Select(suffix => path + suffix)
        .Select(file => File.Exists(file) ? SiloHash.Sha256(File.ReadAllBytes(file)) : "absent"));

    private static void DeleteTarget(string target)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            File.Delete(target + suffix);
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Constant fixture statements only.")]
    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method", Justification = "Synchronous fixture setup.")]
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A writer that crashed with committed pages still in its WAL: the main file's header is not the truth.</summary>
    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method", Justification = "Synchronous fixture setup.")]
    private static void CreateNewerCatalogWithCommittedWal(string target)
    {
        var snapshot = target + ".snapshot";
        using (var connection = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            connection.Open();
            Execute(connection, WalSql);
            Execute(connection, NoCheckpointSql);
            Execute(connection, NewerInWalSql);
            File.Copy(target, snapshot, overwrite: true);
            File.Copy(target + "-wal", snapshot + "-wal", overwrite: true);
        }
        DeleteTarget(target);
        File.Move(snapshot, target);
        File.Move(snapshot + "-wal", target + "-wal");
    }

    [TestMethod]
    [DataRow("unversioned-foreign")]
    [DataRow("other-binding")]
    [DataRow("newer-in-wal")]
    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method", Justification = "Synchronous fixture setup.")]
    public async Task RebuildRefusesAndPreservesAForeignOrNewerCatalogIncludingItsWal(string kind)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        silo.Close();
        var target = Target(silo);
        DeleteTarget(target);
        switch (kind)
        {
            case "unversioned-foreign":
                using (var connection = new SqliteConnection($"Data Source={target};Pooling=False"))
                {
                    connection.Open();
                    Execute(connection, ForeignSql);
                }
                break;
            case "other-binding":
                using (var index = new SiloCatalogIndex(target, new SiloStorageBinding("another-instance", "local-silo", "instance-bucket"), silo.Clock))
                {
                    await index.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                }
                break;
            default:
                CreateNewerCatalogWithCommittedWal(target);
                break;
        }
        var before = Fingerprint(target);
        using var root = SiloStateRoot.Acquire(silo.RootPath);

        var rebuild = () => SiloCatalog.RebuildAsync(root, silo.Binding, new SiloStoreSet(silo.Local, silo.Archive), silo.Authority,
            silo.Authority, silo.Clock, new(silo.Local), CancellationToken.None).AsTask();

        await rebuild.Should().ThrowAsync<Exception>().Where(exception => exception is SiloFormatRefusedException || exception is SiloConflictException)
            .ConfigureAwait(false);
        Fingerprint(target).Should().Be(before, "a refused catalog and its committed WAL are left byte for byte unchanged");
    }

    [TestMethod]
    public async Task AnInterruptedRebuildLeavesNoResidueAfterTheNextRebuild()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        var residue = Target(silo) + ".building";
        await File.WriteAllBytesAsync(residue, "partial"u8.ToArray()).ConfigureAwait(false);
        await File.WriteAllBytesAsync(residue + "-wal", "partial"u8.ToArray()).ConfigureAwait(false);

        await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);

        Residue(silo).Should().BeEmpty();
        (await silo.Index.ReadPublicationAsync(SiloDrafts.CaptureGroup(1), "r1", CancellationToken.None).ConfigureAwait(false))!
            .State.Should().Be("visible");
    }

    [TestMethod]
    [DataRow(nameof(SiloFaultPoint.AfterReplacementReady))]
    [DataRow(nameof(SiloFaultPoint.AfterTargetRetired))]
    public async Task AnInterruptedReplacementIsFinishedBeforeTheCatalogIsNextOpened(string point)
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 2)).ConfigureAwait(false);
        var before = await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false);
        var faults = new SiloTestFaults { Armed = Enum.Parse<SiloFaultPoint>(point) };

        var crash = () => silo.RebuildAsync(null, faults);
        await crash.Should().ThrowAsync<InvalidOperationException>().WithMessage("simulated crash*").ConfigureAwait(false);
        File.Exists(Target(silo) + SiloCatalogFiles.ReplacementSuffix).Should().BeTrue("a complete replacement was staged before the crash");

        await silo.RestartAsync().ConfigureAwait(false);
        await silo.Catalog.VerifyRolesAsync(100, CancellationToken.None).ConfigureAwait(false);

        Residue(silo).Should().BeEmpty();
        (await silo.Index.DescribeAsync(CancellationToken.None).ConfigureAwait(false)).Should().Be(before);
    }
}
