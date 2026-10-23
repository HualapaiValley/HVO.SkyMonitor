using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class EnvironmentalObservationPerformanceTests
{
    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue1557_MaximumLineage_RecordsProductionBaseline()
        => await MeasureMaximumLineageAsync(baseline: true).ConfigureAwait(false);

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue1558_MaximumLineage_RecordsBoundedValidation()
        => await MeasureMaximumLineageAsync(baseline: false).ConfigureAwait(false);

    private async Task MeasureMaximumLineageAsync(bool baseline)
    {
        var issue = baseline ? "1557" : "1558";
        Assert.AreEqual("1", Environment.GetEnvironmentVariable($"HVO_ISSUE_{issue}_EVIDENCE"),
            "Suppress fixture recurring workers for process resource attribution.");
        Assert.IsTrue(GCSettings.IsServerGC, "Run with DOTNET_gcServer=1 to match the production host GC mode.");
        var root = FindRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(root,
            typeof(EnvironmentalObservationPerformanceTests), typeof(EnvironmentalObservationIngestService))
            .ConfigureAwait(false);
        var output = Path.Combine(root, "TestResults", $"issue-{issue}", source.OutputDirectoryName, source.RunId);
        Directory.CreateDirectory(output);
        var connection = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorLineage{issue}_{Guid.NewGuid():N}",
            ApplicationName = $"HVO.Issue{issue}.SyntheticLineage"
        };
        var dbOptions = LineageOptions(connection.ConnectionString);
        await using var setup = new ApplicationDbContext(dbOptions);
        using var telemetry = new EnvironmentalObservationTelemetry();
        try
        {
            await setup.Database.MigrateAsync().ConfigureAwait(false);
            var target = await SeedTargetAsync(setup).ConfigureAwait(false);
            var inputs = Enumerable.Range(0, 256).Select(index => CreateObservation(index, target.SiteId, target.AgentId) with
            {
                ObservationId = DeterministicGuid(600_000 + index),
                Source = CreateObservation(index % 8, target.SiteId, target.AgentId).Source
            }).ToArray();
            foreach (var input in inputs)
            {
                await IngestLineageAsync(dbOptions, telemetry, input, Epoch).ConfigureAwait(false);
            }
            // A permutation makes an accidental sort or deduplication visible in durable ordinals.
            var references = Enumerable.Range(0, 256).Select(index => inputs[index * 73 % 256])
                .Select(input => new EnvironmentalObservationReference(
                    EnvironmentalObservationJson.ComputeSourceIdentitySha256(input), input.ObservationId)).ToArray();
            var sequence = 700_000;
            EnvironmentalObservationV1 Next(int count, int lane = 0) => CreateObservation(
                sequence++, target.SiteId, target.AgentId) with
            {
                Source = CreateObservation(lane, target.SiteId, target.AgentId).Source with
                {
                    Provider = "issue-1557-derived",
                    SourceId = $"derived-{lane}",
                    Kind = EnvironmentalObservationSourceKind.Derived
                },
                Lineage = references.Take(count).ToArray()
            };

            var scenarios = new List<object>();
            var accepted = new List<(EnvironmentalObservationV1 Fact, EnvironmentalObservationIngestResult Receipt)>();
            foreach (var count in new[] { 1, 32, 128, 256 })
            {
                foreach (var concurrency in new[] { 1, 4, 8 })
                {
                    // Establish every lane's source before timing so first-source creation is not hidden in one arm.
                    for (var lane = 0; lane < concurrency; lane++)
                    {
                        var warm = Next(count, lane);
                        accepted.Add((warm, await IngestLineageAsync(dbOptions, telemetry, warm, BenchmarkNow).ConfigureAwait(false)));
                    }
                    for (var warmup = 0; warmup < 5; warmup++)
                    {
                        var warm = Next(count, warmup % concurrency);
                        accepted.Add((warm, await IngestLineageAsync(dbOptions, telemetry, warm, BenchmarkNow).ConfigureAwait(false)));
                    }
                    var facts = Enumerable.Range(0, 30).Select(index => Next(count, index % concurrency)).ToArray();
                    var before = await ReadLineageStorageAsync(connection.ConnectionString).ConfigureAwait(false);
                    var probes = new LineageProbe[30];
                    var latency = new double[30];
                    var receipts = new EnvironmentalObservationIngestResult?[30];
                    var failures = new int?[30];
                    using var process = Process.GetCurrentProcess();
                    var cpuBefore = process.TotalProcessorTime;
                    var allocations = GC.GetTotalAllocatedBytes(true);
                    var peakRss = process.WorkingSet64;
                    using var sampling = new CancellationTokenSource();
                    var sampler = SampleLineageRssAsync(process, value => peakRss = Math.Max(peakRss, value), sampling.Token);
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        await Parallel.ForEachAsync(Enumerable.Range(0, 30),
                            new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (index, cancellationToken) =>
                            {
                                var probe = new LineageProbe(capture: false);
                                probes[index] = probe;
                                var options = LineageOptions(connection.ConnectionString, probe);
                                var operation = Stopwatch.GetTimestamp();
                                try
                                {
                                    receipts[index] = await IngestLineageAsync(options, telemetry, facts[index], BenchmarkNow, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception exception) when (LineageSqlFailure(exception) is not null)
                                {
                                    // Record every known SQL failure; never retry or silently replace a failed attempt.
                                    failures[index] = LineageSqlFailure(exception);
                                    probe.Finish();
                                }
                                latency[index] = Stopwatch.GetElapsedTime(operation).TotalMilliseconds;
                            }).ConfigureAwait(false);
                    }
                    finally
                    {
                        await sampling.CancelAsync().ConfigureAwait(false);
                        await sampler.ConfigureAwait(false);
                    }
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var allocated = GC.GetTotalAllocatedBytes(true) - allocations;
                    var cpu = process.TotalProcessorTime - cpuBefore;
                    var after = await ReadLineageStorageAsync(connection.ConnectionString).ConfigureAwait(false);
                    for (var index = 0; index < facts.Length; index++)
                    {
                        if (receipts[index] is { } receipt)
                        {
                            Assert.AreEqual(EnvironmentalObservationIngestDisposition.Accepted, receipt.Disposition);
                            accepted.Add((facts[index], receipt));
                        }
                        else
                        {
                            Assert.IsNotNull(failures[index]);
                            var identity = EnvironmentalObservationJson.ComputeSourceIdentitySha256(facts[index]);
                            Assert.IsFalse(await setup.EnvironmentalObservations.AnyAsync(row =>
                                row.SourceIdentitySha256 == identity && row.ObservationId == facts[index].ObservationId).ConfigureAwait(false),
                                "A failed attempt must leave no durable observation or its cascading lineage rows.");
                        }
                    }
                    Assert.IsTrue(probes.Where((_, index) => failures[index] is null).All(probe => probe.SourceLookups == (baseline ? count + 1 : 2) && probe.ObservationLookups == (baseline ? count + 1 : 2)),
                        "Inspect the attributed own-identity and lineage validation SELECTs.");
                    Assert.IsTrue(probes.Where((_, index) => failures[index] is null).All(probe => probe.TransactionMilliseconds > 0 && probe.RetentionHeldMilliseconds > 0),
                        "Every derived operation must expose its transaction and retention lock timing.");
                    scenarios.Add(new
                    {
                        References = count,
                        Concurrency = concurrency,
                        Warmups = 5,
                        MeasuredAttempts = 30,
                        Accepted = receipts.Count(receipt => receipt is not null),
                        DeadlockVictims = failures.Count(code => code == 1205),
                        SqlLockFailures = failures.Count(code => code is 51006 or 51007),
                        SqlCommandTimeouts = failures.Count(code => code == -2),
                        Outcomes = failures.Select(code => code is null ? "accepted" : $"sql-{code}").ToArray(),
                        ElapsedMilliseconds = elapsed.TotalMilliseconds,
                        AcceptedIngestsPerSecond = receipts.Count(receipt => receipt is not null) / elapsed.TotalSeconds,
                        AttemptsPerSecond = 30 / elapsed.TotalSeconds,
                        AllAttemptLatencyMilliseconds = LineageDistribution(latency),
                        TransactionMilliseconds = LineageDistribution(probes.Select(probe => probe.TransactionMilliseconds)),
                        RetentionLockHeldMilliseconds = LineageDistribution(probes.Select(probe => probe.RetentionHeldMilliseconds)),
                        SourceLockAcquireMilliseconds = LineageDistribution(probes.Select(probe => probe.SourceLockMilliseconds)),
                        RetentionLockAcquireMilliseconds = LineageDistribution(probes.Select(probe => probe.RetentionAcquireMilliseconds)),
                        SqlCommands = probes.Select(probe => probe.CommandCount).ToArray(),
                        SourceLookups = probes.Select(probe => probe.SourceLookups).ToArray(),
                        ObservationLookups = probes.Select(probe => probe.ObservationLookups).ToArray(),
                        LineageOnlyLookupsPerAcceptedIngest = baseline ? 2 * count : 2,
                        ClientCpuMilliseconds = cpu.TotalMilliseconds,
                        ClientAllocatedBytes = allocated,
                        ClientPeakSampledRssBytes = peakRss,
                        StorageDelta = after.Delta(before)
                    });
                    await File.WriteAllTextAsync(Path.Combine(output, "progress.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { Source = source, Scenarios = scenarios }, EvidenceJsonOptions)).ConfigureAwait(false);
                    TestContext.WriteLine("refs={0} c={1} p95_ms={2:F3} commands={3} deadlocks={4}",
                        count, concurrency, Percentile(latency, 0.95), probes[0].CommandCount, failures.Count(code => code == 1205));
                }
            }

            // Every row, including warmups, is checked outside the timed boundary.
            foreach (var item in accepted)
            {
                await VerifyLineageAsync(dbOptions, item.Fact, item.Receipt).ConfigureAwait(false);
            }
            var replay = accepted.Last();
            var duplicate = await IngestLineageAsync(dbOptions, telemetry, replay.Fact, BenchmarkNow.AddDays(1)).ConfigureAwait(false);
            Assert.AreEqual(replay.Receipt with { Disposition = EnvironmentalObservationIngestDisposition.Duplicate }, duplicate);
            var controls = await RunLineageControlsAsync(dbOptions, telemetry, Next(256), references).ConfigureAwait(false);

            var sql = new List<object>();
            foreach (var count in new[] { 1, 32, 128, 256 })
            {
                var diagnosticFact = Next(count);
                var diagnostic = new LineageProbe(capture: true);
                var diagnosticReceipt = await IngestLineageAsync(LineageOptions(connection.ConnectionString, diagnostic),
                    telemetry, diagnosticFact, BenchmarkNow).ConfigureAwait(false);
                await VerifyLineageAsync(dbOptions, diagnosticFact, diagnosticReceipt).ConfigureAwait(false);
                await setup.EnvironmentalObservations.Where(row => row.Id == diagnosticReceipt.RecordId)
                    .ExecuteDeleteAsync().ConfigureAwait(false);
                sql.Add(new
                {
                    References = count,
                    Replay = await ReplayLineageCommandsAsync(connection.ConnectionString, diagnostic.Commands, output,
                        baseline ? 2 * count + 2 : 4, $"sql-replay-{count}.json.gz").ConfigureAwait(false)
                });
                Assert.AreEqual(0, await setup.EnvironmentalObservations.CountAsync(row => row.Id == diagnosticReceipt.RecordId).ConfigureAwait(false),
                    "The actual-plan replay must roll back all diagnostic writes.");
            }
            var retention = await VerifyLineageRetentionAsync(connection.ConnectionString, telemetry, inputs).ConfigureAwait(false);
            var deadlockGraphs = await ReadLineageDeadlocksAsync(connection.ConnectionString, output).ConfigureAwait(false);
            var evidence = new
            {
                Schema = baseline ? "hvo-issue-1557-maximum-lineage-baseline-v2" : "hvo-issue-1558-maximum-lineage-bounded-v1",
                Source = source,
                RecordedAtUtc = DateTimeOffset.UtcNow,
                ProductionBoundary = baseline ? "EnvironmentalObservationIngestService.IngestAsync; original per-reference validation"
                    : "EnvironmentalObservationIngestService.IngestAsync; two bounded EF lineage projections",
                Environment = new
                {
                    Os = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    Sdk = RunCommand("dotnet", root, "--version"),
                    Cpu = ReadCpuIdentity(),
                    Environment.ProcessorCount,
                    ServerGc = GCSettings.IsServerGC,
                    SqlVersion = await ReadSqlServerVersionAsync(setup).ConfigureAwait(false),
                    SqlImage = IntegrationTestFixture.SqlServerImage,
                    Configuration = "Release",
                    Topology = "Direct service invocation, independent EF context per ingest, disposable SQL database on shared fixture container; no HTTP/object-store payload"
                },
                Workload = new
                {
                    Sizes = new[] { 1, 32, 128, 256 },
                    Concurrency = new[] { 1, 4, 8 },
                    InputSources = 8,
                    InputObservations = 256,
                    DerivedSources = "one per concurrency lane; avoids serializing every operation on one source lock",
                    Setup = "lane-source priming followed by exactly five untimed warmups and 30 timed ingests per arm",
                    Fixture = "DeterministicGuid(600000+i), eight production-ingested source descriptors, ordinal permutation i*73%256",
                    Backlog = "N/A: direct closed-loop calls; no durable worker queue"
                },
                Method = new
                {
                    Timing = "Nearest-rank percentiles. Client wall time includes context construction/disposal; transaction from TransactionStarted through TransactionCommitted; shared retention lock from successful sp_getapplock return through commit.",
                    Resources = "Process-wide CPU and total managed allocations; RSS sampled every 10ms. Includes probes/sampling, excludes SQL Server. Dedicated database DMV deltas cover index leaf operations and log file bytes/size/used space.",
                    Sql = "Separate untimed exact typed-command replay at each size in a rolled-back transaction with STATISTICS XML/IO. Actual plans/rows/reads and write operators are in sql-replay-{size}.json.gz. Replay CPU/reads are diagnostic, not natural concurrency CPU/reads.",
                    LockLimits = "Client completion timestamps approximate server lock release; network/commit response time is included. Source-lock acquisition includes wait and round trip. No claim of deployed throughput or unchanged W3M/W4 budgets.",
                    Writes = "SQL Server STATISTICS IO has no logical-write counter; leaf insert/update/delete DMV deltas and actual plan write-row counts are explicit proxies. Log physical bytes may be buffered."
                },
                Scenarios = scenarios,
                DeadlockGraphs = deadlockGraphs,
                SqlReplay = sql,
                Controls = controls,
                Retention = retention,
                Correctness = new
                {
                    VerifiedAcceptedRows = accepted.Count,
                    ExactOrdinalsAndCompositeIdentity = true,
                    CanonicalPayloadHashes = true,
                    ReplayPreservesRecordIdHashAndReceiptTime = true,
                    DiagnosticReplayRolledBack = true
                },
                Handoff = "Own-identity reads are counted separately from lineage validation. EF insert batches vary with lineage size. All SQL1205/51006/51007/command-timeout failures are retained, checked for atomic rollback, never retried or counted as accepted throughput. This metadata-only 5+30 workload does not replace W3M/W4 qualification."
            };
            var evidencePath = Path.Combine(output, baseline ? "maximum-lineage-baseline.json" : "maximum-lineage-bounded.json");
            await EvidenceSourceIdentity.WriteJsonAsync(evidencePath, evidence, EvidenceJsonOptions).ConfigureAwait(false);
            TestContext.AddResultFile(evidencePath);
            TestContext.WriteLine("issue{0} evidence: {1}", issue, output);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static bool IsLineageDeadlock(Exception exception) => LineageSqlFailure(exception) == 1205;

    private static int? LineageSqlFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql && sql.Number is 1205 or 51006 or 51007 or -2) return sql.Number;
        return null;
    }

    private static DbContextOptions<ApplicationDbContext> LineageOptions(string connection, LineageProbe? probe = null)
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (probe is not null)
        {
            probe.Transactions.Owner = probe;
            builder.AddInterceptors(probe, probe.Transactions);
        }
        return builder.Options;
    }

    private static async Task<EnvironmentalObservationIngestResult> IngestLineageAsync(
        DbContextOptions<ApplicationDbContext> options, EnvironmentalObservationTelemetry telemetry,
        EnvironmentalObservationV1 fact, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = new ApplicationDbContext(options);
        return await new EnvironmentalObservationIngestService(db, new FixedTimeProvider(now),
            Options.Create(new EnvironmentalObservationOptions()), telemetry, NullLogger<EnvironmentalObservationIngestService>.Instance)
            .IngestAsync(fact, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyLineageAsync(DbContextOptions<ApplicationDbContext> options,
        EnvironmentalObservationV1 fact, EnvironmentalObservationIngestResult receipt)
    {
        await using var db = new ApplicationDbContext(options);
        var record = await db.EnvironmentalObservations.AsNoTracking().SingleAsync(row => row.Id == receipt.RecordId).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalObservationJson.ComputeContentSha256(fact), record.PayloadSha256);
        Assert.AreEqual(receipt.ContentSha256, record.PayloadSha256);
        var lineage = await db.EnvironmentalObservationLineage.AsNoTracking()
            .Where(row => row.DerivedObservationRecordId == receipt.RecordId).OrderBy(row => row.Ordinal)
            .Select(row => new { row.Ordinal, row.SourceObservation!.ObservationId, row.SourceObservation.Source!.IdentitySha256 })
            .ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(fact.Lineage.Count, lineage.Length);
        for (var ordinal = 0; ordinal < lineage.Length; ordinal++)
        {
            Assert.AreEqual(ordinal, lineage[ordinal].Ordinal);
            Assert.AreEqual(fact.Lineage[ordinal].ObservationId, lineage[ordinal].ObservationId);
            Assert.AreEqual(fact.Lineage[ordinal].SourceIdentitySha256, lineage[ordinal].IdentitySha256);
        }
    }

    private static async Task<object> RunLineageControlsAsync(DbContextOptions<ApplicationDbContext> options,
        EnvironmentalObservationTelemetry telemetry, EnvironmentalObservationV1 fact, EnvironmentalObservationReference[] references)
    {
        var cases = new Dictionary<string, EnvironmentalObservationReference[]>
        {
            ["missing-source"] = [.. references.Take(255), new(new string('F', 64), references[0].ObservationId)],
            ["missing-observation"] = [.. references.Take(255), new(references[0].SourceIdentitySha256, DeterministicGuid(900_001))],
            ["mismatched-source-observation"] = [.. references.Take(255), new(references[1].SourceIdentitySha256, references[0].ObservationId)],
            ["duplicate-reference"] = [.. references.Take(255), references[0]]
        };
        await using var db = new ApplicationDbContext(options);
        var before = await db.EnvironmentalObservations.CountAsync().ConfigureAwait(false);
        var sourceBefore = await db.EnvironmentalObservationSources.CountAsync().ConfigureAwait(false);
        var lineageBefore = await db.EnvironmentalObservationLineage.CountAsync().ConfigureAwait(false);
        foreach (var item in cases)
        {
            var attempt = fact with { Lineage = item.Value };
            if (item.Key == "duplicate-reference")
                await Assert.ThrowsAsync<ArgumentException>(() => IngestLineageAsync(options, telemetry, attempt, BenchmarkNow)).ConfigureAwait(false);
            else
                await Assert.ThrowsAsync<EnvironmentalObservationConflictException>(() => IngestLineageAsync(options, telemetry, attempt, BenchmarkNow)).ConfigureAwait(false);
            Assert.AreEqual(before, await db.EnvironmentalObservations.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(sourceBefore, await db.EnvironmentalObservationSources.CountAsync().ConfigureAwait(false));
            Assert.AreEqual(lineageBefore, await db.EnvironmentalObservationLineage.CountAsync().ConfigureAwait(false));
        }
        return new { Cases = cases.Keys, AtomicObservationSourceAndLineageCounts = true };
    }

    private static async Task<object> VerifyLineageRetentionAsync(string connection, EnvironmentalObservationTelemetry telemetry,
        EnvironmentalObservationV1[] inputs)
    {
        var unpinned = inputs[0] with { ObservationId = DeterministicGuid(950_000) };
        await IngestLineageAsync(LineageOptions(connection), telemetry, unpinned, Epoch).ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(builder => builder.UseSqlServer(connection));
        await using var provider = services.BuildServiceProvider();
        var worker = new EnvironmentalObservationRetentionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EnvironmentalObservationOptions { ReceiptRetentionDays = 30, RetentionBatchSize = 1000 }),
            new FixedTimeProvider(BenchmarkNow), new EnvironmentalRetentionState(), telemetry,
            NullLogger<EnvironmentalObservationRetentionWorker>.Instance);
        var deleted = await worker.SweepAsync(CancellationToken.None).ConfigureAwait(false);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ids = inputs.Select(input => input.ObservationId).ToArray();
        Assert.AreEqual(256, await db.EnvironmentalObservations.CountAsync(row => ids.Contains(row.ObservationId)).ConfigureAwait(false));
        Assert.AreEqual(1, deleted, "Only the unpinned old control is eligible; all 256 inputs are pinned.");
        Assert.IsFalse(await db.EnvironmentalObservations.AnyAsync(row => row.ObservationId == unpinned.ObservationId).ConfigureAwait(false));
        return new { OldInputRows = 256, OldInputRowsRetained = 256, UnpinnedControlDeleted = true, Deleted = deleted, ProductionSweep = true };
    }

    private static async Task SampleLineageRssAsync(Process process, Action<long> sample, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                process.Refresh();
                sample(process.WorkingSet64);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static object LineageDistribution(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        return new
        {
            Median = Percentile(samples, 0.5),
            P95 = Percentile(samples, 0.95),
            P99 = Percentile(samples, 0.99),
            Maximum = samples.Max(),
            Samples = samples
        };
    }
}
