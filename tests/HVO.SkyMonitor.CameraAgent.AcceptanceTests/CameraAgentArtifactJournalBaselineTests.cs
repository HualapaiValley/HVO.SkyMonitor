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
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The measured SQLite backup and read path is synchronous by design.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only fixed SQL statements declared in this benchmark are executed.")]
public sealed class CameraAgentArtifactJournalBaselineTests
{
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task DisposableSceneBearingJournalSnapshotRestoreAndIndexedLookup()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("HVO_ISSUE1056_CAPTURES"), out var configured)
            ? configured : 1000;
        Assert.IsTrue(count is >= 100 and <= 12000);
        var output = Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-1056");
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, $"journal-{count}.json");
        // A failed run must not leave an older result at the advertised path.
        if (File.Exists(path)) File.Delete(path);
        using var fixture = await GalleryPerformanceFixture.CreateAsync(count, sceneBearingOutputs: true)
            .ConfigureAwait(false);
        var database = fixture.DatabasePath;
        var restored = Path.Combine(fixture.Root, "restored.db");
        using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
        writer.Open();
        using (var insert = writer.CreateCommand())
        {
            insert.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            insert.ExecuteNonQuery();
            insert.CommandText = "PRAGMA wal_autocheckpoint=0;";
            insert.ExecuteNonQuery();
            insert.CommandText = "UPDATE raw_captures SET retention_hold=0 WHERE capture_sequence=1;";
            insert.ExecuteNonQuery();
        }
        var before = new
        {
            Main = new FileInfo(database).Length,
            Wal = File.Exists(database + "-wal") ? new FileInfo(database + "-wal").Length : 0,
            Shm = File.Exists(database + "-shm") ? new FileInfo(database + "-shm").Length : 0
        };
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        using var source = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        source.Open();
        using var target = new SqliteConnection($"Data Source={restored};Pooling=False");
        target.Open();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        source.BackupDatabase(target);
        var snapshotMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var snapshotCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        var snapshotTrialsMs = new List<double> { snapshotMs };
        if (count == 1000)
        {
            // Reuse the seeded source but not the destination or its SQLite page cache.
            for (var trial = 1; trial < 5; trial++)
            {
                var trialPath = Path.Combine(fixture.Root, $"trial-{trial}.db");
                using (var trialTarget = new SqliteConnection($"Data Source={trialPath};Pooling=False"))
                {
                    trialTarget.Open();
                    started = Stopwatch.GetTimestamp();
                    source.BackupDatabase(trialTarget);
                    snapshotTrialsMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.AreEqual(new FileInfo(restored).Length, new FileInfo(trialPath).Length);
                }
                File.Delete(trialPath);
            }
        }
        process.Refresh();
        var rssAfterSnapshot = process.WorkingSet64;
        var sourceHash = Digest(source);
        var restoredHash = Digest(target);
        Assert.AreEqual(sourceHash, restoredHash);
        using (var check = target.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            Assert.AreEqual("ok", check.ExecuteScalar());
            check.CommandText = "SELECT retention_hold FROM raw_captures WHERE capture_sequence=1;";
            Assert.AreEqual(0L, Convert.ToInt64(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }
        var snapshotBytes = new FileInfo(restored).Length;
        target.Close();
        source.Close();
        var restorePath = Path.Combine(fixture.Root, "recovered.db");
        started = Stopwatch.GetTimestamp();
        File.Copy(restored, restorePath);
        var copyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        using (var reopened = new SqliteConnection($"Data Source={restorePath};Mode=ReadOnly;Pooling=False"))
        {
            reopened.Open();
            Assert.AreEqual(sourceHash, Digest(reopened));
        }
        var verifyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        var rssAfterRestore = process.WorkingSet64;
        using (var sources = writer.CreateCommand())
        {
            sources.CommandText = "SELECT COUNT(*) FROM processing_output_sources;";
            Assert.AreEqual(count * 4L, Convert.ToInt64(sources.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
        }

        var queries = new[]
        {
            ("capture", "SELECT manifest_json FROM raw_captures WHERE agent_id='issue-106-gallery' AND capture_sequence=$sequence"),
            ("output", "SELECT descriptor_json FROM processing_outputs WHERE capture_id=$capture ORDER BY node_id LIMIT 1"),
            ("page", "SELECT capture_id FROM raw_captures WHERE agent_id='issue-106-gallery' AND capture_sequence>$sequence ORDER BY capture_sequence LIMIT 50"),
            ("time", "SELECT capture_id FROM raw_captures WHERE exposure_started_unix_ms BETWEEN $from AND $to ORDER BY exposure_started_unix_ms LIMIT 50")
        };
        var timings = new List<object>();
        foreach (var (name, sql) in queries)
        {
            using var command = writer.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$sequence", count / 2);
            command.Parameters.AddWithValue("$capture", CaptureId(count / 2).ToString("N"));
            command.Parameters.AddWithValue("$from", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
            var samples = new double[35];
            for (var index = 0; index < samples.Length; index++)
            {
                if (name is "capture" or "output")
                {
                    var sequence = (index * 7919 % count) + 1;
                    command.Parameters["$sequence"].Value = sequence;
                    command.Parameters["$capture"].Value = CaptureId(sequence).ToString("N");
                }
                else
                {
                    var sequence = (index * 7919 % count) + 1;
                    command.Parameters["$sequence"].Value = sequence;
                    var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddSeconds(sequence * (62d * 24 * 3600 / count));
                    command.Parameters["$from"].Value = from.ToUnixTimeMilliseconds();
                    command.Parameters["$to"].Value = from.AddHours(12).ToUnixTimeMilliseconds();
                }
                started = Stopwatch.GetTimestamp();
                using var reader = command.ExecuteReader();
                var found = false;
                while (reader.Read())
                {
                    _ = reader.GetValue(0);
                    found = true;
                }
                Assert.IsTrue(found, $"{name} query returned no rows");
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            Array.Sort(samples);
            using var plan = writer.CreateCommand();
            plan.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (SqliteParameter parameter in command.Parameters)
            {
                plan.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
            }
            var details = new List<string>();
            using (var reader = plan.ExecuteReader())
            {
                while (reader.Read()) details.Add(reader.GetString(3));
            }
            timings.Add(new { Name = name, MedianMs = samples[17], P95Ms = samples[33], Plan = details });
        }
        var freshConnectionReads = new double[35];
        for (var index = 0; index < freshConnectionReads.Length; index++)
        {
            var sequence = (index * 7919 % count) + 1;
            started = Stopwatch.GetTimestamp();
            using var fresh = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            fresh.Open();
            using var lookup = fresh.CreateCommand();
            lookup.CommandText = "SELECT manifest_json FROM raw_captures WHERE agent_id='issue-106-gallery' AND capture_sequence=$sequence";
            lookup.Parameters.AddWithValue("$sequence", sequence);
            Assert.IsInstanceOfType<byte[]>(lookup.ExecuteScalar());
            freshConnectionReads[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(freshConnectionReads);
        var evidence = new
        {
            Captures = count,
            Outputs = count * 4,
            Before = before,
            SnapshotBytes = snapshotBytes,
            SnapshotMs = snapshotMs,
            SnapshotTrialsMs = snapshotTrialsMs,
            SnapshotCpuMs = snapshotCpuMs,
            WorkingSetBeforeBytes = rssBefore,
            WorkingSetAfterSnapshotBytes = rssAfterSnapshot,
            WorkingSetAfterRestoreBytes = rssAfterRestore,
            AdditionalFileBytesAtRestore = snapshotBytes + new FileInfo(restorePath).Length,
            CopyMs = copyMs,
            ReopenAndVerifyMs = verifyMs,
            SourceAndRestoreSha256 = sourceHash,
            Timings = timings,
            FreshConnectionCaptureLookup = new { MedianMs = freshConnectionReads[17], P95Ms = freshConnectionReads[33] },
            Notes = "Disposable gallery fixture; four parseable preview output descriptors with same large scene per capture. 35 varied-sequence or page/range reads per query, one trial; fresh SQLite connections still share Linux filesystem cache and do not establish cold disk latency. Restore is file copy plus separate read/digest, not full host recovery. Seed WAL was truncated before one uncheckpointed update. No artifact payloads or 30-day retention traffic."
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceOptions)).ConfigureAwait(false);
        TestContext.WriteLine(path);
    }

    private static string Digest(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var sql in new[]
        {
            "SELECT manifest_json FROM raw_captures ORDER BY capture_sequence",
            "SELECT descriptor_json FROM processing_outputs ORDER BY capture_sequence, node_id",
            "SELECT output_identity_sha256, source_artifact_id FROM processing_output_sources ORDER BY output_identity_sha256, source_ordinal"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetFieldType(0) == typeof(byte[]))
                {
                    using var stream = reader.GetStream(0);
                    int read;
                    while ((read = stream.Read(buffer)) != 0)
                        hash.AppendData(buffer.AsSpan(0, read));
                }
                else
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(0)));
                if (reader.FieldCount > 1)
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(reader.GetString(1)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Guid CaptureId(int sequence)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, sequence);
        bytes[4] = 1;
        bytes[15] = 106;
        return new Guid(bytes);
    }
}
