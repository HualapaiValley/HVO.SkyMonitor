using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentArtifactHistoryComparisonTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RetainedHundredThousandPublishedLastFiveHundredCheck()
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1058_FIXTURE");
        var restoredPath = Environment.GetEnvironmentVariable("HVO_ISSUE1058_RESTORED_INDEX");
        var inventoryPath = Environment.GetEnvironmentVariable("HVO_ISSUE1058_INVENTORY");
        if (fixtureRoot is null || restoredPath is null || inventoryPath is null)
            Assert.Inconclusive("Set HVO_ISSUE1058_FIXTURE, HVO_ISSUE1058_RESTORED_INDEX and HVO_ISSUE1058_INVENTORY.");
        var mount = Path.GetFullPath("/mnt/hvo-1058");
        fixtureRoot = Path.GetFullPath(fixtureRoot!);
        restoredPath = Path.GetFullPath(restoredPath!);
        inventoryPath = Path.GetFullPath(inventoryPath!);
        Assert.IsTrue(fixtureRoot.StartsWith(Path.Combine(mount, "fixtures") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsTrue(restoredPath.StartsWith(Path.Combine(mount, "results") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsTrue(inventoryPath.StartsWith(Path.Combine(mount, "results") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        var root = Path.Combine(fixtureRoot, "shadow");
        var timer = Stopwatch.StartNew();
        await LastCaptureInventoryPrototype.PublishAsync(Path.Combine(fixtureRoot, "journal", "raw-ingress.db"),
            Path.Combine(root, "mutable.db"), inventoryPath).ConfigureAwait(false);
        timer.Stop();
        var publicationMs = timer.Elapsed.TotalMilliseconds;
        var inventory = await LastCaptureInventoryPrototype.ReadAsync(inventoryPath).ConfigureAwait(false);
        Assert.AreEqual(100000L, inventory.MaximumSequence);
        timer.Restart();
        await LastCaptureInventoryPrototype.VerifyMembershipAsync(inventoryPath, restoredPath).ConfigureAwait(false);
        timer.Stop();
        var membershipMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, restoredPath,
            minSequence: inventory.MaximumSequence - 499, maxSequence: inventory.MaximumSequence,
            agentId: inventory.AgentId).ConfigureAwait(false);
        timer.Stop();
        TestContext.WriteLine($"Source-and-fact inventory publication {publicationMs:F1} ms, " +
            $"retained inventory bytes {new FileInfo(inventoryPath).Length}, restored-index membership {membershipMs:F1} ms, " +
            $"bounded fact verification {timer.Elapsed.TotalMilliseconds:F1} ms; " +
            "source journal remains authoritative only in this offline prototype.");
    }

    [TestMethod]
    public async Task PublishedLastFiveHundredRejectsOmissionsAndInterruptedPublication()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(501, sceneBearingOutputs: true,
            tenSecondCadence: true).ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var indexPath = Path.Combine(root, "mutable.db");
        await new HourlyArtifactFactsPrototype(root, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var inventoryPath = Path.Combine(fixture.Root, "last-500.json");
        await Assert.ThrowsExactlyAsync<IOException>(() => LastCaptureInventoryPrototype.PublishAsync(
            fixture.DatabasePath, indexPath, inventoryPath, point =>
            {
                if (point == "before-rename") throw new IOException("interrupted");
            })).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(inventoryPath));
        Assert.IsTrue(File.Exists(inventoryPath + ".pending"));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            LastCaptureInventoryPrototype.VerifyMembershipAsync(inventoryPath, indexPath)).ConfigureAwait(false);
        File.Delete(inventoryPath + ".pending");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => LastCaptureInventoryPrototype.PublishAsync(
            fixture.DatabasePath, indexPath, inventoryPath, point =>
            {
                if (point != "before-rename") return;
                using var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False");
                writer.Open();
                using var update = writer.CreateCommand();
                update.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=501;";
                Assert.AreEqual(1, update.ExecuteNonQuery());
            })).ConfigureAwait(false);
        Assert.IsFalse(File.Exists(inventoryPath));
        Assert.IsTrue(File.Exists(inventoryPath + ".pending"));
        File.Delete(inventoryPath + ".pending");
        await LastCaptureInventoryPrototype.PublishAsync(fixture.DatabasePath, indexPath, inventoryPath)
            .ConfigureAwait(false);
        await LastCaptureInventoryPrototype.VerifyMembershipAsync(inventoryPath, indexPath).ConfigureAwait(false);
        foreach (var mutation in new[] { "missing-max", "missing-raw", "missing-output", "relink-output", "changed-hash" })
        {
            var copyPath = Path.Combine(fixture.Root, mutation + ".db");
            using (var original = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False"))
            using (var copy = new SqliteConnection($"Data Source={copyPath};Pooling=False"))
            {
                await original.OpenAsync().ConfigureAwait(false);
                await copy.OpenAsync().ConfigureAwait(false);
                original.BackupDatabase(copy);
                using var change = copy.CreateCommand();
                if (mutation == "missing-max")
                    change.CommandText = "DELETE FROM output_lookup WHERE capture_id=(SELECT capture_id FROM capture_lookup WHERE sequence=501); DELETE FROM capture_lookup WHERE sequence=501;";
                else if (mutation == "missing-raw")
                    change.CommandText = "DELETE FROM output_lookup WHERE capture_id=(SELECT capture_id FROM capture_lookup WHERE sequence=500); DELETE FROM capture_lookup WHERE sequence=500;";
                else if (mutation == "missing-output")
                    change.CommandText = "DELETE FROM output_lookup WHERE output_identity=(SELECT output.output_identity FROM output_lookup output JOIN capture_lookup capture ON capture.capture_id=output.capture_id WHERE capture.sequence=501 LIMIT 1);";
                else if (mutation == "relink-output")
                    change.CommandText = "UPDATE output_lookup SET capture_id=(SELECT capture_id FROM capture_lookup WHERE sequence=1) WHERE output_identity=(SELECT output_identity FROM output_lookup WHERE capture_id=(SELECT capture_id FROM capture_lookup WHERE sequence=501) LIMIT 1);";
                else
                    change.CommandText = "UPDATE capture_lookup SET fact_sha256='BAD' WHERE sequence=501;";
                Assert.IsGreaterThan(0, await change.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                LastCaptureInventoryPrototype.VerifyMembershipAsync(inventoryPath, copyPath)).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task RetainedIndexLastFiveHundredRestoreCheck()
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1058_FIXTURE");
        var restoredPath = Environment.GetEnvironmentVariable("HVO_ISSUE1058_RESTORED_INDEX");
        if (fixtureRoot is null || restoredPath is null)
            Assert.Inconclusive("Set HVO_ISSUE1058_FIXTURE and HVO_ISSUE1058_RESTORED_INDEX.");
        fixtureRoot = Path.GetFullPath(fixtureRoot!);
        restoredPath = Path.GetFullPath(restoredPath!);
        var mount = Path.GetFullPath("/mnt/hvo-1058");
        Assert.IsTrue(fixtureRoot.StartsWith(Path.Combine(mount, "fixtures") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsTrue(restoredPath.StartsWith(Path.Combine(mount, "results") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        using var index = new SqliteConnection($"Data Source={restoredPath};Mode=ReadOnly;Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var bounds = index.CreateCommand();
        bounds.CommandText = "SELECT agent_id,max(sequence) FROM capture_lookup GROUP BY agent_id;";
        using var agents = await bounds.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await agents.ReadAsync().ConfigureAwait(false));
        var agent = agents.GetString(0);
        var maximum = agents.GetInt64(1);
        Assert.IsFalse(await agents.ReadAsync().ConfigureAwait(false), "This fixture must have exactly one agent.");
        await agents.CloseAsync().ConfigureAwait(false);
        const long window = 500;
        var minimum = maximum - window + 1;
        Assert.IsGreaterThan(0L, minimum);
        using var selected = index.CreateCommand();
        selected.CommandText = """
            SELECT sequence, capture_id FROM capture_lookup
            WHERE agent_id=$agent AND sequence BETWEEN $min AND $max ORDER BY sequence;
            """;
        selected.Parameters.AddWithValue("$agent", agent);
        selected.Parameters.AddWithValue("$min", minimum);
        selected.Parameters.AddWithValue("$max", maximum);
        using (var captures = await selected.ExecuteReaderAsync().ConfigureAwait(false))
        {
            var expected = minimum;
            while (await captures.ReadAsync().ConfigureAwait(false))
                Assert.AreEqual(expected++, captures.GetInt64(0));
            Assert.IsTrue(expected == maximum + 1, "The selected sequence window has missing rows.");
        }
        var timer = Stopwatch.StartNew();
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(
            Path.Combine(fixtureRoot, "shadow"), restoredPath, minSequence: minimum,
            maxSequence: maximum, agentId: agent).ConfigureAwait(false);
        timer.Stop();
        TestContext.WriteLine($"Last {window} indexed captures for {agent}, sequences {minimum}-{maximum}; " +
            $"bounded restored-index fact verification {timer.Elapsed.TotalMilliseconds:F1} ms. " +
            "Output membership and maximum sequence still require independent publication evidence.");
    }

    [TestMethod]
    public async Task LatestDayCheckDefersOlderCorruptionButRejectsCurrentDayCorruption()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var shadowRoot = Path.Combine(fixture.Root, "shadow");
        var indexPath = Path.Combine(shadowRoot, "mutable.db");
        await new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var index = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var paths = index.CreateCommand();
        paths.CommandText = "SELECT fact_path,exposure_unix_ms FROM capture_lookup ORDER BY exposure_unix_ms;";
        using var rows = await paths.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await rows.ReadAsync().ConfigureAwait(false));
        var olderPath = Path.Combine(shadowRoot, rows.GetString(0));
        var olderMs = rows.GetInt64(1);
        Assert.IsTrue(await rows.ReadAsync().ConfigureAwait(false));
        var latestPath = Path.Combine(shadowRoot, rows.GetString(0));
        var latestMs = rows.GetInt64(1);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(latestMs).UtcDateTime.Date;
        var from = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.IsLessThan(from, olderMs);
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, indexPath, from, to)
            .ConfigureAwait(false);
        var older = await File.ReadAllBytesAsync(olderPath).ConfigureAwait(false);
        await File.AppendAllTextAsync(olderPath, "tampered").ConfigureAwait(false);
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, indexPath, from, to)
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, indexPath))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(olderPath, older).ConfigureAwait(false);
        var latest = await File.ReadAllBytesAsync(latestPath).ConfigureAwait(false);
        await File.AppendAllTextAsync(latestPath, "tampered").ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, indexPath, from, to))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(latestPath, latest).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task LatestDayIndexSelectionCannotProveDayMembership()
    {
        using var fixture = await GalleryPerformanceFixture.CreateAsync(2, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var root = Path.Combine(fixture.Root, "shadow");
        var indexPath = Path.Combine(root, "mutable.db");
        await new HourlyArtifactFactsPrototype(root, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var index = new SqliteConnection($"Data Source={indexPath};Pooling=False");
        await index.OpenAsync().ConfigureAwait(false);
        using var latest = index.CreateCommand();
        latest.CommandText = "SELECT max(exposure_unix_ms) FROM capture_lookup;";
        var lastMs = Convert.ToInt64(await latest.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(lastMs).UtcDateTime.Date;
        var from = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToUnixTimeMilliseconds();
        using var changed = index.CreateCommand();
        changed.CommandText = "UPDATE capture_lookup SET exposure_unix_ms=exposure_unix_ms-86400000 WHERE exposure_unix_ms=$latest;";
        changed.Parameters.AddWithValue("$latest", lastMs);
        Assert.AreEqual(1, await changed.ExecuteNonQueryAsync().ConfigureAwait(false));
        // This demonstrates the current gap, not acceptable startup behavior.
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, indexPath, from, to)
            .ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(root, indexPath))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RetainedIndexLatestUtcDayRestoreCheck()
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1058_FIXTURE");
        var restoredPath = Environment.GetEnvironmentVariable("HVO_ISSUE1058_RESTORED_INDEX");
        if (fixtureRoot is null || restoredPath is null)
            Assert.Inconclusive("Set HVO_ISSUE1058_FIXTURE and HVO_ISSUE1058_RESTORED_INDEX.");
        fixtureRoot = Path.GetFullPath(fixtureRoot!);
        restoredPath = Path.GetFullPath(restoredPath!);
        var mount = Path.GetFullPath("/mnt/hvo-1058");
        Assert.IsTrue(fixtureRoot.StartsWith(Path.Combine(mount, "fixtures") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsTrue(restoredPath.StartsWith(Path.Combine(mount, "results") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        var shadowRoot = Path.Combine(fixtureRoot, "shadow");
        using var snapshot = new SqliteConnection($"Data Source={restoredPath};Mode=ReadOnly;Pooling=False");
        await snapshot.OpenAsync().ConfigureAwait(false);
        using var latest = snapshot.CreateCommand();
        latest.CommandText = "SELECT max(exposure_unix_ms) FROM capture_lookup;";
        var latestMs = Convert.ToInt64(await latest.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(latestMs).UtcDateTime.Date;
        var from = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToUnixTimeMilliseconds();
        using var counts = snapshot.CreateCommand();
        counts.CommandText = """
            SELECT (SELECT count(*) FROM capture_lookup WHERE exposure_unix_ms >= $from AND exposure_unix_ms < $to),
                   (SELECT count(*) FROM output_lookup output JOIN capture_lookup capture ON capture.capture_id=output.capture_id
                    WHERE capture.exposure_unix_ms >= $from AND capture.exposure_unix_ms < $to);
            """;
        counts.Parameters.AddWithValue("$from", from);
        counts.Parameters.AddWithValue("$to", to);
        using var rows = await counts.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await rows.ReadAsync().ConfigureAwait(false));
        var captures = rows.GetInt64(0);
        var outputs = rows.GetInt64(1);
        Assert.IsGreaterThan(0L, captures);
        await rows.CloseAsync().ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, restoredPath, from, to)
            .ConfigureAwait(false);
        timer.Stop();
        TestContext.WriteLine($"Latest UTC calendar day {day:yyyy-MM-dd}; raw={captures} outputs={outputs}; " +
            $"bounded restored-index fact verification {timer.Elapsed.TotalMilliseconds:F1} ms. " +
            "Older history remains unverified; this is not installer downtime or background repair.");
    }

    [TestMethod]
    public async Task RetainedHundredThousandSnapshotRestoreAndVerify()
    {
        var expectedCaptures = Environment.GetEnvironmentVariable("HVO_ISSUE1058_REUSE_SMOKE") == "1"
            ? 720L : 100000L;
        var fixtureRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1058_FIXTURE");
        var resultRoot = Environment.GetEnvironmentVariable("HVO_ISSUE1058_RESULTS");
        if (fixtureRoot is null || resultRoot is null)
            Assert.Inconclusive("Set HVO_ISSUE1058_FIXTURE and HVO_ISSUE1058_RESULTS for the retained 100K fixture.");
        var mount = Path.GetFullPath("/mnt/hvo-1058");
        fixtureRoot = Path.GetFullPath(fixtureRoot!);
        resultRoot = Path.GetFullPath(resultRoot!);
        Assert.IsTrue(fixtureRoot.StartsWith(Path.Combine(mount, "fixtures") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsTrue(resultRoot.StartsWith(Path.Combine(mount, "results") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.IsFalse(Directory.Exists(resultRoot), "Never overwrite an earlier benchmark's snapshots or evidence.");
        var sourcePath = Path.Combine(fixtureRoot, "journal", "raw-ingress.db");
        var shadowRoot = Path.Combine(fixtureRoot, "shadow");
        var indexPath = Path.Combine(shadowRoot, "mutable.db");
        foreach (var database in new[] { sourcePath, indexPath })
        {
            Assert.IsTrue(File.Exists(database));
            Assert.IsFalse(File.Exists(database + "-wal") && new FileInfo(database + "-wal").Length > 0,
                "A retained database with a nonempty WAL needs a different snapshot qualification.");
        }
        Assert.IsGreaterThan(400L * 1024 * 1024 * 1024,
            new DriveInfo(mount).AvailableFreeSpace, "Retain both source backup and restore alongside the fixture.");
        Directory.CreateDirectory(resultRoot);
        var progressPath = Path.Combine(resultRoot, "stages.log");
        async Task RecordAsync(string stage, Stopwatch? elapsed = null)
        {
            await File.AppendAllTextAsync(progressPath,
                $"{DateTimeOffset.UtcNow:O} {stage} {elapsed?.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} ms{Environment.NewLine}")
                .ConfigureAwait(false);
        }
        await RecordAsync($"start source={new FileInfo(sourcePath).Length} index={new FileInfo(indexPath).Length}")
            .ConfigureAwait(false);
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        using var index = new SqliteConnection($"Data Source={indexPath};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        await index.OpenAsync().ConfigureAwait(false);
        foreach (var (connection, name) in new[] { (source, "journal"), (index, "index") })
        {
            var snapshotPath = Path.Combine(resultRoot, $"{name}-snapshot.db");
            var restoredPath = Path.Combine(resultRoot, $"{name}-restored.db");
            using (var snapshot = new SqliteConnection($"Data Source={snapshotPath};Pooling=False"))
            {
                await snapshot.OpenAsync().ConfigureAwait(false);
                var timer = Stopwatch.StartNew();
                connection.BackupDatabase(snapshot);
                timer.Stop();
                await RecordAsync($"{name} sqlite-backup-copy bytes={new FileInfo(snapshotPath).Length}", timer)
                    .ConfigureAwait(false);
            }
            var restoreTimer = Stopwatch.StartNew();
            File.Copy(snapshotPath, restoredPath);
            restoreTimer.Stop();
            await RecordAsync($"{name} restore-file-copy bytes={new FileInfo(restoredPath).Length}", restoreTimer)
                .ConfigureAwait(false);
            using (var restored = new SqliteConnection($"Data Source={restoredPath};Mode=ReadOnly;Pooling=False"))
            {
                await restored.OpenAsync().ConfigureAwait(false);
                var verifyTimer = Stopwatch.StartNew();
                using var check = restored.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                Assert.AreEqual("ok", await check.ExecuteScalarAsync().ConfigureAwait(false));
                if (name == "journal")
                    check.CommandText = "SELECT (SELECT count(*) FROM raw_captures), (SELECT count(*) FROM processing_outputs);";
                else
                    check.CommandText = "SELECT (SELECT count(*) FROM capture_lookup), (SELECT count(*) FROM output_lookup);";
                using var rows = await check.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await rows.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(expectedCaptures, rows.GetInt64(0));
                Assert.AreEqual(expectedCaptures * 4, rows.GetInt64(1));
                await rows.CloseAsync().ConfigureAwait(false);
                verifyTimer.Stop();
                await RecordAsync($"{name} reopen-integrity-counts", verifyTimer).ConfigureAwait(false);
                if (name == "journal")
                {
                    var digestTimer = Stopwatch.StartNew();
                    var restoredDigest = CameraAgentArtifactJournalBaselineTests.Digest(restored);
                    digestTimer.Stop();
                    await RecordAsync("journal restored-manifest-descriptor-lineage-digest", digestTimer)
                        .ConfigureAwait(false);
                    digestTimer.Restart();
                    Assert.AreEqual(CameraAgentArtifactJournalBaselineTests.Digest(source), restoredDigest);
                    digestTimer.Stop();
                    await RecordAsync("journal source-digest-comparison", digestTimer).ConfigureAwait(false);
                }
            }
            if (name == "index")
            {
                var verifyTimer = Stopwatch.StartNew();
                await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, restoredPath)
                    .ConfigureAwait(false);
                verifyTimer.Stop();
                await RecordAsync("index full-retained-reference-verification", verifyTimer).ConfigureAwait(false);
            }
        }
        await RecordAsync("completed").ConfigureAwait(false);
        TestContext.WriteLine(progressPath);
    }

    [TestMethod]
    public async Task BoundedFixtureSnapshotsRestoreCountsAndRetainedReferences()
    {
        var count = Environment.GetEnvironmentVariable("HVO_ISSUE1058_CAPTURES") == "10000" ? 10000 : 720;
        var trials = count == 10000 ? 1 : 5;
        if (count == 10000)
            Assert.IsGreaterThan(90L * 1024 * 1024 * 1024, new DriveInfo(Path.GetTempPath()).AvailableFreeSpace,
                "10K snapshot/restore requires 90 GiB free for both layouts, source backup and restore copies.");
        using var fixture = await GalleryPerformanceFixture.CreateAsync(count, sceneBearingOutputs: true,
            tenSecondCadence: true).ConfigureAwait(false);
        using (var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await writer.OpenAsync().ConfigureAwait(false);
            using var checkpoint = writer.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using (var result = await checkpoint.ExecuteReaderAsync().ConfigureAwait(false))
            {
                Assert.IsTrue(await result.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(0L, result.GetInt64(0));
            }
            checkpoint.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;";
            await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var shadowRoot = Path.Combine(fixture.Root, "shadow");
        await new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var source = new SqliteConnection($"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False");
        using var shadow = new SqliteConnection($"Data Source={Path.Combine(shadowRoot, "mutable.db")};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        await shadow.OpenAsync().ConfigureAwait(false);
        var captureId = (await ReadAsync(source,
            "SELECT capture_id FROM raw_captures WHERE capture_sequence=$sequence;", "$sequence", 1)
            .ConfigureAwait(false))[0];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var disk = new DriveInfo(Path.GetTempPath());
        var freeBefore = disk.AvailableFreeSpace;
        var peakSampledRss = rssBefore;
        var lowestSampledFree = freeBefore;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try
            {
                while (await timer.WaitForNextTickAsync(sampling.Token).ConfigureAwait(false))
                {
                    process.Refresh();
                    peakSampledRss = Math.Max(peakSampledRss, process.WorkingSet64);
                    lowestSampledFree = Math.Min(lowestSampledFree, disk.AvailableFreeSpace);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        });
        var sourceTimes = new double[trials];
        var shadowTimes = new double[trials];
        var sourceCpuTimes = new double[trials];
        var shadowCpuTimes = new double[trials];
        var sourceRestoreTimes = new double[trials];
        var shadowRestoreTimes = new double[trials];
        var sourceVerifyTimes = new double[trials];
        var restoredSourceDigests = new string[trials];
        var referenceTimes = new double[trials];
        for (var trial = 0; trial < trials; trial++)
        {
            var ordered = trial % 2 == 0
                ? new[] { (source, "source", sourceTimes), (shadow, "index", shadowTimes) }
                : new[] { (shadow, "index", shadowTimes), (source, "source", sourceTimes) };
            foreach (var (connection, name, times) in ordered)
            {
                var snapshotPath = Path.Combine(fixture.Root, $"snapshot-{name}-{trial}.db");
                using (var destination = new SqliteConnection($"Data Source={snapshotPath};Pooling=False"))
                {
                    await destination.OpenAsync().ConfigureAwait(false);
                    var started = Stopwatch.GetTimestamp();
                    var cpuBefore = process.TotalProcessorTime;
                    connection.BackupDatabase(destination);
                    times[trial] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    (name == "source" ? sourceCpuTimes : shadowCpuTimes)[trial] =
                        (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                }
                var restorePath = Path.Combine(fixture.Root, $"restored-{name}-{trial}.db");
                var restoreStarted = Stopwatch.GetTimestamp();
                File.Copy(snapshotPath, restorePath);
                (name == "source" ? sourceRestoreTimes : shadowRestoreTimes)[trial] =
                    Stopwatch.GetElapsedTime(restoreStarted).TotalMilliseconds;
                using (var restored = new SqliteConnection($"Data Source={restorePath};Mode=ReadOnly;Pooling=False"))
                {
                    await restored.OpenAsync().ConfigureAwait(false);
                    using var check = restored.CreateCommand();
                    check.CommandText = "PRAGMA integrity_check;";
                    Assert.AreEqual("ok", await check.ExecuteScalarAsync().ConfigureAwait(false));
                    if (name == "source")
                        check.CommandText = "SELECT (SELECT count(*) FROM raw_captures), (SELECT count(*) FROM processing_outputs), (SELECT retention_hold FROM raw_captures WHERE capture_sequence=1);";
                    else
                        check.CommandText = "SELECT (SELECT count(*) FROM capture_lookup), (SELECT count(*) FROM output_lookup), (SELECT retention_hold FROM capture_lookup WHERE sequence=1);";
                    using var rows = await check.ExecuteReaderAsync().ConfigureAwait(false);
                    Assert.IsTrue(await rows.ReadAsync().ConfigureAwait(false));
                    Assert.AreEqual(count, rows.GetInt64(0));
                    Assert.AreEqual(count * 4L, rows.GetInt64(1));
                    Assert.AreEqual(0L, rows.GetInt64(2));
                    await rows.CloseAsync().ConfigureAwait(false);
                    if (name == "source")
                    {
                        var verifyStarted = Stopwatch.GetTimestamp();
                        restoredSourceDigests[trial] = CameraAgentArtifactJournalBaselineTests.Digest(restored);
                        sourceVerifyTimes[trial] = Stopwatch.GetElapsedTime(verifyStarted).TotalMilliseconds;
                    }
                    else
                    {
                        using var reference = restored.CreateCommand();
                        reference.CommandText = "SELECT fact_path,fact_sha256 FROM capture_lookup WHERE capture_id=$capture;";
                        reference.Parameters.AddWithValue("$capture", captureId);
                        using var fact = await reference.ExecuteReaderAsync().ConfigureAwait(false);
                        Assert.IsTrue(await fact.ReadAsync().ConfigureAwait(false));
                        var path = Path.GetFullPath(Path.Combine(shadowRoot, fact.GetString(0)));
                        Assert.IsTrue(path.StartsWith(Path.Combine(shadowRoot, "hours") + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal));
                        Assert.AreEqual(fact.GetString(1), Convert.ToHexString(SHA256.HashData(
                            await File.ReadAllBytesAsync(path).ConfigureAwait(false))));
                    }
                }
                if (name == "index")
                {
                    var verifyStarted = Stopwatch.GetTimestamp();
                    await CameraAgentHourlyArtifactFactsPrototypeTests.VerifySnapshotAsync(shadowRoot, restorePath)
                        .ConfigureAwait(false);
                    referenceTimes[trial] = Stopwatch.GetElapsedTime(verifyStarted).TotalMilliseconds;
                }
                File.Delete(restorePath);
                File.Delete(snapshotPath);
            }
        }
        var sourceDigest = CameraAgentArtifactJournalBaselineTests.Digest(source);
        foreach (var restoredDigest in restoredSourceDigests) Assert.AreEqual(sourceDigest, restoredDigest);
        await sampling.CancelAsync().ConfigureAwait(false);
        await sampler.ConfigureAwait(false);
        process.Refresh();
        var rssAfter = process.WorkingSet64;
        TestContext.WriteLine($"{count}-capture {trials} correlated online SQLite snapshot-copy trial(s) (ms; " +
            $"new destination per trial, one seeded fixture; integrity/count/sample-reference checks after timing): " +
            $"source [{string.Join(", ", sourceTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}], " +
            $"shadow [{string.Join(", ", shadowTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]; " +
            $"separate restore file copies source [{string.Join(", ", sourceRestoreTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}], " +
            $"index [{string.Join(", ", shadowRestoreTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]; " +
            $"source bytes {new FileInfo(fixture.DatabasePath).Length}, index bytes {new FileInfo(Path.Combine(shadowRoot, "mutable.db")).Length}; " +
            $"separate full retained-fact reference verification (ms) " +
            $"[{string.Join(", ", referenceTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]; " +
            $"source digest verification (ms) [{string.Join(", ", sourceVerifyTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]; " +
            $"backup process CPU source (ms) [{string.Join(", ", sourceCpuTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}], " +
            $"index [{string.Join(", ", shadowCpuTimes.Select(static value => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}]; " +
            $"working set boundary before/after trials {rssBefore}/{rssAfter} B; " +
            $"100ms sampled high-water working set {peakSampledRss} B, " +
            $"sampled extra disk use since trial start {freeBefore - lowestSampledFree} B " +
            $"(process-wide / filesystem-wide, not exact peaks)");
        var before = new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath).ReadFact(captureId);
        using (var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await writer.OpenAsync().ConfigureAwait(false);
            using var update = writer.CreateCommand();
            update.CommandText = "UPDATE raw_captures SET retention_hold=1 WHERE capture_sequence=1;";
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var restart = Stopwatch.GetTimestamp();
        await new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath)
            .ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        var scanMs = Stopwatch.GetElapsedTime(restart).TotalMilliseconds;
        CollectionAssert.AreEqual(before, new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath)
            .ReadFact(captureId));
        using var updated = shadow.CreateCommand();
        updated.CommandText = "SELECT retention_hold FROM capture_lookup WHERE capture_id=$capture;";
        updated.Parameters.AddWithValue("$capture", captureId);
        Assert.AreEqual(1L, Convert.ToInt64(await updated.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        TestContext.WriteLine($"{count}-capture restart full re-projection after one retention-hold change: " +
            $"{scanMs:F1} ms; indexed hold updated, sampled immutable raw fact unchanged");
    }

    [TestMethod]
    public async Task TwoHourSourceAndShadowQueriesReturnIdenticalCaptureAndOutputRows()
    {
        var count = Environment.GetEnvironmentVariable("HVO_ISSUE1058_CAPTURES") switch
        {
            "10000" => 10000,
            "100000" => 100000,
            _ => 720
        };
        if (count >= 10000)
            Assert.IsGreaterThan((count == 100000 ? 600L : 60L) * 1024 * 1024 * 1024,
                new DriveInfo(Path.GetTempPath()).AvailableFreeSpace,
                "Large comparison needs disposable filesystem capacity for source WAL, main file and shadow facts.");
        var retain = Environment.GetEnvironmentVariable("HVO_ISSUE1058_RETAIN") == "1";
        using var fixture = await GalleryPerformanceFixture.CreateAsync(count, sceneBearingOutputs: true,
            tenSecondCadence: true, retainOnDispose: retain).ConfigureAwait(false);
        if (retain) TestContext.WriteLine($"Retained disposable fixture: {fixture.Root}");
        using (var writer = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await writer.OpenAsync().ConfigureAwait(false);
            using var checkpoint = writer.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var result = await checkpoint.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await result.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(0L, result.GetInt64(0), "Seed WAL checkpoint must not be blocked.");
        }
        var shadowRoot = Path.Combine(fixture.Root, "shadow");
        var projection = new HourlyArtifactFactsPrototype(shadowRoot, fixture.DatabasePath);
        await projection.ProjectAsync(CancellationToken.None).ConfigureAwait(false);
        using var source = new SqliteConnection($"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False");
        using var shadow = new SqliteConnection($"Data Source={Path.Combine(shadowRoot, "mutable.db")};Mode=ReadOnly;Pooling=False");
        await source.OpenAsync().ConfigureAwait(false);
        await shadow.OpenAsync().ConfigureAwait(false);
        foreach (var cursor in new[] { 0, 325, 360, count - 50 })
        {
            var expected = await ReadAsync(source, """
                SELECT capture_id FROM raw_captures WHERE agent_id='issue-106-gallery'
                  AND capture_sequence>$cursor ORDER BY capture_sequence LIMIT 50;
                """, "$cursor", cursor).ConfigureAwait(false);
            var actual = await ReadAsync(shadow, """
                SELECT capture_id FROM capture_lookup WHERE agent_id='issue-106-gallery'
                  AND sequence>$cursor ORDER BY sequence LIMIT 50;
                """, "$cursor", cursor).ConfigureAwait(false);
            Assert.HasCount(50, expected);
            CollectionAssert.AreEqual(expected, actual);
        }
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        foreach (var from in new[] { epoch + 100_000L, epoch + 3_550_000L, epoch + 3_610_000L })
        {
            var expected = await ReadAsync(source, """
                SELECT capture_id FROM raw_captures WHERE exposure_started_unix_ms >= $from
                  AND exposure_started_unix_ms < $from+300000 ORDER BY exposure_started_unix_ms,capture_sequence;
                """, "$from", from).ConfigureAwait(false);
            var actual = await ReadAsync(shadow, """
                SELECT capture_id FROM capture_lookup WHERE exposure_unix_ms >= $from
                  AND exposure_unix_ms < $from+300000 ORDER BY exposure_unix_ms,sequence;
                """, "$from", from).ConfigureAwait(false);
            Assert.IsGreaterThan(0, expected.Count);
            CollectionAssert.AreEqual(expected, actual);
        }
        foreach (var sequence in new[] { 1, 360, 361, count })
        {
            var capture = await ReadAsync(source,
                "SELECT capture_id FROM raw_captures WHERE capture_sequence=$sequence;", "$sequence", sequence)
                .ConfigureAwait(false);
            Assert.HasCount(1, capture);
            var expected = await ReadAsync(source, """
                SELECT output_identity_sha256 || ':' || role || ':' || variant FROM processing_outputs
                WHERE capture_id=$capture ORDER BY output_identity_sha256;
                """, "$capture", capture[0]).ConfigureAwait(false);
            var actual = await ReadAsync(shadow, """
                SELECT output_identity || ':' || role || ':' || variant FROM output_lookup
                WHERE capture_id=$capture ORDER BY output_identity;
                """, "$capture", capture[0]).ConfigureAwait(false);
            Assert.HasCount(4, expected);
            CollectionAssert.AreEqual(expected, actual);
        }
        const string sourcePageSql = """
            SELECT capture_id FROM raw_captures WHERE agent_id='issue-106-gallery'
              AND capture_sequence>$cursor ORDER BY capture_sequence LIMIT 50;
            """;
        const string shadowPageSql = """
            SELECT capture_id FROM capture_lookup WHERE agent_id='issue-106-gallery'
              AND sequence>$cursor ORDER BY sequence LIMIT 50;
            """;
        const string sourceTimeSql = """
            SELECT capture_id FROM raw_captures WHERE exposure_started_unix_ms >= $from
              AND exposure_started_unix_ms < $from+300000 ORDER BY exposure_started_unix_ms,capture_sequence;
            """;
        const string shadowTimeSql = """
            SELECT capture_id FROM capture_lookup WHERE exposure_unix_ms >= $from
              AND exposure_unix_ms < $from+300000 ORDER BY exposure_unix_ms,sequence;
            """;
        for (var i = 0; i < 35; i++)
        {
            var cursor = i * 7919 % (count - 50);
            CollectionAssert.AreEqual(await ReadAsync(source, sourcePageSql, "$cursor", cursor).ConfigureAwait(false),
                await ReadAsync(shadow, shadowPageSql, "$cursor", cursor).ConfigureAwait(false));
            var from = epoch + (i * 7919 % (count - 30)) * 10_000L;
            CollectionAssert.AreEqual(await ReadAsync(source, sourceTimeSql, "$from", from).ConfigureAwait(false),
                await ReadAsync(shadow, shadowTimeSql, "$from", from).ConfigureAwait(false));
        }
        var sourcePage = await MeasureAsync(source, sourcePageSql, "$cursor",
            i => i * 7919 % (count - 50)).ConfigureAwait(false);
        var shadowPage = await MeasureAsync(shadow, shadowPageSql, "$cursor",
            i => i * 7919 % (count - 50)).ConfigureAwait(false);
        var sourceTime = await MeasureAsync(source, sourceTimeSql, "$from",
            i => epoch + (i * 7919 % (count - 30)) * 10_000L).ConfigureAwait(false);
        var shadowTime = await MeasureAsync(shadow, shadowTimeSql, "$from",
            i => epoch + (i * 7919 % (count - 30)) * 10_000L).ConfigureAwait(false);
        var sourcePageFacts = new double[35];
        var shadowPageFacts = new double[35];
        using var sourcePageCommand = source.CreateCommand();
        sourcePageCommand.CommandText = """
            SELECT capture_id,manifest_json,manifest_sha256 FROM raw_captures
            WHERE agent_id='issue-106-gallery' AND capture_sequence>$cursor
            ORDER BY capture_sequence LIMIT 50;
            """;
        var sourcePageCursor = sourcePageCommand.Parameters.Add("$cursor", SqliteType.Integer);
        using var shadowPageCommand = shadow.CreateCommand();
        shadowPageCommand.CommandText = """
            SELECT capture_id,fact_path,fact_sha256 FROM capture_lookup
            WHERE agent_id='issue-106-gallery' AND sequence>$cursor
            ORDER BY sequence LIMIT 50;
            """;
        var shadowPageCursor = shadowPageCommand.Parameters.Add("$cursor", SqliteType.Integer);
        for (var i = 0; i < sourcePageFacts.Length; i++)
        {
            var cursor = i * 7919 % (count - 50);
            sourcePageCursor.Value = cursor;
            shadowPageCursor.Value = cursor;
            var started = Stopwatch.GetTimestamp();
            var sourceFacts = new List<(string Id, byte[] Manifest, string Hash, string ExpectedHash)>(50);
            using (var rows = await sourcePageCommand.ExecuteReaderAsync().ConfigureAwait(false))
                while (await rows.ReadAsync().ConfigureAwait(false))
                {
                    var manifest = (byte[])rows.GetValue(1);
                    sourceFacts.Add((rows.GetString(0), manifest, Convert.ToHexString(SHA256.HashData(manifest)),
                        rows.GetString(2)));
                }
            sourcePageFacts[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            var shadowFacts = new List<(string Id, byte[] Manifest, string Hash, string ExpectedHash, string Path,
                string FactHash, string ExpectedFactHash)>(50);
            using (var rows = await shadowPageCommand.ExecuteReaderAsync().ConfigureAwait(false))
                while (await rows.ReadAsync().ConfigureAwait(false))
                {
                    var path = Path.GetFullPath(Path.Combine(shadowRoot, rows.GetString(1)));
                    var factBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    using var fact = JsonDocument.Parse(factBytes);
                    var manifest = fact.RootElement.GetProperty("Manifest").GetBytesFromBase64();
                    shadowFacts.Add((rows.GetString(0), manifest, Convert.ToHexString(SHA256.HashData(manifest)),
                        fact.RootElement.GetProperty("ManifestSha256").GetString()!, path,
                        Convert.ToHexString(SHA256.HashData(factBytes)), rows.GetString(2)));
                }
            shadowPageFacts[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.HasCount(50, sourceFacts);
            Assert.HasCount(50, shadowFacts);
            for (var row = 0; row < sourceFacts.Count; row++)
            {
                Assert.AreEqual(sourceFacts[row].Id, shadowFacts[row].Id);
                Assert.IsTrue(shadowFacts[row].Path.StartsWith(
                    Path.Combine(shadowRoot, "hours") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                Assert.AreEqual(sourceFacts[row].ExpectedHash, sourceFacts[row].Hash);
                Assert.AreEqual(shadowFacts[row].ExpectedFactHash, shadowFacts[row].FactHash);
                Assert.AreEqual(shadowFacts[row].ExpectedHash, shadowFacts[row].Hash);
                CollectionAssert.AreEqual(sourceFacts[row].Manifest, shadowFacts[row].Manifest);
            }
        }
        Array.Sort(sourcePageFacts);
        Array.Sort(shadowPageFacts);
        var sourceManifest = new double[35];
        var shadowManifest = new double[35];
        using var sourceLookup = source.CreateCommand();
        sourceLookup.CommandText = """
            SELECT capture_id,manifest_json,manifest_sha256 FROM raw_captures
            WHERE agent_id='issue-106-gallery' AND capture_sequence=$sequence;
            """;
        var sourceSequence = sourceLookup.Parameters.Add("$sequence", SqliteType.Integer);
        using var shadowLookup = shadow.CreateCommand();
        shadowLookup.CommandText = """
            SELECT capture_id,fact_path,fact_sha256 FROM capture_lookup
            WHERE agent_id='issue-106-gallery' AND sequence=$sequence;
            """;
        var shadowSequence = shadowLookup.Parameters.Add("$sequence", SqliteType.Integer);
        for (var i = 0; i < sourceManifest.Length; i++)
        {
            var sequence = i * 7919 % count + 1;
            sourceSequence.Value = sequence;
            shadowSequence.Value = sequence;
            var started = Stopwatch.GetTimestamp();
            using var original = await sourceLookup.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await original.ReadAsync().ConfigureAwait(false));
            var id = original.GetString(0);
            var sourceBytes = (byte[])original.GetValue(1);
            Assert.AreEqual(original.GetString(2), Convert.ToHexString(SHA256.HashData(sourceBytes)));
            sourceManifest[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            using var indexed = await shadowLookup.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await indexed.ReadAsync().ConfigureAwait(false));
            var shadowId = indexed.GetString(0);
            var path = Path.GetFullPath(Path.Combine(shadowRoot, indexed.GetString(1)));
            var factBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var shadowHash = Convert.ToHexString(SHA256.HashData(factBytes));
            var expectedHash = indexed.GetString(2);
            using var fact = JsonDocument.Parse(factBytes);
            var manifest = fact.RootElement.GetProperty("Manifest").GetBytesFromBase64();
            shadowManifest[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.AreEqual(id, shadowId);
            Assert.IsTrue(path.StartsWith(Path.Combine(shadowRoot, "hours") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
            Assert.AreEqual(expectedHash, shadowHash);
            CollectionAssert.AreEqual(sourceBytes, manifest);
        }
        Array.Sort(sourceManifest);
        Array.Sort(shadowManifest);
        var sourceOutput = new double[35];
        var shadowOutput = new double[35];
        using var sourceProduct = source.CreateCommand();
        sourceProduct.CommandText = """
            SELECT output_identity_sha256,descriptor_json FROM processing_outputs
            WHERE capture_id=$capture ORDER BY output_identity_sha256 LIMIT 1;
            """;
        var sourceCapture = sourceProduct.Parameters.Add("$capture", SqliteType.Text);
        using var shadowProduct = shadow.CreateCommand();
        shadowProduct.CommandText = """
            SELECT output_identity,fact_path,fact_sha256 FROM output_lookup
            WHERE capture_id=$capture ORDER BY output_identity LIMIT 1;
            """;
        var shadowCapture = shadowProduct.Parameters.Add("$capture", SqliteType.Text);
        for (var i = 0; i < sourceOutput.Length; i++)
        {
            var sequence = i * 7919 % count + 1;
            var capture = await ReadAsync(source,
                "SELECT capture_id FROM raw_captures WHERE agent_id='issue-106-gallery' AND capture_sequence=$sequence;",
                "$sequence", sequence).ConfigureAwait(false);
            Assert.HasCount(1, capture);
            sourceCapture.Value = capture[0];
            shadowCapture.Value = capture[0];
            var started = Stopwatch.GetTimestamp();
            using var original = await sourceProduct.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await original.ReadAsync().ConfigureAwait(false));
            var identity = original.GetString(0);
            var sourceBytes = (byte[])original.GetValue(1);
            var sourceHash = SHA256.HashData(sourceBytes);
            sourceOutput[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            using var indexed = await shadowProduct.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await indexed.ReadAsync().ConfigureAwait(false));
            var shadowIdentity = indexed.GetString(0);
            var path = Path.GetFullPath(Path.Combine(shadowRoot, indexed.GetString(1)));
            var factBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var shadowHash = Convert.ToHexString(SHA256.HashData(factBytes));
            var expectedHash = indexed.GetString(2);
            using var fact = JsonDocument.Parse(factBytes);
            var descriptor = fact.RootElement.GetProperty("Descriptor").GetBytesFromBase64();
            var descriptorHash = SHA256.HashData(descriptor);
            shadowOutput[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.AreEqual(identity, shadowIdentity);
            Assert.IsTrue(path.StartsWith(Path.Combine(shadowRoot, "products") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
            Assert.AreEqual(expectedHash, shadowHash);
            CollectionAssert.AreEqual(sourceHash, descriptorHash);
            CollectionAssert.AreEqual(sourceBytes, descriptor);
        }
        Array.Sort(sourceOutput);
        Array.Sort(shadowOutput);
        TestContext.WriteLine($"{count}-capture warm SQLite-only query times (35 varied operations each): " +
            $"50-row page source {sourcePage.Median:F3}/{sourcePage.P95:F3} ms vs shadow " +
            $"{shadowPage.Median:F3}/{shadowPage.P95:F3} ms; 5-minute range source " +
            $"{sourceTime.Median:F3}/{sourceTime.P95:F3} ms vs shadow " +
            $"{shadowTime.Median:F3}/{shadowTime.P95:F3} ms (median/p95, no fact-file reads)");
        TestContext.WriteLine($"{count}-capture 35 varied warm raw manifest reads (SHA-256 on each, " +
            $"byte-for-byte equality): source {sourceManifest[17]:F3}/{sourceManifest[33]:F3} ms vs " +
            $"shadow {shadowManifest[17]:F3}/{shadowManifest[33]:F3} ms (median/p95)");
        TestContext.WriteLine($"{count}-capture 35 varied warm 50-manifest keyset pages (read/hash/parse in timing, " +
            $"identity/checksum/byte equality outside): source {sourcePageFacts[17]:F3}/{sourcePageFacts[33]:F3} ms vs " +
            $"shadow {shadowPageFacts[17]:F3}/{shadowPageFacts[33]:F3} ms (median/p95)");
        TestContext.WriteLine($"{count}-capture 35 varied warm output descriptor reads (SHA-256 on both, " +
            $"byte-for-byte equality): source {sourceOutput[17]:F3}/{sourceOutput[33]:F3} ms vs " +
            $"shadow {shadowOutput[17]:F3}/{shadowOutput[33]:F3} ms (median/p95; capture lookup excluded)");
        TestContext.WriteLine("Page plans: source " + string.Join(" | ", await ExplainAsync(source,
            "SELECT capture_id FROM raw_captures WHERE agent_id='issue-106-gallery' AND capture_sequence>325 ORDER BY capture_sequence LIMIT 50;")
            .ConfigureAwait(false)) + "; shadow " + string.Join(" | ", await ExplainAsync(shadow,
            "SELECT capture_id FROM capture_lookup WHERE agent_id='issue-106-gallery' AND sequence>325 ORDER BY sequence LIMIT 50;")
            .ConfigureAwait(false)));
        TestContext.WriteLine("Time plans: source " + string.Join(" | ", await ExplainAsync(source,
            $"SELECT capture_id FROM raw_captures WHERE exposure_started_unix_ms>={epoch + 3_550_000L} AND exposure_started_unix_ms<{epoch + 3_850_000L} ORDER BY exposure_started_unix_ms,capture_sequence;")
            .ConfigureAwait(false)) + "; shadow " + string.Join(" | ", await ExplainAsync(shadow,
            $"SELECT capture_id FROM capture_lookup WHERE exposure_unix_ms>={epoch + 3_550_000L} AND exposure_unix_ms<{epoch + 3_850_000L} ORDER BY exposure_unix_ms,sequence;")
            .ConfigureAwait(false)));
        TestContext.WriteLine("Output plans: source " + string.Join(" | ", await ExplainAsync(source,
            $"SELECT output_identity_sha256,descriptor_json FROM processing_outputs WHERE capture_id='{Guid.Empty:N}' ORDER BY output_identity_sha256 LIMIT 1;")
            .ConfigureAwait(false)) + "; shadow " + string.Join(" | ", await ExplainAsync(shadow,
            $"SELECT output_identity,fact_path,fact_sha256 FROM output_lookup WHERE capture_id='{Guid.Empty:N}' ORDER BY output_identity LIMIT 1;")
            .ConfigureAwait(false)));
        var sizes = projection.Measure();
        TestContext.WriteLine($"Bytes: source main {new FileInfo(fixture.DatabasePath).Length}, " +
            $"source WAL {(File.Exists(fixture.DatabasePath + "-wal") ? new FileInfo(fixture.DatabasePath + "-wal").Length : 0)}, " +
            $"source SHM {(File.Exists(fixture.DatabasePath + "-shm") ? new FileInfo(fixture.DatabasePath + "-shm").Length : 0)}, " +
            $"shadow index {sizes.IndexBytes}, WAL {sizes.WalBytes}, raw facts {sizes.FactsBytes}, " +
            $"output facts {sizes.ProductBytes}; captures {sizes.Captures}, outputs {sizes.Outputs}");
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed benchmark SQL and numeric or constant IDs declared at call sites are explained.")]
    private static async Task<List<string>> ExplainAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var details = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false)) details.Add(reader.GetString(3));
        return details;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed test SQL is passed and all values are parameterized.")]
    private static async Task<(double Median, double P95)> MeasureAsync(SqliteConnection connection, string sql,
        string parameter, Func<int, object> value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var argument = command.Parameters.Add(parameter, SqliteType.Integer);
        var times = new double[35];
        for (var i = 0; i < times.Length; i++)
        {
            argument.Value = value(i);
            var started = Stopwatch.GetTimestamp();
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false)) _ = reader.GetString(0);
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(times);
        return (times[17], times[33]);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only fixed test query strings declared at call sites are used; values are parameterized.")]
    private static async Task<List<string>> ReadAsync(SqliteConnection connection, string sql, string parameter, object value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter, value);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var results = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false)) results.Add(reader.GetString(0));
        return results;
    }
}
