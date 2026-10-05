using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentArtifactFactsFaultTests
{
    private const string WorkerRootVariable = "HVO_ISSUE1059_WORKER_ROOT";
    private const string WorkerBoundaryVariable = "HVO_ISSUE1059_WORKER_BOUNDARY";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("after-staged-write")]
    [DataRow("after-file-sync")]
    [DataRow("before-rename")]
    [DataRow("after-rename-before-directory-sync")]
    [DataRow("after-rename")]
    [DataRow("after-capture-index-commit")]
    [DataRow("before-output-index-commit")]
    [DataRow("after-output-index-commit")]
    [DataRow("before-ready-commit")]
    [DataRow("after-ready-commit")]
    public async Task KilledProjectorReconcilesExactFactsAndLineage(string boundary)
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "fault-shadow");
        var baselineRoot = Path.Combine(fixture.Root, "baseline-shadow");
        await new HourlyArtifactFactsPrototype(baselineRoot, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var expected = HashFacts(baselineRoot);
        var sourceBefore = await SourceDigestAsync(fixture.DatabasePath).ConfigureAwait(false);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(CameraAgentArtifactFactsFaultTests).FullName + ".ProjectorCrashWorker");
        start.Environment[WorkerRootVariable] = fixture.Root;
        start.Environment[WorkerBoundaryVariable] = boundary;
        using var child = Process.Start(start);
        Assert.IsNotNull(child);
        var stdout = child.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var stderr = child.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        var signal = Path.Combine(fixture.Root, "fault-reached.json");
        var deadline = Stopwatch.StartNew();
        try
        {
            while (!File.Exists(signal) && !child.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(45))
                await Task.Delay(25, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(signal), $"Worker did not reach {boundary}; elapsed={deadline.Elapsed}.");
            using var reached = JsonDocument.Parse(await File.ReadAllBytesAsync(signal, TestContext.CancellationToken)
                .ConfigureAwait(false));
            Assert.AreEqual(boundary, reached.RootElement.GetProperty("Boundary").GetString());
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            TestContext.WriteLine("Interrupted worker: " + reached.RootElement.GetRawText());
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            TestContext.WriteLine(await stdout.ConfigureAwait(false));
            TestContext.WriteLine(await stderr.ConfigureAwait(false));
        }

        Assert.AreNotEqual(0, child.ExitCode, "A normally completed worker is not crash evidence.");
        var index = Path.Combine(root, "mutable.db");
        if (boundary != "after-ready-commit")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, index)).ConfigureAwait(false);
        else
            await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, index).ConfigureAwait(false);
        var beforeBytes = DirectoryBytes(root);
        using var process = Process.GetCurrentProcess();
        var rssBefore = process.WorkingSet64;
        var recovery = Stopwatch.StartNew();
        var visited = 0;
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath, point =>
        {
            if (point == "during-scan") visited++;
        });
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        recovery.Stop();
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, index).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, HashFacts(root));
        Assert.AreEqual(sourceBefore, await SourceDigestAsync(fixture.DatabasePath).ConfigureAwait(false));
        Assert.AreEqual(2L, projection.Measure().Captures);
        Assert.AreEqual(8L, projection.Measure().Outputs);
        Assert.AreEqual(0, Directory.GetFiles(root, "*.pending", SearchOption.AllDirectories).Length);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, HashFacts(root), "A second restart must not duplicate publication.");
        process.Refresh();
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            Boundary = boundary,
            RecoveryMilliseconds = recovery.Elapsed.TotalMilliseconds,
            RecoveryScanRows = visited / 2,
            RssBefore = rssBefore,
            RssAfter = process.WorkingSet64,
            ProcessLifetimePeakRss = process.PeakWorkingSet64,
            DiskBeforeRecovery = beforeBytes,
            DiskAfterRecovery = DirectoryBytes(root),
            FactCount = expected.Length,
            SourceDigest = sourceBefore,
            Scope = "Disposable offline projection; process kill, not power loss or installer pause"
        }));
    }

    [TestMethod]
    public async Task ProjectorCrashWorker()
    {
        var root = Environment.GetEnvironmentVariable(WorkerRootVariable);
        var boundary = Environment.GetEnvironmentVariable(WorkerBoundaryVariable);
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(boundary))
            Assert.Inconclusive("Only the parent crash matrix launches this disposable subprocess worker.");
        root = Path.GetFullPath(root);
        Assert.IsTrue(Path.GetFileName(root).StartsWith("hvo-gallery-performance-", StringComparison.Ordinal));
        Assert.AreEqual(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(root));
        var source = Path.Combine(root, "journal", "raw-ingress.db");
        Assert.IsTrue(File.Exists(source));
        await new HourlyArtifactFactsPrototype(Path.Combine(root, "fault-shadow"), source, point =>
        {
            if (point != boundary) return;
            using var process = Process.GetCurrentProcess();
            var signal = Path.Combine(root, "fault-reached.json");
            File.WriteAllText(signal + ".pending", JsonSerializer.Serialize(new
            {
                Boundary = point,
                ProcessId = Environment.ProcessId,
                Rss = process.WorkingSet64,
                ProcessLifetimePeakRss = process.PeakWorkingSet64,
                CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                RetainedBytesAtBoundary = DirectoryBytes(Path.Combine(root, "fault-shadow"))
            }));
            File.Move(signal + ".pending", signal);
            // The parent kills this entire subprocess tree. No exception unwinding or disposal runs.
            Thread.Sleep(TimeSpan.FromSeconds(90));
            throw new TimeoutException("Parent did not terminate the disposable crash worker.");
        }).ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.Fail("The requested fault point was not reached.");
    }

    [TestMethod]
    [DataRow("hours", "missing")]
    [DataRow("hours", "truncated")]
    [DataRow("hours", "changed")]
    [DataRow("products", "missing")]
    [DataRow("products", "truncated")]
    [DataRow("products", "changed")]
    public async Task DamagedIndexedFactFailsClosedWithoutRewritingEvidence(string tree, string damage)
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var path = Directory.GetFiles(Path.Combine(root, tree), "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First();
        var original = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        if (damage == "missing") File.Delete(path);
        else
        {
            var corrupted = damage == "truncated" ? original[..(original.Length / 2)] : (byte[])original.Clone();
            if (damage == "changed") corrupted[corrupted.Length / 2] ^= 1;
            await File.WriteAllBytesAsync(path, corrupted).ConfigureAwait(false);
        }
        var damaged = HashFacts(root);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, Path.Combine(root, "mutable.db")))
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => projection.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        CollectionAssert.AreEqual(damaged, HashFacts(root), "Fail-closed detection must preserve damaged evidence.");
        await File.WriteAllBytesAsync(path, original).ConfigureAwait(false);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, Path.Combine(root, "mutable.db"))
            .ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(12)]
    public async Task UnchangedRecoveryStillScansAllRetainedHistory(int captureCount)
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(captureCount, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        await new HourlyArtifactFactsPrototype(root, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var expected = HashFacts(root);
        var rows = 0;
        var elapsed = Stopwatch.StartNew();
        await new HourlyArtifactFactsPrototype(root, fixture.DatabasePath, point =>
        {
            if (point == "during-scan") rows++;
        }).ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        elapsed.Stop();
        Assert.AreEqual(captureCount, rows, "This evidence pins the current full-scan limitation; it is not a bounded recovery guarantee.");
        CollectionAssert.AreEqual(expected, HashFacts(root));
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            RetainedCaptures = captureCount,
            RecoveryScannedRows = rows,
            RecoveryMilliseconds = elapsed.Elapsed.TotalMilliseconds,
            RetainedBytes = DirectoryBytes(root),
            BoundedHistoryRecoveryQualified = false
        }));
    }

    [TestMethod]
    public async Task ConcurrentHoldAndRetentionUpdatesInvalidateScanThenReconcileWithoutFactRewrite()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(3, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var original = HashFacts(root);
        var changed = false;
        var concurrent = new HourlyArtifactFactsPrototype(root, fixture.DatabasePath, point =>
        {
            if (point != "during-scan" || changed) return;
            using var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
            writer.Open();
            using var update = writer.CreateCommand();
            update.CommandText = """
                PRAGMA foreign_keys=ON;
                BEGIN IMMEDIATE;
                UPDATE raw_captures SET retention_hold=1 WHERE capture_sequence=1;
                DELETE FROM processing_outputs WHERE capture_sequence=2;
                UPDATE processing_outputs SET availability_state='Missing' WHERE capture_sequence=3;
                COMMIT;
                """;
            Assert.IsTrue(update.ExecuteNonQuery() > 0);
            changed = true;
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => concurrent.ProjectAsync(CancellationToken.None))
            .ConfigureAwait(false);
        Assert.IsTrue(changed, "The concurrent writer must actually commit during the read snapshot.");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, Path.Combine(root, "mutable.db")))
            .ConfigureAwait(false);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(original, HashFacts(root));
        using var index = new SqliteConnection($"Data Source={Path.Combine(root, "mutable.db")};Mode=ReadOnly;Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var check = index.CreateCommand();
        check.CommandText = """
            SELECT (SELECT retention_hold FROM capture_lookup WHERE sequence=1),
                   (SELECT count(*) FROM output_lookup WHERE availability='RemovedFromSource'),
                   (SELECT count(*) FROM output_lookup WHERE availability='Missing');
            """;
        using var row = await check.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await row.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(1L, row.GetInt64(0));
        Assert.AreEqual(4L, row.GetInt64(1));
        Assert.AreEqual(4L, row.GetInt64(2));
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, Path.Combine(root, "mutable.db"))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FactVerificationDoesNotQualifyMissingPhysicalProductRecovery()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        await new HourlyArtifactFactsPrototype(root, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var source = new SqliteConnection($"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        using var select = source.CreateCommand();
        select.CommandText = "SELECT payload_relative_path FROM processing_outputs ORDER BY output_identity_sha256;";
        using var rows = await select.ExecuteReaderAsync().ConfigureAwait(false);
        string? physical = null;
        while (await rows.ReadAsync().ConfigureAwait(false))
        {
            var candidate = Path.GetFullPath(Path.Combine(fixture.Root, rows.GetString(0)));
            Assert.IsTrue(candidate.StartsWith(fixture.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            if (File.Exists(candidate)) { physical = candidate; break; }
        }
        Assert.IsNotNull(physical, "The negative control must remove a real referenced product.");
        var original = await File.ReadAllBytesAsync(physical).ConfigureAwait(false);
        var checksum = Convert.ToHexString(SHA256.HashData(original));
        File.Delete(physical);
        Assert.IsFalse(File.Exists(physical));
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, Path.Combine(root, "mutable.db"))
            .ConfigureAwait(false);
        TestContext.WriteLine($"Unqualified boundary: fact-reference validation passed despite missing physical product {Path.GetRelativePath(fixture.Root, physical)} (length={original.Length}, SHA256={checksum}).");
    }

    private static string[] HashFacts(string root) => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    private static long DirectoryBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private static async Task<string> SourceDigestAsync(string database)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manifest_json FROM raw_captures ORDER BY capture_id;
            SELECT descriptor_json FROM processing_outputs ORDER BY output_identity_sha256;
            SELECT output_identity_sha256,source_ordinal,source_artifact_id
                FROM processing_output_sources ORDER BY output_identity_sha256,source_ordinal;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    var value = reader.GetValue(ordinal);
                    var bytes = value as byte[] ?? System.Text.Encoding.UTF8.GetBytes(
                        Convert.ToString(value, CultureInfo.InvariantCulture)!);
                    digest.AppendData(BitConverter.GetBytes(bytes.Length));
                    digest.AppendData(bytes);
                }
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        return Convert.ToHexString(digest.GetHashAndReset());
    }
}
