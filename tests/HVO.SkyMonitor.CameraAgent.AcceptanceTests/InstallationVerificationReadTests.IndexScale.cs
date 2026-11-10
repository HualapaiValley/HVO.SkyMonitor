using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

public sealed partial class InstallationVerificationReadTests
{
    [TestMethod]
    [TestCategory("Manual")]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each async fixture transfers ownership to await using.")]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "This measures the existing synchronous durable publication adapter, including its explicit fsync barrier.")]
    public async Task RealEndpointRetainedIndexScaleWithEvictedCache()
    {
        var output = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_EVIDENCE_DIR")
            ?? throw new InvalidOperationException(InstallationReadAttributionFixture.EvidenceRootGuidance);
        Assert.IsFalse(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_OWNER_CONFIGURATION")));
        var expectsIndex = Environment.GetEnvironmentVariable("HVO_INSTALLATION_READ_EXPECT_INDEX") == "1";
        var cases = new List<object>();
        foreach (var (sidecars, fanout) in new[] { (100_001, 1), (300_000, 1), (300_000, 3) })
        {
            await using var fixture = await InstallationReadAttributionFixture.CreateAsync(output, solar: true).ConfigureAwait(false);
            var corpus = await fixture.SeedScaleAsync(sidecars, fanout).ConfigureAwait(false);
            // Seed's schema initializer may leave a pooled writer with a mapped SHM.
            // Close fixture pools and checkpoint data before comparing main-file pages.
            SqliteConnection.ClearAllPools();
            using (var checkpoint = new SqliteConnection($"Data Source={fixture.JournalPath};Mode=ReadWrite;Pooling=False"))
            {
                await checkpoint.OpenAsync().ConfigureAwait(false);
                using var command = checkpoint.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var row = await command.ExecuteReaderAsync().ConfigureAwait(false);
                Assert.IsTrue(await row.ReadAsync().ConfigureAwait(false));
                Assert.AreEqual(0L, row.GetInt64(0), "Only an idle disposable fixture may be checkpointed.");
            }
            corpus = corpus with { JournalBytes = new FileInfo(fixture.JournalPath).Length };
            var qualificationStart = Stopwatch.GetTimestamp();
#if !HVO_BASELINE
            await fixture.QualifyLegacyScaleAsync().ConfigureAwait(false);
#endif
            var qualificationMs = Stopwatch.GetElapsedTime(qualificationStart).TotalMilliseconds;
            using var client = fixture.CreateClient();
            using var stages = new InstallationReadStageCollector();
            var first = await MeasureAsync("first-uncapped", fixture, client, stages).ConfigureAwait(false);
            for (var settling = 0; settling < 3; settling++) _ = await fixture.ReadAsync(client).ConfigureAwait(false);
            var inputsBefore = await fixture.HashRetainedInputsAsync().ConfigureAwait(false);
            var warm = new List<ReadMeasurement>();
            var evicted = new List<object>();
            for (var sample = 0; sample < 3; sample++)
                warm.Add(await MeasureAsync("warm", fixture, client, stages).ConfigureAwait(false));
            for (var sample = 0; sample < 3; sample++)
            {
                var residentPages = 0;
                var evictedFiles = 0;
                var evictionRetries = 0;
                var coordinationFiles = 0;
                // Only this marked, disposable fixture's metadata. No global drop_caches or live daemon/data.
                Assert.IsTrue(Path.GetFileName(fixture.Host.Root).StartsWith("iv-", StringComparison.Ordinal));
                foreach (var file in Directory.EnumerateFiles(fixture.IngressRoot, "*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith("-shm", StringComparison.Ordinal)) { coordinationFiles++; continue; }
                    if (new FileInfo(file).Length == 0) continue;
                    var pages = CameraAgentArtifactColdReadTests.EvictAndCount(file);
                    for (var retry = 0; pages > 0 && retry < 5; retry++)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
                        pages = CameraAgentArtifactColdReadTests.EvictAndCount(file);
                        evictionRetries++;
                    }
                    if (pages > 0) TestContext.WriteLine($"Fixture eviction residual: extension={Path.GetExtension(file)}, bytes={new FileInfo(file).Length}, pages={pages}");
                    residentPages += pages;
                    evictedFiles++;
                }
                Assert.AreEqual(0, residentPages, "Eviction must prove zero resident file pages before the timed read.");
                var read = await MeasureAsync("file-page-evicted", fixture, client, stages).ConfigureAwait(false);
                evicted.Add(new { EvictedFiles = evictedFiles, EvictionRetries = evictionRetries, CoordinationFilesExcluded = coordinationFiles, PreReadResidentPages = residentPages, Read = SafeMeasurement(read) });
            }
            if (expectsIndex)
            {
                Assert.AreEqual((long)sidecars, ProofCount(fixture));
                Assert.IsTrue(warm.All(read => ParseCount(read) == 0), "No proof-count overflow may reparse unchanged bytes.");
                Assert.IsTrue(warm.All(read => !read.Stages.Any(stage => stage.Name.StartsWith("deployment-location.validate-retained", StringComparison.Ordinal))),
                    "Qualified endpoint work must not scan the retained corpus.");
            }
            Assert.IsTrue(warm.All(read => read.Identity.GetRawText() == first.Identity.GetRawText()));
            Assert.AreEqual(inputsBefore, await fixture.HashRetainedInputsAsync().ConfigureAwait(false));
            var publication = new List<object>();
            var statePath = Path.Combine(fixture.IngressRoot, "benchmark-owner-state.json");
            for (var sample = 0; sample < 5; sample++)
            {
                var temporary = statePath + ".prepared";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Sequence = sample });
                var allocated = GC.GetTotalAllocatedBytes(true);
                var io = ReadIo();
                var started = Stopwatch.GetTimestamp();
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                HVO.SkyMonitor.CameraAgent.Common.Storage.CameraAgentFileSystem.PublishPrepared(fixture.IngressRoot, temporary, statePath, overwrite: true);
                HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressFileStore.SyncDirectory(fixture.IngressRoot);
                publication.Add(new
                {
                    ElapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                    IoDelta = IoDelta(io)
                });
                var afterWrite = await fixture.ReadAsync(client).ConfigureAwait(false);
                Assert.AreEqual(first.Identity.GetRawText(), afterWrite.GetRawText(), "Managed publication must keep current identity qualified.");
            }
            object? restartQualification = null;
#if !HVO_BASELINE
            using (var restartTrace = StartTrace("durable-ledger-restart")!)
            {
                var started = Stopwatch.GetTimestamp();
                await fixture.Host.RestartAsync().ConfigureAwait(false);
                _ = await fixture.Loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var observed = stages.ForTrace(restartTrace.TraceId);
                var reparsed = observed.Sum(stage => stage.NumericTags.TryGetValue("attribution.parse.count", out var count)
                    ? Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture) : 0L);
                Assert.AreEqual(1L, reparsed, "Only the newly published auxiliary state may miss persisted proofs, including beyond the former cap.");
                using var restartedClient = fixture.CreateClient();
                Assert.AreEqual(first.Identity.GetRawText(), (await fixture.ReadAsync(restartedClient).ConfigureAwait(false)).GetRawText());
                restartQualification = new { ElapsedMs = elapsed, Parses = reparsed, Requalified = true };
            }
#endif
            cases.Add(new
            {
                Captures = corpus.JournalCount,
                Sidecars = corpus.FileCount,
                RawJournalRows = corpus.JournalCount,
                ProductsPerCapture = fanout,
                Corpus = corpus,
                QualificationMs = qualificationMs,
                FirstUncapped = SafeMeasurement(first),
                Warm = warm.Select(SafeMeasurement).ToArray(),
                Evicted = evicted,
                ManagedPublication = publication,
                RestartQualification = restartQualification,
                PersistedProofs = ProofCount(fixture)
            });
            var path = Path.Combine(output, "installation-read-ledger-scale.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Revision = Environment.GetEnvironmentVariable("HVO_ATTRIBUTION_REVISION"),
                Configuration = "Installer CameraConfiguration.Generate; fixture-local storage roots; completed synthetic owner",
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                HeapAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                CacheDefinition = "Idle disposable journal WAL checkpointed/truncated after construction; fsync + POSIX_FADV_DONTNEED and mincore=0 for retained data/main DB/checkpoint files before each evicted sample; transient SQLite SHM coordination files excluded; metadata/controller caches and full host cold start uncontrolled",
                Cases = cases
            }, InstallationReadAttributionFixture.EvidenceJson)).ConfigureAwait(false);
            TestContext.AddResultFile(path);
        }
    }

    private static long ParseCount(ReadMeasurement read)
        => read.Stages.Where(stage => stage.NumericTags.ContainsKey("attribution.parse.count"))
            .Sum(stage => Convert.ToInt64(stage.NumericTags["attribution.parse.count"], System.Globalization.CultureInfo.InvariantCulture));

    private static object SafeMeasurement(ReadMeasurement read) => new
    {
        read.Kind,
        read.ElapsedMs,
        read.CpuMs,
        read.ProcessAllocatedBytes,
        read.WorkingSetBytes,
        read.PeakWorkingSetBytes,
        read.IoDelta,
        Parses = ParseCount(read),
        RetainedScanSpans = read.Stages.Count(stage => stage.Name.StartsWith("deployment-location.validate-retained", StringComparison.Ordinal)),
        CacheHits = read.Stages.Where(stage => stage.NumericTags.ContainsKey("attribution.cache_hits"))
            .Sum(stage => Convert.ToInt64(stage.NumericTags["attribution.cache_hits"], System.Globalization.CultureInfo.InvariantCulture)),
        Identity = read.Identity.EnumerateObject().Where(property => property.Name != "rawIngressRoot")
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
    };

    private static long ProofCount(InstallationReadAttributionFixture fixture)
    {
        var path = Path.Combine(fixture.IngressRoot, ".location", "retained-validation.v2.db");
        if (File.Exists(path))
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM proofs;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        path = Path.Combine(fixture.IngressRoot, ".location", "retained-validation.v1.protected");
        if (!File.Exists(path)) return 0;
        var plaintext = fixture.Host.Services.GetRequiredService<IDeploymentLocationProtector>().Unprotect(File.ReadAllBytes(path));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(plaintext.AsSpan(40));
    }
}
