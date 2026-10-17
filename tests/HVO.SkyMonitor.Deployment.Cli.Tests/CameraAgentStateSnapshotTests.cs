using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The small-database snapshot a migrating upgrade takes (#1314): which stores it covers, how it verifies a copy,
/// and how a restore puts every store back exactly.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentStateSnapshotTests
{
    private static readonly ImageInstallationIdentity Image = new(
        "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}", "amd64", null);

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CreateAsync_CorruptStore_IsRefusedRatherThanSnapshotted()
    {
        using var root = new SnapshotRoot();
        var store = CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, "raw/journal/raw-ingress.db");
        CreateDatabase(store, 400);
        // Overwrite part of a b-tree page in the middle of the file.
        await using (var stream = new FileStream(store, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(2 * 4096 + 100, SeekOrigin.Begin);
            stream.Write(new byte[512]);
        }

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentStateSnapshot.CreateAsync(
            root.Paths, root.InstanceId, Guid.NewGuid(), Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None));

        StringAssert.Contains(exception.Message, "raw/journal/raw-ingress.db", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_PutsEveryStoreBackExactlyAndIsRepeatable()
    {
        using var root = new SnapshotRoot();
        var raw = CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, "raw/journal/raw-ingress.db");
        var outbox = CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, "raw/outbox/artifact-outbox.db");
        CreateDatabase(raw, 25);
        var operationId = Guid.NewGuid();
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(root.Paths, root.InstanceId, operationId, Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);
        var expected = Rows(raw);
        // A candidate writes to the journal, creates a store the snapshot recorded as absent, and leaves sidecars.
        CreateDatabase(outbox, 3);
        await using (var connection = new SqliteConnection($"Data Source={raw};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO rows(value) VALUES('candidate');";
            await command.ExecuteNonQueryAsync();
        }
        await File.WriteAllTextAsync(raw + "-journal", "hot journal left by a crash");

        await CameraAgentStateSnapshot.RestoreAsync(root.Paths, root.InstanceId, snapshot, root.Uid, root.Gid, [], await CameraAgentStateSnapshot.PlanRestoreAsync(root.Paths, root.InstanceId, snapshot, [], CancellationToken.None), CancellationToken.None);
        var once = await SafeFileSystem.ComputeSha256Async(raw, CancellationToken.None);
        await CameraAgentStateSnapshot.RestoreAsync(root.Paths, root.InstanceId, snapshot, root.Uid, root.Gid, [], await CameraAgentStateSnapshot.PlanRestoreAsync(root.Paths, root.InstanceId, snapshot, [], CancellationToken.None), CancellationToken.None);

        CollectionAssert.AreEqual(expected, Rows(raw));
        Assert.AreEqual(once, await SafeFileSystem.ComputeSha256Async(raw, CancellationToken.None));
        Assert.IsFalse(File.Exists(raw + "-journal"));
        Assert.IsFalse(File.Exists(outbox));
        Assert.IsFalse(Directory.EnumerateFiles(Path.GetDirectoryName(raw)!, "*.tmp").Any());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task VerifyAsync_RejectsASnapshotRecordedForAnotherOperationOrInstance()
    {
        using var root = new SnapshotRoot();
        CreateDatabase(CameraAgentStateSnapshot.StorePath(root.Paths.StateRoot, "raw/journal/raw-ingress.db"), 5);
        var snapshot = await CameraAgentStateSnapshot.CreateAsync(root.Paths, root.InstanceId, Guid.NewGuid(), Image, CameraAgentStateLayout.SqliteStores, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentStateSnapshot.VerifyAsync(
            root.Paths, Guid.NewGuid(), snapshot, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentStateSnapshot.VerifyAsync(
            root.Paths, root.InstanceId, snapshot with { ManifestSha256 = new string('0', 64) }, CancellationToken.None));
    }

    private static void CreateDatabase(string path, int rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
        command.CommandText = "CREATE TABLE rows(id INTEGER PRIMARY KEY, value TEXT NOT NULL);" + string.Concat(
            Enumerable.Range(1, rows).Select(static index => $"INSERT INTO rows(value) VALUES('{new string('r', 200)}-{index}');"));
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static List<string> Rows(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM rows ORDER BY id;";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private sealed class SnapshotRoot : IDisposable
    {
        public SnapshotRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), $"hvo-snapshot-{Guid.NewGuid():N}");
            InstanceId = Guid.NewGuid();
            Paths = InstallationPaths.Create(Root, InstanceId, HygV42.CatalogId);
            foreach (var directory in new[] { Paths.InstanceRoot, Paths.StateRoot, Paths.OperationsRoot })
                SafeFileSystem.CreateOwnerDirectory(directory);
            SafeFileSystem.CreateOwnerDirectory(Path.Combine(Paths.OperationsRoot, "lifecycle"));
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public InstallationPaths Paths { get; }
        public uint Uid { get; } = NativeLinux.getuid();
        public uint Gid { get; } = NativeLinux.getgid();

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            SafeFileSystem.MakeTreeOwnerWritable(Root);
            Directory.Delete(Root, recursive: true);
        }
    }
}
