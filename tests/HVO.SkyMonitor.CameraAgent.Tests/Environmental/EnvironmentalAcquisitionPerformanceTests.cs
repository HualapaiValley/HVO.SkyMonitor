using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "The evidence harness executes only fixed SQL and fixed query-plan statements defined in this file.")]
public sealed class EnvironmentalAcquisitionPerformanceTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private const int KindCount = 12;
    private const int A2FactCount = 10_000;
    private const int A2BatchSize = 100;
    private const int A3PageSize = 50;
    private const int A3Warmups = 20;
    private const int A3Measurements = 200;
    private const int A4CaptureCount = 360;
    private const int A5FactCount = 600;
    private const int A5RestartAfter = 300;
    private const int A5DeliveryCapacity = 100;
    private const long WorkingSetCeilingBytes = 256L * 1024 * 1024;
    private const int LocalHistoryP95CeilingMilliseconds = 1_000;
    private const int AuthenticatedHistoryP95CeilingMilliseconds = 5_000;
    private const int ResponsePayloadCeilingBytes = 512 * 1024;
    private const string RepeatCommand =
        "dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj " +
        "--no-build --configuration Release --filter \"FullyQualifiedName=" +
        "HVO.SkyMonitor.CameraAgent.Tests.Environmental.EnvironmentalAcquisitionPerformanceTests." +
        "Issue209_A1ToA6_RecordsReproducibleEvidence\"";
    private static readonly DateTimeOffset Epoch = new(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly EnvironmentalObservationResolvedTarget DeliveryTarget = new(
        Guid.Parse("20900000-0000-0000-0000-000000000001"),
        Guid.Parse("20900000-0000-0000-0000-000000000002"));
    private static readonly string[] HistoryPositions = ["first", "middle", "late"];
    private static readonly JsonSerializerOptions EvidenceJsonOptions = CreateEvidenceJsonOptions();

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Issue209_A1ToA6_RecordsReproducibleEvidence()
    {
        if (!string.Equals(BuildConfiguration, "Release", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Canonical issue #209 evidence must be collected from a Release build.");
        }

        var repositoryRoot = FindRepositoryRoot();
        var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
        var revision = git.Dirty
            ? $"worktree-{git.DirtyFingerprintSha256[..12].ToUpperInvariant()}"
            : git.CommitSha[..12].ToUpperInvariant();
        var evidenceDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-209", revision);
        var evidencePath = Path.Combine(evidenceDirectory, "environmental-acquisition-performance.json");
        var workRoot = Path.Combine(Path.GetTempPath(), $"issue-209-environment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceDirectory);
        Directory.CreateDirectory(workRoot);

        StabilizeGc();
        using var workingSet = new WorkingSetTracker();
        using var signals = new RuntimeSignalRecorder();
        try
        {
            var a1 = await MeasureA1Async(workingSet).ConfigureAwait(false);
            var a2Root = Path.Combine(workRoot, "a2-10000");
            var a2 = await MeasureA2Async(a2Root, workingSet).ConfigureAwait(false);
            var a3 = await MeasureA3Async(
                Path.Combine(workRoot, "a3-1000"), a2Root, workingSet, signals).ConfigureAwait(false);
            var a4 = await MeasureA4Async(workingSet).ConfigureAwait(false);
            var a5 = await MeasureA5Async(Path.Combine(workRoot, "a5"), workingSet).ConfigureAwait(false);
            var a6 = await MeasureA6Async(Path.Combine(workRoot, "a6"), workingSet).ConfigureAwait(false);

            signals.RecordObservableInstruments();
            var signalEvidence = signals.Snapshot();
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.triggers"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.acquisitions"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.journal.commits"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.associations"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.acquisition.duration"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.journal.commit.duration"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.payload.size"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.history.query.duration"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.inflight"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.queue.depth"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.journal.records"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.journal.bytes"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.journal.oldest.age"));
            Assert.IsTrue(signalEvidence.MetricNames.Contains("skymonitor.environment.local.sources"));
            Assert.IsTrue(signalEvidence.ActivityNames.Contains("environment.acquire"));
            Assert.IsTrue(signalEvidence.ActivityNames.Contains("environment.local.commit"));
            Assert.IsTrue(signalEvidence.ActivityNames.Contains("environment.associate"));
            Assert.IsTrue(signalEvidence.ActivityNames.Contains("environment.history.query"));
            Assert.IsTrue(signalEvidence.ActivityNames.Contains("environment.delivery.project"));
            Assert.IsEmpty(signalEvidence.ForbiddenMetricTagKeys);
            Assert.IsFalse(signalEvidence.ContainsForbiddenSignalValue);

            workingSet.Sample();
            Assert.IsTrue(
                workingSet.GrowthBytes <= WorkingSetCeilingBytes,
                $"Working-set growth {workingSet.GrowthBytes} exceeded {WorkingSetCeilingBytes} bytes.");

            var evidence = new
            {
                Schema = "hvo-issue-209-environmental-acquisition-performance-v1",
                Issue = 209,
                GeneratedUtc = DateTimeOffset.UtcNow,
                Revision = revision,
                Git = git,
                RepeatCommand,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                    RuntimeVersion = Environment.Version.ToString(),
                    Configuration = BuildConfiguration,
                    PinnedSdk = ReadPinnedSdkVersion(repositoryRoot),
                    ServerGc = GCSettings.IsServerGC,
                    ProcessorCount = Environment.ProcessorCount,
                    TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    StopwatchFrequency = Stopwatch.Frequency,
                    SqliteVersion = await ReadSqliteVersionAsync(a2Root).ConfigureAwait(false),
                    Storage = new
                    {
                        FileSystem = new DriveInfo(Path.GetPathRoot(workRoot)!).DriveFormat,
                        PathRoot = Path.GetPathRoot(workRoot),
                        PhysicalDeviceClass = "Unavailable inside the test process; no physical-media inference is made."
                    }
                },
                Fixture = new
                {
                    EpochUtc = Epoch,
                    Seed = 209,
                    LatitudeDegrees = 35.5599378,
                    LongitudeDegrees = -113.9119818,
                    ElevationMeters = 520,
                    TimeZoneId = "America/Phoenix",
                    PeriodSeconds = 30,
                    ValidForSeconds = 120,
                    StaleAfterSeconds = 45,
                    ObservationKinds = Enum.GetNames<EnvironmentalObservationKind>()
                },
                A1 = a1,
                A2 = a2,
                A3 = a3,
                A4 = a4,
                A5 = a5,
                A6 = a6,
                RuntimeSignals = signalEvidence,
                SafetyCeilings = new
                {
                    LocalHistoryP95Milliseconds = LocalHistoryP95CeilingMilliseconds,
                    AuthenticatedHistoryP95Milliseconds = AuthenticatedHistoryP95CeilingMilliseconds,
                    ResponsePayloadBytes = ResponsePayloadCeilingBytes,
                    WorkingSetGrowthBytes = WorkingSetCeilingBytes,
                    ObservedWorkingSetStartBytes = workingSet.StartBytes,
                    ObservedWorkingSetPeakBytes = workingSet.PeakBytes,
                    ObservedWorkingSetGrowthBytes = workingSet.GrowthBytes,
                    Result = "Pass"
                },
                Limitations = new[]
                {
                    "A3 invokes the production local SQLite history API directly. It enforces the stricter 1000 ms local ceiling; the 5000 ms authenticated HTTP ceiling is recorded but cannot be claimed without hosting the CameraAgent authentication stack.",
                    "A3 response bytes are the exact retained canonical fact payload bytes returned by a page. HTTP framing and UI projection bytes are not present in this test project path.",
                    "Microsoft.Data.Sqlite does not expose physical bytes written, statement counters, automatic checkpoint counts, or fsync counts. A2 records API transaction boundaries, synchronous=FULL, WAL footprints, and explicit checkpoint results instead.",
                    "The current local CommitLocalAsync API has no fault-injection seam. A6 injects caller-visible failures immediately before and immediately after the production local commit and verifies restart state at both boundaries.",
                    "The current regime trigger contract carries RegimeChange but not named from/to regimes. A4 records one pinned Night-to-Twilight logical transition as exactly one production RegimeChange trigger.",
                    "A5 uses the production durable projection, claim, acknowledgement, and worker drain path with an in-process idempotent receiver; it does not claim LogicHost network or SQL Server performance."
                },
                Correctness = new
                {
                    A1DeterministicCanonicalHashes = true,
                    A2ExactTargetlessRowsAndHashes = true,
                    A3BoundedIndexedPagesAndAssociationPlans = true,
                    A4GlobalAndPerSourceConcurrencyBounded = true,
                    A5AllLocalAndCentralLogicalFactsConverged = true,
                    A6FaultRestartRetentionHealthAndSignalChecks = true,
                    Result = "Pass"
                }
            };

            var temporaryPath = string.Concat(evidencePath, ".tmp");
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            File.Move(temporaryPath, evidencePath, overwrite: true);
            TestContext.WriteLine($"Issue #209 environmental acquisition evidence: {evidencePath}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    private static async Task<object> MeasureA1Async(WorkingSetTracker workingSet)
    {
        var sources = CreateVirtualSources("a1", [EnvironmentalAcquisitionTrigger.OnDemand]);
        var location = Location();
        var warmupHashes = new List<string>(5);
        var measuredHashes = new List<string>(30);
        var measuredMilliseconds = new List<double>(30);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (var batch = 0; batch < 35; batch++)
        {
            var started = Stopwatch.GetTimestamp();
            using var batchHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var source in sources)
            {
                var acquired = await source.AcquireAsync(
                    new EnvironmentalSourceAcquisitionContext(
                        EnvironmentalAcquisitionTrigger.OnDemand, Epoch, location),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Produced, acquired.Outcome);
                Assert.IsNotNull(acquired.Fact);
                Assert.AreEqual(EnvironmentalObservationSourceKind.Simulated, acquired.Fact.Source.Kind);
                var validation = EnvironmentalObservationFactJson.Validate(acquired.Fact);
                Assert.IsTrue(validation.IsValid, $"{validation.ReasonCode}:{validation.FieldPath}");
                AppendLengthPrefixed(batchHash, EnvironmentalObservationFactJson.Serialize(acquired.Fact));
            }
            var hash = Convert.ToHexString(batchHash.GetHashAndReset());
            if (batch < 5)
            {
                warmupHashes.Add(hash);
            }
            else
            {
                measuredHashes.Add(hash);
                measuredMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                aggregate.AppendData(Convert.FromHexString(hash));
            }
            workingSet.Sample();
        }

        Assert.HasCount(KindCount, sources);
        Assert.AreEqual(1, warmupHashes.Concat(measuredHashes).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(1, sources.Count(source =>
            source.Descriptor.Kind == EnvironmentalObservationKind.CameraSensorTemperature &&
            string.Equals(source.Descriptor.RigId, "rig-1", StringComparison.Ordinal)));
        return new
        {
            WarmupBatches = 5,
            MeasuredBatches = 30,
            KindsPerBatch = KindCount,
            CanonicalPayloadsMeasured = 30 * KindCount,
            BatchCanonicalSha256 = measuredHashes[0],
            AggregateCanonicalSha256 = Convert.ToHexString(aggregate.GetHashAndReset()),
            MedianBatchMilliseconds = Percentile(measuredMilliseconds, 0.5),
            P95BatchMilliseconds = Percentile(measuredMilliseconds, 0.95),
            MinimumBatchMilliseconds = measuredMilliseconds.Min(),
            MaximumBatchMilliseconds = measuredMilliseconds.Max(),
            ByteAndHashIdenticalAcrossWarmupAndMeasuredBatches = true,
            SimulatedProvenanceVerified = true,
            Result = "Pass"
        };
    }

    private static async Task<object> MeasureA2Async(string root, WorkingSetTracker workingSet)
    {
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(Epoch);
        var sources = CreateVirtualSources("a2", [EnvironmentalAcquisitionTrigger.Periodic]);
        using var store = new SqliteEnvironmentalObservationOutbox(
            clock,
            maximumLocalRecords: A2FactCount + 1_000,
            maximumLocalBytes: 256L * 1024 * 1024);
        _ = await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        StabilizeGc();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationsStart = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetStart = process.WorkingSet64;
        var batchMilliseconds = new List<double>(A2FactCount / A2BatchSize);
        var commitMilliseconds = new List<double>(A2FactCount);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var started = Stopwatch.GetTimestamp();

        for (var batch = 0; batch < A2FactCount / A2BatchSize; batch++)
        {
            var batchStarted = Stopwatch.GetTimestamp();
            for (var offset = 0; offset < A2BatchSize; offset++)
            {
                var index = batch * A2BatchSize + offset;
                var fact = await GenerateFactAsync(sources, index, EnvironmentalAcquisitionTrigger.Periodic)
                    .ConfigureAwait(false);
                var commitStarted = Stopwatch.GetTimestamp();
                var committed = await store.CommitLocalAsync(root, fact, CancellationToken.None).ConfigureAwait(false);
                commitMilliseconds.Add(Stopwatch.GetElapsedTime(commitStarted).TotalMilliseconds);
                Assert.AreEqual(LocalEnvironmentalObservationCommitDisposition.Committed, committed.Disposition);
                Assert.AreEqual(EnvironmentalObservationProjectionDisposition.NotRequested, committed.ProjectionDisposition);
                aggregate.AppendData(Convert.FromHexString(committed.Record.ContentSha256));
                clock.Advance(TimeSpan.FromMilliseconds(1000d / 12));
            }
            batchMilliseconds.Add(Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
            workingSet.Sample();
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationsStart;
        var workingSetEnd = process.WorkingSet64;
        var beforeCheckpoint = ReadSqliteFileStats(root);
        var passiveCheckpoint = await CheckpointAsync(root, "PASSIVE").ConfigureAwait(false);
        var truncateCheckpoint = await CheckpointAsync(root, "TRUNCATE").ConfigureAwait(false);
        var afterCheckpoint = ReadSqliteFileStats(root);
        var pragmas = await ReadPragmasAsync(root).ConfigureAwait(false);
        var snapshot = await store.GetLocalSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
        var rowEvidence = await ReadJournalRowEvidenceAsync(root).ConfigureAwait(false);

        Assert.AreEqual(A2FactCount, snapshot.StoredCount);
        Assert.AreEqual(A2FactCount, rowEvidence.Rows);
        Assert.AreEqual(KindCount, rowEvidence.Kinds);
        Assert.AreEqual(KindCount, rowEvidence.Sources);
        Assert.AreEqual(A2FactCount, rowEvidence.DistinctObservations);
        Assert.AreEqual(A2FactCount, rowEvidence.DistinctContentHashes);
        Assert.AreEqual("wal", pragmas.JournalMode, ignoreCase: true);
        Assert.AreEqual(2L, pragmas.Synchronous);
        Assert.IsTrue(workingSet.PeakBytes - workingSetStart <= WorkingSetCeilingBytes);

        return new
        {
            Workload = new
            {
                Facts = A2FactCount,
                Sources = KindCount,
                LogicalBatchSize = A2BatchSize,
                LogicalBatches = A2FactCount / A2BatchSize,
                Path = "Production VirtualEnvironmentalSource -> EnvironmentalObservationFactV1 -> SqliteEnvironmentalObservationOutbox.CommitLocalAsync"
            },
            Timing = new
            {
                TotalMilliseconds = elapsed.TotalMilliseconds,
                FactsPerSecond = A2FactCount / elapsed.TotalSeconds,
                MedianCommitMilliseconds = Percentile(commitMilliseconds, 0.5),
                P95CommitMilliseconds = Percentile(commitMilliseconds, 0.95),
                MedianLogicalBatchMilliseconds = Percentile(batchMilliseconds, 0.5),
                P95LogicalBatchMilliseconds = Percentile(batchMilliseconds, 0.95),
                CpuDeltaMilliseconds = cpuMilliseconds,
                AllocatedBytes = allocatedBytes,
                WorkingSetStartBytes = workingSetStart,
                WorkingSetEndBytes = workingSetEnd,
                WorkingSetPeakBytes = workingSet.PeakBytes
            },
            SQLite = new
            {
                BeforeCheckpoint = beforeCheckpoint,
                PassiveCheckpoint = passiveCheckpoint,
                TruncateCheckpoint = truncateCheckpoint,
                AfterCheckpoint = afterCheckpoint,
                pragmas.JournalMode,
                pragmas.Synchronous,
                ProductionCommitApiCalls = A2FactCount,
                ProductionTransactionCommitBoundaries = A2FactCount,
                StatementCount = "Unavailable from Microsoft.Data.Sqlite; no synthetic count is reported.",
                FsyncBoundaries = "synchronous=FULL is verified. Exact fsync calls require operating-system tracing outside this in-process harness.",
                PhysicalBytesWritten = "Unavailable from Microsoft.Data.Sqlite; database, WAL, and shared-memory file lengths are retained."
            },
            Correctness = new
            {
                snapshot.StoredCount,
                snapshot.StoredBytes,
                snapshot.OverflowCount,
                rowEvidence.Rows,
                rowEvidence.Kinds,
                rowEvidence.Sources,
                rowEvidence.DistinctObservations,
                rowEvidence.DistinctContentHashes,
                OrderedCommitContentSha256 = Convert.ToHexString(aggregate.GetHashAndReset()),
                Result = "Pass"
            }
        };
    }

    private static async Task<object> MeasureA3Async(
        string oneThousandRoot,
        string tenThousandRoot,
        WorkingSetTracker workingSet,
        RuntimeSignalRecorder signals)
    {
        Directory.CreateDirectory(oneThousandRoot);
        var airSource = CreateVirtualSources("a3-1000", [EnvironmentalAcquisitionTrigger.OnDemand])
            .Single(source => source.Descriptor.Kind == EnvironmentalObservationKind.AirTemperature);
        using (var seedStore = new SqliteEnvironmentalObservationOutbox(maximumLocalRecords: 2_000))
        {
            for (var index = 0; index < 1_000; index++)
            {
                var result = await airSource.AcquireAsync(
                    new EnvironmentalSourceAcquisitionContext(
                        EnvironmentalAcquisitionTrigger.OnDemand,
                        Epoch.AddSeconds(index),
                        Location()),
                    CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(result.Fact);
                _ = await seedStore.CommitLocalAsync(
                    oneThousandRoot, result.Fact, CancellationToken.None).ConfigureAwait(false);
                if ((index + 1) % A2BatchSize == 0)
                {
                    workingSet.Sample();
                }
            }
        }

        using var telemetry = new EnvironmentalAcquisitionTelemetry();
        using (var associationStore = new SqliteEnvironmentalObservationOutbox())
        {
            var associationService = new EnvironmentalAssociationService(
                associationStore,
                associationStore,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = tenThousandRoot }),
                new FixedTimeProvider(Epoch.AddHours(3)),
                telemetry);
            var associations = await associationService.AssociateAsync(
                DeterministicGuid(30_000),
                30_000,
                Epoch.AddSeconds(9_996),
                Epoch.AddSeconds(9_997),
                null,
                [EnvironmentalObservationKind.AirTemperature],
                CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, associations);
            Assert.IsTrue(associations[0].Status is LocalEnvironmentalAssociationStatus.Fresh or
                LocalEnvironmentalAssociationStatus.Stale);
        }

        var datasetEvidence = new List<object>();
        foreach (var dataset in new[]
        {
            new HistoryDataset(1_000, oneThousandRoot),
            new HistoryDataset(A2FactCount, tenThousandRoot)
        })
        {
            using var store = new SqliteEnvironmentalObservationOutbox(acquisitionTelemetry: telemetry);
            var filteredRows = await ScalarLongAsync(
                dataset.Root,
                "SELECT COUNT(*) FROM environmental_observation_journal WHERE observation_kind = 'AirTemperature';")
                .ConfigureAwait(false);
            Assert.IsTrue(filteredRows > A3PageSize * 2);
            var cursors = new Dictionary<string, LocalEnvironmentalObservationCursor?>
            {
                ["first"] = null,
                ["middle"] = await ReadHistoryCursorAsync(
                    dataset.Root, filteredRows / 2 - 1).ConfigureAwait(false),
                ["late"] = await ReadHistoryCursorAsync(
                    dataset.Root, filteredRows - A3PageSize - 1).ConfigureAwait(false)
            };
            var concurrencyEvidence = new List<object>();
            foreach (var concurrency in new[] { 1, 10, 50 })
            {
                _ = await RunHistoryQueriesAsync(
                    store, dataset.Root, cursors, A3Warmups, concurrency, workingSet).ConfigureAwait(false);
                var measured = await RunHistoryQueriesAsync(
                    store, dataset.Root, cursors, A3Measurements, concurrency, workingSet).ConfigureAwait(false);
                var durations = measured.Select(static sample => sample.Milliseconds).ToArray();
                var p95 = Percentile(durations, 0.95);
                var maximumPayloadBytes = measured.Max(static sample => sample.PayloadBytes);
                Assert.IsTrue(
                    p95 < LocalHistoryP95CeilingMilliseconds,
                    $"{dataset.Rows}-row concurrency-{concurrency} p95 was {p95:F3} ms.");
                Assert.IsTrue(maximumPayloadBytes <= ResponsePayloadCeilingBytes);
                Assert.IsTrue(measured.All(static sample => sample.Records == A3PageSize));
                Assert.AreEqual(3, measured.Select(static sample => sample.Position).Distinct().Count());
                concurrencyEvidence.Add(new
                {
                    Concurrency = concurrency,
                    WarmupQueries = A3Warmups,
                    MeasuredQueries = A3Measurements,
                    MedianMilliseconds = Percentile(durations, 0.5),
                    P95Milliseconds = p95,
                    MaximumMilliseconds = durations.Max(),
                    QueriesPerSecond = A3Measurements / measured.Max(static sample => sample.RunElapsedSeconds),
                    MaximumRecordsMaterializedPerQuery = measured.Max(static sample => sample.Records),
                    MaximumConcurrentRecordsMaterialized = concurrency * A3PageSize,
                    MaximumCanonicalPayloadBytes = maximumPayloadBytes,
                    Positions = measured.GroupBy(static sample => sample.Position)
                        .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal),
                    LocalCeilingMilliseconds = LocalHistoryP95CeilingMilliseconds,
                    Result = "Pass"
                });
            }
            datasetEvidence.Add(new
            {
                DatasetRows = dataset.Rows,
                FilteredKind = EnvironmentalObservationKind.AirTemperature,
                FilteredRows = filteredRows,
                PageSize = A3PageSize,
                Positions = HistoryPositions,
                Concurrency = concurrencyEvidence
            });
        }

        var historyPlan = await ReadQueryPlanAsync(
            tenThousandRoot,
            """
            SELECT record_id FROM environmental_observation_journal
            WHERE observation_kind = $kind
                AND (observed_at_unix_ms < $observed
                    OR (observed_at_unix_ms = $observed AND record_id < $record))
            ORDER BY observed_at_unix_ms DESC, record_id DESC LIMIT 51;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$kind", EnvironmentalObservationKind.AirTemperature.ToString());
                command.Parameters.AddWithValue("$observed", Epoch.AddHours(3).ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$record", long.MaxValue);
            }).ConfigureAwait(false);
        var associationPlan = await ReadQueryPlanAsync(
            tenThousandRoot,
            """
            SELECT capture_sequence, observation_kind, rig_id, policy_identity_sha256
            FROM environmental_capture_associations
            WHERE capture_id = $capture
            ORDER BY observation_kind, policy_identity_sha256;
            """,
            command => command.Parameters.AddWithValue("$capture", DeterministicGuid(30_000).ToString("D")))
            .ConfigureAwait(false);
        Assert.IsTrue(historyPlan.Any(line => line.Contains("ix_environment_local_page", StringComparison.Ordinal)));
        Assert.IsTrue(associationPlan.Any(line =>
            line.Contains("INDEX", StringComparison.Ordinal) &&
            (line.Contains("ix_environment_association_capture", StringComparison.Ordinal) ||
                line.Contains("sqlite_autoindex_environmental_capture_associations", StringComparison.Ordinal))));

        signals.RecordObservableInstruments();
        return new
        {
            PageSize = A3PageSize,
            WarmupsPerDatasetAndConcurrency = A3Warmups,
            MeasurementsPerDatasetAndConcurrency = A3Measurements,
            ConcurrencyLevels = new[] { 1, 10, 50 },
            Datasets = datasetEvidence,
            QueryPlans = new
            {
                KindTimeHistory = historyPlan,
                CaptureKindAssociation = associationPlan,
                RequiredHistoryIndex = "ix_environment_local_page",
                RequiredAssociationIndex = "ix_environment_association_capture or the stricter UNIQUE " +
                    "(capture_id, observation_kind, policy_identity_sha256) SQLite auto-index"
            },
            Ceilings = new
            {
                IndexedLocalP95Milliseconds = LocalHistoryP95CeilingMilliseconds,
                AuthenticatedConcurrency50P95Milliseconds = AuthenticatedHistoryP95CeilingMilliseconds,
                AuthenticatedCeilingExecuted = false,
                MaximumResponsePayloadBytes = ResponsePayloadCeilingBytes
            },
            BoundedRetention = new
            {
                MeasurementsRetainOnlyScalarTimingCountAndByteSamples = true,
                MaximumRecordsMaterializedPerQuery = A3PageSize,
                MaximumRecordsMaterializedAtConcurrency50 = 50 * A3PageSize,
                NoPayloadCollectionProportionalToHistory = true
            },
            Result = "Pass"
        };
    }

    private static async Task<object> MeasureA4Async(WorkingSetTracker workingSet)
    {
        using var telemetry = new EnvironmentalAcquisitionTelemetry();
        using var fixture = CreateTrackingCoordinator("a4-main", telemetry);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < A4CaptureCount; index++)
        {
            var sequence = index + 1L;
            var captureId = DeterministicGuid(40_000 + index);
            var observed = Epoch.AddSeconds(index * 5L);
            if (index % 6 == 0)
            {
                _ = await fixture.Coordinator.AcquireTriggerAsync(
                    EnvironmentalAcquisitionTrigger.Periodic,
                    observed,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            _ = await fixture.Coordinator.AcquireTriggerAsync(
                EnvironmentalAcquisitionTrigger.BeforeCapture,
                observed,
                sequence,
                captureId,
                CancellationToken.None).ConfigureAwait(false);
            _ = await fixture.Coordinator.AcquireTriggerAsync(
                EnvironmentalAcquisitionTrigger.EveryNthCapture,
                observed,
                sequence,
                captureId,
                CancellationToken.None).ConfigureAwait(false);
            _ = await fixture.Coordinator.AcquireTriggerAsync(
                EnvironmentalAcquisitionTrigger.AfterCapture,
                observed,
                sequence,
                captureId,
                CancellationToken.None).ConfigureAwait(false);
            if (index == A4CaptureCount / 2)
            {
                _ = await fixture.Coordinator.AcquireTriggerAsync(
                    EnvironmentalAcquisitionTrigger.RegimeChange,
                    observed,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            if ((index + 1) % 30 == 0)
            {
                workingSet.Sample();
            }
        }
        var elapsed = Stopwatch.GetElapsedTime(started);

        const int expectedPeriodic = A4CaptureCount / 6 * KindCount;
        const int expectedBefore = A4CaptureCount * KindCount;
        const int expectedAfter = A4CaptureCount * KindCount;
        const int expectedEveryThird = A4CaptureCount / 3 * KindCount;
        Assert.AreEqual(expectedPeriodic, fixture.Tracker.Count(EnvironmentalAcquisitionTrigger.Periodic));
        Assert.AreEqual(expectedBefore, fixture.Tracker.Count(EnvironmentalAcquisitionTrigger.BeforeCapture));
        Assert.AreEqual(expectedAfter, fixture.Tracker.Count(EnvironmentalAcquisitionTrigger.AfterCapture));
        Assert.AreEqual(expectedEveryThird, fixture.Tracker.Count(EnvironmentalAcquisitionTrigger.EveryNthCapture));
        Assert.AreEqual(1, fixture.Tracker.Count(EnvironmentalAcquisitionTrigger.RegimeChange));
        Assert.AreEqual(4, fixture.Tracker.MaximumGlobalInflight);
        Assert.IsTrue(fixture.Tracker.MaximumPerSourceInflight.Values.All(static value => value == 1));
        Assert.AreEqual(expectedPeriodic + expectedBefore + expectedAfter + expectedEveryThird + 1,
            fixture.Publisher.PublishCount);

        using var pinned = CreateTrackingCoordinator("a4-pinned", telemetry);
        var pinnedTimes = new[]
        {
            Epoch.AddSeconds(5),
            Epoch.AddSeconds(14),
            Epoch.AddSeconds(29),
            Epoch.AddSeconds(44),
            Epoch.AddSeconds(59),
            Epoch.AddSeconds(74)
        };
        for (var index = 0; index < pinnedTimes.Length; index++)
        {
            _ = await pinned.Coordinator.AcquireTriggerAsync(
                EnvironmentalAcquisitionTrigger.EveryNthCapture,
                pinnedTimes[index],
                index + 1L,
                DeterministicGuid(41_000 + index),
                CancellationToken.None).ConfigureAwait(false);
        }
        _ = await pinned.Coordinator.AcquireTriggerAsync(
            EnvironmentalAcquisitionTrigger.RegimeChange,
            pinnedTimes[3],
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(
            new long[] { 3, 6 },
            pinned.Tracker.CaptureSequences(EnvironmentalAcquisitionTrigger.EveryNthCapture));
        Assert.AreEqual(2 * KindCount, pinned.Tracker.Count(EnvironmentalAcquisitionTrigger.EveryNthCapture));
        Assert.AreEqual(1, pinned.Tracker.Count(EnvironmentalAcquisitionTrigger.RegimeChange));

        return new
        {
            LogicalCaptures = A4CaptureCount,
            LogicalDurationMinutes = 30,
            CaptureCadenceSeconds = 5,
            PeriodicCadenceSeconds = 30,
            ConfiguredMaximumGlobalInflight = 4,
            ObservedMaximumGlobalInflight = fixture.Tracker.MaximumGlobalInflight,
            ObservedMaximumPerSourceInflight = fixture.Tracker.MaximumPerSourceInflight,
            TriggerAcquisitions = new
            {
                Periodic = expectedPeriodic,
                BeforeCapture = expectedBefore,
                AfterCapture = expectedAfter,
                EveryThirdCapture = expectedEveryThird,
                RegimeChange = 1,
                Total = fixture.Publisher.PublishCount
            },
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            PinnedBoundaryFixture = new
            {
                CaptureTimesUtc = pinnedTimes,
                CaptureSequences = Enumerable.Range(1, 6).ToArray(),
                EveryThirdFiredSequences = pinned.Tracker.CaptureSequences(
                    EnvironmentalAcquisitionTrigger.EveryNthCapture),
                EveryThirdAcquisitions = pinned.Tracker.Count(EnvironmentalAcquisitionTrigger.EveryNthCapture),
                LogicalRegimeTransition = "Night-to-Twilight",
                RegimeChangeAcquisitions = pinned.Tracker.Count(EnvironmentalAcquisitionTrigger.RegimeChange)
            },
            Result = "Pass"
        };
    }

    private static async Task<object> MeasureA5Async(string root, WorkingSetTracker workingSet)
    {
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(Epoch);
        var sources = CreateVirtualSources("a5", [EnvironmentalAcquisitionTrigger.Periodic]);
        LocalEnvironmentalObservationSnapshot restartSnapshot;
        using (var beforeRestart = new SqliteEnvironmentalObservationOutbox(
            clock,
            maximumRecords: A5DeliveryCapacity,
            maximumLocalRecords: A5FactCount + 100))
        {
            for (var index = 0; index < A5RestartAfter; index++)
            {
                var fact = await GenerateFactAtLogicalRateAsync(sources, index).ConfigureAwait(false);
                _ = await beforeRestart.CommitLocalAsync(
                    root, fact, DeliveryTarget, CancellationToken.None).ConfigureAwait(false);
                clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 12));
            }
            restartSnapshot = await beforeRestart.GetLocalSnapshotAsync(root, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(A5RestartAfter, restartSnapshot.StoredCount);
            Assert.IsTrue((await beforeRestart.GetSnapshotAsync(root, CancellationToken.None)
                .ConfigureAwait(false)).StoredCount <= A5DeliveryCapacity);
        }
        SqliteConnection.ClearAllPools();

        using var restarted = new SqliteEnvironmentalObservationOutbox(
            clock,
            maximumRecords: A5DeliveryCapacity,
            maximumLocalRecords: A5FactCount + 100);
        for (var index = A5RestartAfter; index < A5FactCount; index++)
        {
            var fact = await GenerateFactAtLogicalRateAsync(sources, index).ConfigureAwait(false);
            _ = await restarted.CommitLocalAsync(
                root, fact, DeliveryTarget, CancellationToken.None).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 12));
            if ((index + 1) % A2BatchSize == 0)
            {
                workingSet.Sample();
            }
        }

        var localBeforeRecovery = await restarted.GetLocalSnapshotAsync(root, CancellationToken.None)
            .ConfigureAwait(false);
        var deliveryBeforeRecovery = await restarted.GetSnapshotAsync(root, CancellationToken.None)
            .ConfigureAwait(false);
        var waitingBeforeRecovery = await ScalarLongAsync(
            root,
            "SELECT COUNT(*) FROM environmental_observation_central_projection WHERE status = 'waiting';")
            .ConfigureAwait(false);
        Assert.AreEqual(A5FactCount, localBeforeRecovery.StoredCount);
        Assert.AreEqual(A5DeliveryCapacity, deliveryBeforeRecovery.StoredCount);
        Assert.AreEqual(A5FactCount - A5DeliveryCapacity, waitingBeforeRecovery);

        var receiver = new IdempotentReceiver(clock);
        var lostLease = await restarted.ClaimAsync(
            root, "issue-209-lost-ack", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lostLease);
        var acceptedBeforeLostAcknowledgement = receiver.Ingest(lostLease.Record.Observation);
        Assert.AreEqual(EnvironmentalObservationDeliveryDisposition.Accepted,
            acceptedBeforeLostAcknowledgement.Disposition);
        clock.Advance(TimeSpan.FromSeconds(10));

        using var transport = new ReceiverTransport(receiver);
        using var deliveryTelemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(), clock);
        using var service = new EnvironmentalObservationDeliveryService(
            transport,
            NullTargetResolver.Instance,
            restarted,
            new EnvironmentalObservationDeliveryWakeup(),
            new EnvironmentalObservationDeliveryState(),
            deliveryTelemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalDelivery = DeliveryOptions(A5DeliveryCapacity)
            }),
            clock,
            NullLogger<EnvironmentalObservationDeliveryService>.Instance);
        var oldestAgeBeforeRecovery = deliveryBeforeRecovery.OldestPendingUtc is { } oldest
            ? clock.GetUtcNow() - oldest
            : TimeSpan.Zero;
        var recoveryStarted = Stopwatch.GetTimestamp();
        var drainPasses = 0;
        long maximumObservedDeliveryBacklog = deliveryBeforeRecovery.StoredCount;
        while (true)
        {
            var snapshot = await restarted.GetSnapshotAsync(root, CancellationToken.None).ConfigureAwait(false);
            var waiting = await ScalarLongAsync(
                root,
                "SELECT COUNT(*) FROM environmental_observation_central_projection WHERE status = 'waiting';")
                .ConfigureAwait(false);
            maximumObservedDeliveryBacklog = Math.Max(maximumObservedDeliveryBacklog, snapshot.StoredCount);
            Assert.IsTrue(snapshot.StoredCount <= A5DeliveryCapacity);
            if (snapshot.StoredCount == 0 && waiting == 0)
            {
                break;
            }
            _ = await service.DrainBatchAsync(
                root, DeliveryOptions(A5DeliveryCapacity), CancellationToken.None).ConfigureAwait(false);
            drainPasses++;
            workingSet.Sample();
            Assert.IsTrue(drainPasses <= 10, "A5 recovery did not converge in the bounded number of passes.");
        }
        var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
        var localAfterRecovery = await restarted.GetLocalSnapshotAsync(root, CancellationToken.None)
            .ConfigureAwait(false);
        var deliveryAfterRecovery = await restarted.GetSnapshotAsync(root, CancellationToken.None)
            .ConfigureAwait(false);
        var distinctLocalRows = await ScalarLongAsync(
            root,
            "SELECT COUNT(DISTINCT source_identity_sha256 || ':' || observation_id) FROM environmental_observation_journal;")
            .ConfigureAwait(false);

        Assert.AreEqual(A5FactCount, localAfterRecovery.StoredCount);
        Assert.AreEqual(A5FactCount, distinctLocalRows);
        Assert.AreEqual(A5FactCount, receiver.Count);
        Assert.AreEqual(0, deliveryAfterRecovery.StoredCount);
        Assert.AreEqual(1, transport.DuplicateCount);
        Assert.AreEqual(A5FactCount - 1, transport.AcceptedCount);
        Assert.IsTrue(A5FactCount / recoveryElapsed.TotalSeconds > 12);

        return new
        {
            Facts = A5FactCount,
            LogicalArrivalRatePerSecond = 12,
            LogicalArrivalDurationSeconds = 50,
            RestartAfterFacts = A5RestartAfter,
            RestartLocalRows = restartSnapshot.StoredCount,
            DeliveryCapacity = A5DeliveryCapacity,
            MaximumObservedDeliveryBacklog = maximumObservedDeliveryBacklog,
            StandaloneCentralAttempts = 0,
            BeforeRecovery = new
            {
                LocalRows = localBeforeRecovery.StoredCount,
                LocalBytes = localBeforeRecovery.StoredBytes,
                DeliveryRows = deliveryBeforeRecovery.StoredCount,
                WaitingProjectionRows = waitingBeforeRecovery,
                OldestAgeSeconds = oldestAgeBeforeRecovery.TotalSeconds
            },
            LostAcknowledgementFixture = new
            {
                FirstDisposition = acceptedBeforeLostAcknowledgement.Disposition,
                ReplayDisposition = EnvironmentalObservationDeliveryDisposition.Duplicate,
                DuplicateDeliveries = transport.DuplicateCount
            },
            Recovery = new
            {
                DrainPasses = drainPasses,
                ElapsedMilliseconds = recoveryElapsed.TotalMilliseconds,
                DrainFactsPerSecond = A5FactCount / recoveryElapsed.TotalSeconds,
                TransportRequests = transport.SendCount,
                AcceptedByRecoveryTransport = transport.AcceptedCount,
                DuplicateByRecoveryTransport = transport.DuplicateCount,
                CentralLogicalFacts = receiver.Count,
                FinalDeliveryBacklog = deliveryAfterRecovery.StoredCount,
                FinalLocalRows = localAfterRecovery.StoredCount,
                DistinctLocalLogicalFacts = distinctLocalRows
            },
            Result = "Pass"
        };
    }

    private static async Task<object> MeasureA6Async(string root, WorkingSetTracker workingSet)
    {
        Directory.CreateDirectory(root);
        var boundaryRoot = Path.Combine(root, "commit-boundaries");
        Directory.CreateDirectory(boundaryRoot);
        using var boundaryStore = new SqliteEnvironmentalObservationOutbox();
        using var beforeFixture = CreateSingleVirtualCoordinator(
            boundaryRoot,
            new BoundaryPublisher(boundaryStore, boundaryRoot, Boundary.BeforeCommit),
            "a6-boundary",
            sourceTimeoutMilliseconds: 5_000);
        var before = await beforeFixture.Coordinator.AcquireSourceAsync(
            "a6-boundary",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Failed, before.Disposition);
        Assert.AreEqual("journal-unavailable", before.Reason);
        Assert.AreEqual(0, (await boundaryStore.GetLocalSnapshotAsync(
            boundaryRoot, CancellationToken.None).ConfigureAwait(false)).StoredCount);

        using var afterFixture = CreateSingleVirtualCoordinator(
            boundaryRoot,
            new BoundaryPublisher(boundaryStore, boundaryRoot, Boundary.AfterCommit),
            "a6-boundary",
            sourceTimeoutMilliseconds: 5_000);
        var after = await afterFixture.Coordinator.AcquireSourceAsync(
            "a6-boundary",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Failed, after.Disposition);
        boundaryStore.Dispose();
        SqliteConnection.ClearAllPools();
        using var boundaryRestart = new SqliteEnvironmentalObservationOutbox();
        var afterRestart = await boundaryRestart.GetLocalSnapshotAsync(boundaryRoot, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1, afterRestart.StoredCount);

        var timeoutRoot = Path.Combine(root, "timeout");
        Directory.CreateDirectory(timeoutRoot);
        using var timeoutStore = new SqliteEnvironmentalObservationOutbox();
        using var timeoutFixture = CreateSingleVirtualCoordinator(
            timeoutRoot,
            new RecordingPublisher(),
            "a6-timeout",
            sourceTimeoutMilliseconds: 100,
            delayMilliseconds: 500,
            stateStore: timeoutStore);
        var timeout = await timeoutFixture.Coordinator.AcquireSourceAsync(
            "a6-timeout",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.TimedOut, timeout.Disposition);

        var throwRoot = Path.Combine(root, "source-throw");
        Directory.CreateDirectory(throwRoot);
        using var throwFixture = CreateThrowingCoordinator(throwRoot);
        var sourceThrow = await throwFixture.Coordinator.AcquireSourceAsync(
            "a6-throw",
            EnvironmentalAcquisitionTrigger.OnDemand,
            Epoch,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Failed, sourceThrow.Disposition);
        Assert.AreEqual("source-failure", sourceThrow.Reason);

        var invalidRoot = Path.Combine(root, "invalid");
        Directory.CreateDirectory(invalidRoot);
        using var invalidStore = new SqliteEnvironmentalObservationOutbox();
        var validFact = await GenerateFactAsync(
            CreateVirtualSources("a6-invalid", [EnvironmentalAcquisitionTrigger.OnDemand]),
            0,
            EnvironmentalAcquisitionTrigger.OnDemand).ConfigureAwait(false);
        var invalidRejected = false;
        try
        {
            _ = await invalidStore.CommitLocalAsync(
                invalidRoot,
                validFact with { Value = validFact.Value with { NumericValue = double.NaN } },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            invalidRejected = true;
        }
        Assert.IsTrue(invalidRejected);

        using var queueTelemetry = new EnvironmentalAcquisitionTelemetry();
        var queueOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = timeoutRoot,
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                QueueCapacity = 16,
                Sources = [SingleSourceConfiguration("a6-timeout", delayMilliseconds: 500)]
            }
        });
        using var queueService = new EnvironmentalAcquisitionService(
            timeoutFixture.Coordinator,
            timeoutStore,
            timeoutStore,
            queueTelemetry,
            new FixedTimeProvider(Epoch),
            queueOptions,
            NullLogger<EnvironmentalAcquisitionService>.Instance);
        var acceptedQueueWrites = Enumerable.Range(0, 16).Count(index => queueService.TryEnqueue(
            new EnvironmentalTriggerRequest(EnvironmentalAcquisitionTrigger.OnDemand, Epoch.AddSeconds(index))));
        var queueRejected = !queueService.TryEnqueue(
            new EnvironmentalTriggerRequest(EnvironmentalAcquisitionTrigger.OnDemand, Epoch.AddSeconds(16)));
        Assert.AreEqual(16, acceptedQueueWrites);
        Assert.IsTrue(queueRejected);

        var lockRoot = Path.Combine(root, "database-lock");
        Directory.CreateDirectory(lockRoot);
        using var lockStore = new SqliteEnvironmentalObservationOutbox(busyTimeoutSeconds: 1);
        _ = await lockStore.CommitLocalAsync(lockRoot, validFact, CancellationToken.None).ConfigureAwait(false);
        var databasePath = DatabasePath(lockRoot);
        using var lockingConnection = new SqliteConnection($"Data Source={databasePath}");
        await lockingConnection.OpenAsync().ConfigureAwait(false);
        using (var lockCommand = lockingConnection.CreateCommand())
        {
            lockCommand.CommandText = "BEGIN IMMEDIATE;";
            await lockCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var databaseLockObserved = false;
        try
        {
            var lockedFact = await GenerateFactAsync(
                CreateVirtualSources("a6-lock", [EnvironmentalAcquisitionTrigger.OnDemand]),
                1,
                EnvironmentalAcquisitionTrigger.OnDemand).ConfigureAwait(false);
            _ = await lockStore.CommitLocalAsync(lockRoot, lockedFact, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            databaseLockObserved = true;
        }
        finally
        {
            using var rollback = lockingConnection.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(databaseLockObserved);

        var retentionRoot = Path.Combine(root, "retention");
        Directory.CreateDirectory(retentionRoot);
        var retentionClock = new MutableTimeProvider(Epoch);
        using var retentionTelemetry = new EnvironmentalAcquisitionTelemetry();
        using var retentionStore = new SqliteEnvironmentalObservationOutbox(retentionClock);
        var retentionSources = CreateVirtualSources("a6-retention", [EnvironmentalAcquisitionTrigger.OnDemand]);
        var pinnedFact = await GenerateSpecificFactAsync(
            retentionSources, EnvironmentalObservationKind.AirTemperature, Epoch).ConfigureAwait(false);
        var freeFact = await GenerateSpecificFactAsync(
            retentionSources, EnvironmentalObservationKind.RelativeHumidity, Epoch).ConfigureAwait(false);
        var pinnedCommit = await retentionStore.CommitLocalAsync(
            retentionRoot, pinnedFact, CancellationToken.None).ConfigureAwait(false);
        var freeCommit = await retentionStore.CommitLocalAsync(
            retentionRoot, freeFact, CancellationToken.None).ConfigureAwait(false);
        retentionClock.Advance(TimeSpan.FromDays(20));
        var associationService = new EnvironmentalAssociationService(
            retentionStore,
            retentionStore,
            Options.Create(new CameraAgentHostOptions { RawIngressRoot = retentionRoot }),
            retentionClock,
            retentionTelemetry);
        var association = await associationService.AssociateAsync(
            DeterministicGuid(60_000),
            60_000,
            Epoch.AddSeconds(1),
            Epoch.AddSeconds(2),
            null,
            [EnvironmentalObservationKind.AirTemperature],
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(pinnedCommit.Record.RecordId, association[0].SelectedRecordId);
        retentionClock.Advance(TimeSpan.FromDays(20));
        var retained = await retentionStore.RetainLocalAsync(
            retentionRoot, Epoch.AddDays(9), 100, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(1, retained.RemovedCount);
        Assert.IsNotNull(await retentionStore.ReadLocalDetailAsync(
            retentionRoot, pinnedCommit.Record.RecordId, CancellationToken.None).ConfigureAwait(false));
        Assert.IsNull(await retentionStore.ReadLocalDetailAsync(
            retentionRoot, freeCommit.Record.RecordId, CancellationToken.None).ConfigureAwait(false));
        var duplicate = await retentionStore.CommitLocalAsync(
            retentionRoot, pinnedFact, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalEnvironmentalObservationCommitDisposition.Duplicate, duplicate.Disposition);

        var outageRoot = Path.Combine(root, "delivery-outage");
        Directory.CreateDirectory(outageRoot);
        var outageClock = new MutableTimeProvider(Epoch);
        using var outageStore = new SqliteEnvironmentalObservationOutbox(outageClock, maximumRecords: 1);
        _ = await outageStore.CommitLocalAsync(
            outageRoot, validFact, DeliveryTarget, CancellationToken.None).ConfigureAwait(false);
        var outageLease = await outageStore.ClaimAsync(
            outageRoot, "a6-outage", TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(outageLease);
        await outageStore.RetryAsync(
            outageRoot,
            outageLease,
            outageClock.GetUtcNow().AddSeconds(5),
            "transport-unavailable",
            CancellationToken.None).ConfigureAwait(false);
        var outageSnapshot = await outageStore.GetSnapshotAsync(outageRoot, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1, outageSnapshot.RetryCount);
        Assert.AreEqual(1, (await outageStore.GetLocalSnapshotAsync(
            outageRoot, CancellationToken.None).ConfigureAwait(false)).StoredCount);

        var health = new EnvironmentalAcquisitionHealthCheck(
            timeoutStore,
            timeoutStore,
            queueOptions,
            new FixedTimeProvider(Epoch));
        var healthResult = await health.CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Unhealthy, healthResult.Status);
        Assert.IsFalse(healthResult.Data.Values.Any(value =>
            value?.ToString()?.Contains(root, StringComparison.Ordinal) == true));

        var signalRoot = Path.Combine(root, "signals");
        Directory.CreateDirectory(signalRoot);
        using var signalStore = new SqliteEnvironmentalObservationOutbox();
        using var signalAcquisitionTelemetry = new EnvironmentalAcquisitionTelemetry();
        using var signalDeliveryTelemetry = new EnvironmentalObservationDeliveryTelemetry(
            new EnvironmentalObservationDeliveryState(), new FixedTimeProvider(Epoch));
        var signalPublisher = new EnvironmentalObservationPublisher(
            NullTargetResolver.Instance,
            signalStore,
            new EnvironmentalObservationDeliveryWakeup(),
            new EnvironmentalObservationDeliveryState(),
            signalDeliveryTelemetry,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = signalRoot,
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            }),
            new FixedTimeProvider(Epoch),
            signalAcquisitionTelemetry);
        _ = await signalPublisher.PublishAsync(validFact).ConfigureAwait(false);
        workingSet.Sample();

        return new
        {
            Faults = new
            {
                BeforeLocalCommit = new
                {
                    Executed = true,
                    Receipt = before.Disposition,
                    DurableRowsAfterFailure = 0,
                    Injection = "Harness publisher throws immediately before production CommitLocalAsync."
                },
                AfterLocalCommit = new
                {
                    Executed = true,
                    Receipt = after.Disposition,
                    DurableRowsAfterRestart = afterRestart.StoredCount,
                    Injection = "Harness publisher throws immediately after production CommitLocalAsync returns committed."
                },
                Timeout = new { Executed = true, DelayMilliseconds = 500, TimeoutMilliseconds = 100, timeout.Disposition },
                SourceThrow = new { Executed = true, sourceThrow.Disposition, sourceThrow.Reason },
                InvalidFact = new { Executed = true, Rejected = invalidRejected, DurableRows = 0 },
                QueueCapacity = new { Executed = true, Capacity = 16, Accepted = acceptedQueueWrites, RejectedAtCapacity = queueRejected },
                DatabaseLock = new { Executed = true, BusyTimeoutSeconds = 1, FailureObserved = databaseLockObserved },
                Restart = new { Executed = true, DurableRows = afterRestart.StoredCount },
                DuplicateReplay = new { Executed = true, Disposition = duplicate.Disposition, DurableRows = retained.RemainingCount },
                RetentionHold = new
                {
                    Executed = true,
                    RemovedUnpinnedRows = retained.RemovedCount,
                    PinnedRecordId = pinnedCommit.Record.RecordId,
                    AssociationStatus = association[0].Status,
                    PinnedEvidenceSurvived = true
                },
                DeliveryOutage = new
                {
                    Executed = true,
                    RetryRows = outageSnapshot.RetryCount,
                    LocalRows = 1,
                    LocalHistoryUnaffected = true
                }
            },
            Health = new
            {
                Executed = true,
                healthResult.Status,
                healthResult.Description,
                healthResult.Data,
                FailureReasonAndPathExcluded = true
            },
            Checklist = new
            {
                LocalCommitBoundaryRestartConvergence = true,
                SourceFailureIsExplicitAndDoesNotManufactureFact = true,
                QueueAndDeliveryCapacityAreBounded = true,
                DatabaseContentionIsExplicit = true,
                DuplicatePublicationIsIdempotent = true,
                RetentionPreservesAssociationEvidence = true,
                DeliveryOutageDoesNotDeleteLocalHistory = true,
                HealthDataIsBoundedAndSanitized = true,
                RuntimeSignalLabelsAreScannedByTopLevelHarness = true
            },
            Result = "Pass"
        };
    }

    private static async Task<IReadOnlyList<HistoryQuerySample>> RunHistoryQueriesAsync(
        SqliteEnvironmentalObservationOutbox store,
        string root,
        Dictionary<string, LocalEnvironmentalObservationCursor?> cursors,
        int count,
        int concurrency,
        WorkingSetTracker workingSet)
    {
        var samples = new ConcurrentBag<HistoryQuerySample>();
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var runStarted = Stopwatch.GetTimestamp();
        var tasks = Enumerable.Range(0, count).Select(async index =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var position = HistoryPositions[index % HistoryPositions.Length];
                var started = Stopwatch.GetTimestamp();
                var page = await store.ReadLocalPageAsync(
                    root,
                    EnvironmentalObservationKind.AirTemperature,
                    A3PageSize,
                    cursors[position],
                    CancellationToken.None).ConfigureAwait(false);
                var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                samples.Add(new HistoryQuerySample(
                    position,
                    milliseconds,
                    page.Items.Count,
                    page.Items.Sum(static item => item.PayloadBytes),
                    Stopwatch.GetElapsedTime(runStarted).TotalSeconds));
                workingSet.Sample();
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return samples.ToArray();
    }

    private static async Task<LocalEnvironmentalObservationCursor> ReadHistoryCursorAsync(string root, long offset)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_at_unix_ms, record_id
            FROM environmental_observation_journal
            WHERE observation_kind = 'AirTemperature'
            ORDER BY observed_at_unix_ms DESC, record_id DESC
            LIMIT 1 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new LocalEnvironmentalObservationCursor(
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            reader.GetInt64(1));
    }

    private static async Task<IReadOnlyList<string>> ReadQueryPlanAsync(
        string root,
        string sql,
        Action<SqliteCommand> configure)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = string.Concat("EXPLAIN QUERY PLAN ", sql);
        configure(command);
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var plan = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            plan.Add(reader.GetString(3));
        }
        return plan;
    }

    private static async Task<JournalRowEvidence> ReadJournalRowEvidenceAsync(string root)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COUNT(DISTINCT observation_kind), COUNT(DISTINCT source_identity_sha256),
                COUNT(DISTINCT observation_id), COUNT(DISTINCT content_sha256)
            FROM environmental_observation_journal;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new JournalRowEvidence(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private static async Task<SqlitePragmas> ReadPragmasAsync(string root)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = Convert.ToString(
            await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture)!;
        command.CommandText = "PRAGMA synchronous;";
        var synchronous = Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new SqlitePragmas(journalMode, synchronous);
    }

    private static async Task<SqliteCheckpoint> CheckpointAsync(string root, string mode)
    {
        Assert.IsTrue(mode is "PASSIVE" or "TRUNCATE");
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA wal_checkpoint({mode});";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new SqliteCheckpoint(mode, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static SqliteFileStats ReadSqliteFileStats(string root)
    {
        var database = DatabasePath(root);
        return new SqliteFileStats(
            FileLength(database),
            FileLength(string.Concat(database, "-wal")),
            FileLength(string.Concat(database, "-shm")));
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static async Task<long> ScalarLongAsync(string root, string sql)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath(root)};Mode=ReadWrite");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static string DatabasePath(string root)
        => Path.Combine(root, ".environment", "environmental-observation-outbox.db");

    private static async Task<string> ReadSqliteVersionAsync(string root)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture)!;
    }

    private static IEnvironmentalSource[] CreateVirtualSources(
        string prefix,
        IReadOnlyList<EnvironmentalAcquisitionTrigger> triggers,
        int delayMilliseconds = 0)
    {
        var kinds = Enum.GetValues<EnvironmentalObservationKind>();
        Assert.HasCount(KindCount, kinds);
        return kinds.Select((kind, index) =>
        {
            var descriptor = new EnvironmentalSourceDescriptor(
                $"{prefix}-{kind}",
                "VirtualEnvironment",
                kind,
                true,
                triggers,
                Epoch,
                30,
                3,
                120,
                45,
                kind == EnvironmentalObservationKind.CameraSensorTemperature ? "rig-1" : null,
                JsonSerializer.SerializeToElement(new { }));
            var value = Baseline(kind);
            var noise = kind switch
            {
                EnvironmentalObservationKind.AirTemperature => 0.25,
                EnvironmentalObservationKind.RelativeHumidity => 0.5,
                EnvironmentalObservationKind.CloudCover => 0.05,
                _ => 0
            };
            return (IEnvironmentalSource)new VirtualEnvironmentalSource(
                descriptor,
                new VirtualEnvironmentalSourceOptions(
                    209,
                    Epoch,
                    value.Numeric,
                    value.Boolean,
                    noise,
                    value.Boolean.HasValue ? null : 0.1,
                    DelayMilliseconds: delayMilliseconds));
        }).ToArray();
    }

    private static async Task<EnvironmentalObservationFactV1> GenerateFactAsync(
        IEnvironmentalSource[] sources,
        int index,
        EnvironmentalAcquisitionTrigger trigger)
    {
        var source = sources[index % sources.Length];
        var acquired = await source.AcquireAsync(
            new EnvironmentalSourceAcquisitionContext(
                trigger,
                Epoch.AddSeconds(index),
                Location()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalSourceAcquisitionOutcome.Produced, acquired.Outcome);
        Assert.IsNotNull(acquired.Fact);
        return acquired.Fact;
    }

    private static async Task<EnvironmentalObservationFactV1> GenerateFactAtLogicalRateAsync(
        IEnvironmentalSource[] sources,
        int index)
    {
        var source = sources[index % sources.Length];
        var observed = Epoch.AddTicks(index * (TimeSpan.TicksPerSecond / 12));
        var acquired = await source.AcquireAsync(
            new EnvironmentalSourceAcquisitionContext(
                EnvironmentalAcquisitionTrigger.Periodic,
                observed,
                Location()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquired.Fact);
        return acquired.Fact;
    }

    private static async Task<EnvironmentalObservationFactV1> GenerateSpecificFactAsync(
        IEnvironmentalSource[] sources,
        EnvironmentalObservationKind kind,
        DateTimeOffset observedAtUtc)
    {
        var source = sources.Single(item => item.Descriptor.Kind == kind);
        var acquired = await source.AcquireAsync(
            new EnvironmentalSourceAcquisitionContext(
                EnvironmentalAcquisitionTrigger.OnDemand,
                observedAtUtc,
                Location()),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquired.Fact);
        return acquired.Fact;
    }

    private static (double? Numeric, bool? Boolean) Baseline(EnvironmentalObservationKind kind)
        => kind switch
        {
            EnvironmentalObservationKind.AirTemperature => (12.5, null),
            EnvironmentalObservationKind.RelativeHumidity => (42, null),
            EnvironmentalObservationKind.AtmosphericPressure => (85_000, null),
            EnvironmentalObservationKind.WindSpeed => (4, null),
            EnvironmentalObservationKind.WindDirection => (250, null),
            EnvironmentalObservationKind.WindGust => (7, null),
            EnvironmentalObservationKind.PrecipitationRate => (0, null),
            EnvironmentalObservationKind.RainState => (null, false),
            EnvironmentalObservationKind.SkyBrightness => (21.2, null),
            EnvironmentalObservationKind.SkyQuality => (20.8, null),
            EnvironmentalObservationKind.CloudCover => (0.2, null),
            EnvironmentalObservationKind.CameraSensorTemperature => (-10, null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static DeploymentLocationSnapshot Location()
        => DeploymentLocationSnapshot.Create(
            "issue-209-location",
            1,
            "performance-fixture",
            null,
            Epoch.AddDays(-1),
            null,
            35.5599378,
            -113.9119818,
            520,
            "America/Phoenix");

    private static TrackingCoordinatorFixture CreateTrackingCoordinator(
        string prefix,
        EnvironmentalAcquisitionTelemetry telemetry)
    {
        var tracker = new ConcurrencyTracker();
        var publisher = new RecordingPublisher();
        var services = new ServiceCollection().AddSingleton(tracker).BuildServiceProvider();
        var configurations = Enum.GetValues<EnvironmentalObservationKind>().Select(kind =>
        {
            var triggers = new List<EnvironmentalAcquisitionTrigger>
            {
                EnvironmentalAcquisitionTrigger.Periodic,
                EnvironmentalAcquisitionTrigger.BeforeCapture,
                EnvironmentalAcquisitionTrigger.AfterCapture,
                EnvironmentalAcquisitionTrigger.EveryNthCapture
            };
            if (kind == EnvironmentalObservationKind.CloudCover)
            {
                triggers.Add(EnvironmentalAcquisitionTrigger.RegimeChange);
            }
            var value = Baseline(kind);
            return new EnvironmentalSourceConfiguration
            {
                Id = $"{prefix}-{kind}",
                Type = "TrackingVirtual",
                Kind = kind,
                Required = true,
                Triggers = triggers,
                ScheduleEpochUtc = Epoch,
                PeriodSeconds = 30,
                EveryNthCapture = 3,
                ValidForSeconds = 120,
                StaleAfterSeconds = 45,
                RigId = kind == EnvironmentalObservationKind.CameraSensorTemperature ? "rig-1" : null,
                Options = CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(
                    209,
                    Epoch,
                    value.Numeric,
                    value.Boolean,
                    DelayMilliseconds: 2))
            };
        }).ToArray();
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.GetTempPath(),
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                MaximumConcurrency = 4,
                SourceTimeoutMilliseconds = 5_000,
                Sources = configurations
            }
        });
        var factory = new EnvironmentalSourceFactory(
            services,
            [new EnvironmentalSourceRegistration(
                "TrackingVirtual", typeof(TrackingVirtualSource), typeof(VirtualEnvironmentalSourceOptions))]);
        var coordinator = new EnvironmentalAcquisitionCoordinator(
            factory,
            publisher,
            new FixedDeploymentLocationStore(Location()),
            options,
            new FixedTimeProvider(Epoch),
            telemetry: telemetry);
        return new TrackingCoordinatorFixture(services, coordinator, tracker, publisher);
    }

    private static CoordinatorFixture CreateSingleVirtualCoordinator(
        string root,
        IEnvironmentalObservationPublisher publisher,
        string sourceId,
        int sourceTimeoutMilliseconds,
        int delayMilliseconds = 0,
        IEnvironmentalAcquisitionStateStore? stateStore = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var configuration = SingleSourceConfiguration(sourceId, delayMilliseconds);
        var factory = new EnvironmentalSourceFactory(
            services,
            [new EnvironmentalSourceRegistration(
                "VirtualEnvironment", typeof(VirtualEnvironmentalSource), typeof(VirtualEnvironmentalSourceOptions))]);
        var coordinator = new EnvironmentalAcquisitionCoordinator(
            factory,
            publisher,
            new FixedDeploymentLocationStore(Location()),
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    MaximumConcurrency = 4,
                    SourceTimeoutMilliseconds = sourceTimeoutMilliseconds,
                    Sources = [configuration]
                }
            }),
            new FixedTimeProvider(Epoch),
            stateStore);
        return new CoordinatorFixture(services, coordinator);
    }

    private static EnvironmentalSourceConfiguration SingleSourceConfiguration(
        string sourceId,
        int delayMilliseconds = 0)
        => new()
        {
            Id = sourceId,
            Type = "VirtualEnvironment",
            Kind = EnvironmentalObservationKind.AirTemperature,
            Required = true,
            Triggers = [EnvironmentalAcquisitionTrigger.OnDemand],
            ScheduleEpochUtc = Epoch,
            PeriodSeconds = 30,
            EveryNthCapture = 3,
            ValidForSeconds = 120,
            StaleAfterSeconds = 45,
            Options = CaptureContractJson.SerializeToElement(new VirtualEnvironmentalSourceOptions(
                209,
                Epoch,
                12.5,
                null,
                0.25,
                0.1,
                DelayMilliseconds: delayMilliseconds))
        };

    private static CoordinatorFixture CreateThrowingCoordinator(string root)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var configuration = new EnvironmentalSourceConfiguration
        {
            Id = "a6-throw",
            Type = "HarnessThrow",
            Kind = EnvironmentalObservationKind.AirTemperature,
            Required = true,
            Triggers = [EnvironmentalAcquisitionTrigger.OnDemand],
            ScheduleEpochUtc = Epoch,
            Options = CaptureContractJson.SerializeToElement(new ThrowSourceOptions())
        };
        var factory = new EnvironmentalSourceFactory(
            services,
            [new EnvironmentalSourceRegistration(
                "HarnessThrow", typeof(ThrowingEnvironmentalSource), typeof(ThrowSourceOptions))]);
        var coordinator = new EnvironmentalAcquisitionCoordinator(
            factory,
            new RecordingPublisher(),
            new FixedDeploymentLocationStore(Location()),
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    Sources = [configuration]
                }
            }),
            new FixedTimeProvider(Epoch));
        return new CoordinatorFixture(services, coordinator);
    }

    private static EnvironmentalObservationDeliveryOptions DeliveryOptions(int batchSize)
        => new()
        {
            BatchSize = batchSize,
            LeaseSeconds = 10,
            RequestTimeoutSeconds = 1,
            RetryInitialDelaySeconds = 1,
            RetryMaximumDelaySeconds = 1,
            MaximumAttempts = 10,
            MaximumPendingCount = A5DeliveryCapacity
        };

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        Assert.IsNotEmpty(ordered);
        var rank = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[rank];
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, byte[] payload)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        hash.AppendData(length);
        hash.AppendData(payload);
    }

    private static Guid DeterministicGuid(int value)
        => Guid.Parse($"20900000-0000-0000-0000-{value:D12}");

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static JsonSerializerOptions CreateEvidenceJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json")) &&
                (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    File.Exists(Path.Combine(current.FullName, ".git"))))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private static async Task<GitEvidence> ReadGitEvidenceAsync(string repositoryRoot)
    {
        var commit = (await RunCommandAsync("git", repositoryRoot, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var branch = (await RunCommandAsync(
            "git", repositoryRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        var status = await RunCommandAsync(
            "git", repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var diff = await RunCommandAsync("git", repositoryRoot, "diff", "--binary", "HEAD").ConfigureAwait(false);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes(status));
        fingerprint.AppendData(Encoding.UTF8.GetBytes(diff));
        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("?? ", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            var relativePath = line[3..].Trim('"');
            if (relativePath.Contains("/TestResults/", StringComparison.Ordinal) ||
                relativePath.StartsWith("TestResults/", StringComparison.Ordinal))
            {
                continue;
            }
            var path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                fingerprint.AppendData(Encoding.UTF8.GetBytes(relativePath));
                fingerprint.AppendData(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            }
        }
        var fingerprintSha256 = Convert.ToHexString(fingerprint.GetHashAndReset());
        return new GitEvidence(
            commit,
            branch,
            !string.IsNullOrWhiteSpace(status),
            fingerprintSha256,
            status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
    }

    private static async Task<string> RunCommandAsync(
        string fileName,
        string workingDirectory,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with {process.ExitCode}: {error}");
        }
        return output;
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "EnvironmentalSourceFactory instantiates this source through ActivatorUtilities.")]
    private sealed class TrackingVirtualSource : IEnvironmentalSource
    {
        private readonly VirtualEnvironmentalSource _inner;
        private readonly ConcurrencyTracker _tracker;

        public TrackingVirtualSource(
            EnvironmentalSourceDescriptor descriptor,
            VirtualEnvironmentalSourceOptions options,
            ConcurrencyTracker tracker)
        {
            Descriptor = descriptor;
            _tracker = tracker;
            _inner = new VirtualEnvironmentalSource(descriptor with { Type = "VirtualEnvironment" }, options);
        }

        public EnvironmentalSourceDescriptor Descriptor { get; }

        public async ValueTask<EnvironmentalSourceAcquisitionResult> AcquireAsync(
            EnvironmentalSourceAcquisitionContext context,
            CancellationToken cancellationToken)
        {
            using var scope = _tracker.Enter(Descriptor.Id, context.Trigger, context.CaptureSequence);
            return await _inner.AcquireAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ConcurrencyTracker
    {
        private readonly ConcurrentDictionary<string, int> _currentBySource = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _maximumBySource = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<EnvironmentalAcquisitionTrigger, int> _counts = [];
        private readonly ConcurrentDictionary<EnvironmentalAcquisitionTrigger, ConcurrentBag<long>> _sequences = [];
        private int _currentGlobal;
        private int _maximumGlobal;

        public int MaximumGlobalInflight => Volatile.Read(ref _maximumGlobal);
        public IReadOnlyDictionary<string, int> MaximumPerSourceInflight => _maximumBySource;

        public IDisposable Enter(
            string sourceId,
            EnvironmentalAcquisitionTrigger trigger,
            long? captureSequence)
        {
            _counts.AddOrUpdate(trigger, 1, static (_, count) => count + 1);
            if (captureSequence is { } sequence)
            {
                _sequences.GetOrAdd(trigger, static _ => []).Add(sequence);
            }
            var sourceCurrent = _currentBySource.AddOrUpdate(sourceId, 1, static (_, count) => count + 1);
            _maximumBySource.AddOrUpdate(sourceId, sourceCurrent, (_, maximum) => Math.Max(maximum, sourceCurrent));
            var globalCurrent = Interlocked.Increment(ref _currentGlobal);
            UpdateMaximum(ref _maximumGlobal, globalCurrent);
            return new TrackerScope(this, sourceId);
        }

        public int Count(EnvironmentalAcquisitionTrigger trigger)
            => _counts.GetValueOrDefault(trigger);

        public long[] CaptureSequences(EnvironmentalAcquisitionTrigger trigger)
            => _sequences.TryGetValue(trigger, out var values)
                ? values.Distinct().Order().ToArray()
                : [];

        private static void UpdateMaximum(ref int maximum, int value)
        {
            var observed = Volatile.Read(ref maximum);
            while (observed < value)
            {
                var previous = Interlocked.CompareExchange(ref maximum, value, observed);
                if (previous == observed)
                {
                    return;
                }
                observed = previous;
            }
        }

        private void Exit(string sourceId)
        {
            _currentBySource.AddOrUpdate(sourceId, 0, static (_, count) => count - 1);
            Interlocked.Decrement(ref _currentGlobal);
        }

        private sealed class TrackerScope(ConcurrencyTracker tracker, string sourceId) : IDisposable
        {
            public void Dispose() => tracker.Exit(sourceId);
        }
    }

    private sealed class RecordingPublisher : IEnvironmentalObservationPublisher
    {
        private int _publishCount;
        public int PublishCount => Volatile.Read(ref _publishCount);

        public ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _publishCount);
            return ValueTask.FromResult(new EnvironmentalObservationPublishResult(
                EnvironmentalObservationPublishDisposition.Enqueued,
                null));
        }
    }

    private enum Boundary
    {
        BeforeCommit,
        AfterCommit
    }

    private sealed class BoundaryPublisher(
        ILocalEnvironmentalObservationStore store,
        string root,
        Boundary boundary) : IEnvironmentalObservationPublisher
    {
        public async ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
            EnvironmentalObservationFactV1 fact,
            CancellationToken cancellationToken = default)
        {
            if (boundary == Boundary.BeforeCommit)
            {
                throw new IOException("Injected before local commit.");
            }
            _ = await store.CommitLocalAsync(root, fact, cancellationToken).ConfigureAwait(false);
            throw new IOException("Injected after local commit.");
        }
    }

    private sealed record ThrowSourceOptions(bool Enabled = true);

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
        Justification = "EnvironmentalSourceFactory instantiates this source through ActivatorUtilities.")]
    private sealed class ThrowingEnvironmentalSource(
        EnvironmentalSourceDescriptor descriptor,
        ThrowSourceOptions options) : IEnvironmentalSource
    {
        public EnvironmentalSourceDescriptor Descriptor { get; } = descriptor;

        public ValueTask<EnvironmentalSourceAcquisitionResult> AcquireAsync(
            EnvironmentalSourceAcquisitionContext context,
            CancellationToken cancellationToken)
            => options.Enabled
                ? throw new IOException("Injected source failure.")
                : ValueTask.FromResult(new EnvironmentalSourceAcquisitionResult(
                    EnvironmentalSourceAcquisitionOutcome.Missing, "source-missing"));
    }

    private sealed class FixedDeploymentLocationStore : IDeploymentLocationStore
    {
        public FixedDeploymentLocationStore(DeploymentLocationSnapshot active)
        {
            Active = active;
        }

        public DeploymentLocationSnapshot? Active { get; }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(Active!);

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => Active!;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class IdempotentReceiver(TimeProvider timeProvider)
    {
        private readonly Dictionary<(string Source, Guid Observation), EnvironmentalObservationAcknowledgement> _facts = [];
        public int Count => _facts.Count;

        public EnvironmentalObservationAcknowledgement Ingest(EnvironmentalObservationV1 observation)
        {
            var source = EnvironmentalObservationJson.ComputeSourceIdentitySha256(observation);
            var key = (source, observation.ObservationId);
            if (_facts.TryGetValue(key, out var existing))
            {
                return existing with { Disposition = EnvironmentalObservationDeliveryDisposition.Duplicate };
            }
            var acknowledgement = new EnvironmentalObservationAcknowledgement(
                EnvironmentalObservationAcknowledgement.CurrentSchemaVersion,
                observation.ObservationId,
                source,
                EnvironmentalObservationJson.ComputeContentSha256(observation),
                timeProvider.GetUtcNow(),
                EnvironmentalObservationDeliveryDisposition.Accepted);
            _facts.Add(key, acknowledgement);
            return acknowledgement;
        }
    }

    private sealed class ReceiverTransport(IdempotentReceiver receiver) : IEnvironmentalObservationTransport, IDisposable
    {
        public int SendCount { get; private set; }
        public int AcceptedCount { get; private set; }
        public int DuplicateCount { get; private set; }

        public ValueTask<EnvironmentalObservationTransportResult> SendAsync(
            EnvironmentalObservationV1 observation,
            CancellationToken cancellationToken)
        {
            SendCount++;
            var acknowledgement = receiver.Ingest(observation);
            if (acknowledgement.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted)
            {
                AcceptedCount++;
            }
            else
            {
                DuplicateCount++;
            }
            return ValueTask.FromResult(new EnvironmentalObservationTransportResult(
                EnvironmentalObservationTransportDisposition.Acknowledged,
                acknowledgement.Disposition == EnvironmentalObservationDeliveryDisposition.Accepted
                    ? "accepted"
                    : "duplicate",
                acknowledgement));
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullTargetResolver : IEnvironmentalObservationTargetResolver
    {
        public static NullTargetResolver Instance { get; } = new();

        public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<EnvironmentalObservationResolvedTarget?>(null);
    }

    private sealed class CoordinatorFixture(ServiceProvider services, EnvironmentalAcquisitionCoordinator coordinator)
        : IDisposable
    {
        public EnvironmentalAcquisitionCoordinator Coordinator { get; } = coordinator;

        public void Dispose()
        {
            Coordinator.Dispose();
            services.Dispose();
        }
    }

    private sealed class TrackingCoordinatorFixture(
        ServiceProvider services,
        EnvironmentalAcquisitionCoordinator coordinator,
        ConcurrencyTracker tracker,
        RecordingPublisher publisher) : IDisposable
    {
        public EnvironmentalAcquisitionCoordinator Coordinator { get; } = coordinator;
        public ConcurrencyTracker Tracker { get; } = tracker;
        public RecordingPublisher Publisher { get; } = publisher;

        public void Dispose()
        {
            Coordinator.Dispose();
            services.Dispose();
        }
    }

    private sealed class WorkingSetTracker : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly object _gate = new();

        public WorkingSetTracker()
        {
            _process.Refresh();
            StartBytes = _process.WorkingSet64;
            PeakBytes = StartBytes;
        }

        public long StartBytes { get; }
        public long PeakBytes { get; private set; }
        public long GrowthBytes => Math.Max(0, PeakBytes - StartBytes);

        public void Sample()
        {
            lock (_gate)
            {
                _process.Refresh();
                PeakBytes = Math.Max(PeakBytes, _process.WorkingSet64);
            }
        }

        public void Dispose() => _process.Dispose();
    }

    private sealed class RuntimeSignalRecorder : IDisposable
    {
        private static readonly HashSet<string> AllowedMetricTagKeys = new(StringComparer.Ordinal)
        {
            "schema",
            "trigger",
            "observation_kind",
            "source_kind",
            "quality",
            "outcome",
            "reason",
            "association_status"
        };
        private readonly ConcurrentDictionary<string, byte> _metricNames = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _metricTagKeys = new(StringComparer.Ordinal);
        private readonly ConcurrentBag<string> _metricTagValues = [];
        private readonly ConcurrentDictionary<string, byte> _activityNames = new(StringComparer.Ordinal);
        private readonly MeterListener _meterListener = new();
        private readonly ActivityListener _activityListener = new();

        public RuntimeSignalRecorder()
        {
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(
                    instrument.Meter.Name,
                    EnvironmentalAcquisitionTelemetry.InstrumentationName,
                    StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>(RecordMeasurement);
            _meterListener.SetMeasurementEventCallback<int>(RecordMeasurement);
            _meterListener.SetMeasurementEventCallback<double>(RecordMeasurement);
            _meterListener.Start();

            _activityListener.ShouldListenTo = source => string.Equals(
                source.Name,
                EnvironmentalAcquisitionTelemetry.InstrumentationName,
                StringComparison.Ordinal);
            _activityListener.Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData;
            _activityListener.ActivityStopped = activity => _activityNames.TryAdd(activity.OperationName, 0);
            ActivitySource.AddActivityListener(_activityListener);
        }

        public void RecordObservableInstruments() => _meterListener.RecordObservableInstruments();

        public RuntimeSignalEvidence Snapshot()
        {
            var forbiddenKeys = _metricTagKeys.Keys.Where(key => !AllowedMetricTagKeys.Contains(key))
                .Order(StringComparer.Ordinal).ToArray();
            var forbiddenValue = _metricTagValues.Any(value =>
                value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                value.Contains("35.5599378", StringComparison.Ordinal));
            return new RuntimeSignalEvidence(
                EnvironmentalAcquisitionTelemetry.InstrumentationName,
                _metricNames.Keys.Order(StringComparer.Ordinal).ToArray(),
                _activityNames.Keys.Order(StringComparer.Ordinal).ToArray(),
                _metricTagKeys.Keys.Order(StringComparer.Ordinal).ToArray(),
                forbiddenKeys,
                forbiddenValue,
                _metricNames.ContainsKey("skymonitor.environment.local.history.query.duration"),
                _metricNames.ContainsKey("skymonitor.environment.local.journal.oldest.age"));
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }

        private void RecordMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
            where T : struct
        {
            _metricNames.TryAdd(instrument.Name, 0);
            foreach (var tag in tags)
            {
                _metricTagKeys.TryAdd(tag.Key, 0);
                if (tag.Value is not null)
                {
                    _metricTagValues.Add(Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }
    }

    private sealed record GitEvidence(
        string CommitSha,
        string Branch,
        bool Dirty,
        string DirtyFingerprintSha256,
        int ChangedPathCount);

    private sealed record HistoryDataset(int Rows, string Root);

    private sealed record HistoryQuerySample(
        string Position,
        double Milliseconds,
        int Records,
        int PayloadBytes,
        double RunElapsedSeconds);

    private sealed record JournalRowEvidence(
        long Rows,
        long Kinds,
        long Sources,
        long DistinctObservations,
        long DistinctContentHashes);

    private sealed record SqlitePragmas(string JournalMode, long Synchronous);

    private sealed record SqliteCheckpoint(
        string Mode,
        int Busy,
        int LogFrames,
        int CheckpointedFrames);

    private sealed record SqliteFileStats(
        long DatabaseBytes,
        long WalBytes,
        long SharedMemoryBytes)
    {
        public long TotalBytes => DatabaseBytes + WalBytes + SharedMemoryBytes;
    }

    private sealed record RuntimeSignalEvidence(
        string InstrumentationName,
        IReadOnlyList<string> MetricNames,
        IReadOnlyList<string> ActivityNames,
        IReadOnlyList<string> MetricTagKeys,
        IReadOnlyList<string> ForbiddenMetricTagKeys,
        bool ContainsForbiddenSignalValue,
        bool HistoryQueryHistogramPresent,
        bool JournalOldestAgeGaugePresent);
}
