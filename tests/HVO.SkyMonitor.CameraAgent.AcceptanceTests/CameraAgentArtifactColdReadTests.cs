using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SupportedOSPlatform("linux")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentArtifactColdReadTests
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc.so.6")]
    private static extern int posix_fadvise(int fd, long offset, long length, int advice);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int mincore(nint address, nuint length, [Out] byte[] vector);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int munmap(nint address, nuint length);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DisposableExt4FileScopedColdManifestReads()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("HVO_ISSUE1058_COLD") != "1")
            Assert.Inconclusive("Opt in with HVO_ISSUE1058_COLD=1 on isolated disposable ext4 scratch.");
        using var fixture = await GalleryPerformanceFixture.CreateAsync(100, sceneBearingOutputs: true,
            tenSecondCadence: true).ConfigureAwait(false);
        var stat = new ProcessStartInfo("stat", "-f -c %T -- " + fixture.Root)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using (var filesystem = Process.Start(stat)!)
        {
            var type = await filesystem.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await filesystem.WaitForExitAsync().ConfigureAwait(false);
            Assert.AreEqual(0, filesystem.ExitCode);
            Assert.AreEqual("ext2/ext3", type.Trim(), "Cold qualification requires disposable ext4.");
        }
        using (var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await writer.OpenAsync().ConfigureAwait(false);
            using var checkpoint = writer.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var rows = await checkpoint.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await rows.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(0L, rows.GetInt64(0));
        }
        var root = Path.Combine(fixture.Root, "shadow");
        await new HourlyArtifactFactsPrototype(root, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var indexPath = Path.Combine(root, "mutable.db");
        foreach (var database in new[] { fixture.DatabasePath, indexPath })
            Assert.IsFalse(File.Exists(database + "-wal") && new FileInfo(database + "-wal").Length > 0,
                "A nonempty SQLite WAL prevents a cold main-file comparison.");
        var sourceTimes = new List<double>();
        var shadowTimes = new List<double>();
        var sourceResidentAfter = 0;
        var indexResidentAfter = 0;
        var shadowResidentAfter = 0;
        for (var i = 0; i < 35; i++)
        {
            var sequence = i * 37 % 100 + 1;
            string factPath;
            using (var index = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
            {
                await index.OpenAsync().ConfigureAwait(false);
                using var pathQuery = index.CreateCommand();
                pathQuery.CommandText = "SELECT fact_path FROM capture_lookup WHERE sequence=$sequence;";
                pathQuery.Parameters.AddWithValue("$sequence", sequence);
                factPath = Path.GetFullPath(Path.Combine(root, (string)(await pathQuery.ExecuteScalarAsync()
                    .ConfigureAwait(false))!));
            }
            Assert.IsTrue(factPath.StartsWith(Path.Combine(root, "hours") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
            var sourceFirst = i % 2 == 0;
            byte[] sourceBytes = [];
            byte[] shadowBytes = [];
            for (var pass = 0; pass < 2; pass++)
            {
                if (sourceFirst == (pass == 0))
                {
                    Assert.AreEqual(0, EvictAndCount(fixture.DatabasePath));
                    var started = Stopwatch.GetTimestamp();
                    using var journal = new SqliteConnection($"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False");
                    await journal.OpenAsync().ConfigureAwait(false);
                    using var query = journal.CreateCommand();
                    query.CommandText = "SELECT manifest_json,manifest_sha256 FROM raw_captures WHERE capture_sequence=$sequence;";
                    query.Parameters.AddWithValue("$sequence", sequence);
                    using var row = await query.ExecuteReaderAsync().ConfigureAwait(false);
                    Assert.IsTrue(await row.ReadAsync().ConfigureAwait(false));
                    sourceBytes = (byte[])row.GetValue(0);
                    var hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
                    sourceTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.AreEqual(row.GetString(1), hash);
                    sourceResidentAfter += CountResident(fixture.DatabasePath);
                }
                else
                {
                    Assert.AreEqual(0, EvictAndCount(indexPath));
                    Assert.AreEqual(0, EvictAndCount(factPath));
                    var started = Stopwatch.GetTimestamp();
                    using var index = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
                    await index.OpenAsync().ConfigureAwait(false);
                    using var query = index.CreateCommand();
                    query.CommandText = "SELECT fact_path,fact_sha256 FROM capture_lookup WHERE sequence=$sequence;";
                    query.Parameters.AddWithValue("$sequence", sequence);
                    using var row = await query.ExecuteReaderAsync().ConfigureAwait(false);
                    Assert.IsTrue(await row.ReadAsync().ConfigureAwait(false));
                    Assert.AreEqual(factPath, Path.GetFullPath(Path.Combine(root, row.GetString(0))));
                    var factBytes = await File.ReadAllBytesAsync(factPath).ConfigureAwait(false);
                    var factHash = Convert.ToHexString(SHA256.HashData(factBytes));
                    using var fact = JsonDocument.Parse(factBytes);
                    shadowBytes = fact.RootElement.GetProperty("Manifest").GetBytesFromBase64();
                    shadowTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.AreEqual(row.GetString(1), factHash);
                    indexResidentAfter += CountResident(indexPath);
                    shadowResidentAfter += CountResident(factPath);
                }
            }
            CollectionAssert.AreEqual(sourceBytes, shadowBytes);
        }
        sourceTimes.Sort();
        shadowTimes.Sort();
        TestContext.WriteLine($"35 paired file-page-cold reads (pre-read zero-resident main DB/index/fact pages) " +
            $"on disposable ext4; " +
            $"source median/p95 {sourceTimes[17]:F3}/{sourceTimes[33]:F3} ms; " +
            $"shadow {shadowTimes[17]:F3}/{shadowTimes[33]:F3} ms; order alternated, " +
            $"post-read source/index/fact resident-page sums " +
            $"{sourceResidentAfter}/{indexResidentAfter}/{shadowResidentAfter}; " +
            "fact path is looked up before timing; metadata/controller cache and full app cold start uncontrolled.");
    }

    private static int EvictAndCount(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var length = RandomAccess.GetLength(file);
        if (length is <= 0 or > 2_000_000_000)
            throw new InvalidDataException("Cold probe requires a bounded nonempty disposable file.");
        var fd = checked((int)file.DangerousGetHandle());
        RandomAccess.FlushToDisk(file);
        var error = posix_fadvise(fd, 0, 0, 4);
        if (error != 0) throw new IOException($"File-scoped DONTNEED failed: {error}");
        return CountResident(file, length);
    }

    private static int CountResident(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return CountResident(file, RandomAccess.GetLength(file));
    }

    private static int CountResident(Microsoft.Win32.SafeHandles.SafeFileHandle file, long length)
    {
        var fd = checked((int)file.DangerousGetHandle());
        var address = mmap(0, checked((nuint)length), 1, 1, fd, 0);
        if (address == -1) throw new IOException($"mmap failed: {Marshal.GetLastPInvokeError()}");
        try
        {
            var pages = checked((int)((length + Environment.SystemPageSize - 1) / Environment.SystemPageSize));
            var vector = new byte[pages];
            if (mincore(address, checked((nuint)length), vector) != 0)
                throw new IOException($"mincore failed: {Marshal.GetLastPInvokeError()}");
            return vector.Count(static value => (value & 1) != 0);
        }
        finally
        {
            _ = munmap(address, checked((nuint)length));
        }
    }
}
